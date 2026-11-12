using Avalonia.Media;

namespace AVAPageStream;

/// <summary>
/// The colours of the window: dark slate around the page, with a warm vermilion for what is selected, and the
/// traditional magenta and violet of margin and column guides.
/// </summary>
internal static class Look
{
    public static readonly IBrush Window = Brush("#16181D");
    public static readonly IBrush Bar = Brush("#1D2027");
    public static readonly IBrush Pasteboard = Brush("#2B2F38");
    public static readonly IBrush Border = Brush("#323744");
    public static readonly IBrush Heading = Brush("#8A93A3");
    public static readonly IBrush Text = Brush("#DCE1EA");
    public static readonly IBrush Faint = Brush("#5E6676");
    public static readonly IBrush Accent = Brush("#FF6A3D");
    public static readonly IBrush Error = Brush("#FF5C5C");

    public static readonly IBrush Paper = Brushes.White;
    public static readonly IBrush PageShadow = Brush("#60000000");
    public static readonly IBrush Ruler = Brush("#20242C");
    public static readonly IBrush RulerTick = Brush("#7D8696");
    public static readonly IBrush RulerMark = Brush("#FF6A3D");

    public static readonly IPen MarginGuide = new Pen(Brush("#C040C0"), 1);
    public static readonly IPen ColumnGuide = new Pen(Brush("#8060E0"), 1);
    public static readonly IPen BaselineGuide = new Pen(Brush("#5000A8E8"), 1);
    public static readonly IPen BleedGuide = new Pen(Brush("#E04040"), 1, new DashStyle([4, 3], 0));
    public static readonly IPen RulerGuide = new Pen(Brush("#00A8E8"), 1);
    public static readonly IPen FrameEdge = new Pen(Brush("#9AA3B5"), 1, new DashStyle([3, 3], 0));
    public static readonly IPen Selection = new Pen(Brush("#FF6A3D"), 1);
    public static readonly IPen Chain = new Pen(Brush("#FF6A3D"), 1.5);
    public static readonly IPen Marquee = new Pen(Brush("#FF6A3D"), 1, new DashStyle([4, 3], 0));
    public static readonly IBrush Handle = Brush("#FF6A3D");
    public static readonly IBrush TextSelection = Brush("#5A3D8BFF");
    public static readonly IBrush Caret = Brushes.Black;
    public static readonly IBrush Overset = Brush("#E02020");
    public static readonly IBrush GridLine = Brush("#2A4FA8E0");
    public static readonly IBrush LinkFrom = Brush("#30FF6A3D");

    private static SolidColorBrush Brush(string colour) => new(Color.Parse(colour));
}
