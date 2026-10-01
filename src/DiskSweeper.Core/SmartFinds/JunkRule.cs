using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskSweeper.Core.SmartFinds;

/// <summary>One entry of JunkRules.json: a kind of junk and the places it lives.</summary>
public sealed class JunkRule
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public required string Id { get; init; }

    public required string Title { get; init; }

    public FindCategory Category { get; init; }

    public Confidence Confidence { get; init; }

    /// <summary>"contents" (empty the folder), "folder" (delete the folder or file itself), or "advice" (just explain).</summary>
    public string Mode { get; init; } = "contents";

    public int MinAgeDays { get; init; }

    public bool Admin { get; init; }

    public required IReadOnlyList<string> Paths { get; init; }

    public required string Explanation { get; init; }

    public string? Advice { get; init; }

    public string? AdviceCommand { get; init; }

    public static IReadOnlyList<JunkRule> LoadBuiltIn()
    {
        using var stream = typeof(JunkRule).Assembly.GetManifestResourceStream("DiskSweeper.Core.SmartFinds.JunkRules.json")
            ?? throw new InvalidOperationException("JunkRules.json is missing from the build.");
        return JsonSerializer.Deserialize<List<JunkRule>>(stream, Json) ?? [];
    }

    /// <summary>
    /// Turns a rule path into the real paths that exist on this PC: expands %VARIABLES% (plus %LOCALLOW%)
    /// and * wildcards in any part, e.g. "...\User Data\*\Cache" matches every browser profile.
    /// </summary>
    public static IEnumerable<string> Expand(string pattern)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expanded = Environment.ExpandEnvironmentVariables(
            pattern.Replace("%LOCALLOW%", Path.Join(profile, @"AppData\LocalLow"), StringComparison.OrdinalIgnoreCase));
        if (expanded.Contains('%')) yield break; // A variable this PC doesn't have.

        string[] parts = expanded.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) yield break;

        var current = new List<string> { parts[0] + Path.DirectorySeparatorChar };
        for (int i = 1; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            var next = new List<string>();
            foreach (string folder in current)
            {
                if (!parts[i].Contains('*'))
                {
                    next.Add(Path.Join(folder, parts[i]));
                    continue;
                }
                try
                {
                    var matches = last
                        ? Directory.EnumerateFileSystemEntries(LongPath.ToExtended(folder), parts[i])
                        : Directory.EnumerateDirectories(LongPath.ToExtended(folder), parts[i]);
                    next.AddRange(matches.Select(LongPath.ToNormal));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            current = next;
        }

        foreach (string path in current)
        {
            if (FileSystemWalk.Exists(path)) yield return path;
        }
    }
}
