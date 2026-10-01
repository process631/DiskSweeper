using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        try
        {
            var job = AdminJob.Decode(encodedJob);
            var results = job.Run(new DeleteService(new DeletionLog()));
            AdminJob.WriteResults(resultFile, results);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(resultFile + ".error.txt", ex.ToString());
            }
            catch (IOException)
            {
            }
            return 1;
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
