using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DiskSweeper.Core.Deletion;

namespace DiskSweeper.Core.Tests;

public class DeleteServiceTests
{
    private static DeleteService NewService(Sandbox box) => new(new DeletionLog(box.PathOf(@"_log\log.csv")));

    [Fact]
    public void Permanent_delete_handles_read_only_hidden_long_and_oddly_named_files()
    {
        using var box = new Sandbox();
        string deep = string.Join('\\', Enumerable.Repeat(new string('x', 60), 5));
        box.CreateFile(@"game\readonly.cfg", 100, FileAttributes.ReadOnly);
        box.CreateFile(@"game\hidden.dat", 200, FileAttributes.Hidden | FileAttributes.System);
        box.CreateFile(Path.Join(@"game", deep, "deep.bin"), 300);
        box.CreateFile(@"game\trailing dot.\file ", 400);
        Sandbox.Run("attrib", "+R", box.PathOf("game"));

        var result = NewService(box).Delete(box.PathOf("game"), DeleteMethod.Permanent);

        Assert.Equal(DeleteOutcome.Deleted, result.Outcome);
        Assert.Equal(1_000, result.Bytes);
        Assert.False(FileSystemWalk.Exists(box.PathOf("game")));
    }

    [Fact]
    public void Deleting_a_folder_removes_links_inside_it_but_never_what_they_point_to()
    {
        using var box = new Sandbox();
        string precious = box.CreateFile(@"precious\photo.jpg", 5_000);
        box.CreateFile(@"leftover\junk.bin", 10);
        box.CreateJunction(@"leftover\shortcut-to-precious", "precious");

        var result = NewService(box).Delete(box.PathOf("leftover"), DeleteMethod.Permanent);

        Assert.Equal(DeleteOutcome.Deleted, result.Outcome);
        Assert.Equal(10, result.Bytes);
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void Deleting_a_link_itself_keeps_its_target()
    {
        using var box = new Sandbox();
        string precious = box.CreateFile(@"precious\photo.jpg", 5_000);
        string link = box.CreateJunction("link", "precious");

        var result = NewService(box).Delete(link, DeleteMethod.Permanent);

        Assert.Equal(DeleteOutcome.Deleted, result.Outcome);
        Assert.False(FileSystemWalk.Exists(link));
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void A_file_held_open_is_reported_with_the_program_holding_it()
    {
        using var box = new Sandbox();
        string file = box.CreateFile(@"game\locked.pak", 1_000);
        box.CreateFile(@"game\free.pak", 500);
        var service = NewService(box);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = service.Delete(box.PathOf("game"), DeleteMethod.Permanent);

            Assert.Equal(DeleteOutcome.Failed, blocked.Outcome);
            Assert.Equal(BlockReason.InUse, blocked.Reason);
            Assert.Equal(500, blocked.Bytes);
            Assert.Contains(blocked.LockedBy, l => l.ProcessId == Environment.ProcessId);
            Assert.Equal(file, Assert.Single(blocked.FailedItems).Path);
        }

        Assert.Equal(DeleteOutcome.Deleted, service.Delete(box.PathOf("game"), DeleteMethod.Permanent).Outcome);
    }

    [Fact]
    public void Closing_the_program_that_holds_a_file_lets_it_be_deleted()
    {
        using var box = new Sandbox();
        string file = box.CreateFile("held.bin", 100);
        string ready = box.PathOf("ready.flag");
        var holder = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-Command",
                $"$f = [IO.File]::Open('{file}', 'Open', 'Read', 'None'); New-Item '{ready}' | Out-Null; Start-Sleep 60",
            },
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        box.OnCleanup(() => { if (!holder.HasExited) holder.Kill(); });
        SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(30));

        var service = NewService(box);
        var blocked = service.Delete(file, DeleteMethod.Permanent);
        Assert.Equal(BlockReason.InUse, blocked.Reason);
        var locker = Assert.Single(blocked.LockedBy);
        Assert.Equal(holder.Id, locker.ProcessId);

        var report = AppCloser.Close(blocked.LockedBy, force: true);

        Assert.Contains(report.Closed, l => l.ProcessId == holder.Id);
        Assert.Equal(DeleteOutcome.Deleted, service.Delete(file, DeleteMethod.Permanent).Outcome);
    }

    [Fact]
    public void Dry_run_reports_the_size_and_touches_nothing()
    {
        using var box = new Sandbox();
        box.CreateFile(@"cache\a.bin", 700);
        box.CreateFile(@"cache\b.bin", 300);

        var result = NewService(box).Delete(box.PathOf("cache"), DeleteMethod.Permanent, dryRun: true);

        Assert.Equal(DeleteOutcome.WouldDelete, result.Outcome);
        Assert.Equal(1_000, result.Bytes);
        Assert.True(Directory.Exists(box.PathOf("cache")));
    }

    [Fact]
    public void Protected_paths_are_refused_before_anything_happens()
    {
        using var box = new Sandbox();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var result = NewService(box).Delete(Path.Join(windows, "explorer.exe"), DeleteMethod.Permanent);

        Assert.Equal(DeleteOutcome.Refused, result.Outcome);
        Assert.Equal(BlockReason.Protected, result.Reason);
        Assert.True(File.Exists(Path.Join(windows, "explorer.exe")));
    }

    [Fact]
    public void Every_attempt_is_written_to_the_log()
    {
        using var box = new Sandbox();
        box.CreateFile("a, b.bin", 2_048);
        var service = NewService(box);

        service.Delete(box.PathOf("a, b.bin"), DeleteMethod.Permanent);
        service.Delete(box.PathOf("missing.bin"), DeleteMethod.Permanent);
        service.Log!.Write("Recycle", "Failed", 0, @"C:\x", "Windows said \"no\"");

        string[] lines = File.ReadAllLines(service.Log.FilePath, Encoding.UTF8);
        Assert.Equal("Time,Action,Outcome,Bytes,Size,Path,Details", lines[0]);
        Assert.Contains(",Delete,Deleted,2048,2.0 KB,\"", lines[1]);
        Assert.EndsWith("a, b.bin\",", lines[1]);
        Assert.Contains(",Delete,AlreadyGone,0,", lines[2]);
        Assert.EndsWith("\"Windows said \"\"no\"\"\"", lines[3]);
    }

    [AdminFact]
    public void Taking_ownership_deletes_what_permissions_lock_out()
    {
        using var box = new Sandbox();
        string folder = box.CreateFolder("system-owned");
        box.CreateFile(@"system-owned\leftover.dll", 300);
        box.OnCleanup(() => { if (FileSystemWalk.Exists(folder)) Ownership.Take(folder); });

        // Leave only SYSTEM with access, like a leftover from an installer that ran as SYSTEM.
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (string path in new[] { box.PathOf(@"system-owned\leftover.dll") })
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        var folderSecurity = new DirectorySecurity();
        folderSecurity.SetAccessRuleProtection(true, false);
        folderSecurity.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(folderSecurity);

        var service = NewService(box);
        var blocked = service.Delete(folder, DeleteMethod.Permanent);
        Assert.Equal(BlockReason.AccessDenied, blocked.Reason);

        var fixedResult = service.TakeOwnershipAndDelete(folder, DeleteMethod.Permanent);

        Assert.Equal(DeleteOutcome.Deleted, fixedResult.Outcome);
        Assert.False(FileSystemWalk.Exists(folder));
    }

    [Fact]
    public void Recycle_bin_refuses_paths_it_cannot_hold_and_says_why()
    {
        using var box = new Sandbox();
        string odd = box.CreateFile(@"name ending in dot.\x.bin", 10);

        var result = NewService(box).Delete(odd, DeleteMethod.RecycleBin);

        Assert.Equal(BlockReason.RecycleBinCantTakeIt, result.Reason);
        Assert.True(FileSystemWalk.Exists(odd));
    }

    [Fact]
    public void Recycling_moves_the_item_to_the_bin()
    {
        using var box = new Sandbox();
        string file = box.CreateFile("recycle-me.txt", 64);

        var result = NewService(box).Delete(file, DeleteMethod.RecycleBin);
        RemoveFromRecycleBin(file);

        Assert.Equal(DeleteOutcome.Recycled, result.Outcome);
        Assert.Equal(64, result.Bytes);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Admin_jobs_survive_the_trip_through_a_command_line()
    {
        var paths = Enumerable.Range(0, 2_000).Select(i => $@"C:\Games\Leftover {i}\with ünïcode").ToList();

        var jobs = AdminJob.Split(AdminAction.TakeOwnershipAndDelete, paths, DeleteMethod.Permanent).ToList();

        Assert.True(jobs.Count > 1);
        Assert.All(jobs, job => Assert.True(job.Encode().Length <= AdminJob.MaxEncodedLength));
        var decoded = jobs.Select(job => AdminJob.Decode(job.Encode())).ToList();
        Assert.Equal(paths, decoded.SelectMany(job => job.Paths));
        Assert.All(decoded, job => Assert.Equal(AdminAction.TakeOwnershipAndDelete, job.Action));
    }

    /// <summary>Removes the test's own file from the Recycle Bin so tests don't fill the real bin.</summary>
    private static void RemoveFromRecycleBin(string originalPath)
    {
        string bin = Path.Join(Path.GetPathRoot(originalPath), "$Recycle.Bin", Elevation.CurrentUserSid);
        byte[] needle = Encoding.Unicode.GetBytes(originalPath);
        foreach (string info in Directory.EnumerateFiles(bin, "$I*"))
        {
            byte[] content = File.ReadAllBytes(info);
            if (content.AsSpan().IndexOf(needle) < 0) continue;
            File.Delete(info);
            File.Delete(Path.Join(bin, "$R" + Path.GetFileName(info)[2..]));
        }
    }
}
