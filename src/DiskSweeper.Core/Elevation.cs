using System.Security.Principal;

namespace DiskSweeper.Core;

public static class Elevation
{
    /// <summary>True when this process runs with administrator rights ("Run as administrator").</summary>
    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Security ID of the signed-in user, e.g. "S-1-5-21-…". Used with icacls so names with spaces are never an issue.</summary>
    public static string CurrentUserSid { get; } = WindowsIdentity.GetCurrent().User!.Value;
}
