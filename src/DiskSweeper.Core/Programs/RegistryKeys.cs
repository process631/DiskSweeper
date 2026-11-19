using Microsoft.Win32;
using DiskSweeper.Core.Deletion;

namespace DiskSweeper.Core.Programs;

/// <summary>
/// Deletes one program's registry key and refuses anything broader. The uninstall entry under
/// Microsoft\Windows\CurrentVersion\Uninstall is the one exception, and only for a specific product.
/// </summary>
public static class RegistryKeys
{
    private static readonly HashSet<string> DeniedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Classes", "Policies", "Clients", "AppModel", "RegisteredApplications",
        "CurrentVersion", "Uninstall", "Control", "System", "Hardware", "Security", "SAM", "Environment",
        "Explorer", "CLSID", "Interface", "TypeLib", "Record", "AppID", "Wow6432Node",
    };

    /// <summary>Why this key must not be deleted, or null when it is a specific program key.</summary>
    public static string? WhyUnsafe(string keyPath)
    {
        if (!TrySplit(keyPath, out _, out var parts, out string? error)) return error ?? "That isn't a registry key DiskSweeper can remove.";
        if (parts.Count == 0 || !parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase))
            return "Only a key under Software is considered, and only one program's own key.";

        int index = 1;
        if (index < parts.Count && parts[index].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) index++;
        if (index >= parts.Count) return "That key is too broad to delete.";

        if (UninstallProduct(parts, index) is string product)
            return IsSafeLeaf(product) ? null : "That uninstall entry is not a specific program.";

        for (int i = index; i < parts.Count; i++)
        {
            if (DeniedSegments.Contains(parts[i]))
                return $"It's under {parts[i]}, which is shared or belongs to Windows.";
        }
        return IsSafeLeaf(parts[^1]) ? null : "That key's name is too generic to delete.";
    }

    public static bool NeedsAdmin(string keyPath) =>
        keyPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
        || keyPath.StartsWith(@"HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase);

    public static bool Exists(string keyPath)
    {
        if (!TrySplit(keyPath, out var hive, out var parts, out _)) return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(string.Join('\\', parts));
            return key is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Deletes the key. A dry run only checks and logs. Unsafe keys are refused even for a dry run of the delete itself.</summary>
    public static DeleteResult Delete(string keyPath, bool dryRun, DeletionLog? log)
    {
        string shown = keyPath.Trim();
        if (WhyUnsafe(shown) is string why)
            return Record(log, shown, new DeleteResult { Path = shown, Outcome = DeleteOutcome.Refused, Reason = BlockReason.Protected, Message = why });

        if (!TrySplit(shown, out var hive, out var parts, out string? error))
            return Record(log, shown, new DeleteResult { Path = shown, Outcome = DeleteOutcome.Refused, Reason = BlockReason.Protected, Message = error });

        string sub = string.Join('\\', parts);
        bool present = Exists(shown);
        if (!present)
            return Record(log, shown, new DeleteResult { Path = shown, Outcome = DeleteOutcome.AlreadyGone, Message = "The registry key was already gone." });

        if (dryRun)
            return Record(log, shown, new DeleteResult { Path = shown, Outcome = DeleteOutcome.WouldDelete, Message = "Dry run: the registry key would be deleted." });

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            return Record(log, shown, new DeleteResult { Path = shown, Outcome = DeleteOutcome.Deleted, Message = "Registry key deleted." });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Record(log, shown, new DeleteResult
            {
                Path = shown, Outcome = DeleteOutcome.Failed, Reason = BlockReason.Other,
                Message = "Deleting this registry key needs administrator rights.",
            });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return Record(log, shown, new DeleteResult { Path = shown, Outcome = DeleteOutcome.Failed, Reason = BlockReason.Other, Message = ex.Message });
        }
    }

    public static bool SameKey(string? left, string? right) =>
        left is not null && right is not null && Canonical(left).Equals(Canonical(right), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string keyPath)
    {
        if (!TrySplit(keyPath, out var hive, out var parts, out _)) return keyPath.Trim();
        string hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
        return hiveName + "\\" + string.Join('\\', parts);
    }

    private static bool TrySplit(string keyPath, out RegistryHive hive, out List<string> parts, out string? error)
    {
        hive = RegistryHive.CurrentUser;
        parts = [];
        error = null;
        if (string.IsNullOrWhiteSpace(keyPath) || keyPath.Contains("..", StringComparison.Ordinal) || keyPath.Contains('/', StringComparison.Ordinal))
        {
            error = "That isn't a registry key DiskSweeper can remove.";
            return false;
        }

        var raw = keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (raw.Length < 2)
        {
            error = "That key is too broad to delete.";
            return false;
        }

        hive = raw[0].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            _ => (RegistryHive)(-1),
        };
        if ((int)hive == -1)
        {
            error = "Only keys under HKEY_CURRENT_USER or HKEY_LOCAL_MACHINE are considered.";
            return false;
        }

        parts = raw.Skip(1).ToList();
        if (parts.Any(part => part is "." or ".." or "*" || part.Contains(':', StringComparison.Ordinal)))
        {
            error = "That isn't a registry key DiskSweeper can remove.";
            parts = [];
            return false;
        }
        return true;
    }

    /// <summary>The product name when the path is exactly the uninstall key, otherwise null.</summary>
    private static string? UninstallProduct(IReadOnlyList<string> parts, int index)
    {
        // Software\[WOW6432Node\]Microsoft\Windows\CurrentVersion\Uninstall\<product> is the only Windows path we delete.
        string[] expected = ["Microsoft", "Windows", "CurrentVersion", "Uninstall"];
        if (index + expected.Length >= parts.Count) return null;
        for (int i = 0; i < expected.Length; i++)
        {
            if (!parts[index + i].Equals(expected[i], StringComparison.OrdinalIgnoreCase)) return null;
        }
        if (index + expected.Length != parts.Count - 1) return null;
        return parts[^1];
    }

    private static bool IsSafeLeaf(string leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf) || DeniedSegments.Contains(leaf)) return false;
        if (leaf is "." or ".." or "*") return false;
        string norm = RemovalNames.Normalize(leaf);
        if (norm.Length < 2) return false;
        // "Games" or "Cache" as a whole key would sweep up something shared.
        string[] never = ["game", "games", "app", "data", "cache", "temp", "windows", "microsoft", "software", "system", "update"];
        return !never.Contains(norm);
    }

    private static DeleteResult Record(DeletionLog? log, string path, DeleteResult result)
    {
        try
        {
            log?.Write("Registry", result.Outcome.ToString(), 0, path, result.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }
}
