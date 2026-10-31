namespace DiskSweeper.Core.SmartFinds;

/// <summary>
/// Which Cleanup advice commands DiskSweeper is willing to start. The rules file is compiled into the app,
/// and this keeps an edited or replaced command from opening an arbitrary program or a PowerShell payload.
/// Shell commands and executables are an exact list. Settings links and https links are allowed by shape.
/// </summary>
public static class AdviceCommands
{
    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "powercfg /h off",
        "DISM /Online /Cleanup-Image /StartComponentCleanup",
        "SystemPropertiesPerformance.exe",
        "SystemPropertiesProtection.exe",
    };

    public static bool IsAllowed(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 240 || command.Any(char.IsControl) || command.Contains('"') || command.Contains('\''))
            return false;

        if (KnownCommands.Contains(command)) return true;

        if (!command.StartsWith("start ", StringComparison.OrdinalIgnoreCase)) return false;

        string target = command[6..].Trim();
        if (target.Length == 0 || target.Contains(' ')) return false;
        return target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
