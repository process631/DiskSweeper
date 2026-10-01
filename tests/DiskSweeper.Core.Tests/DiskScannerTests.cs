using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Core.Tests;

public class DiskScannerTests
{
    private static Task<ScanResult> Scan(string path, bool useBackupPrivilege = false) =>
        new DiskScanner(new ScanOptions { MaxParallelism = 4, UseBackupPrivilege = useBackupPrivilege }).ScanAsync(path);

    [Fact]
    public async Task Totals_include_every_nested_file_and_children_are_sorted_largest_first()
    {
        using var box = new Sandbox();
        box.CreateFile("a.bin", 1_000);
        box.CreateFile(@"games\big.pak", 50_000);
        box.CreateFile(@"games\saves\slot1.sav", 2_000);
        box.CreateFolder("empty");

        var result = await Scan(box.Root);

        var root = result.Root;
        Assert.Equal(53_000, root.Size);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(3, root.FolderCount);
        Assert.Equal(["games", "a.bin", "empty"], root.Children.Select(c => c.Name));

        var games = root.Children[0];
        Assert.Equal(52_000, games.Size);
        Assert.Equal(["big.pak", "saves"], games.Children.Select(c => c.Name));
        Assert.Equal(box.PathOf(@"games\saves\slot1.sav"), games.Children[1].Children[0].FullPath);
        Assert.Empty(result.UnreadableFolders);
    }

    [Fact]
    public async Task Folder_modified_time_is_the_newest_change_inside_it()
    {
        using var box = new Sandbox();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        box.CreateFile(@"game\old.dat", 10, modifiedUtc: old);
        box.CreateFile(@"game\deep\new.dat", 10, modifiedUtc: newer);

        var result = await Scan(box.Root);

        Assert.True(result.FindNode(box.PathOf("game"))!.LastWriteTimeUtc >= newer);
    }

    [Fact]
    public async Task Hidden_and_system_files_are_counted()
    {
        using var box = new Sandbox();
        box.CreateFile("hidden.sys", 4_096, FileAttributes.Hidden | FileAttributes.System);
        box.CreateFile("visible.txt", 100);

        var result = await Scan(box.Root);

        Assert.Equal(4_196, result.Root.Size);
    }

    [Fact]
    public async Task Junctions_are_listed_but_never_followed()
    {
        using var box = new Sandbox();
        box.CreateFile(@"elsewhere\secret.bin", 10_000);
        box.CreateFile(@"scanme\real.bin", 100);
        box.CreateJunction(@"scanme\link", "elsewhere");

        var result = await Scan(box.PathOf("scanme"));

        Assert.Equal(100, result.Root.Size);
        Assert.Equal(1, result.LinkCount);
        var link = Assert.Single(result.Root.Children, c => c.Name == "link");
        Assert.True(link.IsLink);
        Assert.Empty(link.Children);
    }

    [Fact]
    public async Task Long_paths_and_names_Explorer_cannot_handle_are_counted()
    {
        using var box = new Sandbox();
        string deep = string.Join('\\', Enumerable.Repeat(new string('d', 50), 6));
        box.CreateFile(Path.Join(deep, "deep.bin"), 300);
        box.CreateFile(@"trailing space \file.", 20);

        var result = await Scan(box.Root);

        Assert.True(box.PathOf(deep).Length > 300);
        Assert.Equal(320, result.Root.Size);
        Assert.Equal(300, result.FindNode(box.PathOf(Path.Join(deep, "deep.bin")))!.Size);
        Assert.NotNull(result.FindNode(box.PathOf(@"trailing space \file.")));
    }

    [SkippableFact]
    public async Task Folders_that_cannot_be_read_are_reported_and_the_scan_carries_on()
    {
        // Git Bash switches the backup privilege on for admin processes, which (correctly) lets the scan read everything.
        Skip.If(Privileges.IsEnabled(Privileges.Backup), "The backup privilege is on, so no folder is unreadable.");
        using var box = new Sandbox();
        box.CreateFile(@"locked\inside.bin", 500);
        box.CreateFile(@"open\inside.bin", 700);
        box.DenyListing("locked");

        var result = await Scan(box.Root);

        Assert.Equal(700, result.Root.Size);
        Assert.Equal([box.PathOf("locked")], result.UnreadableFolders);
        Assert.True(result.FindNode(box.PathOf("locked"))!.IsUnreadable);
    }

    [AdminFact]
    public async Task Administrators_can_count_folders_whose_permissions_lock_them_out()
    {
        bool wasEnabled = Privileges.IsEnabled(Privileges.Backup);
        using var box = new Sandbox();
        box.CreateFile(@"locked\inside.bin", 500);
        box.DenyListing("locked");

        try
        {
            var result = await Scan(box.Root, useBackupPrivilege: true);

            Assert.Equal(500, result.Root.Size);
            Assert.Empty(result.UnreadableFolders);
        }
        finally
        {
            if (!wasEnabled) Privileges.TryDisable(Privileges.Backup);
        }
    }

    [Fact]
    public async Task Online_only_cloud_files_take_no_local_space()
    {
        using var box = new Sandbox();
        box.CreateFile("cloud.mp4", 9_000, FileAttributes.Offline);
        box.CreateFile("local.mp4", 1_000);

        var result = await Scan(box.Root);

        Assert.Equal(1_000, result.Root.Size);
        Assert.Equal(1, result.CloudOnlyFileCount);
        Assert.Equal(9_000, result.CloudOnlyBytes);
        Assert.True(result.FindNode(box.PathOf("cloud.mp4"))!.IsCloudOnly);
    }

    [Fact]
    public async Task Largest_files_come_back_biggest_first()
    {
        using var box = new Sandbox();
        box.CreateFile("small.bin", 10);
        box.CreateFile(@"a\huge.bin", 9_000);
        box.CreateFile(@"b\c\medium.bin", 500);
        box.CreateFile("tiny.bin", 1);

        var result = await Scan(box.Root);

        Assert.Equal(["huge.bin", "medium.bin"], result.GetLargestFiles(2).Select(f => f.Name));
        Assert.Equal(["small.bin", "tiny.bin"], result.GetLargestFiles(5, f => f.Size < 100).Select(f => f.Name));
    }

    [Fact]
    public async Task A_cancelled_scan_throws_instead_of_returning_partial_results()
    {
        using var box = new Sandbox();
        box.CreateFile("x.bin", 1);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DiskScanner(new ScanOptions { UseBackupPrivilege = false }).ScanAsync(box.Root, cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task Missing_folder_fails_right_away()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Scan(@"C:\definitely\not\here\" + Guid.NewGuid()));
    }

    [Theory]
    [InlineData("C:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:\"", @"C:\")]
    [InlineData(@"C:\Users\", @"C:\Users")]
    public void Paths_typed_by_people_are_normalized(string typed, string expected)
    {
        Assert.Equal(expected, LongPath.Normalize(typed));
    }
}
