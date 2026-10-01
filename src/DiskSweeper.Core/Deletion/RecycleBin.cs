using System.Runtime.InteropServices;

namespace DiskSweeper.Core.Deletion;

public static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    /// <summary>
    /// The shell's Recycle Bin can't take paths of 260+ characters or names ending in a dot or space.
    /// Those have to be deleted permanently instead.
    /// </summary>
    public static bool CanTake(string path) =>
        path.Length < 260 && !path.Split(Path.DirectorySeparatorChar).Skip(1).Any(part => part.EndsWith('.') || part.EndsWith(' '));

    /// <summary>
    /// Moves a file or folder to the Recycle Bin without any prompts, except Windows' own warning when
    /// something is too big for the bin and would be deleted for good. Returns the shell's error code (0 = success).
    /// </summary>
    public static int Send(string path)
    {
        return StaThread.Run(() =>
        {
            var operation = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = path + '\0', // The list must end with two nulls; marshalling adds the second.
                fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning),
            };
            int code = SHFileOperation(ref operation);
            return code == 0 && operation.fAnyOperationsAborted ? -1 : code;
        });
    }

    /// <summary>Total size and item count across the Recycle Bins of all drives.</summary>
    public static (long Bytes, long Items) Query()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        int hr = SHQueryRecycleBin(null, ref info);
        return hr == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Permanently empties every Recycle Bin. Returns false if it was already empty or Windows refused.</summary>
    public static bool Empty()
    {
        int hr = StaThread.Run(() => SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound));
        return hr == 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
