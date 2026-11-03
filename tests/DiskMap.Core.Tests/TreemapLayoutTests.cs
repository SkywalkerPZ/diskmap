using DiskMap.Core.Scanning;
using DiskMap.Core.Treemap;

namespace DiskMap.Core.Tests;

public class TreemapLayoutTests
{
    private static FileNode Dir(string name, params FileNode[] kids)
    {
        var d = new FileNode { Name = name, IsDirectory = true };
        foreach (var k in kids) { k.Parent = d; d.Children.Add(k); d.Size += k.Size; }
        return d;
    }

    private static FileNode File(string name, long size) => new() { Name = name, Size = size };

    private static readonly RectD Bounds = new(0, 0, 200, 100);

    [Fact]
    public void Areas_AreProportionalToSize()
    {
        var root = Dir("r", File("a", 60), File("b", 30), File("c", 10));
        var rects = TreemapLayout.Layout(root, Bounds, 0, 0);
        Assert.Equal(3, rects.Count);
        double total = Bounds.W * Bounds.H;
        foreach (var r in rects)
        {
            double expected = r.Size / 100.0 * total;
            Assert.InRange(r.Bounds.W * r.Bounds.H, expected * 0.99, expected * 1.01);
        }
    }

    [Fact]
    public void Rects_StayInsideBoundsAndDontOverlap()
    {
        var sub = Dir("sub", File("x", 20), File("y", 15), File("z", 5));
        var root = Dir("r", File("a", 40), File("b", 25), sub, File("c", 10));
        var rects = TreemapLayout.Layout(root, Bounds, 1, 0);
        const double e = 1e-6;
        foreach (var r in rects)
        {
            Assert.True(r.Bounds.X >= -e && r.Bounds.Y >= -e);
            Assert.True(r.Bounds.X + r.Bounds.W <= Bounds.W + e && r.Bounds.Y + r.Bounds.H <= Bounds.H + e);
        }
        var top = rects.Where(r => r.Depth == 0).ToList();
        for (int i = 0; i < top.Count; i++)
            for (int j = i + 1; j < top.Count; j++)
            {
                var a = top[i].Bounds; var b = top[j].Bounds;
                bool overlap = a.X < b.X + b.W - e && b.X < a.X + a.W - e && a.Y < b.Y + b.H - e && b.Y < a.Y + a.H - e;
                Assert.False(overlap);
            }
    }

    [Fact]
    public void SmallChildren_AreAggregated()
    {
        var root = Dir("r", Enumerable.Range(0, 1000).Select(i => File($"f{i}", 1)).ToArray());
        var rects = TreemapLayout.Layout(root, new RectD(0, 0, 10, 10), 0, 1);
        var only = Assert.Single(rects);
        Assert.Null(only.Node);
        Assert.Equal("1,000 small items", only.Label);
        Assert.Equal(1000, only.Size);
    }

    [Fact]
    public void ZeroSizeChildren_AreOmitted()
    {
        var root = Dir("r", File("a", 10), File("empty", 0));
        var rects = TreemapLayout.Layout(root, Bounds, 0, 0);
        Assert.Equal("a", Assert.Single(rects).Label);
    }

    [Fact]
    public void Depth_IsLimitedByMaxDepth()
    {
        var deep = Dir("deep", File("q", 5));
        var sub = Dir("sub", File("x", 20), deep);
        var root = Dir("r", sub, File("a", 30));
        var rects = TreemapLayout.Layout(root, Bounds, 1, 0);
        Assert.Contains(rects, r => r.Depth == 1);
        Assert.DoesNotContain(rects, r => r.Depth > 1);
        Assert.Equal(0, TreemapLayout.Layout(root, Bounds, 0, 0).Max(r => r.Depth));
    }
}
