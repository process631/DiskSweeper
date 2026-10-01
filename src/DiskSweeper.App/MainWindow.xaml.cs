using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskSweeper.App.ViewModels;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.App;

/// <summary>Only view plumbing lives here (header clicks, keys, double-clicks); everything else is in MainViewModel.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void TreeHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: TreeSort sort }) ViewModel.Tree.SortBy(sort);
    }

    private void TreeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the tick box or the arrow, which handle themselves.
        if (e.OriginalSource is DependencyObject source && IsInsideToggle(source)) return;
        if (ViewModel.Tree.SelectedRow is { CanExpand: true } row) row.IsExpanded = !row.IsExpanded;
    }

    private void TreeList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel.Tree.SelectedRow is not TreeRowViewModel row) return;
        switch (e.Key)
        {
            case Key.Right when row.CanExpand && !row.IsExpanded:
                row.IsExpanded = true;
                e.Handled = true;
                break;
            case Key.Left when row.IsExpanded:
                row.IsExpanded = false;
                e.Handled = true;
                break;
            case Key.Space when row.CanSelect:
                row.IsChecked = !row.IsChecked;
                e.Handled = true;
                break;
        }
    }

    private void ExtensionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExtensionGrid.SelectedItem is ExtensionRow row) ViewModel.ShowExtensionCommand.Execute(row);
    }

    private void Treemap_NodeActivated(object? sender, FileNode node) => ViewModel.Treemap.Root = node;

    private static bool IsInsideToggle(DependencyObject start)
    {
        for (var current = start; current is not null and not ListViewItem;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ToggleButton) return true; // CheckBox is a ToggleButton too.
        }
        return false;
    }
}
