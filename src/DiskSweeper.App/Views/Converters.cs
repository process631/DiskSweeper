using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskSweeper.Core.Analysis;

namespace DiskSweeper.App.Views;

/// <summary>Binds an enum to a TabControl's SelectedIndex (enum value N ↔ tab N).</summary>
public sealed class EnumIndexConverter : IValueConverter
{
    public static readonly EnumIndexConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => System.Convert.ToInt32(value, culture);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index && index >= 0 ? Enum.ToObject(typeof(ViewModels.MainTab), index) : Binding.DoNothing;
}

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a Smart finds confidence label to its badge color.</summary>
public sealed class BadgeBrushConverter : IValueConverter
{
    private static readonly Brush Safe = Frozen(Color.FromRgb(0x2E, 0x8B, 0x57));
    private static readonly Brush Review = Frozen(Color.FromRgb(0xC2, 0x7C, 0x0E));
    private static readonly Brush Windows = Frozen(Color.FromRgb(0x2B, 0x6C, 0xB0));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "Safe" => Safe,
        "Review" => Review,
        _ => Windows,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Maps a file kind to its treemap color, so lists and the treemap legend match.</summary>
public sealed class KindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value switch
        {
            FileKind k => k,
            string s when Enum.TryParse<FileKind>(s, out var parsed) => parsed,
            _ => FileKind.Other,
        };
        var brush = new SolidColorBrush(TreemapView.KindColors[kind]);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
