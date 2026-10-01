using DiskSweeper.Core.Scanning;

namespace DiskSweeper.Core.Analysis;

public readonly record struct TreemapRect(FileNode Node, double X, double Y, double Width, double Height, int Depth)
{
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing &amp; van Wijk): each file or folder becomes a rectangle whose
/// area matches its size, kept as close to square as possible so big items are easy to spot and click.
/// </summary>
public static class Treemap
{
    /// <summary>
    /// Lays out <paramref name="root"/>'s contents in a width × height area, nesting folders up to
    /// <paramref name="maxDepth"/> levels. Rectangles smaller than <paramref name="minSide"/> pixels on
    /// either side are left out (their space still counts in their parent). Folders big enough get a
    /// <paramref name="header"/>-high strip at the top for their name.
    /// </summary>
    public static List<TreemapRect> Layout(FileNode root, double width, double height, int maxDepth = 3, double minSide = 3, double padding = 2, double header = 0, int maxRects = 25_000)
    {
        var rects = new List<TreemapRect>();
        if (width <= 0 || height <= 0 || root.Size <= 0) return rects;

        var queue = new Queue<(FileNode Folder, double X, double Y, double W, double H, int Depth)>();
        queue.Enqueue((root, 0, 0, width, height, 0));
        while (queue.Count > 0 && rects.Count < maxRects)
        {
            var (folder, x, y, w, h, depth) = queue.Dequeue();
            var items = folder.Children.Where(c => c.Size > 0).ToList();
            if (items.Count == 0) continue;

            foreach (var rect in Squarify(items, x, y, w, h, depth))
            {
                if (rect.Width < minSide || rect.Height < minSide) continue;
                rects.Add(rect);

                double top = header > 0 && rect.Height > header * 3 && rect.Width > header * 4 ? header : 0;
                bool canNest = rect.Node.IsDirectory && depth + 1 < maxDepth
                    && rect.Width > padding * 2 + minSide && rect.Height > padding * 2 + top + minSide;
                if (canNest)
                    queue.Enqueue((rect.Node, rect.X + padding, rect.Y + padding + top, rect.Width - padding * 2, rect.Height - padding * 2 - top, depth + 1));
            }
        }
        return rects;
    }

    /// <summary>Lays out one level: <paramref name="items"/> must be sorted largest first.</summary>
    public static List<TreemapRect> Squarify(IReadOnlyList<FileNode> items, double x, double y, double width, double height, int depth)
    {
        var result = new List<TreemapRect>(items.Count);
        double total = items.Sum(i => (double)i.Size);
        if (total <= 0) return result;

        double scale = width * height / total;
        var row = new List<(FileNode Node, double Area)>();
        double rowArea = 0;

        int index = 0;
        while (index < items.Count)
        {
            var item = items[index];
            double area = item.Size * scale;
            double side = Math.Min(width, height);

            if (row.Count == 0 || Worst(row, rowArea + area, area, side) <= Worst(row, rowArea, 0, side))
            {
                row.Add((item, area));
                rowArea += area;
                index++;
                continue;
            }

            PlaceRow(row, rowArea, ref x, ref y, ref width, ref height, depth, result);
            row.Clear();
            rowArea = 0;
        }
        if (row.Count > 0) PlaceRow(row, rowArea, ref x, ref y, ref width, ref height, depth, result);
        return result;
    }

    /// <summary>The worst (largest) aspect ratio in a row, optionally with one extra item of <paramref name="extra"/> area.</summary>
    private static double Worst(List<(FileNode Node, double Area)> row, double sum, double extra, double side)
    {
        if (sum <= 0) return double.MaxValue;
        double max = extra;
        double min = extra > 0 ? extra : double.MaxValue;
        foreach (var (_, area) in row)
        {
            max = Math.Max(max, area);
            min = Math.Min(min, area);
        }
        double sideSquared = side * side;
        double sumSquared = sum * sum;
        return Math.Max(sideSquared * max / sumSquared, sumSquared / (sideSquared * min));
    }

    private static void PlaceRow(List<(FileNode Node, double Area)> row, double rowArea, ref double x, ref double y, ref double width, ref double height, int depth, List<TreemapRect> result)
    {
        if (width >= height)
        {
            // Column along the left edge.
            double columnWidth = height > 0 ? rowArea / height : 0;
            double offset = y;
            foreach (var (node, area) in row)
            {
                double itemHeight = columnWidth > 0 ? area / columnWidth : 0;
                result.Add(new TreemapRect(node, x, offset, columnWidth, itemHeight, depth));
                offset += itemHeight;
            }
            x += columnWidth;
            width -= columnWidth;
        }
        else
        {
            // Row along the top edge.
            double rowHeight = width > 0 ? rowArea / width : 0;
            double offset = x;
            foreach (var (node, area) in row)
            {
                double itemWidth = rowHeight > 0 ? area / rowHeight : 0;
                result.Add(new TreemapRect(node, offset, y, itemWidth, rowHeight, depth));
                offset += itemWidth;
            }
            y += rowHeight;
            height -= rowHeight;
        }
    }
}
