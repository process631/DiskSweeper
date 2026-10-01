using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskSweeper.Core;
using DiskSweeper.Core.Analysis;
using DiskSweeper.Core.Scanning;

namespace DiskSweeper.App.Views;

/// <summary>
/// Draws a folder as nested rectangles sized by disk use, colored by file type. Hover shows what a block
/// is, double-click zooms into a folder, right-click opens the context menu for the block under the mouse.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(FileNode), typeof(TreemapView), new FrameworkPropertyMetadata(null, OnLayoutInputChanged));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(TreemapView), new FrameworkPropertyMetadata(0, OnLayoutInputChanged));

    public static readonly DependencyProperty HoveredNodeProperty = DependencyProperty.Register(
        nameof(HoveredNode), typeof(FileNode), typeof(TreemapView), new FrameworkPropertyMetadata(null));

    /// <summary>Colors per file kind, chosen to stay readable on both light and dark backgrounds.</summary>
    public static readonly IReadOnlyDictionary<FileKind, Color> KindColors = new Dictionary<FileKind, Color>
    {
        [FileKind.Video] = Color.FromRgb(0xE1, 0x57, 0x59),
        [FileKind.Audio] = Color.FromRgb(0xF2, 0x8E, 0x2B),
        [FileKind.Images] = Color.FromRgb(0xED, 0xC9, 0x48),
        [FileKind.Archives] = Color.FromRgb(0xB0, 0x7A, 0xA1),
        [FileKind.DiskImages] = Color.FromRgb(0x9C, 0x75, 0x5F),
        [FileKind.Programs] = Color.FromRgb(0x4E, 0x79, 0xA7),
        [FileKind.GameData] = Color.FromRgb(0x59, 0xA1, 0x4F),
        [FileKind.Documents] = Color.FromRgb(0x76, 0xB7, 0xB2),
        [FileKind.Code] = Color.FromRgb(0xFF, 0x9D, 0xA7),
        [FileKind.System] = Color.FromRgb(0x9A, 0x93, 0x8F),
        [FileKind.Other] = Color.FromRgb(0x7F, 0x8C, 0x99),
    };

    private const double Header = 16;
    private const int MaxDepth = 4;
    private static readonly Dictionary<FileKind, Brush> KindBrushes = KindColors.ToDictionary(p => p.Key, p => (Brush)Freeze(new SolidColorBrush(p.Value)));
    private static readonly Brush FolderFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));
    private static readonly Brush LinkFill = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)));
    private static readonly Pen Border = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x10, 0x10)), 0.6));
    private static readonly Pen HoverPen = Freeze(new Pen(Brushes.White, 2.5));
    private static readonly Pen HoverOutline = Freeze(new Pen(Brushes.Black, 4.5));
    private static readonly Typeface LabelFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private List<TreemapRect> _rects = [];
    private Size _laidOutFor;
    private TreemapRect? _hovered;

    /// <summary>Double-click: zoom into this folder.</summary>
    public event EventHandler<FileNode>? NodeActivated;

    /// <summary>Single click: show this block's details.</summary>
    public event EventHandler<FileNode>? NodeClicked;

    public FileNode? Root
    {
        get => (FileNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    /// <summary>The block under the mouse; context-menu commands use it as their target. Set by the control itself.</summary>
    public FileNode? HoveredNode
    {
        get => (FileNode?)GetValue(HoveredNodeProperty);
        set => SetValue(HoveredNodeProperty, value);
    }

    private static void OnLayoutInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TreemapView)d;
        view._rects = [];
        view._laidOutFor = default;
        view._hovered = null;
        view.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (Root is null || size.Width < 10 || size.Height < 10) return;

        if (_laidOutFor != size)
        {
            _rects = Treemap.Layout(Root, size.Width, size.Height, MaxDepth, minSide: 2, padding: 2, header: Header);
            _laidOutFor = size;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var rect in _rects)
        {
            var box = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            var fill = rect.Node.IsLink ? LinkFill : rect.Node.IsDirectory ? FolderFill : KindBrushes[FileTypes.KindOf(rect.Node.Extension)];
            dc.DrawRectangle(fill, Border, box);
            DrawLabel(dc, rect, box, pixelsPerDip);
        }

        if (_hovered is TreemapRect hovered)
        {
            var box = new Rect(hovered.X, hovered.Y, hovered.Width, hovered.Height);
            dc.DrawRectangle(null, HoverOutline, box);
            dc.DrawRectangle(null, HoverPen, box);
        }
    }

    private void DrawLabel(DrawingContext dc, TreemapRect rect, Rect box, double pixelsPerDip)
    {
        if (rect.Width < 46 || rect.Height < 15) return;

        bool folder = rect.Node.IsDirectory;
        bool hasHeader = rect.Height > Header * 3 && rect.Width > Header * 4; // Same rule as Treemap.Layout.
        if (folder && !hasHeader && rect.Node.Children.Count > 0 && rect.Depth + 1 < MaxDepth) return;
        Brush textBrush = folder ? (Brush)(TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.Gray) : TextOn(FileTypes.KindOf(rect.Node.Extension));
        string text = $"{rect.Node.Name}  {ByteSize.Format(rect.Node.Size)}";
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFont, 11, textBrush, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, rect.Width - 6),
            MaxTextHeight = folder ? Header : Math.Max(1, rect.Height - 4),
            Trimming = TextTrimming.CharacterEllipsis,
        };

        dc.PushClip(new RectangleGeometry(box));
        dc.DrawText(formatted, new Point(rect.X + 3, rect.Y + 1));
        dc.Pop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        TreemapRect? hit = null;
        for (int i = _rects.Count - 1; i >= 0; i--)
        {
            // Later rectangles are deeper, so the first hit from the end is the innermost block.
            if (_rects[i].Contains(point.X, point.Y))
            {
                hit = _rects[i];
                break;
            }
        }

        if (hit?.Node == _hovered?.Node) return;
        _hovered = hit;
        HoveredNode = hit?.Node;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (ContextMenu?.IsOpen == true) return;
        _hovered = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 1 && _hovered is TreemapRect clicked) NodeClicked?.Invoke(this, clicked.Node);
        if (e.ClickCount == 2 && _hovered is TreemapRect hit)
        {
            // Double-clicking a file zooms into the folder it's in.
            var target = hit.Node.IsDirectory && !hit.Node.IsLink ? hit.Node : hit.Node.Parent;
            if (target is not null && target != Root) NodeActivated?.Invoke(this, target);
        }
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        if (HoveredNode is null) e.Handled = true;
        base.OnContextMenuOpening(e);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    private static Brush TextOn(FileKind kind)
    {
        var c = KindColors[kind];
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        return luminance > 0.6 ? Brushes.Black : Brushes.White;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
