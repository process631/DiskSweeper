using System.Windows;

namespace DiskSweeper.App.Infrastructure;

public static class Dialogs
{
    public static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static void ShowError(Exception ex) =>
        MessageBox.Show(Owner!, ex.Message, "DiskSweeper", MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(string message) =>
        MessageBox.Show(Owner!, message, "DiskSweeper", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message, string title = "DiskSweeper") =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
