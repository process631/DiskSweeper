using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.SmartFinds;

namespace DiskSweeper.Core.Tests;

public class SmartFindsTests
{
    [Fact]
    public void Vdf_reads_nested_blocks_escapes_and_comments()
    {
        var vdf = Vdf.Parse("""
            "libraryfolders"
            {
                // Steam writes comments sometimes
                "0"
                {
                    "path"      "C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "123" }
                }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """);

        var folders = vdf.Block("libraryfolders")!;
        Assert.Equal(["0", "1"], folders.Blocks.Select(b => b.Key));
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders.Block("0")!["path"]);
        Assert.Equal("123", folders.Block("0")!.Block("apps")!["228980"]);
        Assert.Equal(@"D:\SteamLibrary", folders.Block("1")!["PATH"]);
    }

    [Theory]
    [InlineData("Riot Games", "Riot Client", true)]
    [InlineData("EasyAntiCheat_EOS", "EasyAntiCheat_EOS", true)]
    [InlineData("Ubisoft", "Ubisoft Connect", true)]
    [InlineData("Alienware", "AlienwareCommandCenter", true)]
    [InlineData("BraveSoftware", "Brave", true)]
    [InlineData("TslGame", "Discord", false)]
    [InlineData("Respawn", "Google Chrome", false)]
    [InlineData("log", "Blogger", false)]
    public void Folder_names_match_program_names_loosely(string folder, string program, bool expected)
    {
        Assert.Equal(expected, NameMatcher.Matches(folder, program));
    }

    [Fact]
    public void Store_package_families_become_names()
    {
        Assert.Equal(["DellInc", "AlienwareCommandCenter"], LeftoverFinder.StoreAppNames(["DellInc.AlienwareCommandCenter_htrsf667h5kn2"]));
    }

    [Fact]
    public void Rule_paths_expand_variables_and_wildcards()
    {
        using var box = new Sandbox();
        box.CreateFile(@"User Data\Default\Cache\a.bin", 1);
        box.CreateFile(@"User Data\Profile 2\Cache\b.bin", 1);
        box.CreateFolder(@"User Data\Profile 3");
        Environment.SetEnvironmentVariable("DISKSWEEPER_TEST_ROOT", box.Root);

        var paths = JunkRule.Expand(@"%DISKSWEEPER_TEST_ROOT%\User Data\*\Cache").Order().ToList();

        Assert.Equal([box.PathOf(@"User Data\Default\Cache"), box.PathOf(@"User Data\Profile 2\Cache")], paths);
        Assert.Empty(JunkRule.Expand(@"%NO_SUCH_VARIABLE_HERE%\Cache"));
    }

    [Fact]
    public void No_junk_rule_can_point_at_a_protected_place()
    {
        var rules = JunkRule.LoadBuiltIn();
        Assert.NotEmpty(rules);

        foreach (var rule in rules.Where(r => r.Mode != "advice"))
        {
            foreach (string pattern in rule.Paths)
            {
                string path = Environment.ExpandEnvironmentVariables(pattern.Replace("%LOCALLOW%", @"%USERPROFILE%\AppData\LocalLow")).Replace("*", "x");
                // "contents" rules delete what's inside, "folder" rules the thing itself.
                string target = rule.Mode == "contents" ? Path.Join(path, "some-cache-file") : path;
                Assert.True(ProtectedPaths.WhyProtected(target) is null, $"Rule {rule.Id} targets protected {target}: {ProtectedPaths.WhyProtected(target)}");
            }
        }
    }

    [Fact]
    public void Steam_leftovers_are_folders_and_caches_of_games_that_are_not_installed()
    {
        using var box = new Sandbox();
        box.CreateFile(@"steamapps\appmanifest_10.acf", 0);
        File.WriteAllText(box.PathOf(@"steamapps\appmanifest_10.acf"), """
            "AppState" { "appid" "10" "name" "Counter-Strike" "installdir" "Half-Life" }
            """);
        box.CreateFile(@"steamapps\common\Half-Life\hl.exe", 100);
        box.CreateFile(@"steamapps\common\Uninstalled Game\data.pak", 500);
        box.CreateFile(@"steamapps\shadercache\10\cache.bin", 10);
        box.CreateFile(@"steamapps\shadercache\999\cache.bin", 10);
        box.CreateFile(@"steamapps\workshop\content\999\mod.bin", 10);
        box.CreateFolder(@"steamapps\downloading\777");

        var library = new SteamLauncher.SteamLibrary(box.Root, SteamLauncher.LoadManifests(box.Root));
        var findings = SmartFinder.SteamLibraryFindings([library]).ToList();

        Assert.Equal("Counter-Strike", Assert.Single(library.Games).Name);
        Assert.Equal(
            [box.PathOf(@"steamapps\common\Uninstalled Game"), box.PathOf(@"steamapps\downloading"),
             box.PathOf(@"steamapps\shadercache\999"), box.PathOf(@"steamapps\workshop\content\999")],
            findings.Select(f => f.Path).Order());
        Assert.Equal(Confidence.Review, findings.Single(f => f.Path.EndsWith("downloading")).Confidence);
    }

    [Fact]
    public void Games_left_by_an_uninstalled_launcher_are_safe_leftovers()
    {
        using var box = new Sandbox();
        box.CreateFolder(@"Epic Games\Fortnite");
        box.CreateFolder(@"Epic Games\Launcher");
        var gone = new LauncherState
        {
            Name = "Epic Games Launcher",
            LauncherInstalled = false,
            GameRoots = [box.PathOf("Epic Games")],
            NonGameFolders = new HashSet<string>(["Launcher"], StringComparer.OrdinalIgnoreCase),
        };
        var stillThere = new LauncherState
        {
            Name = "Epic Games Launcher",
            LauncherInstalled = true,
            GameRoots = [box.PathOf("Epic Games")],
            Games = [new InstalledGame("Epic", "Fortnite", box.PathOf(@"Epic Games\Fortnite"))],
            NonGameFolders = gone.NonGameFolders,
        };

        var finding = Assert.Single(SmartFinder.LauncherLeftovers(gone));
        Assert.Equal(box.PathOf(@"Epic Games\Fortnite"), finding.Path);
        Assert.Equal(Confidence.Safe, finding.Confidence);
        Assert.Empty(SmartFinder.LauncherLeftovers(stillThere));
    }

    [Fact]
    public void Contents_findings_only_include_items_older_than_the_age_limit()
    {
        using var box = new Sandbox();
        box.CreateFile(@"temp\old.tmp", 10, modifiedUtc: DateTime.UtcNow.AddDays(-10));
        box.CreateFile(@"temp\fresh.tmp", 10);
        var finding = new Finding
        {
            Id = "t", Title = "Temp", Path = box.PathOf("temp"), Category = FindCategory.Junk,
            Confidence = Confidence.Safe, Explanation = "", ContentsOnly = true, MinAgeDays = 2,
        };

        Assert.Equal([box.PathOf(@"temp\old.tmp")], finding.ItemsToDelete());
    }

    [Fact]
    public void Command_lines_from_the_registry_give_their_folder()
    {
        Assert.Equal(@"C:\Program Files\App", InstalledSoftware.FolderOfCommand("\"C:\\Program Files\\App\\uninstall.exe\" /S"));
        Assert.Equal(@"C:\Games\Thing", InstalledSoftware.FolderOfCommand(@"C:\Games\Thing\thing.exe,0"));
        Assert.Null(InstalledSoftware.FolderOfCommand("MsiExec.exe /X{1234}"));
    }
}
