using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace AVAPageStream;

/// <summary>
/// The page view with scroll bars along its right and bottom. The bars cover the page and a stretch of pasteboard
/// round it (most of a view's width or height, so any corner of the page can be brought to the middle), and widen to
/// take in wherever the view has been scrolled or zoomed to.
/// </summary>
public sealed class PageScroller : Grid
{
    private readonly PageView _view;
    private bool _updating;

    public ScrollBar Horizontal { get; } = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false };
    public ScrollBar Vertical { get; } = new() { Orientation = Orientation.Vertical, AllowAutoHide = false };

    public PageScroller(PageView view)
    {
        _view = view;
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions("*,Auto");
        Children.Add(view);
        SetColumn(Vertical, 1);
        SetRow(Horizontal, 1);
        Children.Add(Vertical);
        Children.Add(Horizontal);
        Horizontal.ValueChanged += (_, _) => Scrolled();
        Vertical.ValueChanged += (_, _) => Scrolled();
        view.ViewChanged += Update;
        view.SizeChanged += (_, _) => Update();
        view.SelectionChanged += Update; // a new document, or another page size
    }

    /// <summary>Sets the bars from where the page is in the view.</summary>
    public void Update()
    {
        var w = Math.Max(1, _view.Bounds.Width - PageView.RulerSize);
        var h = Math.Max(1, _view.Bounds.Height - PageView.RulerSize);
        var doc = _view.Editor.Doc.Setup;
        _updating = true;
        try
        {
            // In view pixels from the page's top-left corner: the left (top) edge of what is in view.
            Set(Horizontal, PageView.RulerSize - _view.Origin.X, doc.PageWidth * _view.Scale, w);
            Set(Vertical, PageView.RulerSize - _view.Origin.Y, doc.PageHeight * _view.Scale, h);
        }
        finally
        {
            _updating = false;
        }
    }

    private static void Set(ScrollBar bar, double at, double page, double viewport)
    {
        var pad = Math.Max(200, viewport * 0.8);
        var min = Math.Min(-pad, at);
        var max = Math.Max(page + pad - viewport, at);
        bar.Minimum = min;
        bar.Maximum = Math.Max(min, max);
        bar.ViewportSize = viewport;
        bar.SmallChange = 40;
        bar.LargeChange = viewport * 0.9;
        bar.Value = at;
    }

    private void Scrolled()
    {
        if (_updating) return;
        _view.ScrollTo(new Point(PageView.RulerSize - Horizontal.Value, PageView.RulerSize - Vertical.Value));
    }
}
