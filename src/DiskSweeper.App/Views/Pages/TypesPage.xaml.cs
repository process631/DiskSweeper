using System.Windows.Controls;
using System.Windows.Input;
using DiskSweeper.App.ViewModels;

namespace DiskSweeper.App.Views.Pages;

public partial class TypesPage : UserControl
{
    public TypesPage()
    {
        InitializeComponent();
    }

    private void ExtensionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExtensionGrid.SelectedItem is ExtensionRow row) ((MainViewModel)DataContext).ShowExtensionCommand.Execute(row);
    }
}
