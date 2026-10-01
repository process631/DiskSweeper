using DiskSweeper.Core.Deletion;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Core.SmartFinds;

public sealed class SmartFinderOptions
{
    /// <summary>Hide findings smaller than this (Windows-managed items are always shown).</summary>
    public long MinimumSize { get; init; } = 10L << 20;

    /// <summary>A possible leftover is only shown if nothing inside it changed for this many days.</summary>
    public int LeftoverQuietDays { get; init; } = 30;

    /// <summary>Installers and archives in Downloads older than this are suggested.</summary>
    public int OldDownloadDays { get; init; } = 30;
}

/// <summary>
/// Collects everything worth cleaning: junk from JunkRules.json, leftovers of uninstalled games and
/// programs, old downloads, the Recycle Bin, and Windows-managed space (with advice instead of a delete).
/// Pass a <see cref="ScanResult"/> to reuse its sizes; otherwise each place is measured directly.
/// </summary>
public sealed class SmartFinder(ScanResult? scan = null, SmartFinderOptions? options = null)
{
    private static readonly string[] DownloadExtensions =
        [".exe", ".msi", ".msix", ".appx", ".iso", ".img", ".ova", ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".cab", ".dmg"];

    private readonly SmartFinderOptions _options = options ?? new SmartFinderOptions();

    public Task<IReadOnlyList<Finding>> FindAsync(IProgress<string>? status = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Find(status, cancellationToken), cancellationToken);

    public IReadOnlyList<Finding> Find(IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        if (Elevation.IsElevated) Privileges.TryEnable(Privileges.Backup);

        status?.Report("Checking installed programs and services…");
        var software = InstalledSoftware.Load();
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<Finding>();
        findings.AddRange(FromRules(JunkRule.LoadBuiltIn()));
        if (RecycleBinFinding() is Finding bin) findings.Add(bin);

        status?.Report("Checking game launchers…");
        var launchers = new List<LauncherState>();
        findings.AddRange(SteamFindings(launchers));
        foreach (var launcher in new[] { EpicLauncher.Load(software.Programs), GogLauncher.Load(software.Programs) })
        {
            launchers.Add(launcher);
            findings.AddRange(LauncherLeftovers(launcher));
        }
        cancellationToken.ThrowIfCancellationRequested();

        status?.Report("Looking for leftovers of uninstalled programs…");
        var leftovers = new LeftoverFinder(software, launchers, findings.Select(f => f.Path)).Find().ToList();
        findings.AddRange(leftovers);
        findings.AddRange(OldDownloads());

        findings = RemoveOverlaps(findings);

        status?.Report($"Measuring {findings.Count} places…");
        Parallel.ForEach(findings, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, Measure);

        var quietSince = DateTime.UtcNow.AddDays(-_options.LeftoverQuietDays);
        return findings
            .Where(f => f.Category == FindCategory.WindowsManaged ? f.Size > 0 : f.Size >= _options.MinimumSize)
            .Where(f => !leftovers.Contains(f) || f.LastChangedUtc is null || f.LastChangedUtc < quietSince)
            .OrderByDescending(f => f.Size)
            .ToList();
    }

    private static IEnumerable<Finding> FromRules(IEnumerable<JunkRule> rules)
    {
        foreach (var rule in rules)
        {
            foreach (string pattern in rule.Paths)
            {
                foreach (string path in JunkRule.Expand(pattern))
                {
                    bool advice = rule.Mode.Equals("advice", StringComparison.OrdinalIgnoreCase);
                    yield return new Finding
                    {
                        Id = $"{rule.Id}:{path}",
                        Title = rule.Title,
                        Path = path,
                        Category = rule.Category,
                        Confidence = rule.Confidence,
                        Explanation = rule.Explanation,
                        Action = advice ? FindingAction.AdviceOnly : FindingAction.Delete,
                        ContentsOnly = rule.Mode.Equals("contents", StringComparison.OrdinalIgnoreCase),
                        MinAgeDays = rule.MinAgeDays,
                        NeedsAdmin = rule.Admin,
                        Advice = rule.Advice,
                        AdviceCommand = rule.AdviceCommand,
                    };
                }
            }
        }
    }

    private static Finding? RecycleBinFinding()
    {
        var (bytes, items) = RecycleBin.Query();
        if (bytes <= 0) return null;
        return new Finding
        {
            Id = "recycle-bin",
            Title = $"Recycle Bin ({items:N0} items)",
            Path = "Recycle Bin",
            Category = FindCategory.Junk,
            Confidence = Confidence.Review,
            Action = FindingAction.EmptyRecycleBin,
            Size = bytes,
            Explanation = "Things you deleted earlier still use space until the bin is emptied. After emptying they can't be restored.",
        };
    }

    private IEnumerable<Finding> SteamFindings(List<LauncherState> launchers)
    {
        string? steam = SteamLauncher.FindInstallFolder();
        if (steam is null) yield break;

        var libraries = SteamLauncher.LoadLibraries(steam);
        launchers.Add(new LauncherState
        {
            Name = "Steam",
            LauncherInstalled = File.Exists(Path.Join(steam, "steam.exe")),
            Games = libraries.SelectMany(l => l.Games).ToList(),
            GameRoots = libraries.Select(l => Path.Join(l.Path, @"steamapps\common")).ToList(),
        });

        foreach (var finding in SteamLibraryFindings(libraries)) yield return finding;

        foreach (string sub in new[] { @"appcache\httpcache", "logs", "dumps" })
        {
            string path = Path.Join(steam, sub);
            if (!Directory.Exists(path)) continue;
            yield return new Finding
            {
                Id = "steam-cache:" + path, Title = "Steam cache and logs", Path = path, Category = FindCategory.Junk,
                Confidence = Confidence.Safe, ContentsOnly = true, Explanation = "Store pages, logs and crash reports Steam keeps around. Steam recreates them.",
            };
        }
    }

    /// <summary>Orphaned game folders, shader caches and workshop content in Steam libraries, plus unfinished downloads.</summary>
    internal static IEnumerable<Finding> SteamLibraryFindings(IReadOnlyList<SteamLauncher.SteamLibrary> libraries)
    {
        foreach (var library in libraries)
        {
            string apps = Path.Join(library.Path, "steamapps");
            var installDirs = new HashSet<string>(library.Games.Select(g => Path.GetFileName(g.InstallPath ?? "")), StringComparer.OrdinalIgnoreCase);
            var installedIds = new HashSet<string>(libraries.SelectMany(l => l.Games).Select(g => g.Id ?? ""));

            foreach (string folder in Subfolders(Path.Join(apps, "common")).Where(f => !installDirs.Contains(Path.GetFileName(f))))
            {
                yield return GameLeftover("steam-common", folder, $"Leftover Steam game: {Path.GetFileName(folder)}",
                    "No Steam game installed in this library uses this folder. Steam leaves folders like this behind when a game's uninstall doesn't finish or files were added by mods.",
                    Confidence.Safe);
            }

            foreach (var (kind, label, explanation) in new[]
            {
                ("shadercache", "shader cache", "Shader cache of a Steam game that's no longer installed."),
                (@"workshop\content", "workshop mods", "Workshop mods of a Steam game that's no longer installed."),
                ("compatdata", "compatibility data", "Compatibility data of a Steam game that's no longer installed."),
            })
            {
                foreach (string folder in Subfolders(Path.Join(apps, kind)).Where(f => !installedIds.Contains(Path.GetFileName(f))))
                    yield return GameLeftover("steam-" + label.Replace(' ', '-'), folder, $"Steam {label} of removed app {Path.GetFileName(folder)}", explanation, Confidence.Safe);
            }

            foreach (var (sub, title, confidence, explanation) in new[]
            {
                ("downloading", "Unfinished Steam downloads", Confidence.Review, "Partly downloaded games and updates. Only clean this if nothing is downloading or paused in Steam."),
                ("temp", "Steam temporary files", Confidence.Safe, "Temporary files from Steam updates."),
            })
            {
                string path = Path.Join(apps, sub);
                if (!Directory.Exists(path)) continue;
                yield return new Finding
                {
                    Id = $"steam-{sub}:{path}", Title = title, Path = path, Category = FindCategory.Junk,
                    Confidence = confidence, ContentsOnly = true, Explanation = explanation,
                };
            }
        }
    }

    internal static IEnumerable<Finding> LauncherLeftovers(LauncherState launcher)
    {
        var installed = new HashSet<string>(launcher.Games.Select(g => g.InstallPath).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (string root in launcher.GameRoots)
        {
            foreach (string folder in Subfolders(root))
            {
                if (installed.Contains(folder) || launcher.NonGameFolders.Contains(Path.GetFileName(folder))) continue;
                if (installed.Any(game => LongPath.IsSameOrInside(game, folder))) continue;

                yield return launcher.LauncherInstalled
                    ? GameLeftover("launcher-leftover", folder, $"Leftover {launcher.Name} game: {Path.GetFileName(folder)}",
                        $"{launcher.Name} is installed but has no record of a game in this folder. If you moved the game yourself, it may still be in use.",
                        Confidence.Review)
                    : GameLeftover("launcher-gone", folder, $"Game left by removed {launcher.Name}: {Path.GetFileName(folder)}",
                        $"{launcher.Name} isn't installed anymore, so nothing can start or update this game. It was left behind when the launcher was uninstalled.",
                        Confidence.Safe);
            }
        }
    }

    private static Finding GameLeftover(string id, string path, string title, string explanation, Confidence confidence) => new()
    {
        Id = $"{id}:{path}",
        Title = title,
        Path = path,
        Category = FindCategory.GameLeftover,
        Confidence = confidence,
        Explanation = explanation,
    };

    private IEnumerable<Finding> OldDownloads()
    {
        string downloads = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        List<FileSystemWalk.Entry> entries;
        try
        {
            entries = FileSystemWalk.List(downloads);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var cutoff = DateTime.UtcNow.AddDays(-_options.OldDownloadDays);
        var old = entries
            .Where(e => !e.IsDirectory && e.LastWriteTimeUtc < cutoff && DownloadExtensions.Contains(Path.GetExtension(e.Path).ToLowerInvariant()))
            .ToList();

        const long Big = 100L << 20;
        foreach (var file in old.Where(e => e.Length >= Big))
        {
            yield return new Finding
            {
                Id = "download:" + file.Path,
                Title = $"Old download: {Path.GetFileName(file.Path)}",
                Path = file.Path,
                Category = FindCategory.Downloads,
                Confidence = Confidence.Review,
                Size = file.Length,
                LastChangedUtc = file.LastWriteTimeUtc,
                Explanation = $"An installer, disk image or archive you downloaded on {file.LastWriteTimeUtc.ToLocalTime():d}. Once something is installed or extracted, the download is rarely needed.",
            };
        }

        var small = old.Where(e => e.Length < Big).ToList();
        if (small.Count > 0)
        {
            yield return new Finding
            {
                Id = "downloads-small:" + downloads,
                Title = $"{small.Count} older installers and archives in Downloads",
                Path = downloads,
                Category = FindCategory.Downloads,
                Confidence = Confidence.Review,
                Files = small.Select(e => e.Path).ToList(),
                Size = small.Sum(e => e.Length),
                Explanation = $"Installers, disk images and archives older than {_options.OldDownloadDays} days, each under 100 MB.",
            };
        }
    }

    /// <summary>Drops duplicates and anything already covered by a bigger finding that deletes its whole folder.</summary>
    private static List<Finding> RemoveOverlaps(List<Finding> findings)
    {
        var kept = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in findings)
        {
            if (finding.Files is null && !seen.Add(finding.Path)) continue;
            bool covered = findings.Any(other => !ReferenceEquals(other, finding) && other.Action == FindingAction.Delete
                && !other.ContentsOnly && other.Files is null
                && other.Path.Length < finding.Path.Length && LongPath.IsSameOrInside(finding.Path, other.Path));
            if (!covered) kept.Add(finding);
        }
        return kept;
    }

    private void Measure(Finding finding)
    {
        if (finding.Action == FindingAction.EmptyRecycleBin || finding.Size > 0) return;

        if (finding.Action == FindingAction.Delete && (finding.ContentsOnly || finding.Files is not null))
        {
            long total = 0;
            DateTime newest = default;
            foreach (string item in finding.ItemsToDelete())
            {
                var (size, time) = SizeOf(item);
                total += size;
                if (time > newest) newest = time;
            }
            finding.Size = total;
            finding.LastChangedUtc = newest == default ? null : newest;
            return;
        }

        var (bytes, changed) = SizeOf(finding.Path);
        finding.Size = bytes;
        finding.LastChangedUtc = changed == default ? null : changed;
    }

    private (long Size, DateTime Newest) SizeOf(string path)
    {
        if (scan?.FindNode(path) is FileNode node && !node.IsUnreadable) return (node.Size, node.LastWriteTimeUtc);

        long size = 0;
        DateTime newest = default;
        foreach (var entry in FileSystemWalk.Walk(path))
        {
            if (!entry.IsDirectory && (entry.Attributes & FileSystemWalk.CloudOnlyAttributes) == 0) size += entry.Length;
            if (entry.LastWriteTimeUtc > newest) newest = entry.LastWriteTimeUtc;
        }
        return (size, newest);
    }

    private static IEnumerable<string> Subfolders(string folder)
    {
        try
        {
            return FileSystemWalk.List(folder).Where(e => e.IsDirectory && !e.IsLink).Select(e => e.Path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
