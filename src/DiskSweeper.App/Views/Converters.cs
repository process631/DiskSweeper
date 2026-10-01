using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskSweeper.Core.Analysis;

namespace DiskSweeper.App.Views;

/// <summary>Visible when the bound enum value matches the converter parameter (e.g. which page is open).</summary>
public sealed class EnumVisibilityConverter : IValueConverter
{
    public static readonly EnumVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Adds up the sizes of the items in a list group, for the group header ("Junk and caches · 21.3 GB").</summary>
public sealed class GroupTotalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is System.Collections.IEnumerable items
            ? Core.ByteSize.Format(items.OfType<ViewModels.FindingRowViewModel>().Sum(row => row.Size))
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when any of the bound booleans is true (e.g. scanning or deleting).</summary>
public sealed class AnyTrueToVisibilityConverter : IMultiValueConverter
{
    public static readonly AnyTrueToVisibilityConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Any(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>1 − x, for the empty part of a share bar.</summary>
public sealed class OneMinusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double share ? Math.Max(0, 1 - share) : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
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
