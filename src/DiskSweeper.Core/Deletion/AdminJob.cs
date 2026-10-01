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

    public static void WriteResults(string file, IReadOnlyList<DeleteResult> results) =>
        File.WriteAllText(file, JsonSerializer.Serialize(results, Json));

    public static IReadOnlyList<DeleteResult> ReadResults(string file) =>
        JsonSerializer.Deserialize<List<DeleteResult>>(File.ReadAllText(file), Json) ?? [];
}
