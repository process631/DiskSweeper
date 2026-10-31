using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskSweeper.Core;
using DiskSweeper.Core.Deletion;

namespace DiskSweeper.App.Infrastructure;

/// <summary>
/// Runs admin-only fixes (take ownership, delete on restart) without restarting the app: it starts a hidden
/// copy of DiskSweeper with "Run as administrator", which shows the usual Windows permission prompt, does
/// the one job and exits. When DiskSweeper already runs as administrator, the work happens in-process.
/// </summary>
public static class ElevatedRunner
{
    public const string JobArgument = "--admin-job";
    public const string ResultArgument = "--result";

    public static async Task<IReadOnlyList<DeleteResult>> RunAsync(AdminAction action, IReadOnlyList<string> paths, DeleteMethod method, DeleteService service)
    {
        if (Elevation.IsElevated)
            return await Task.Run(() => new AdminJob(action, paths, method).Run(service));

        var results = new List<DeleteResult>();
        foreach (var job in AdminJob.Split(action, paths, method))
        {
            string resultFile = Path.Join(Path.GetTempPath(), $"DiskSweeper-{Guid.NewGuid():N}.json");
            CreateResultFile(resultFile);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { JobArgument, job.Encode(), ResultArgument, resultFile },
            };

            try
            {
                using var process = Process.Start(start)!;
                await process.WaitForExitAsync();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: "No" at the Windows permission prompt.
                results.AddRange(job.Paths.Select(path => new DeleteResult
                {
                    Path = path, Outcome = DeleteOutcome.Failed, Reason = BlockReason.AccessDenied,
                    Message = "Cancelled at the Windows permission prompt.",
                }));
                continue;
            }

            if (File.Exists(resultFile))
            {
                results.AddRange(AdminJob.ReadResults(resultFile));
                File.Delete(resultFile);
            }
            else
            {
                results.AddRange(job.Paths.Select(path => new DeleteResult
                {
                    Path = path, Outcome = DeleteOutcome.Failed, Reason = BlockReason.Other, Message = "The administrator helper didn't report back.",
                }));
            }
        }
        return results;
    }

    /// <summary>Entry point of the hidden elevated copy. Returns the process exit code.</summary>
    public static int RunHelper(string encodedJob, string resultFile)
    {
        // Refuse before doing any work. A crafted command line must not delete files or write outside temp.
        if (!AdminJob.IsAcceptableResultFile(resultFile)) return 1;

        try
        {
            var job = AdminJob.Decode(encodedJob);
            var results = job.Run(new DeleteService(new DeletionLog()));
            AdminJob.WriteResults(resultFile, results);
            return 0;
        }
        catch (Exception ex)
        {
            TryWriteError(resultFile, ex);
            return 1;
        }
    }

    /// <summary>Creates the result file first so its name is fixed before the permission prompt, and only this user can replace it.</summary>
    private static void CreateResultFile(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null) return;

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var rights = FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete;
            security.AddAccessRule(new FileSystemAccessRule(user, rights, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), rights, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), rights, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IdentityNotMappedException or PlatformNotSupportedException or InvalidOperationException or IOException)
        {
            // The path check still keeps the write inside this temp file.
        }
    }

    private static void TryWriteError(string resultFile, Exception ex)
    {
        if (!AdminJob.IsAcceptableResultFile(resultFile)) return;

        string full = Path.GetFullPath(resultFile);
        string errorFile = Path.Join(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".error.txt");
        try
        {
            if (File.Exists(errorFile) && (File.GetAttributes(errorFile) & FileAttributes.ReparsePoint) != 0) return;
            File.WriteAllText(errorFile, ex.ToString());
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Starts a new DiskSweeper as administrator. Returns false if the permission prompt was declined.</summary>
    public static bool RestartElevated(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", ArgumentList = { "--path", path } });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }
}
