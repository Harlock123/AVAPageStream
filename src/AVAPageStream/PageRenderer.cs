using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AVAPageStream.Core;
using Page = AVAPageStream.Core.Page;

namespace AVAPageStream;

/// <summary>
/// The layouts of a document's stories, each kept until something it depends on changes: the story's text, its
/// formats, or the frames (and columns) it flows through. A change elsewhere, or to another story, lays nothing out
/// again, so typing in one story costs only that story.
/// </summary>
internal sealed class Layouts(ITextMeasurer measurer)
{
    private sealed record Entry(string Text, Run[] Runs, ParaFormat[] Paras, Area[] Areas, FieldContext Fields, TextLayout Layout);

    // By story, and by the page a master's story is drawn on: its page-number fields differ from page to page.
    private readonly Dictionary<(string, int), Entry> _layouts = [];

    public ITextMeasurer Measurer => measurer;

    /// <summary>How many times a story has been laid out (for tests).</summary>
    public int LaidOut { get; private set; }

    public void Clear() => _layouts.Clear();

    /// <summary>
    /// A story's layout. <paramref name="page"/> is the page (from 1) a master's story is being drawn under; left out,
    /// fields count from the page the story's first frame is on.
    /// </summary>
    public TextLayout? Of(Document doc, string storyId, int? page = null)
    {
        if (!doc.Stories.TryGetValue(storyId, out var story)) return null;
        var fields = new FieldContext(page ?? Chains.PageOf(doc, story), doc.Pages.Count);
        var key = (storyId, page ?? -1);
        var areas = Chains.Areas(doc, story);
        if (_layouts.TryGetValue(key, out var e) && e.Text == story.Text && e.Fields == fields &&
            e.Runs.AsSpan().SequenceEqual(story.Runs.ToArray()) && e.Paras.AsSpan().SequenceEqual(story.Paras.ToArray()) &&
            e.Areas.AsSpan().SequenceEqual(areas.ToArray()))
            return e.Layout;
        var layout = TextFlow.Layout(story, areas, measurer, fields);
        LaidOut++;
        _layouts[key] = new Entry(story.Text, [.. story.Runs], [.. story.Paras], [.. areas], fields, layout);
        return layout;
    }
}

/// <summary>
/// Draws a page, with its master under it, in page points. The screen, PDF and PNG all draw through here, so what is
/// exported is what was seen. Guides, frame edges and selection are drawn by the page view on top, never here.
/// </summary>
internal sealed class PageRenderer
{
    private readonly Dictionary<string, IBrush> _brushes = [];
    private readonly Dictionary<string, Bitmap?> _bitmaps = [];

    public PageRenderer(Layouts layouts) => Layouts = layouts;

    public Layouts Layouts { get; }

    /// <summary>Whether to show what only the screen needs: crossed boxes for pictures without an image.</summary>
    public bool Screen { get; init; } = true;

    public void Draw(DrawingContext dc, Document doc, Sheet sheet)
    {
        dc.FillRectangle(Look.Paper, new Rect(0, 0, doc.Setup.PageWidth, doc.Setup.PageHeight));
        using (dc.PushClip(new Rect(0, 0, doc.Setup.PageWidth, doc.Setup.PageHeight)))
        {
            if (sheet is Page page && doc.MasterFor(page) is { } master)
            {
                var number = doc.Pages.IndexOf(page) + 1;
                foreach (var o in master.Objects) DrawObject(dc, doc, o, number);
            }
            foreach (var o in sheet.Objects) DrawObject(dc, doc, o);
        }
    }

    /// <summary>Draws one object; <paramref name="page"/> is the page a master's object is drawn under.</summary>
    public void DrawObject(DrawingContext dc, Document doc, PageObject o, int? page = null)
    {
        using var turned = o.Rotation != 0 ? dc.PushTransform(Turn(o, 1, new Vector(0, 0))) : (DrawingContext.PushedState?)null;
        var rect = new Rect(o.X, o.Y, Math.Max(0, o.W), Math.Max(0, o.H));
        var fill = BrushOf(doc, o.Fill);
        var pen = o.Stroke is not null && o.StrokeWidth > 0 && BrushOf(doc, o.Stroke) is { } sb ? new Pen(sb, o.StrokeWidth) : null;
        switch (o)
        {
            case RectangleObject r:
                dc.DrawRectangle(fill, pen, rect, r.CornerRadius, r.CornerRadius);
                break;
            case EllipseObject:
                dc.DrawEllipse(fill, pen, rect.Center, rect.Width / 2, rect.Height / 2);
                break;
            case LineObject line:
                if (pen is not null)
                {
                    var (a, b) = line.Ends;
                    dc.DrawLine(new Pen(pen.Brush, pen.Thickness, lineCap: PenLineCap.Round), new Point(a.X, a.Y), new Point(b.X, b.Y));
                }
                break;
            case TextFrame frame:
                if (fill is not null || pen is not null) dc.DrawRectangle(fill, pen, rect);
                DrawText(dc, doc, frame, page);
                break;
            case GroupObject group:
                foreach (var child in group.Children) DrawObject(dc, doc, child, page);
                break;
            case PathObject path:
                if (path.Nodes.Count > 1) dc.DrawGeometry(path.Closed ? fill : null, pen is null ? null : new Pen(pen.Brush, pen.Thickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), PathGeometry(path));
                break;
            case PictureObject picture:
                if (fill is not null) dc.FillRectangle(fill, rect);
                DrawPicture(dc, doc, picture, rect);
                if (pen is not null) dc.DrawRectangle(null, pen, rect);
                break;
        }
    }

    private void DrawText(DrawingContext dc, Document doc, TextFrame frame, int? page)
    {
        if (Layouts.Of(doc, frame.StoryId, page) is not { } layout) return;
        foreach (var line in layout.Lines)
        {
            if (layout.Areas[line.Area].FrameId != frame.Id) continue;
            foreach (var piece in line.Pieces)
            {
                var f = piece.Format;
                var brush = BrushOf(doc, f.Colour) ?? Brushes.Black;
                var text = layout.Display(piece.Start, piece.Length).Replace('\t', ' ');
                var baseline = line.Baseline - f.Shift;
                if (f.Tracking == 0)
                {
                    var ft = FontSupport.Format(text, f, brush);
                    dc.DrawText(ft, new Point(piece.X, baseline - ft.Baseline));
                }
                else
                {
                    for (var i = 0; i < text.Length; i++)
                    {
                        if (text[i] == ' ') continue;
                        var ft = FontSupport.Format(text[i].ToString(), f, brush);
                        var x = piece.X + TextFlow.Measure(Layouts.Measurer, text[..i], f);
                        dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
                    }
                }
                if (f.Underline)
                {
                    var visible = text.TrimEnd();
                    var w = TextFlow.Measure(Layouts.Measurer, visible, f);
                    var y = baseline + f.Size * 0.12;
                    dc.DrawLine(new Pen(brush, Math.Max(0.5, f.Size * 0.06)), new Point(piece.X, y), new Point(piece.X + w, y));
                }
            }
        }
    }

    private void DrawPicture(DrawingContext dc, Document doc, PictureObject picture, Rect frame)
    {
        if (BitmapOf(doc, picture.Image) is not { } bitmap)
        {
            if (!Screen) return;
            var pen = new Pen(new SolidColorBrush(Color.Parse("#A0A8B8")), 0.75);
            dc.DrawRectangle(new SolidColorBrush(Color.Parse("#F0F2F5")), pen, frame);
            dc.DrawLine(pen, frame.TopLeft, frame.BottomRight);
            dc.DrawLine(pen, frame.TopRight, frame.BottomLeft);
            return;
        }
        // The image's size in points: its pixels at the resolution it says it was made for.
        var size = new Size(bitmap.PixelSize.Width * 72 / bitmap.Dpi.X, bitmap.PixelSize.Height * 72 / bitmap.Dpi.Y);
        var dest = Placement(picture.Fit, size, frame).Translate(new Vector(picture.OffsetX, picture.OffsetY));
        using (dc.PushClip(frame))
            dc.DrawImage(bitmap, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), dest);
    }

    /// <summary>
    /// The transform that turns an object about its centre, for drawing at <paramref name="scale"/> view units a point
    /// with the page's corner at <paramref name="origin"/> (1 and nothing for drawing in page points).
    /// </summary>
    public static Matrix Turn(PageObject o, double scale, Vector origin)
    {
        var c = Shapes.Centre(o);
        var (cx, cy) = (origin.X + c.X * scale, origin.Y + c.Y * scale);
        return Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(o.Rotation * Math.PI / 180) * Matrix.CreateTranslation(cx, cy);
    }

    /// <summary>A path's outline: straight segments between corners, and cubic Béziers where a point has handles.</summary>
    public static StreamGeometry PathGeometry(PathObject path, IReadOnlyList<PathNode>? nodes = null, bool? closed = null)
    {
        nodes ??= path.Nodes;
        var close = closed ?? path.Closed;
        var g = new StreamGeometry();
        using var c = g.Open();
        if (nodes.Count == 0) return g;
        c.BeginFigure(new Point(nodes[0].X, nodes[0].Y), close);
        void Segment(PathNode a, PathNode b)
        {
            if (a.Out is null && b.In is null) c.LineTo(new Point(b.X, b.Y));
            else
            {
                var p1 = a.Out ?? a.At;
                var p2 = b.In ?? b.At;
                c.CubicBezierTo(new Point(p1.X, p1.Y), new Point(p2.X, p2.Y), new Point(b.X, b.Y));
            }
        }
        for (var i = 1; i < nodes.Count; i++) Segment(nodes[i - 1], nodes[i]);
        if (close && nodes.Count > 2) Segment(nodes[^1], nodes[0]);
        c.EndFigure(close);
        return g;
    }

    /// <summary>Where an image of <paramref name="size"/> goes in its frame for a fit.</summary>
    public static Rect Placement(Fit fit, Size size, Rect frame)
    {
        if (size.Width <= 0 || size.Height <= 0) return frame;
        switch (fit)
        {
            case Fit.Stretch: return frame;
            case Fit.Actual: return new Rect(frame.X, frame.Y, size.Width, size.Height);
            default:
                var sx = frame.Width / size.Width;
                var sy = frame.Height / size.Height;
                var s = fit == Fit.Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
                var w = size.Width * s;
                var h = size.Height * s;
                return new Rect(frame.X + (frame.Width - w) / 2, frame.Y + (frame.Height - h) / 2, w, h);
        }
    }

    private Bitmap? BitmapOf(Document doc, string key)
    {
        if (key.Length == 0) return null;
        if (_bitmaps.TryGetValue(key, out var bitmap)) return bitmap;
        try
        {
            bitmap = doc.Images.Get(key) is { } bytes ? new Bitmap(new MemoryStream(bytes)) : null;
        }
        catch (Exception)
        {
            bitmap = null; // a damaged image shows as a missing one
        }
        _bitmaps[key] = bitmap;
        return bitmap;
    }

    /// <summary>The brush for a colour: a swatch's own colour for a reference to one.</summary>
    public IBrush? BrushOf(Document doc, string? colour) => BrushOf(Colours.Resolve(colour, doc.Swatches));

    public IBrush? BrushOf(string? colour)
    {
        if (colour is null) return null;
        if (_brushes.TryGetValue(colour, out var brush)) return brush;
        brush = Colours.TryParse(colour, out var c) ? new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)) : null;
        if (brush is not null) _brushes[colour] = brush;
        return brush;
    }
}

/// <summary>One page drawn at its own size in points, for exporting: Avalonia renders it into a bitmap or a PDF page.</summary>
internal sealed class PageVisual : Control
{
    private readonly PageRenderer _renderer;
    private readonly Document _doc;
    private readonly Sheet _sheet;

    public PageVisual(Document doc, Sheet sheet, PageRenderer renderer)
    {
        _doc = doc;
        _sheet = sheet;
        _renderer = renderer;
        Width = doc.Setup.PageWidth;
        Height = doc.Setup.PageHeight;
        Measure(new Size(Width, Height));
        Arrange(new Rect(0, 0, Width, Height));
    }

    public override void Render(DrawingContext context) => _renderer.Draw(context, _doc, _sheet);
}
