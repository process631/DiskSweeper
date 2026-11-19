using DiskSweeper.Core.Programs;

namespace DiskSweeper.Core.Tests;

/// <summary>
/// Leftover matching has to stay strict: similar names, publishers and launcher folders must not be swept up
/// with the program the user picked.
/// </summary>
public class ProgramRemovalTests
{
    private const string Root = @"D:\DiskSweeperTest";

    [Fact]
    public void Fortnite_does_not_take_epic_launcher_files_or_a_sibling_game()
    {
        string epic = Path.Join(Root, "Epic Games");
        string fortnite = Path.Join(epic, "Fortnite");
        string launcher = Path.Join(epic, "Launcher");
        string rocket = Path.Join(epic, "RocketLeague");
        string local = Path.Join(Root, "Local");
        var fortniteApp = Game("Fortnite", "Epic Games, Inc.", fortnite) with
        {
            UninstallKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite",
        };
        var items = Match(
            fortniteApp,
            others:
            [
                Game("Rocket League", "Epic Games, Inc.", rocket),
                Game("Epic Games Launcher", "Epic Games, Inc.", launcher),
            ],
            folders:
            [
                epic, fortnite, launcher, rocket,
                Path.Join(local, "FortniteGame"),
                Path.Join(local, "EpicGamesLauncher"),
                Path.Join(local, "Epic"),
                Path.Join(Root, "ProgramData", "Epic"),
                Path.Join(Root, "Documents", "Fortnite"),
                Path.Join(Root, "Documents", "Epic Games"),
                Path.Join(Root, "Documents", "Epic Games", "Fortnite"),
                Path.Join(Root, "Saved Games", "Fortnite"),
                Path.Join(Root, "Fortnite Festival"),
            ],
            shortcuts:
            [
                new(Path.Join(Root, "Desktop", "Fortnite.lnk"), Path.Join(fortnite, "Fortnite.exe")),
                new(Path.Join(Root, "Desktop", "Play.lnk"), Path.Join(fortnite, "Fortnite.exe")),
                new(Path.Join(Root, "Desktop", "Epic Games Launcher.lnk"), Path.Join(launcher, "EpicGamesLauncher.exe")),
                new(Path.Join(Root, "Desktop", "Fortnite.lnk"), Path.Join(rocket, "RocketLeague.exe")),
            ],
            keys:
            [
                @"HKCU\Software\Epic Games\Fortnite",
                @"HKCU\Software\Epic Games",
                @"HKCU\Software\Epic",
                @"HKCU\Software\Fortnite",
                @"HKCU\Software\Microsoft\Fortnite",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RocketLeague",
            ]);

        Selected(items, fortnite);
        Selected(items, Path.Join(local, "FortniteGame"));
        Selected(items, Path.Join(Root, "Documents", "Fortnite"));
        Selected(items, Path.Join(Root, "Documents", "Epic Games", "Fortnite"));
        Selected(items, Path.Join(Root, "Saved Games", "Fortnite"));
        Selected(items, Path.Join(Root, "Desktop", "Play.lnk"));
        Selected(items, @"HKCU\Software\Epic Games\Fortnite");
        Selected(items, @"HKCU\Software\Fortnite");
        Selected(items, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite");

        Absent(items, epic);
        Absent(items, launcher);
        Absent(items, rocket);
        Absent(items, Path.Join(local, "EpicGamesLauncher"));
        Absent(items, Path.Join(local, "Epic"));
        Absent(items, Path.Join(Root, "ProgramData", "Epic"));
        Absent(items, Path.Join(Root, "Documents", "Epic Games"));
        Absent(items, Path.Join(Root, "Fortnite Festival"));
        Absent(items, Path.Join(Root, "Desktop", "Epic Games Launcher.lnk"));
        Absent(items, @"HKCU\Software\Epic Games");
        Absent(items, @"HKCU\Software\Epic");
        Absent(items, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RocketLeague");
        Refused(items, @"HKCU\Software\Microsoft\Fortnite");
    }

    [Fact]
    public void Shortcut_whose_name_matches_but_whose_target_is_another_game_is_skipped()
    {
        string fortnite = Path.Join(Root, "Epic Games", "Fortnite");
        string rocket = Path.Join(Root, "Epic Games", "RocketLeague");
        string link = Path.Join(Root, "Desktop", "Fortnite.lnk");
        var items = Match(
            Game("Fortnite", "Epic Games, Inc.", fortnite),
            others: [Game("Rocket League", "Epic Games, Inc.", rocket)],
            shortcuts: [new(link, Path.Join(rocket, "RocketLeague.exe"))]);

        Absent(items, link);
    }

    [Fact]
    public void Visual_Studio_and_Visual_Studio_Code_do_not_match_each_other()
    {
        string vs = Path.Join(Root, "Visual Studio");
        string code = Path.Join(Root, "Visual Studio Code");
        string vsData = Path.Join(Root, "VisualStudio");
        string codeData = Path.Join(Root, "Code");

        var fromStudio = Match(Game("Visual Studio", "Microsoft", vs), others: [Game("Visual Studio Code", "Microsoft", code)], folders: [vs, code, vsData, codeData]);
        Selected(fromStudio, vs);
        Selected(fromStudio, vsData);
        Absent(fromStudio, code);
        Absent(fromStudio, codeData);

        var fromCode = Match(Game("Visual Studio Code", "Microsoft", code), others: [Game("Visual Studio", "Microsoft", vs)], folders: [vs, code, vsData, codeData]);
        Selected(fromCode, code);
        Absent(fromCode, vs);
        Absent(fromCode, vsData);
    }

    [Fact]
    public void Roblox_player_does_not_take_Roblox_Studio_or_their_shared_folder()
    {
        string shared = Path.Join(Root, "Local", "Roblox");
        string player = Path.Join(shared, "Versions", "version-player");
        string studio = Path.Join(shared, "Versions", "version-studio");
        string studioData = Path.Join(Root, "Local", "RobloxStudio");
        var playerApp = Game("Roblox", "Roblox Corporation", player, "roblox:player", AppSource.Roblox);
        var studioApp = Game("Roblox Studio", "Roblox Corporation", studio, "roblox:studio", AppSource.Roblox);

        var removingPlayer = Match(playerApp, others: [studioApp], folders: [shared, player, studio, studioData]);
        Selected(removingPlayer, player);
        Refused(removingPlayer, shared);
        Absent(removingPlayer, studio);
        Absent(removingPlayer, studioData);

        var removingStudio = Match(studioApp, others: [playerApp], folders: [shared, player, studio, studioData]);
        Selected(removingStudio, studio);
        Selected(removingStudio, studioData);
        Absent(removingStudio, shared);
        Absent(removingStudio, player);
    }

    [Fact]
    public void Counter_Strike_does_not_match_Counter_Strike_2()
    {
        string cs = Path.Join(Root, "Counter-Strike");
        string cs2 = Path.Join(Root, "Counter-Strike 2");
        var items = Match(
            Game("Counter-Strike", null, cs),
            others: [Game("Counter-Strike 2", null, cs2)],
            folders: [cs, cs2, Path.Join(Root, "CounterStrike2")]);

        Selected(items, cs);
        Absent(items, cs2);
        Absent(items, Path.Join(Root, "CounterStrike2"));
    }

    [Theory]
    [InlineData("EA", "EasyAntiCheat")]
    [InlineData("EA", "Electronic Arts")]
    [InlineData("Cup", "Cuphead")]
    [InlineData("Note", "Notepad")]
    [InlineData("Notepad", "Note")]
    [InlineData("Chrome", "Chromium")]
    [InlineData("Google Chrome", "Chromium")]
    [InlineData("My Game", "My Game Editor")]
    [InlineData("My Game Editor", "My Game")]
    [InlineData("Fortnite", "Fortnite Festival")]
    [InlineData("Fortnite Festival", "Fortnite")]
    [InlineData("CCleaner", "CCleaner Browser")]
    public void Similar_names_do_not_match(string program, string folder)
    {
        string path = Path.Join(Root, folder);
        var items = Match(Game(program, null, Path.Join(Root, "Install")), folders: [path]);
        Absent(items, path);
    }

    [Fact]
    public void Adobe_Photoshop_does_not_take_the_Adobe_folder_or_Adobe_Reader()
    {
        string adobe = Path.Join(Root, "Adobe");
        string photoshop = Path.Join(adobe, "Photoshop");
        string reader = Path.Join(adobe, "Reader");
        var items = Match(
            Game("Adobe Photoshop", "Adobe Inc.", photoshop),
            others: [Game("Adobe Reader", "Adobe Inc.", reader)],
            folders: [adobe, photoshop, reader, Path.Join(Root, "Adobe Reader")]);

        Selected(items, photoshop);
        Absent(items, adobe);
        Absent(items, reader);
        Absent(items, Path.Join(Root, "Adobe Reader"));
    }

    [Fact]
    public void A_generic_name_does_not_search_for_leftovers()
    {
        string games = Path.Join(Root, "Games");
        var plan = RemovalMatcher.Match(Game("Games", null, games), [], [games, Path.Join(Root, "MyGames")], [], []);

        Assert.Contains("too generic", plan.Note, StringComparison.OrdinalIgnoreCase);
        Assert.All(plan.Items, item => Assert.False(item.SelectedByDefault));
        Absent(plan.Items, Path.Join(Root, "MyGames"));
    }

    [Fact]
    public void Version_and_architecture_noise_still_matches_the_program()
    {
        string folder = Path.Join(Root, "MyApp");
        Selected(Match(Game("MyApp (x64)", null, Path.Join(Root, "Other")), folders: [folder]), folder);
        Selected(Match(Game("MyApp", null, Path.Join(Root, "Other")), folders: [Path.Join(Root, "MyApp 1.2.3")]), Path.Join(Root, "MyApp 1.2.3"));
        Selected(Match(Game("Fortnite", null, Path.Join(Root, "Other")), folders: [Path.Join(Root, "FortniteCache")]), Path.Join(Root, "FortniteCache"));
    }

    [Fact]
    public void A_folder_inside_the_install_folder_is_not_listed_twice()
    {
        string install = Path.Join(Root, "Fortnite");
        string child = Path.Join(install, "FortniteCache");
        string link = Path.Join(install, "Fortnite.lnk");
        var items = Match(Game("Fortnite", null, install), folders: [install, child], shortcuts: [new(link, Path.Join(install, "bin", "game.exe"))]);

        Selected(items, install);
        Absent(items, child);
        Absent(items, link);
    }

    [Fact]
    public void A_refused_parent_does_not_hide_the_real_game_folder()
    {
        string epic = Path.Join(Root, "Epic Games");
        string fortnite = Path.Join(epic, "Fortnite");
        var items = Match(Game("Fortnite", "Epic Games, Inc.", epic), folders: [epic, fortnite]);

        Refused(items, epic);
        Selected(items, fortnite);
    }

    [Fact]
    public void An_ambiguous_folder_that_matches_two_programs_is_skipped()
    {
        string shared = Path.Join(Root, "Widget");
        var items = Match(
            Game("Widget", "North", Path.Join(Root, "WidgetNorth")),
            others: [Game("Widget", "South", Path.Join(Root, "WidgetSouth"))],
            folders: [shared]);

        Absent(items, shared);
        Selected(items, Path.Join(Root, "WidgetNorth"));
    }

    [Fact]
    public void Steam_game_takes_its_shader_cache_but_not_the_library_or_another_game()
    {
        string common = Path.Join(Root, "Steam", "steamapps", "common");
        string hades = Path.Join(common, "Hades");
        string stardew = Path.Join(common, "Stardew Valley");
        string shader = Path.Join(Root, "Steam", "steamapps", "shadercache", "1145360");
        var items = Match(
            Game("Hades", null, hades, owned: [shader], source: AppSource.Steam),
            others: [Game("Stardew Valley", null, stardew, source: AppSource.Steam)],
            folders: [common, Path.Join(Root, "Steam", "steamapps"), hades, stardew, shader, Path.Join(Root, "HadesII")]);

        Selected(items, hades);
        Selected(items, shader);
        Absent(items, common);
        Absent(items, Path.Join(Root, "Steam", "steamapps"));
        Absent(items, stardew);
        Absent(items, Path.Join(Root, "HadesII"));
    }

    [SkippableFact]
    public void Windows_and_Program_Files_are_refused()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Skip.If(windows.Length == 0 || programs.Length == 0);

        string insideWindows = Path.Join(windows, "Fortnite");
        var protectedItem = Assert.Single(Match(Game("Fortnite", null, insideWindows)));
        Assert.False(protectedItem.SelectedByDefault);
        Assert.NotNull(protectedItem.Refusal);

        var root = Assert.Single(Match(Game("Something", null, programs)));
        Assert.False(root.SelectedByDefault);
        Assert.NotNull(root.Refusal);
    }

    [Fact]
    public void Publisher_folder_is_kept_and_only_the_product_inside_it_is_removed()
    {
        string publisher = Path.Join(Root, "Local", "Epic Games");
        string product = Path.Join(publisher, "Fortnite");
        var items = Match(Game("Fortnite", "Epic Games, Inc.", Path.Join(Root, "Install")), folders: [publisher, product]);

        Absent(items, publisher);
        Selected(items, product);
    }

    [Fact]
    public void Registry_safety_allows_a_product_key_and_refuses_windows()
    {
        Assert.Null(RegistryKeys.WhyUnsafe(@"HKCU\Software\Fortnite"));
        Assert.Null(RegistryKeys.WhyUnsafe(@"HKCU\Software\Epic Games\Fortnite"));
        Assert.Null(RegistryKeys.WhyUnsafe(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite"));
        Assert.Null(RegistryKeys.WhyUnsafe(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{12345678-1234-1234-1234-123456789012}"));

        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKLM\SOFTWARE"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKLM\SOFTWARE\Microsoft"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKCU\Software\Microsoft\Windows"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKLM\SYSTEM\CurrentControlSet\Services\Fortnite"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKCU\Software\..\Microsoft"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKCR\Software\Fortnite"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKCU\Software\Games"));
        Assert.NotNull(RegistryKeys.WhyUnsafe(@"HKCU\Software\Cache"));
    }

    [Fact]
    public void Uninstall_commands_are_silent_only_when_that_is_real()
    {
        var quiet = UninstallCommand.TryParse("\"C:\\Program Files\\App\\uninstall.exe\" /S", "ignored.exe");
        Assert.NotNull(quiet);
        Assert.Equal(UninstallStyle.Silent, quiet.Style);
        Assert.Equal(@"C:\Program Files\App\uninstall.exe", quiet.FileName);
        Assert.Equal(["/S"], quiet.Arguments);

        var msi = UninstallCommand.TryParse(null, "MsiExec.exe /I{12345678-1234-1234-1234-123456789012}");
        Assert.NotNull(msi);
        Assert.Equal(UninstallStyle.Silent, msi.Style);
        Assert.Contains("/X{12345678-1234-1234-1234-123456789012}", msi.Arguments);
        Assert.Contains("/qn", msi.Arguments);
        Assert.Contains("/norestart", msi.Arguments);

        var alreadyQuiet = UninstallCommand.TryParse(null, "msiexec.exe /X{GUID} /quiet");
        Assert.NotNull(alreadyQuiet);
        Assert.DoesNotContain("/qn", alreadyQuiet.Arguments);
        Assert.Contains("/X{GUID}", alreadyQuiet.Arguments);

        var inno = UninstallCommand.TryParse(null, "\"C:\\Game\\unins000.exe\"");
        Assert.NotNull(inno);
        Assert.Equal(["/VERYSILENT", "/NORESTART"], inno.Arguments);

        var innoSilent = UninstallCommand.TryParse(null, "\"C:\\Game\\unins000.exe\" /SILENT");
        Assert.NotNull(innoSilent);
        Assert.Equal(["/SILENT"], innoSilent.Arguments);

        var normal = UninstallCommand.TryParse(null, "\"C:\\Game\\uninstall.exe\" /args");
        Assert.NotNull(normal);
        Assert.Equal(UninstallStyle.Interactive, normal.Style);
        Assert.Equal(["/args"], normal.Arguments);

        var store = UninstallCommand.Store("SpotifyAB.SpotifyMusic_1.2.3.0_x64__abc123");
        Assert.NotNull(store);
        Assert.Contains("Remove-AppxPackage -Package 'SpotifyAB.SpotifyMusic_1.2.3.0_x64__abc123'", store.DisplayText);
        Assert.Null(UninstallCommand.Store("Spotify; calc.exe"));

        var steam = UninstallCommand.Steam(@"C:\Steam\steam.exe", "12345");
        Assert.Equal(UninstallStyle.LauncherHandoff, steam.Style);
        Assert.Equal(["steam://uninstall/12345"], steam.Arguments);
        Assert.Throws<ArgumentException>(() => UninstallCommand.Steam(@"C:\Steam\steam.exe", "nope"));
    }

    [Fact]
    public void Store_package_names_parse_the_main_package_only()
    {
        Assert.True(ProgramCatalog.TryParsePackage(
            "SpotifyAB.SpotifyMusic_1.2.3.0_x64__abc123",
            out string name, out string version, out string arch, out string resource, out string publisher));
        Assert.Equal("SpotifyAB.SpotifyMusic", name);
        Assert.Equal("1.2.3.0", version);
        Assert.Equal("x64", arch);
        Assert.Equal("", resource);
        Assert.Equal("abc123", publisher);

        Assert.True(ProgramCatalog.TryParsePackage(
            "Contoso.Game_2.0.0.0_neutral_split.scale-100_abc123",
            out _, out _, out _, out resource, out _));
        Assert.Equal("split.scale-100", resource);
    }

    [Fact]
    public void Running_program_and_its_launcher_block_removal_unrelated_processes_do_not()
    {
        string install = Path.Join(Root, "Epic Games", "Fortnite");
        var app = Game("Fortnite", "Epic Games, Inc.", install, source: AppSource.Epic);
        var processes = new List<RunningPrograms.Snapshot>
        {
            new(1, DateTime.UtcNow, "FortniteClient-Win64-Shipping", Path.Join(install, "FortniteClient-Win64-Shipping.exe")),
            new(2, DateTime.UtcNow, "EpicGamesLauncher", Path.Join(Root, "Epic Games", "Launcher", "EpicGamesLauncher.exe")),
            new(3, DateTime.UtcNow, "steam", Path.Join(Root, "Steam", "steam.exe")),
            new(4, DateTime.UtcNow, "notepad", Path.Join(Root, "Windows", "notepad.exe")),
            new(5, DateTime.UtcNow, "Epic", Path.Join(Root, "Epic", "Epic.exe")),
            new(6, DateTime.UtcNow, "explorer", Path.Join(Root, "Windows", "explorer.exe")),
        };

        var blocking = RunningPrograms.Blocking(processes, app, [install], uninstall: null);
        Assert.Equal([1, 2], blocking.Select(process => process.ProcessId).Order());

        var handoff = UninstallCommand.Steam(Path.Join(Root, "Steam", "steam.exe"), "10");
        var steamApp = Game("Hades", null, Path.Join(Root, "Hades"), source: AppSource.Steam) with { SteamAppId = "10", SteamExe = handoff.FileName };
        var withSteam = RunningPrograms.Blocking(processes, steamApp, [Path.Join(Root, "Hades")], handoff);
        Assert.DoesNotContain(withSteam, process => process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase));

        var uninstall = new UninstallLaunch(processes[1].ExecutablePath!, [], UninstallStyle.Interactive, "");
        var whileUninstalling = RunningPrograms.Blocking(processes, app, [install], uninstall);
        Assert.DoesNotContain(whileUninstalling, process => process.ProcessId == 2);
        Assert.Contains(whileUninstalling, process => process.ProcessId == 1);
    }

    [Fact]
    public void Roblox_player_does_not_treat_Roblox_Studio_as_its_process()
    {
        var player = Game("Roblox", "Roblox Corporation", Path.Join(Root, "player"), source: AppSource.Roblox);
        var processes = new List<RunningPrograms.Snapshot>
        {
            new(8, DateTime.UtcNow, "RobloxPlayerBeta", null),
            new(9, DateTime.UtcNow, "RobloxStudioBeta", null),
        };

        var blocking = RunningPrograms.Blocking(processes, player, [], null);
        Assert.Equal([8], blocking.Select(process => process.ProcessId));
    }

    [Fact]
    public void Catalog_merge_keeps_similar_programs_apart_and_folds_the_same_one()
    {
        string fortnite = Path.Join(Root, "Epic Games", "Fortnite");
        string epic = Path.Join(Root, "Epic Games");
        string rocket = Path.Join(Root, "Epic Games", "RocketLeague");
        var fromEpic = Game("Fortnite", null, fortnite, "epic:Fortnite", AppSource.Epic);
        var fromRegistry = Game("Fortnite", "Epic Games, Inc.", epic, "reg:fortnite") with
        {
            UninstallString = "\"C:\\Uninstall.exe\"",
            UninstallKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite",
            MachineWide = true,
        };

        var merged = Assert.Single(ProgramCatalog.Merge([fromRegistry, fromEpic]));
        Assert.Equal(AppSource.Epic, merged.Source);
        Assert.Equal(Norm(fortnite), merged.InstallLocation);
        Assert.Equal("Epic Games, Inc.", merged.Publisher);
        Assert.Equal(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite", merged.UninstallKey);
        Assert.Contains("Uninstall.exe", merged.UninstallString);

        Assert.Equal(2, ProgramCatalog.Merge(
        [
            Game("Fortnite", null, fortnite, "epic:Fortnite", AppSource.Epic),
            Game("Rocket League", null, rocket, "epic:Rocket", AppSource.Epic),
        ]).Count);

        Assert.Equal(2, ProgramCatalog.Merge(
        [
            Game("Microsoft Visual C++ 2015-2022 Redistributable (x64)", null, Path.Join(Root, "VcX64")),
            Game("Microsoft Visual C++ 2015-2022 Redistributable (x86)", null, Path.Join(Root, "VcX86")),
        ]).Count);

        string shared = Path.Join(Root, "Local", "Roblox");
        string version = Path.Join(shared, "Versions", "version-player");
        var roblox = Assert.Single(ProgramCatalog.Merge(
        [
            Game("Roblox", "Roblox Corporation", version, "roblox:player", AppSource.Roblox),
            Game("Roblox Player", "Roblox Corporation", shared, "reg:roblox") with { UninstallString = "\"C:\\Roblox\\uninstall.exe\"" },
        ]));
        Assert.Equal(AppSource.Roblox, roblox.Source);
        Assert.Equal(Norm(version), roblox.InstallLocation);
        Assert.Contains("uninstall.exe", roblox.UninstallString);
        Assert.Equal(2, ProgramCatalog.Merge(
        [
            Game("Roblox", null, version, "roblox:player", AppSource.Roblox),
            Game("Roblox Studio", null, Path.Join(shared, "Versions", "version-studio"), "roblox:studio", AppSource.Roblox),
        ]).Count);
    }

    [Fact]
    public void A_registry_entry_under_Epic_Games_is_labeled_as_Epic()
    {
        var app = ProgramCatalog.Classify(Game("Fortnite", null, Path.Join(Root, "Epic Games", "Fortnite")));
        Assert.Equal(AppSource.Epic, app.Source);

        var launcher = ProgramCatalog.Classify(Game("Epic Games Launcher", null, Path.Join(Root, "Epic Games", "Launcher")));
        Assert.Equal(AppSource.Registry, launcher.Source);
    }

    private static CatalogApp Game(
        string name,
        string? publisher,
        string install,
        string? id = null,
        AppSource source = AppSource.Registry,
        IReadOnlyList<string>? owned = null) => new()
    {
        Id = id ?? name + ":" + install,
        Name = name,
        Publisher = publisher,
        Source = source,
        InstallLocation = install,
        OwnedPaths = owned ?? [],
        UninstallKey = id is not null && id.StartsWith("reg:", StringComparison.Ordinal) ? null : null,
    };

    private static IReadOnlyList<RemovalItem> Match(
        CatalogApp target,
        IEnumerable<CatalogApp>? others = null,
        IEnumerable<string>? folders = null,
        IEnumerable<ShortcutHint>? shortcuts = null,
        IEnumerable<string>? keys = null) =>
        RemovalMatcher.Match(target, others?.ToList() ?? [], folders ?? [], shortcuts ?? [], keys ?? []).Items;

    private static string Norm(string path) => LongPath.Normalize(path);

    private static void Selected(IReadOnlyList<RemovalItem> items, string path)
    {
        string expected = Norm(path);
        var item = items.SingleOrDefault(candidate => candidate.Location.Equals(expected, StringComparison.OrdinalIgnoreCase));
        Assert.True(item is { SelectedByDefault: true, Refusal: null }, $"Expected {expected} to be selected. Had: {Describe(items)}");
    }

    private static void Absent(IReadOnlyList<RemovalItem> items, string path)
    {
        string expected = Norm(path);
        Assert.True(items.All(item => !item.Location.Equals(expected, StringComparison.OrdinalIgnoreCase)), $"Did not expect {expected}. Had: {Describe(items)}");
    }

    private static void Refused(IReadOnlyList<RemovalItem> items, string path)
    {
        string expected = Norm(path);
        var item = items.SingleOrDefault(candidate => candidate.Location.Equals(expected, StringComparison.OrdinalIgnoreCase));
        Assert.True(item is { SelectedByDefault: false, Refusal: not null }, $"Expected {expected} to be refused. Had: {Describe(items)}");
    }

    private static string Describe(IReadOnlyList<RemovalItem> items) =>
        string.Join("; ", items.Select(item => $"{item.Location} selected={item.SelectedByDefault}"));
}
