using DiskSweeper.Core.Analysis;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Core.Tests;

public class AnalysisTests
{
    private static Task<ScanResult> Scan(string path) =>
        new DiskScanner(new ScanOptions { MaxParallelism = 4, UseBackupPrivilege = false }).ScanAsync(path);

    [Fact]
    public async Task Treemap_areas_match_sizes_and_stay_inside_the_box()
    {
        using var box = new Sandbox();
        box.CreateFile("a.bin", 6_000);
        box.CreateFile("b.bin", 3_000);
        box.CreateFile(@"c\c1.bin", 500);
        box.CreateFile(@"c\c2.bin", 500);
        var root = (await Scan(box.Root)).Root;

        var rects = Treemap.Layout(root, 400, 300, maxDepth: 2, minSide: 0, padding: 0);

        var top = rects.Where(r => r.Depth == 0).ToList();
        Assert.Equal(3, top.Count);
        Assert.Equal(400 * 300, top.Sum(r => r.Width * r.Height), precision: 3);
        foreach (var rect in top)
        {
            Assert.Equal(400.0 * 300 * rect.Node.Size / root.Size, rect.Width * rect.Height, precision: 3);
            Assert.InRange(rect.X, -0.001, 400);
            Assert.InRange(rect.X + rect.Width, 0, 400.001);
            Assert.InRange(rect.Y + rect.Height, 0, 300.001);
        }
        for (int i = 0; i < top.Count; i++)
        {
            for (int j = i + 1; j < top.Count; j++)
            {
                var (a, b) = (top[i], top[j]);
                double overlapW = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                double overlapH = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.False(overlapW > 0.001 && overlapH > 0.001, $"{a.Node.Name} overlaps {b.Node.Name}");
            }
        }
        Assert.Equal(2, rects.Count(r => r.Depth == 1)); // c's two files, nested inside c.
    }

    [Fact]
    public async Task File_types_add_up_by_extension_and_kind()
    {
        using var box = new Sandbox();
        box.CreateFile("movie.mp4", 5_000);
        box.CreateFile(@"x\clip.MKV", 1_000);
        box.CreateFile("setup.exe", 300);
        box.CreateFile("README", 10);
        var root = (await Scan(box.Root)).Root;

        var byExtension = FileTypes.ByExtension(root);
        var byKind = FileTypes.ByKind(byExtension);

        Assert.Equal(".mp4", byExtension[0].Extension);
        Assert.Contains(byExtension, s => s.Extension == "(no extension)" && s.Size == 10);
        Assert.Equal(new FileKindStats(FileKind.Video, 6_000, 2), byKind[0]);
    }

    [Fact]
    public async Task Saved_scans_load_back_identically()
    {
        using var box = new Sandbox();
        box.CreateFile(@"games\big.pak", 9_000);
        box.CreateFile(@"games\saves\s.sav", 100);
        box.CreateFile("note.txt", 5);
        box.CreateJunction("link", "games");
        string previousFolder = ScanStore.Folder;
        ScanStore.Folder = box.PathOf("_scans");
        box.OnCleanup(() => ScanStore.Folder = previousFolder);
        var original = await Scan(box.Root);

        string file = ScanStore.Save(original);
        var loaded = ScanStore.Load(file);

        Assert.Equal(ScanMethod.Saved, loaded.Method);
        Assert.Equal(original.LinkCount, loaded.LinkCount);
        var before = original.EnumerateNodes().Select(n => (n.FullPath, n.Size, n.Flags, n.FileCount, n.FolderCount, n.LastWriteTimeUtc)).ToList();
        var after = loaded.EnumerateNodes().Select(n => (n.FullPath, n.Size, n.Flags, n.FileCount, n.FolderCount, n.LastWriteTimeUtc)).ToList();
        Assert.Equal(before, after);

        var listed = Assert.Single(ScanStore.List(box.Root));
        Assert.Equal(original.Root.Size, listed.TotalSize);
    }

    [Fact]
    public async Task Comparison_points_at_the_folder_that_actually_grew()
    {
        using var box = new Sandbox();
        box.CreateFile(@"Users\me\Games\Old\data.pak", 1_000);
        box.CreateFile(@"Users\me\Docs\a.txt", 50);
        box.CreateFile(@"Users\me\Gone\big.iso", 3_000);
        var before = await Scan(box.Root);

        File.Delete(box.PathOf(@"Users\me\Gone\big.iso"));
        box.CreateFile(@"Users\me\Games\Old\patch.pak", 8_000);
        box.CreateFile(@"Users\me\Games\New\game.pak", 4_000);
        var after = await Scan(box.Root);

        var changes = ScanComparer.Compare(before.Root, after.Root, minimumChange: 1_000);

        // Descends to the exact items: the new patch file, the new game folder, the deleted image.
        Assert.Equal(
            [(box.PathOf(@"Users\me\Games\Old\patch.pak"), ChangeKind.New, 8_000L),
             (box.PathOf(@"Users\me\Games\New"), ChangeKind.New, 4_000L),
             (box.PathOf(@"Users\me\Gone\big.iso"), ChangeKind.Gone, -3_000L)],
            changes.Select(c => (c.Path, c.Kind, c.Delta)));
    }

    [AdminFact]
    public async Task Fast_scan_reads_the_same_sizes_as_Windows()
    {
        using var box = new Sandbox();
        string file = box.CreateFile(@"mft\odd-size.bin", 1_234_567);
        box.CreateFile(@"mft\tiny.txt", 12); // Small enough to live inside its MFT record.
        string link = box.CreateFile(@"mft\linked.bin", 4_096);
        Sandbox.Run("cmd", "/c", "mklink", "/H", box.PathOf(@"mft\hardlink.bin"), link);
        string drive = Path.GetPathRoot(box.Root)!;

        // The file table on disk catches up with brand-new files a few seconds after they're created.
        ScanResult result;
        int attempt = 0;
        do
        {
            if (attempt++ > 0) await Task.Delay(2_000);
            result = await new MftScanner().ScanAsync(drive);
        }
        while (result.FindNode(box.PathOf(@"mft\hardlink.bin")) is null && attempt < 8);

        Assert.Equal(ScanMethod.MasterFileTable, result.Method);
        var folder = result.FindNode(box.PathOf("mft"))!;
        Assert.Equal(["odd-size.bin", "hardlink.bin", "linked.bin", "tiny.txt"], folder.Children.Select(c => c.Name));
        Assert.Equal(1_234_567, result.FindNode(file)!.Size);
        Assert.Equal(12, result.FindNode(box.PathOf(@"mft\tiny.txt"))!.Size);
        Assert.Equal(1_234_567 + 4_096 * 2 + 12, folder.Size);
    }
}
