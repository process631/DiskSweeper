using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskSweeper.Core;

/// <summary>
/// Turns Windows privileges on or off for this process. Administrators hold privileges such as
/// SeBackupPrivilege, but Windows keeps them switched off until a program asks for them.
/// </summary>
public static class Privileges
{
    /// <summary>Lets this process read any file or folder regardless of its permissions. It does not allow changing or deleting anything.</summary>
    public const string Backup = "SeBackupPrivilege";

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;
    private const int TokenInformationPrivileges = 3;

    /// <summary>Switches a privilege on. Returns false if this process doesn't hold it (for example, not running as administrator).</summary>
    public static bool TryEnable(string privilege) => TrySet(privilege, enable: true);

    public static bool TryDisable(string privilege) => TrySet(privilege, enable: false);

    public static bool IsEnabled(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid)) return false;

            // Layout: a uint count, then `count` entries of { LUID (8 bytes), uint attributes }.
            GetTokenInformation(token, TokenInformationPrivileges, IntPtr.Zero, 0, out int size);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenInformationPrivileges, buffer, size, out _)) return false;
                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = buffer + 4 + i * 12;
                    if ((uint)Marshal.ReadInt32(entry) == luid.LowPart && Marshal.ReadInt32(entry + 4) == luid.HighPart)
                        return ((uint)Marshal.ReadInt32(entry + 8) & SePrivilegeEnabled) != 0;
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool TrySet(string privilege, bool enable)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid)) throw new Win32Exception();

            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = enable ? SePrivilegeEnabled : 0 };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) return false;

            // AdjustTokenPrivileges "succeeds" even when the privilege isn't held; this error says so.
            return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);
}
