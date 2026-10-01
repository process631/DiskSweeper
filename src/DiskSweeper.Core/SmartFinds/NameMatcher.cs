using System.Text;

namespace DiskSweeper.Core.SmartFinds;

/// <summary>
/// Loose matching between folder names and program names, e.g. "Riot Games" ↔ "Riot Games, Inc." or
/// "EasyAntiCheat_EOS" ↔ "Easy Anti-Cheat". Errs on the side of "matches", because a false match only
/// hides a leftover, while a missed match would suggest deleting something still in use.
/// </summary>
public static class NameMatcher
{
    private static readonly string[] CompanySuffixes =
        ["incorporated", "corporation", "software", "limited", "company", "studios", "studio", "games", "gmbh", "corp", "inc", "llc", "ltd", "co", "ab", "sa", "srl", "bv"];

    /// <summary>Lower-case letters and digits only: "Easy Anti-Cheat (EOS)" → "easyanticheateos".</summary>
    public static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && c < 128) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>The name without trailing company words: "Ubisoft Entertainment Inc." → "ubisoftentertainment".</summary>
    public static string Core(string name)
    {
        var words = name.Split([' ', ',', '.', '-', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && CompanySuffixes.Contains(words[^1].ToLowerInvariant())) words.RemoveAt(words.Count - 1);
        return Normalize(string.Join("", words));
    }

    /// <summary>True when a folder name and a program/publisher name plausibly refer to the same thing.</summary>
    public static bool Matches(string folderName, string programName)
    {
        string folder = Normalize(folderName);
        string program = Normalize(programName);
        if (folder.Length < 3 || program.Length < 3) return folder.Length > 0 && folder == program;
        if (folder == program) return true;

        string folderCore = Core(folderName);
        string programCore = Core(programName);
        if (folderCore.Length >= 3 && programCore.Length >= 3 && (folderCore == programCore)) return true;

        // "steam" inside "steamworkscommon", or "eadesktop" inside "ea": only when the shorter one is distinctive enough.
        if (folder.Length >= 4 && program.Contains(folder)) return true;
        if (program.Length >= 5 && folder.Contains(program)) return true;
        if (folderCore.Length >= 4 && programCore.Length >= 4 && (programCore.Contains(folderCore) || folderCore.Contains(programCore))) return true;
        return false;
    }
}
