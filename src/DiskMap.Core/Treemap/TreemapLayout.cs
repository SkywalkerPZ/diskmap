using System.Globalization;
using DiskMap.Core.Scanning;

namespace DiskMap.Core.Treemap;

public readonly record struct RectD(double X, double Y, double W, double H);

/// <summary>Node is null for an aggregated "N small items" block.</summary>
public sealed record TreemapRect(FileNode? Node, string Label, long Size, RectD Bounds, int Depth);

public static class TreemapLayout
{
    /// <summary>Flat list; depth 0 = root's direct children. Children of a rect are laid out inside it, inset 1 px.</summary>
    public static IReadOnlyList<TreemapRect> Layout(FileNode root, RectD bounds, int maxDepth, double minArea)
    {
        var result = new List<TreemapRect>();
        Place(root, bounds, 0, maxDepth, minArea, result);
        return result;
    }

    private sealed record Item(FileNode? Node, string Label, long Size);

    private static void Place(FileNode parent, RectD rect, int depth, int maxDepth, double minArea, List<TreemapRect> output)
    {
        if (rect.W <= 0 || rect.H <= 0) return;
        var kids = parent.Children.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
        long total = kids.Sum(c => c.Size);
        if (total <= 0) return;

        double areaPerByte = rect.W * rect.H / total;
        var items = kids.Where(c => c.Size * areaPerByte >= minArea).Select(c => new Item(c, c.Name, c.Size)).ToList();
        var small = kids.Where(c => c.Size * areaPerByte < minArea).ToList();
        if (small.Count == 1) items.Add(new Item(small[0], small[0].Name, small[0].Size));
        else if (small.Count > 1)
            items.Add(new Item(null, $"{small.Count.ToString("N0", CultureInfo.InvariantCulture)} small items", small.Sum(c => c.Size)));

        foreach (var (item, r) in Squarify(items, rect, areaPerByte))
        {
            output.Add(new TreemapRect(item.Node, item.Label, item.Size, r, depth));
            if (item.Node is { IsDirectory: true } dir && depth < maxDepth)
                Place(dir, new RectD(r.X + 1, r.Y + 1, r.W - 2, r.H - 2), depth + 1, maxDepth, minArea, output);
        }
    }

    // Bruls, Huizing, van Wijk. Items arrive sorted descending, with any aggregate last.
    private static IEnumerable<(Item, RectD)> Squarify(List<Item> items, RectD rect, double areaPerByte)
    {
        int i = 0;
        while (i < items.Count)
        {
            double side = Math.Min(rect.W, rect.H);
            int j = i;
            double sum = 0, worst = double.MaxValue;
            while (j < items.Count)
            {
                double s2 = sum + items[j].Size * areaPerByte;
                double w = Worst(items, i, j, s2, side, areaPerByte);
                if (j > i && w > worst) break;
                sum = s2; worst = w; j++;
            }
            bool wide = rect.W >= rect.H; // row runs along the shorter side
            double thick = last(j) ? (wide ? rect.W : rect.H) : sum / side;
            double pos = wide ? rect.Y : rect.X;
            for (int k = i; k < j; k++)
            {
                double len = items[k].Size * areaPerByte / thick;
                if (k == j - 1) len = (wide ? rect.Y + rect.H : rect.X + rect.W) - pos; // absorb rounding
                yield return (items[k], wide ? new RectD(rect.X, pos, thick, len) : new RectD(pos, rect.Y, len, thick));
                pos += len;
            }
            rect = wide ? new RectD(rect.X + thick, rect.Y, rect.W - thick, rect.H)
                        : new RectD(rect.X, rect.Y + thick, rect.W, rect.H - thick);
            i = j;
        }
        bool last(int end) => end >= items.Count;
    }

    private static double Worst(List<Item> items, int from, int to, double sum, double side, double areaPerByte)
    {
        double max = items[from].Size * areaPerByte, min = items[to].Size * areaPerByte;
        if (min > max) (min, max) = (max, min);
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }
}
