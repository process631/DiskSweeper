using System.Windows;

namespace DiskSweeper.App;

/// <summary>The window shell: sidebar, page header, pages and selection bar. All behaviour lives in MainViewModel and the pages.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
