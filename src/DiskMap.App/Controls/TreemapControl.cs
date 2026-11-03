using DiskMap.Core.Scanning;
using DiskMap.Core.Treemap;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace DiskMap.App.Controls;

/// <summary>Win2D treemap of <see cref="Root"/>. Click reports the node (NodeTapped), double-click asks to zoom in.</summary>
public sealed partial class TreemapControl : UserControl
{
    private const int MaxDepth = 3;
    private const double MinArea = 36;

    // Muted, mid-tone colours that read on light and dark backgrounds; one per top-level folder.
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0x5B, 0x8D, 0xB8), Color.FromArgb(255, 0x6A, 0xA5, 0x8E), Color.FromArgb(255, 0xC2, 0x9F, 0x5E),
        Color.FromArgb(255, 0x93, 0x7D, 0xB5), Color.FromArgb(255, 0x5F, 0xA3, 0xA8), Color.FromArgb(255, 0xB0, 0x8A, 0x78),
        Color.FromArgb(255, 0x7F, 0x9C, 0x62), Color.FromArgb(255, 0xA9, 0x86, 0xA2),
    ];

    private readonly CanvasControl _canvas = new();
    private IReadOnlyList<TreemapRect> _rects = [];
    private FileNode? _laidOutRoot;
    private Size _laidOutSize;
    private bool _dirty;

    public TreemapControl()
    {
        Content = _canvas;
        IsTabStop = false;
        AutomationProperties.SetName(this, Str.Get("MapName"));
        _canvas.Draw += OnDraw;
        _canvas.Tapped += OnTapped;
        _canvas.DoubleTapped += OnDoubleTapped;
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        // The page is cached for the app's lifetime, so the canvas is never torn down (no RemoveFromVisualTree).
    }

    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(object), typeof(TreemapControl), new PropertyMetadata(null, (d, _) => ((TreemapControl)d)._canvas.Invalidate()));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(object), typeof(TreemapControl), new PropertyMetadata(null, (d, _) => ((TreemapControl)d)._canvas.Invalidate()));

    // Typed object: XAML type info cannot describe FileNode (it has required members).
    public object? Root { get => GetValue(RootProperty); set => SetValue(RootProperty, value); }
    public object? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    /// <summary>The node under a click, or null for empty space; the owner decides what becomes Selected.</summary>
    public event EventHandler<FileNode?>? NodeTapped;
    public event EventHandler<FileNode>? ZoomRequested;

    /// <summary>Re-lays out after the tree under <see cref="Root"/> changed in place.</summary>
    public void Refresh()
    {
        _dirty = true;
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var size = sender.Size;
        if (_dirty || Root != _laidOutRoot || size != _laidOutSize)
        {
            _rects = Root is not FileNode root ? [] : TreemapLayout.Layout(root, new RectD(0, 0, size.Width, size.Height), MaxDepth, MinArea);
            _laidOutRoot = Root as FileNode;
            _laidOutSize = size;
            _dirty = false;
        }

        var ds = args.DrawingSession;
        var dark = ActualTheme == ElementTheme.Dark;
        var grey = dark ? Color.FromArgb(255, 0x5C, 0x5C, 0x5C) : Color.FromArgb(255, 0xA8, 0xA8, 0xA8);
        var edge = Color.FromArgb(dark ? (byte)110 : (byte)70, 0, 0, 0);
        using var font = new CanvasTextFormat
        {
            FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
        };

        int top = -1;
        Rect? selected = null;
        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            if (r.Depth == 0) top++;
            var rect = new Rect(r.Bounds.X, r.Bounds.Y, r.Bounds.W, r.Bounds.H);
            var fill = r.Node is null ? grey : Shade(Palette[top % Palette.Length], r.Depth, dark);
            ds.FillRectangle(rect, fill);
            ds.DrawRectangle(rect, edge, 0.5f);
            if (r.Node is not null && ReferenceEquals(r.Node, Selected)) selected = rect;

            // Only rects with no children drawn inside show a label (a parent's label would be painted over).
            var leaf = i + 1 == _rects.Count || _rects[i + 1].Depth <= r.Depth;
            if (leaf && rect.Width >= 44 && rect.Height >= 18)
            {
                var text = Luminance(fill) > 0.55 ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A) : Colors.White;
                var label = $"{r.Label}  {SizeFormat.Format(r.Size)}";
                ds.DrawText(label, new Rect(rect.X + 4, rect.Y + 2, rect.Width - 8, 16), text, font);
            }
        }

        if (selected is { } s)
        {
            var outline = dark ? Colors.White : Colors.Black;
            ds.DrawRectangle(new Rect(s.X + 1, s.Y + 1, Math.Max(0, s.Width - 2), Math.Max(0, s.Height - 2)), outline, 2.5f);
        }
    }

    private static Color Shade(Color c, int depth, bool dark)
    {
        // nested rects get lighter; the dark theme starts from a dimmer base
        double baseMix = dark ? -0.2 : 0, t = baseMix + 0.13 * depth;
        byte Mix(byte v) => (byte)Math.Clamp(t >= 0 ? v + (255 - v) * t : v * (1 + t), 0, 255);
        return Color.FromArgb(255, Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    /// <summary>The deepest rect under the point that stands for a real node (aggregates fall through to their parent).</summary>
    private FileNode? HitTest(Point p)
    {
        for (int i = _rects.Count - 1; i >= 0; i--)
        {
            var b = _rects[i].Bounds;
            if (_rects[i].Node is { } n && p.X >= b.X && p.X < b.X + b.W && p.Y >= b.Y && p.Y < b.Y + b.H) return n;
        }
        return null;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e) => NodeTapped?.Invoke(this, HitTest(e.GetPosition(_canvas)));

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (HitTest(e.GetPosition(_canvas)) is { } n) ZoomRequested?.Invoke(this, n);
    }
}
