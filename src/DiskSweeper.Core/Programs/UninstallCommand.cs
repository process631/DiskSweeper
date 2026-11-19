using System.Text;
using System.Text.RegularExpressions;

namespace DiskSweeper.Core.Programs;

public enum UninstallStyle
{
    /// <summary>The uninstaller can finish without a window.</summary>
    Silent,

    /// <summary>The program's own uninstaller is shown, because it has no quiet mode we can trust.</summary>
    Interactive,

    /// <summary>A launcher is asked to uninstall. It often returns before the game is actually gone.</summary>
    LauncherHandoff,
}

/// <summary>A program the user can start to uninstall something, already split into a file and arguments.</summary>
public sealed record UninstallLaunch(string FileName, IReadOnlyList<string> Arguments, UninstallStyle Style, string DisplayText);

/// <summary>
/// Turns an uninstall string into a command. Quiet strings and known silent switches (msiexec, Inno Setup)
/// are used when they are real; anything else is left as the normal uninstaller so a guessed switch can't
/// skip the program's own confirmation.
/// </summary>
public static partial class UninstallCommand
{
    public static UninstallLaunch? TryCreate(CatalogApp app)
    {
        if (app.Source == AppSource.MicrosoftStore && app.StorePackageFullName is { Length: > 0 } package)
            return Store(package);
        if (app.SteamAppId is { Length: > 0 } appId && !string.IsNullOrWhiteSpace(app.SteamExe))
            return Steam(app.SteamExe, appId);
        return TryParse(app.QuietUninstallString, app.UninstallString);
    }

    /// <summary>Prefers <paramref name="quiet"/> when it is present. Otherwise parses <paramref name="normal"/> and silences it only when the uninstaller is known to allow that.</summary>
    public static UninstallLaunch? TryParse(string? quiet, string? normal)
    {
        if (!string.IsNullOrWhiteSpace(quiet) && Split(quiet) is { } quietCommand)
            return Finish(quietCommand.File, quietCommand.Args, preferSilent: true);

        if (string.IsNullOrWhiteSpace(normal) || Split(normal) is not { } command) return null;
        return Finish(command.File, command.Args, preferSilent: false);
    }

    public static UninstallLaunch? Store(string packageFullName)
    {
        if (!PackageName().IsMatch(packageFullName) || !packageFullName.Contains('_', StringComparison.Ordinal)) return null;
        string powershell = Path.Join(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        string script = $"try {{ Remove-AppxPackage -Package '{packageFullName}' -ErrorAction Stop }} catch {{ exit 1 }}";
        var args = new[] { "-NoProfile", "-NonInteractive", "-Command", script };
        return new UninstallLaunch(powershell, args, UninstallStyle.Silent, Display(powershell, args));
    }

    public static UninstallLaunch Steam(string steamExe, string appId)
    {
        if (!AppId().IsMatch(appId)) throw new ArgumentException("Steam app id must be digits.", nameof(appId));
        var args = new[] { "steam://uninstall/" + appId };
        return new UninstallLaunch(steamExe, args, UninstallStyle.LauncherHandoff, Display(steamExe, args));
    }

    private static UninstallLaunch Finish(string file, List<string> args, bool preferSilent)
    {
        string name = FileNameOf(file);
        bool msiexec = name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
        if (msiexec) args = PreferMsiUninstall(args);

        // Known installers get their real silent switches. A quiet string we don't recognize is still
        // treated as silent (it was written to be), but we don't invent a switch for a normal uninstaller.
        bool already = AlreadySilent(args);
        if (msiexec && !already)
        {
            args = args.Concat(["/qn", "/norestart"]).ToList();
            already = true;
        }
        else if (InnoUninstaller().IsMatch(name) && !already)
        {
            args = args.Concat(["/VERYSILENT", "/NORESTART"]).ToList();
            already = true;
        }

        var style = already || preferSilent ? UninstallStyle.Silent : UninstallStyle.Interactive;
        return new UninstallLaunch(file, args, style, Display(file, args));
    }

    private static List<string> PreferMsiUninstall(List<string> args)
    {
        var copy = args.ToList();
        for (int i = 0; i < copy.Count; i++)
        {
            if (copy[i].StartsWith("/I{", StringComparison.OrdinalIgnoreCase) || copy[i].StartsWith("-I{", StringComparison.OrdinalIgnoreCase))
                copy[i] = "/X" + copy[i][2..];
            else if ((copy[i].Equals("/I", StringComparison.OrdinalIgnoreCase) || copy[i].Equals("-I", StringComparison.OrdinalIgnoreCase))
                && i + 1 < copy.Count && copy[i + 1].StartsWith('{'))
                copy[i] = "/X";
        }
        return copy;
    }

    private static bool AlreadySilent(IReadOnlyList<string> args)
    {
        foreach (string arg in args)
        {
            if (SilentSwitch().IsMatch(arg)) return true;
        }
        return false;
    }

    private static (string File, List<string> Args)? Split(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        string file;
        string rest;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end <= 1) return null;
            file = command[1..end];
            rest = command[(end + 1)..].Trim();
        }
        else
        {
            var match = Executable().Match(command);
            if (match.Success)
            {
                file = match.Value;
                rest = command[file.Length..].Trim();
            }
            else
            {
                int space = command.IndexOf(' ');
                file = space < 0 ? command : command[..space];
                rest = space < 0 ? "" : command[(space + 1)..].Trim();
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            }
        }

        if (string.IsNullOrWhiteSpace(file)) return null;
        return (file, SplitArgs(rest));
    }

    private static List<string> SplitArgs(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool quote = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quote = !quote;
                continue;
            }
            if (!quote && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) args.Add(current.ToString());
        return args;
    }

    private static string Display(string file, IReadOnlyList<string> args)
    {
        string joined = string.Join(' ', args.Select(Quote));
        return joined.Length == 0 ? Quote(file) : Quote(file) + " " + joined;
    }

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;

    /// <summary>Last segment, whether the string uses Windows or Unix separators.</summary>
    private static string FileNameOf(string path)
    {
        int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return slash < 0 ? path : path[(slash + 1)..];
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*$")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"^\d{1,10}$")]
    private static partial Regex AppId();

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoUninstaller();

    [GeneratedRegex(@"^(/quiet|/qn|/qb|/verysilent|/silent|/s|-quiet|--silent|-s)$", RegexOptions.IgnoreCase)]
    private static partial Regex SilentSwitch();

    [GeneratedRegex(@"^[A-Za-z]:\\.*?\.(exe|bat|cmd)", RegexOptions.IgnoreCase)]
    private static partial Regex Executable();
}
