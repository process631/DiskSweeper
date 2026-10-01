using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskSweeper.Core.Scanning;

/// <summary>
/// Scans a whole NTFS drive by reading its Master File Table directly, the way WizTree does. NTFS keeps one
/// 1 KB record per file in that table, so reading it front to back sees every file without opening a single
/// folder: typically 10x faster than walking folders. Needs administrator rights and reads only.
/// </summary>
public sealed class MftScanner
{
    private const int RootRecord = 5;
    private const int ExtendRecord = 11;
    private const int FirstUserRecord = 16;
    private const uint AttrStandardInformation = 0x10;
    private const uint AttrAttributeList = 0x20;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrReparsePoint = 0xC0;
    private const uint AttrEnd = 0xFFFFFFFF;
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const uint ReparseTagSymlink = 0xA000000C;
    private const int ChunkSize = 8 << 20;

    /// <summary>Whether a path can be scanned this way, and if not, why.</summary>
    public static bool CanScan(string path, out string reason)
    {
        string full = LongPath.Normalize(path);
        if (!Elevation.IsElevated)
        {
            reason = "Fast scanning needs administrator rights.";
            return false;
        }
        if (!string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "Fast scanning works on whole local drives only (like C:\\).";
            return false;
        }
        try
        {
            var drive = new DriveInfo(full);
            if (!drive.IsReady || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Fast scanning needs an NTFS drive ({full} is {(drive.IsReady ? drive.DriveFormat : "not ready")}).";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }
        reason = "";
        return true;
    }

    public Task<ScanResult> ScanAsync(string drive, ScanProgress? progress = null, CancellationToken cancellationToken = default)
    {
        string root = LongPath.Normalize(drive);
        if (!CanScan(root, out string reason)) throw new NotSupportedException(reason);
        return Task.Run(() => Scan(root, progress ?? new ScanProgress(), cancellationToken), cancellationToken);
    }

    private static ScanResult Scan(string rootPath, ScanProgress progress, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        using var volume = Volume.Open(rootPath[..2]);
        var mftExtents = volume.ReadMftExtents(out long mftBytes);
        int recordCount = checked((int)(mftBytes / volume.RecordSize));
        var records = new RecordTable(recordCount);

        // Read the table in large chunks, following its extents in order (record number = position / record size).
        var buffer = new byte[ChunkSize];
        long recordIndex = 0;
        foreach (var (offset, length) in mftExtents)
        {
            for (long done = 0; done < length && recordIndex < recordCount; done += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(ChunkSize, length - done);
                volume.Read(offset + done, buffer.AsSpan(0, toRead));

                for (int at = 0; at + volume.RecordSize <= toRead && recordIndex < recordCount; at += volume.RecordSize, recordIndex++)
                    records.Parse(buffer.AsSpan(at, volume.RecordSize), (int)recordIndex, volume.BytesPerSector);

                progress.SetFraction((double)recordIndex / recordCount);
            }
        }

        var result = records.BuildTree(rootPath, progress);
        TreeTotals.Compute(result.Root);
        return new ScanResult(result.Root, startedAt, stopwatch.Elapsed, ScanMethod.MasterFileTable, [], result.Links, result.CloudFiles, result.CloudBytes);
    }

    /// <summary>Everything learned from the records, indexed by record number.</summary>
    private sealed class RecordTable(int count)
    {
        private const byte InUse = 1;
        private const byte IsDirectory = 2;
        private const byte IsLink = 4;

        private readonly byte[] _flags = new byte[count];
        private readonly int[] _parent = new int[count];
        private readonly string?[] _name = new string?[count];
        private readonly byte[] _namePriority = new byte[count];
        private readonly long[] _size = new long[count];
        private readonly long[] _modified = new long[count];
        private readonly uint[] _attributes = new uint[count];
        private readonly List<(int Record, int Parent, string Name)> _extraNames = [];
        private readonly object _gate = new();

        public void Parse(Span<byte> record, int index, int bytesPerSector)
        {
            if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8) || !ApplyFixups(record, bytesPerSector)) return;

            ushort headerFlags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
            if ((headerFlags & 1) == 0) return; // Free record.

            // Extension records hold overflow attributes of a "base" record; credit what they contain to it.
            long baseReference = BinaryPrimitives.ReadInt64LittleEndian(record[0x20..]) & 0xFFFF_FFFF_FFFF;
            int owner = baseReference == 0 ? index : (int)baseReference;
            if (owner >= count) return;
            if (baseReference == 0)
            {
                _flags[owner] |= InUse;
                if ((headerFlags & 2) != 0) _flags[owner] |= IsDirectory;
            }

            int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
            while (offset + 16 <= record.Length)
            {
                var attribute = record[offset..];
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(attribute);
                if (type == AttrEnd) break;
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[4..]);
                if (length <= 0 || offset + length > record.Length) break;
                attribute = attribute[..length];

                bool nonResident = attribute[8] != 0;
                int nameLength = attribute[9];

                switch (type)
                {
                    case AttrStandardInformation when !nonResident:
                    {
                        var value = ResidentValue(attribute);
                        if (value.Length >= 36)
                        {
                            _modified[owner] = BinaryPrimitives.ReadInt64LittleEndian(value[8..]);
                            _attributes[owner] = BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
                        }
                        break;
                    }
                    case AttrFileName when !nonResident:
                        ReadFileName(ResidentValue(attribute), owner);
                        break;
                    case AttrData when nameLength == 0:
                        if (!nonResident)
                        {
                            _size[owner] = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
                        }
                        else if (BinaryPrimitives.ReadInt64LittleEndian(attribute[16..]) == 0)
                        {
                            // Only the first piece (starting at cluster 0) carries the real size.
                            _size[owner] = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
                        }
                        break;
                    case AttrReparsePoint:
                    {
                        uint tag = nonResident ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(ResidentValue(attribute));
                        if (tag is ReparseTagMountPoint or ReparseTagSymlink) _flags[owner] |= IsLink;
                        break;
                    }
                }
                offset += length;
            }
        }

        private void ReadFileName(ReadOnlySpan<byte> value, int owner)
        {
            if (value.Length < 66) return;
            byte nameChars = value[64];
            byte space = value[65];
            if (space == 2 || value.Length < 66 + nameChars * 2) return; // DOS 8.3 short names duplicate the real name.

            int parent = (int)(BinaryPrimitives.ReadInt64LittleEndian(value) & 0xFFFF_FFFF_FFFF);
            string name = Encoding.Unicode.GetString(value.Slice(66, nameChars * 2));
            byte priority = space is 1 or 3 ? (byte)2 : (byte)1; // Prefer Win32 names over POSIX ones.

            if (_name[owner] is null)
            {
                _name[owner] = name;
                _parent[owner] = parent;
                _namePriority[owner] = priority;
            }
            else if (_parent[owner] == parent && string.Equals(_name[owner], name, StringComparison.OrdinalIgnoreCase))
            {
                if (priority > _namePriority[owner])
                {
                    _name[owner] = name;
                    _namePriority[owner] = priority;
                }
            }
            else
            {
                // A hard link: the same file listed in another folder (or under another name).
                lock (_gate) _extraNames.Add((owner, parent, name));
            }
        }

        public (FileNode Root, long Links, long CloudFiles, long CloudBytes) BuildTree(string rootPath, ScanProgress progress)
        {
            var nodes = new FileNode?[count];
            var root = new FileNode(rootPath, null, NodeFlags.Directory, 0, FromFileTime(_modified[RootRecord]), FileAttributes.Directory)
            {
                ChildList = [],
            };
            nodes[RootRecord] = root;

            long links = 0, cloudFiles = 0, cloudBytes = 0, files = 0, folders = 0, bytes = 0;
            FileNode Create(int record, string name)
            {
                bool directory = (_flags[record] & IsDirectory) != 0;
                var attributes = (FileAttributes)(_attributes[record] & 0xFFFFF) | (directory ? FileAttributes.Directory : 0);
                var flags = directory ? NodeFlags.Directory : NodeFlags.None;
                long size = directory ? 0 : _size[record];

                if (directory && (_flags[record] & IsLink) != 0)
                {
                    flags |= NodeFlags.Link;
                    links++;
                }
                else if (!directory && (attributes & FileSystemWalk.CloudOnlyAttributes) != 0)
                {
                    flags |= NodeFlags.CloudOnly;
                    cloudFiles++;
                    cloudBytes += size;
                    size = 0;
                }

                var node = new FileNode(name, null, flags, size, FromFileTime(_modified[record]), attributes);
                if (directory && (flags & NodeFlags.Link) == 0) node.ChildList = [];
                return node;
            }

            for (int i = FirstUserRecord; i < count; i++)
            {
                if ((_flags[i] & InUse) != 0 && _name[i] is string name) nodes[i] = Create(i, name);
            }

            void Attach(FileNode node, int parentRecord)
            {
                if (parentRecord < 0 || parentRecord >= count || nodes[parentRecord] is not { ChildList: { } siblings } parent) return;
                node.Parent = parent;
                siblings.Add(node);
                if (node.IsDirectory)
                {
                    folders++;
                }
                else
                {
                    parent.Size += node.Size;
                    parent.FileCount++;
                    files++;
                    bytes += node.Size;
                }
            }

            for (int i = FirstUserRecord; i < count; i++)
            {
                if (nodes[i] is FileNode node && i != ExtendRecord) Attach(node, _parent[i]);
            }
            foreach (var (record, parent, name) in _extraNames)
            {
                if (nodes[record] is FileNode original && !original.IsDirectory) Attach(Create(record, name), parent);
            }

            // Anything whose chain of parents doesn't reach the root (NTFS's own files under $Extend,
            // half-deleted entries) is simply not reachable from the tree, just like in a folder walk.
            progress.Add(files, folders, bytes);
            return (root, links, cloudFiles, cloudBytes);
        }

        private static DateTime FromFileTime(long fileTime) =>
            fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
    }

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attribute)
    {
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
        return offset + length <= attribute.Length ? attribute.Slice(offset, length) : [];
    }

    /// <summary>
    /// NTFS protects each record against torn writes by replacing the last two bytes of every sector with a
    /// sequence number and keeping the real bytes in a table at the start. This puts the real bytes back.
    /// </summary>
    private static bool ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount == 0 || usaOffset + usaCount * 2 > record.Length) return false;

        int stride = record.Length / (usaCount - 1 == 0 ? 1 : usaCount - 1); // One fixup per sector-sized stride of the record.
        ushort check = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * stride - 2;
            if (end + 2 > record.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != check) return false;
            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>Raw, read-only access to a drive's sectors.</summary>
    private sealed class Volume : IDisposable
    {
        private readonly SafeFileHandle _handle;

        private Volume(SafeFileHandle handle) => _handle = handle;

        public int BytesPerSector { get; private set; }

        public int ClusterSize { get; private set; }

        public int RecordSize { get; private set; }

        private long MftOffset { get; set; }

        public static Volume Open(string driveLetterWithColon)
        {
            var handle = CreateFile(@"\\.\" + driveLetterWithColon, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't open drive {driveLetterWithColon} for reading.");

            var volume = new Volume(handle);
            var boot = new byte[4096];
            volume.Read(0, boot);
            if (!boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) throw new NotSupportedException("Not an NTFS drive.");

            volume.BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
            int sectorsPerCluster = boot[0x0D] <= 0x80 ? boot[0x0D] : 1 << (256 - boot[0x0D]);
            volume.ClusterSize = volume.BytesPerSector * sectorsPerCluster;
            volume.MftOffset = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30)) * volume.ClusterSize;
            sbyte clustersPerRecord = (sbyte)boot[0x40];
            volume.RecordSize = clustersPerRecord > 0 ? clustersPerRecord * volume.ClusterSize : 1 << -clustersPerRecord;
            return volume;
        }

        public void Read(long offset, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = RandomAccess.Read(_handle, buffer[total..], offset + total);
                if (read <= 0) throw new EndOfStreamException("The drive ended earlier than its file table says.");
                total += read;
            }
        }

        /// <summary>Where the Master File Table itself lives on disk, as (byte offset, byte length) pieces in order.</summary>
        public List<(long Offset, long Length)> ReadMftExtents(out long mftBytes)
        {
            // Record 0 is the table's own entry. Reads must be whole sectors, so read a full cluster.
            var cluster = new byte[Math.Max(ClusterSize, RecordSize)];
            Read(MftOffset, cluster);
            var record0 = cluster.AsSpan(0, RecordSize);
            if (!record0[..4].SequenceEqual("FILE"u8) || !ApplyFixups(record0, BytesPerSector))
                throw new InvalidDataException("The drive's file table looks damaged.");

            var runs = new List<(long StartVcn, List<(long Lcn, long Clusters)> Runs)>();
            mftBytes = 0;
            var listedElsewhere = new List<long>();
            CollectDataRuns(record0, runs, ref mftBytes, listedElsewhere);

            // A very fragmented table describes the rest of itself in extra records (named by an attribute list).
            foreach (long recordNumber in listedElsewhere.Distinct())
            {
                long position = recordNumber * RecordSize;
                long diskOffset = Translate(runs, position);
                long aligned = diskOffset - diskOffset % BytesPerSector;
                var raw = new byte[Math.Max(BytesPerSector, RecordSize) + BytesPerSector];
                Read(aligned, raw);
                var span = raw.AsSpan((int)(diskOffset - aligned), RecordSize);
                if (span[..4].SequenceEqual("FILE"u8) && ApplyFixups(span, BytesPerSector)) CollectDataRuns(span, runs, ref mftBytes, null);
            }

            if (mftBytes == 0) throw new InvalidDataException("The drive's file table has no size.");
            return runs.OrderBy(r => r.StartVcn)
                .SelectMany(r => r.Runs)
                .Where(r => r.Lcn >= 0)
                .Select(r => (r.Lcn * ClusterSize, r.Clusters * ClusterSize))
                .ToList();
        }

        private long Translate(List<(long StartVcn, List<(long Lcn, long Clusters)> Runs)> runs, long bytePosition)
        {
            long vcn = bytePosition / ClusterSize;
            foreach (var (startVcn, list) in runs.OrderBy(r => r.StartVcn))
            {
                long current = startVcn;
                foreach (var (lcn, clusters) in list)
                {
                    if (vcn < current + clusters) return (lcn + (vcn - current)) * ClusterSize + bytePosition % ClusterSize;
                    current += clusters;
                }
            }
            throw new InvalidDataException("A file table record lies outside the table.");
        }

        private static void CollectDataRuns(Span<byte> record, List<(long, List<(long, long)>)> runs, ref long mftBytes, List<long>? listedElsewhere)
        {
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
            while (offset + 16 <= record.Length)
            {
                var attribute = record[offset..];
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(attribute);
                if (type == AttrEnd) break;
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[4..]);
                if (length <= 0 || offset + length > record.Length) break;
                attribute = attribute[..length];

                if (type == AttrData && attribute[8] != 0 && attribute[9] == 0)
                {
                    long startVcn = BinaryPrimitives.ReadInt64LittleEndian(attribute[16..]);
                    if (startVcn == 0) mftBytes = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
                    int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
                    runs.Add((startVcn, DecodeRuns(attribute[runOffset..])));
                }
                else if (type == AttrAttributeList && listedElsewhere is not null && attribute[8] == 0)
                {
                    var list = ResidentValue(attribute);
                    for (int at = 0; at + 26 <= list.Length;)
                    {
                        uint entryType = BinaryPrimitives.ReadUInt32LittleEndian(list[at..]);
                        int entryLength = BinaryPrimitives.ReadUInt16LittleEndian(list[(at + 4)..]);
                        long reference = BinaryPrimitives.ReadInt64LittleEndian(list[(at + 16)..]) & 0xFFFF_FFFF_FFFF;
                        if (entryType == AttrData && reference != 0) listedElsewhere.Add(reference);
                        if (entryLength <= 0) break;
                        at += entryLength;
                    }
                }
                offset += length;
            }
        }

        /// <summary>
        /// Decodes NTFS "data runs": each run is a header byte (sizes of the next two fields), a cluster count,
        /// and a signed jump from the previous run's starting cluster. A missing jump means a sparse (empty) run.
        /// </summary>
        private static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> data)
        {
            var runs = new List<(long, long)>();
            long lcn = 0;
            int at = 0;
            while (at < data.Length && data[at] != 0)
            {
                int lengthSize = data[at] & 0x0F;
                int offsetSize = data[at] >> 4;
                at++;
                if (at + lengthSize + offsetSize > data.Length) break;

                long clusters = ReadLittleEndian(data.Slice(at, lengthSize), signed: false);
                at += lengthSize;
                if (offsetSize == 0)
                {
                    runs.Add((-1, clusters));
                }
                else
                {
                    lcn += ReadLittleEndian(data.Slice(at, offsetSize), signed: true);
                    runs.Add((lcn, clusters));
                }
                at += offsetSize;
            }
            return runs;
        }

        private static long ReadLittleEndian(ReadOnlySpan<byte> bytes, bool signed)
        {
            long value = 0;
            for (int i = bytes.Length - 1; i >= 0; i--) value = (value << 8) | bytes[i];
            if (signed && bytes.Length > 0 && bytes.Length < 8 && (bytes[^1] & 0x80) != 0) value -= 1L << (bytes.Length * 8);
            return value;
        }

        public void Dispose() => _handle.Dispose();
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
