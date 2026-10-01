using System.Windows;
using DiskSweeper.App.ViewModels;

namespace DiskSweeper.App.Views;

/// <summary>
/// The last check before anything is deleted: what goes, how (Recycle Bin or permanent), what's skipped as
/// protected, and an extra tick for big or permanent deletes. Cancel is the default button.
/// </summary>
public partial class ConfirmDeleteWindow : Window
{
    public ConfirmDeleteWindow(ConfirmDeleteViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
