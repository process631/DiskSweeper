using System.Windows;
using DiskSweeper.App.Infrastructure;
using DiskSweeper.App.ViewModels;

namespace DiskSweeper.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A hidden elevated copy started by ElevatedRunner: do the one job and leave, no window.
        int job = Array.IndexOf(e.Args, ElevatedRunner.JobArgument);
        int result = Array.IndexOf(e.Args, ElevatedRunner.ResultArgument);
        if (job >= 0 && result >= 0 && job + 1 < e.Args.Length && result + 1 < e.Args.Length)
        {
            Shutdown(ElevatedRunner.RunHelper(e.Args[job + 1], e.Args[result + 1]));
            return;
        }

        base.OnStartup(e);

        int pathIndex = Array.IndexOf(e.Args, "--path");
        string? startPath = pathIndex >= 0 && pathIndex + 1 < e.Args.Length ? e.Args[pathIndex + 1] : null;

        var settings = AppSettings.Load();
        var viewModel = new MainViewModel(settings, startPath);
        var window = new MainWindow { DataContext = viewModel };
        window.Closed += (_, _) => settings.Save();
        MainWindow = window;
        window.Show();

        _ = StartAsync(viewModel, startPath is not null);
    }

    private static async Task StartAsync(MainViewModel viewModel, bool scanNow)
    {
        await viewModel.LoadLastScanAsync();
        if (scanNow || !viewModel.HasScan) viewModel.ScanCommand.Execute(null);
    }
}
