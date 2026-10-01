using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskSweeper.App.ViewModels;

namespace DiskSweeper.App.Views.Pages;

/// <summary>Folder tree: header clicks sort, double-click or arrow keys open folders, Space ticks.</summary>
public partial class FoldersPage : UserControl
{
    public FoldersPage()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: TreeSort sort }) return;
        ViewModel.Tree.SortBy(sort);
        ShowSortArrows();
    }

    private void List_Loaded(object sender, RoutedEventArgs e) => ShowSortArrows();

    /// <summary>Puts ↓ or ↑ after the name of the column the tree is sorted by.</summary>
    private void ShowSortArrows()
    {
        if (TreeList.View is not GridView grid || DataContext is not MainViewModel viewModel) return;
        foreach (var column in grid.Columns)
        {
            if (column.Header is not GridViewColumnHeader { Tag: TreeSort sort } header) continue;
            string arrow = sort == viewModel.Tree.Sort ? (viewModel.Tree.Descending ? "  ↓" : "  ↑") : "";
            header.Content = sort + arrow;
        }
    }

    /// <summary>GridView columns can't stretch, so give the Name column whatever width the others leave.</summary>
    private void List_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TreeList.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != NameColumn).Sum(c => c.ActualWidth);
        NameColumn.Width = Math.Max(220, TreeList.ActualWidth - others - SystemParameters.VerticalScrollBarWidth - 16);
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicks on the tick box or the arrow already did their own thing.
        if (e.OriginalSource is DependencyObject source && IsInsideToggle(source)) return;
        if (ViewModel.Tree.SelectedRow is { CanExpand: true } row) row.IsExpanded = !row.IsExpanded;
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
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
