using System.Text;
using System.Text.Json;

namespace DiskSweeper.Core.Deletion;

public enum AdminAction
{
    Delete,
    TakeOwnershipAndDelete,
    DeleteOnRestart,
}

/// <summary>
/// A fix that needs administrator rights, run by a short-lived elevated copy of the app so the main window
/// doesn't have to restart. The job travels on the elevated process's command line (which other programs
/// can't change after launch), never through a file another program could swap.
/// </summary>
public sealed record AdminJob(AdminAction Action, IReadOnlyList<string> Paths, DeleteMethod Method)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Windows caps a command line at 32,767 characters; stay well clear of it.</summary>
    public const int MaxEncodedLength = 24_000;

    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json)));

    public static AdminJob Decode(string encoded) =>
        JsonSerializer.Deserialize<AdminJob>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), Json)
            ?? throw new ArgumentException("Empty admin job.");

    /// <summary>Splits paths into jobs small enough to pass on a command line.</summary>
    public static IEnumerable<AdminJob> Split(AdminAction action, IEnumerable<string> paths, DeleteMethod method)
    {
        var batch = new List<string>();
        foreach (string path in paths)
        {
            batch.Add(path);
            if (new AdminJob(action, batch, method).Encode().Length > MaxEncodedLength && batch.Count > 1)
            {
                batch.RemoveAt(batch.Count - 1);
                yield return new AdminJob(action, batch.ToList(), method);
                batch = [path];
            }
        }
        if (batch.Count > 0) yield return new AdminJob(action, batch, method);
    }

    public IReadOnlyList<DeleteResult> Run(DeleteService service, CancellationToken cancellationToken = default) =>
        Paths.Select(path => Action switch
        {
            AdminAction.TakeOwnershipAndDelete => service.TakeOwnershipAndDelete(path, Method, cancellationToken),
            AdminAction.DeleteOnRestart => service.ScheduleForRestart(path, cancellationToken),
            _ => service.Delete(path, Method, cancellationToken: cancellationToken),
        }).ToList();

    /// <summary>
    /// True when <paramref name="path"/> is a <c>DiskSweeper-{32 hex}.json</c> file directly in the temp folder.
    /// The elevated helper will not write anywhere else, and it will not follow a link planted at that name.
    /// </summary>
    public static bool IsAcceptableResultFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // A second colon is an NTFS alternate data stream (C:\Temp\file.json:evil), not part of the file name.
        int colon = full.IndexOf(':');
        if (colon >= 0 && full.IndexOf(':', colon + 1) >= 0) return false;

        const string prefix = "DiskSweeper-";
        const string suffix = ".json";
        string name = Path.GetFileName(full);
        if (name.Length != prefix.Length + 32 + suffix.Length
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char c in name.AsSpan(prefix.Length, 32))
        {
            if (!Uri.IsHexDigit(c)) return false;
        }

        string? directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory)) return false;

        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(directory), temp, comparison)) return false;

        try
        {
            // GetAttributes does not follow the last component, so a dangling symlink is visible here
            // even though File.Exists is false for it.
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    public static void WriteResults(string file, IReadOnlyList<DeleteResult> results)
    {
        if (!IsAcceptableResultFile(file))
            throw new ArgumentException("Result file must be a DiskSweeper-*.json file directly in the temp folder.", nameof(file));

        File.WriteAllText(Path.GetFullPath(file), JsonSerializer.Serialize(results, Json));
    }

    public static IReadOnlyList<DeleteResult> ReadResults(string file) =>
        JsonSerializer.Deserialize<List<DeleteResult>>(File.ReadAllText(file), Json) ?? [];
}
