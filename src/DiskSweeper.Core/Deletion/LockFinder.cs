using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DiskSweeper.Core.Deletion;

public enum LockerKind
{
    Unknown = 0,
    App = 1,
    BackgroundApp = 2,
    Service = 3,
    Explorer = 4,
    Console = 5,

    /// <summary>A process Windows can't run without, like csrss or wininit. Never closed by DiskSweeper.</summary>
    Critical = 1000,
}

/// <summary>A running program that has a file open, so Windows won't let it be deleted.</summary>
public sealed record LockingProcess(int ProcessId, DateTime StartTimeUtc, string DisplayName, string ProcessName, string? ServiceName, LockerKind Kind)
{
    public bool IsThisApp => ProcessId == Environment.ProcessId;

    public override string ToString() =>
        ServiceName is { Length: > 0 } ? $"{DisplayName} (service {ServiceName})" : $"{DisplayName} ({ProcessName}, PID {ProcessId})";
}

/// <summary>
/// Asks the Windows Restart Manager which processes have files open. This is the same mechanism
/// installers use for "close these apps to continue", and it needs no admin rights or extra tools.
/// </summary>
public static class LockFinder
{
    private const int ErrorMoreData = 234;
    private const int MaxFilesPerQuery = 1000;

    public static IReadOnlyList<LockingProcess> Find(IEnumerable<string> files)
    {
        var lockers = new Dictionary<int, LockingProcess>();
        foreach (var batch in files.Select(LongPath.ToNormal).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(MaxFilesPerQuery))
        {
            foreach (var locker in FindBatch(batch)) lockers.TryAdd(locker.ProcessId, locker);
        }
        return lockers.Values.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<LockingProcess> FindBatch(string[] files)
    {
        var found = new List<LockingProcess>();
        var key = new StringBuilder(33);
        if (RmStartSession(out uint session, 0, key) != 0) return found;

        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return found;

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;
            RmProcessInfo[]? infos = null;
            int result;
            do
            {
                count = needed;
                infos = needed == 0 ? null : new RmProcessInfo[needed];
                result = RmGetList(session, out needed, ref count, infos, ref reasons);
            }
            while (result == ErrorMoreData);

            if (result != 0 || infos is null) return found;

            for (int i = 0; i < count; i++)
            {
                var info = infos[i];
                int pid = info.Process.dwProcessId;
                long ticks = ((long)info.Process.ProcessStartTime.dwHighDateTime << 32) | (uint)info.Process.ProcessStartTime.dwLowDateTime;
                found.Add(new LockingProcess(
                    pid,
                    DateTime.FromFileTimeUtc(ticks),
                    string.IsNullOrWhiteSpace(info.strAppName) ? ProcessNameOf(pid) : info.strAppName,
                    ProcessNameOf(pid),
                    string.IsNullOrWhiteSpace(info.strServiceShortName) ? null : info.strServiceShortName,
                    Enum.IsDefined(typeof(LockerKind), info.ApplicationType) ? (LockerKind)info.ApplicationType : LockerKind.Unknown));
            }
        }
        finally
        {
            RmEndSession(session);
        }
        return found;
    }

    private static string ProcessNameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "unknown";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] files, uint applicationCount, RmUniqueProcess[]? applications, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? processes, ref uint rebootReasons);
}
