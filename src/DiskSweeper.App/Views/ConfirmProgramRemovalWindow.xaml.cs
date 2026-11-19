using System.Windows;
using DiskSweeper.App.ViewModels;

namespace DiskSweeper.App.Views;

/// <summary>Confirms a program removal: uninstaller, leftovers, Recycle Bin or permanent, and a dry run.</summary>
public partial class ConfirmProgramRemovalWindow : Window
{
    public ConfirmProgramRemovalWindow(ConfirmProgramRemovalViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
