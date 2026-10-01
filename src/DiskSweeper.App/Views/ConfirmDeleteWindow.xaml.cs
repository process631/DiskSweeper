using System.Windows;
using DiskSweeper.App.ViewModels;
using DiskSweeper.Core.Deletion;

namespace DiskSweeper.App.Views;

/// <summary>
/// The last check before anything is deleted: lists every item with its size and warnings, shows what will
/// be skipped as protected, and for big or permanent deletes requires an extra tick. Cancel is the default button.
/// </summary>
public partial class ConfirmDeleteWindow : Window
{
    private readonly DeletePlan _plan;

    public ConfirmDeleteWindow(DeletePlan plan)
    {
        InitializeComponent();
        _plan = plan;
        DataContext = plan;
        ConfirmButton.Content = plan.DryRun ? "Run dry run"
            : plan.Method == DeleteMethod.RecycleBin ? "Move to Recycle Bin"
            : "Delete permanently";
        ConfirmButton.IsEnabled = !plan.NeedsExtraConfirmation;
    }

    private void ExtraConfirm_Changed(object sender, RoutedEventArgs e) =>
        ConfirmButton.IsEnabled = !_plan.NeedsExtraConfirmation || ExtraConfirm.IsChecked == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
