using System.Windows;
using DiskSweeper.App.ViewModels;

namespace DiskSweeper.App.Views;

/// <summary>Shows what happened to each item, with buttons for the fixes that apply to anything that refused.</summary>
public partial class DeleteResultsWindow : Window
{
    public DeleteResultsWindow(DeleteResultsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
