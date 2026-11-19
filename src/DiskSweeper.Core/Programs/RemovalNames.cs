using System.Text.RegularExpressions;

namespace DiskSweeper.Core.Programs;

/// <summary>
/// Strict name matching for deleting a program's leftovers. A match has to be the program itself
/// (or that name plus a data suffix like "Cache"), never a publisher, a shared word, or a longer
/// different product. "Epic" does not match Fortnite, and "Visual Studio" does not match
/// "Visual Studio Code".
/// </summary>
public static partial class RemovalNames
{
    // Words that never identify a program. A folder with only this name is left alone.
    private static readonly HashSet<string> NeverMatch = new(StringComparer.Ordinal)
    {
        "game", "games", "app", "apps", "application", "applications", "data", "cache", "caches",
        "temp", "tmp", "common", "shared", "software", "tool", "tools", "update", "updates",
        "service", "services", "client", "runtime", "redist", "redistributable", "desktop",
        "online", "helper", "bin", "program", "programs", "windows", "microsoft", "system",
        "files", "file", "local", "roaming", "locallow", "appdata", "programdata", "users",
        "public", "default", "installer", "setup", "content", "binaries", "saved", "settings",
        "config", "configuration", "logs", "log", "shadercache", "webcache", "crashdumps", "dumps",
    };

    // Real products use these words, but only as the whole name ("Steam" the client). They never
    // match a longer folder, and a longer product never matches a folder that is only this word.
    private static readonly HashSet<string> ExactOnly = new(StringComparer.Ordinal)
    {
        "epic", "steam", "valve", "adobe", "google", "studio", "studios", "launcher", "player",
        "origin", "gog", "riot", "nvidia", "intel", "amd", "ea", "ubisoft", "mozilla", "oracle",
        "python", "dotnet", "node", "unity", "electronic", "arts",
    };

    // Extra words a leftover folder may add. "editor", "studio", "player" and "code" are not here:
    // those are different products (Roblox Studio, Visual Studio Code).
    private static readonly HashSet<string> AllowedSuffixes = new(StringComparer.Ordinal)
    {
        "game", "games", "data", "cache", "caches", "settings", "config", "configuration",
        "saved", "binaries", "content", "logs", "log", "temp", "tmp", "shadercache",
        "webcache", "crashdumps", "dumps", "savedgames",
    };

    // Legal endings only. "Games" and "Studio" stay, because they are part of the product name.
    private static readonly string[] LegalSuffixes =
    [
        "incorporated", "corporation", "limited", "company", "gmbh", "corp", "inc", "llc", "ltd", "plc", "co", "ab", "sa", "srl", "bv", "ag",
    ];

    /// <summary>True when the program name is too vague to hunt for leftover folders.</summary>
    public static bool IsTooGeneric(string programName)
    {
        string program = Normalize(StripNoise(programName));
        return program.Length < 4 || NeverMatch.Contains(program);
    }

    /// <summary>
    /// True when <paramref name="candidateName"/> is this program and not merely a similar one.
    /// Equality, or the program name plus a data suffix ("Fortnite" → "FortniteGame").
    /// </summary>
    public static bool Matches(string candidateName, string programName)
    {
        string folder = Normalize(StripNoise(candidateName));
        string program = Normalize(StripNoise(programName));
        if (folder.Length < 4 || program.Length < 4) return false;
        if (NeverMatch.Contains(folder) || NeverMatch.Contains(program)) return false;
        if (folder == program) return true;
        if (ExactOnly.Contains(folder) || ExactOnly.Contains(program)) return false;
        if (!folder.StartsWith(program, StringComparison.Ordinal)) return false;
        return AllowedSuffixes.Contains(folder[program.Length..]);
    }

    /// <summary>
    /// <see cref="Matches"/>, and also the product name with its publisher stripped off the front
    /// ("Microsoft OneDrive" published by Microsoft matches a folder named "OneDrive").
    /// The stripped remainder still has to be specific: "Epic Games Launcher" does not match a folder named "Launcher".
    /// </summary>
    public static bool MatchesProduct(string candidateName, string programName, string? publisher)
    {
        if (Matches(candidateName, programName)) return true;
        string? remainder = ProductWithoutPublisher(programName, publisher);
        if (remainder is null) return false;
        string norm = Normalize(StripNoise(remainder));
        if (norm.Length < 4 || NeverMatch.Contains(norm) || ExactOnly.Contains(norm)) return false;
        return Matches(candidateName, remainder);
    }

    /// <summary>
    /// True when a folder is this publisher and not a generic parent. "Epic Games" matches
    /// "Epic Games, Inc."; "Epic" does not.
    /// </summary>
    public static bool PublisherMatches(string folderName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return false;
        var words = SignificantPublisherWords(publisher);
        if (words.Count == 0) return false;
        string folder = Normalize(StripNoise(folderName));
        string full = Normalize(string.Concat(words));
        if (folder.Length < 4 || full.Length < 4) return false;
        if (NeverMatch.Contains(folder) || ExactOnly.Contains(folder)) return false;
        if (folder == full) return true;
        if (words.Count < 2) return false;
        string first = Normalize(words[0]);
        return first.Length >= 5 && !NeverMatch.Contains(first) && !ExactOnly.Contains(first) && folder == first;
    }

    /// <summary>Same program for merging catalog entries. Versions are ignored; x64 and x86 are not.</summary>
    public static bool SameIdentity(string left, string right)
    {
        string a = Normalize(StripVersion(left));
        string b = Normalize(StripVersion(right));
        if (a.Length < 4 || b.Length < 4) return false;
        if (NeverMatch.Contains(a) || NeverMatch.Contains(b)) return false;
        return a == b;
    }

    public static string Normalize(string name) => SmartFinds.NameMatcher.Normalize(name);

    /// <summary>Drops trademarks, "(x64)" and a trailing dotted version. A single trailing number stays (Counter-Strike 2).</summary>
    public static string StripNoise(string name)
    {
        name = name.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Replace("©", "", StringComparison.Ordinal);
        name = Architecture().Replace(name, " ");
        name = TrailingVersion().Replace(name, "");
        return name.Trim();
    }

    private static string StripVersion(string name)
    {
        name = name.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Replace("©", "", StringComparison.Ordinal);
        return TrailingVersion().Replace(name, "").Trim();
    }

    private static string? ProductWithoutPublisher(string programName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;
        var publisherWords = SignificantPublisherWords(publisher);
        var programWords = Words(StripNoise(programName));
        if (publisherWords.Count == 0 || programWords.Count <= publisherWords.Count) return null;
        for (int i = 0; i < publisherWords.Count; i++)
        {
            if (!Normalize(programWords[i]).Equals(Normalize(publisherWords[i]), StringComparison.Ordinal)) return null;
        }
        return string.Join(' ', programWords.Skip(publisherWords.Count));
    }

    private static List<string> SignificantPublisherWords(string publisher)
    {
        var words = Words(publisher);
        while (words.Count > 1 && LegalSuffixes.Contains(words[^1], StringComparer.OrdinalIgnoreCase)) words.RemoveAt(words.Count - 1);
        return words;
    }

    private static List<string> Words(string name) =>
        name.Split([' ', ',', '.', '-', '_', '(', ')', '[', ']', '&', '+', '/'], StringSplitOptions.RemoveEmptyEntries).ToList();

    [GeneratedRegex(@"\s*[\(\[]\s*(x64|x86|amd64|arm64|64-bit|32-bit|win64|win32)\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Architecture();

    [GeneratedRegex(@"[\s._-]+v?\d+\.\d+(?:\.\d+)*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();
}
