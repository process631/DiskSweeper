using System.Windows.Controls;
using DiskSweeper.App.ViewModels;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.App.Views.Pages;

/// <summary>Treemap: click shows details, double-click zooms into a folder.</summary>
public partial class TreemapPage : UserControl
{
    public TreemapPage()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void Treemap_NodeActivated(object? sender, FileNode node) => ViewModel.Treemap.Root = node;

    private void Treemap_NodeClicked(object? sender, FileNode node) => ViewModel.Details.Node = node;
}
