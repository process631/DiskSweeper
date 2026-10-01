using System.Windows;
using System.Windows.Controls;

namespace DiskSweeper.App.Views;

/// <summary>
/// Lays children out side by side, each as wide as its share of the total <see cref="WeightProperty"/>.
/// Used for stacked bars like "what's using space". In an ItemsControl, set the weight on the item
/// container through ItemContainerStyle.
/// </summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetWeight(DependencyObject element) => (double)element.GetValue(WeightProperty);

    public static void SetWeight(DependencyObject element, double value) => element.SetValue(WeightProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double total = InternalChildren.Cast<UIElement>().Sum(WeightOf);
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            double width = total > 0 ? finalSize.Width * WeightOf(child) / total : 0;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }

    private static double WeightOf(UIElement child) => Math.Max(0, GetWeight(child));
}
