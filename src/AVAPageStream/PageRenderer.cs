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
    private sealed record Entry(string Text, Run[] Runs, ParaFormat[] Paras, Area[] Areas, FieldContext Fields, BaselineGrid? Grid, TextLayout Layout);

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
        var number = page ?? Chains.PageOf(doc, story);
        var fields = new FieldContext(number, doc.Pages.Count, number > 0 ? doc.PageLabel(number - 1) : null);
        var key = (storyId, page ?? -1);
        var areas = Chains.Areas(doc, story);
        var grid = story.Table is null ? doc.Setup.Baselines : null; // a table's cells keep to their rows
        if (_layouts.TryGetValue(key, out var e) && e.Text == story.Text && e.Fields == fields && e.Grid == grid &&
            e.Runs.AsSpan().SequenceEqual(story.Runs.ToArray()) && e.Paras.AsSpan().SequenceEqual(story.Paras.ToArray()) &&
            e.Areas.AsSpan().SequenceEqual(areas.ToArray()))
            return e.Layout;
        var layout = TextFlow.Layout(story, areas, measurer, fields, grid);
        LaidOut++;
        _layouts[key] = new Entry(story.Text, [.. story.Runs], [.. story.Paras], [.. areas], fields, grid, layout);
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
    private readonly Dictionary<string, PictureSource?> _pictures = [];

    public PageRenderer(Layouts layouts) => Layouts = layouts;

    public Layouts Layouts { get; }

    /// <summary>Whether to show what only the screen needs: crossed boxes for pictures without an image.</summary>
    public bool Screen { get; init; } = true;

    /// <summary>
    /// Draws a sheet on its paper. Artwork runs out past the page's edge by <paramref name="bleed"/> (the document's
    /// bleed on screen and in a PDF with crop marks; none otherwise) and is cut off there.
    /// </summary>
    public void Draw(DrawingContext dc, Document doc, Sheet sheet, double bleed = 0)
    {
        dc.FillRectangle(Look.Paper, new Rect(0, 0, doc.Setup.PageWidth, doc.Setup.PageHeight));
        using (dc.PushClip(new Rect(-bleed, -bleed, doc.Setup.PageWidth + 2 * bleed, doc.Setup.PageHeight + 2 * bleed)))
        {
            if (sheet is Page page && doc.MasterFor(page) is { } master)
            {
                var number = doc.Pages.IndexOf(page) + 1;
                foreach (var o in doc.Ordered(master.Objects).Where(o => Shown(doc, o))) DrawObject(dc, doc, o, number);
            }
            foreach (var o in doc.Ordered(sheet.Objects).Where(o => Shown(doc, o))) DrawObject(dc, doc, o);
        }
    }

    /// <summary>Whether an object's layer is drawn: shown, and (in a PDF, a picture or a print) printing.</summary>
    private bool Shown(Document doc, PageObject o) => doc.LayerOf(o) is var l && l.Visible && (Screen || l.Print);

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
                    if (line.ArrowEnd) Arrowhead(dc, pen, a, b);
                    if (line.ArrowStart) Arrowhead(dc, pen, b, a);
                }
                break;
            case TextFrame frame:
                if (fill is not null || pen is not null) dc.DrawRectangle(fill, pen, rect);
                DrawStory(dc, doc, frame.StoryId, id => id == frame.Id, page);
                break;
            case TableObject table:
                DrawTable(dc, doc, table, fill, pen, page);
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

    /// <summary>A table: its fill and header rows' fill, each cell's text, then the rules round and between the cells.</summary>
    private void DrawTable(DrawingContext dc, Document doc, TableObject t, IBrush? fill, IPen? pen, int? page)
    {
        if (t.Rows == 0 || t.Columns == 0) return;
        if (fill is not null) dc.FillRectangle(fill, new Rect(t.X, t.Y, t.W, t.H));
        if (t.HeaderRows > 0 && BrushOf(doc, t.HeaderFill) is { } header)
            dc.FillRectangle(header, new Rect(t.X, t.Y, t.W, t.RowHeights.Take(t.HeaderRows).Sum()));
        for (var i = 0; i < t.Cells.Count; i++)
        {
            var cell = t.CellId(i);
            var box = t.CellBox(i);
            using (dc.PushClip(new Rect(box.X, box.Y, box.W, box.H)))
                DrawStory(dc, doc, t.Cells[i], id => id == cell, page);
        }
        if (pen is null) return;
        dc.DrawRectangle(null, pen, new Rect(t.X, t.Y, t.W, t.H));
        var x = t.X;
        foreach (var w in t.ColumnWidths.SkipLast(1)) { x += w; dc.DrawLine(pen, new Point(x, t.Y), new Point(x, t.Y + t.H)); }
        var y = t.Y;
        foreach (var h in t.RowHeights.SkipLast(1)) { y += h; dc.DrawLine(pen, new Point(t.X, y), new Point(t.X + t.W, y)); }
    }

    /// <summary>The lines of a story that are set in the areas <paramref name="inArea"/> picks (a frame, or a table's cell).</summary>
    private void DrawStory(DrawingContext dc, Document doc, string storyId, Func<string, bool> inArea, int? page)
    {
        if (Layouts.Of(doc, storyId, page) is not { } layout) return;
        foreach (var line in layout.Lines)
        {
            if (!inArea(layout.Areas[line.Area].FrameId)) continue;
            foreach (var piece in line.Pieces)
            {
                var given = piece.Format;
                var f = TextFlow.AsSet(given);
                var brush = BrushOf(doc, f.Colour) ?? Brushes.Black;
                var baseline = line.Baseline - f.Shift + piece.Drop;
                if (piece.Tab)
                {
                    if (piece.Leader != '\0') DrawLeader(dc, piece, baseline, brush);
                    continue;
                }
                var text = layout.Display(piece.Start, piece.Length) + (piece.Hyphen ? "-" : "");
                if (f.SmallCaps)
                {
                    foreach (var (part, pf, offset) in TextFlow.SmallCapsPieces(text, f))
                    {
                        var ft = FontSupport.Format(part, pf, brush);
                        var x = piece.X + TextFlow.Measure(Layouts.Measurer, text[..offset], f);
                        dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
                    }
                }
                else if (f.Tracking == 0)
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

    /// <summary>A filled arrowhead at <paramref name="tip"/>, pointing away from <paramref name="from"/>, sized to the stroke.</summary>
    private static void Arrowhead(DrawingContext dc, IPen pen, Pt from, Pt tip)
    {
        var (dx, dy) = (tip.X - from.X, tip.Y - from.Y);
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len == 0) return;
        var size = Math.Max(5, pen.Thickness * 4);
        var (ux, uy) = (dx / len, dy / len);
        var back = new Point(tip.X - ux * size, tip.Y - uy * size);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(tip.X, tip.Y), true);
            c.LineTo(new Point(back.X - uy * size * 0.45, back.Y + ux * size * 0.45));
            c.LineTo(new Point(back.X + uy * size * 0.45, back.Y - ux * size * 0.45));
            c.EndFigure(true);
        }
        dc.DrawGeometry(pen.Brush, null, g);
    }

    /// <summary>A tab's leader: its character repeated across the gap, on an even grid so leaders above each other line up.</summary>
    private void DrawLeader(DrawingContext dc, Piece tab, double baseline, IBrush brush)
    {
        var mark = tab.Leader.ToString();
        var step = Math.Max(1, TextFlow.Measure(Layouts.Measurer, mark + " ", tab.Format));
        var first = Math.Ceiling(tab.X / step) * step;
        var ft = FontSupport.Format(mark, tab.Format, brush);
        for (var x = first; x + step <= tab.X + tab.Width + 0.01; x += step)
            dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
    }

    private void DrawPicture(DrawingContext dc, Document doc, PictureObject picture, Rect frame)
    {
        var source = PictureOf(doc, picture.Image);
        if (source is null || source is PostScriptPicture { Shown: null })
        {
            if (!Screen) return;
            var pen = new Pen(new SolidColorBrush(Color.Parse("#A0A8B8")), 0.75);
            dc.DrawRectangle(new SolidColorBrush(Color.Parse("#F0F2F5")), pen, frame);
            dc.DrawLine(pen, frame.TopLeft, frame.BottomRight);
            dc.DrawLine(pen, frame.TopRight, frame.BottomLeft);
            if (source is PostScriptPicture eps)
            {
                var label = FontSupport.Format("EPS: " + eps.How, new CharFormat(Fonts.Sans, 8), new SolidColorBrush(Color.Parse("#5A6478")));
                label.MaxTextWidth = Math.Max(20, frame.Width - 8);
                using (dc.PushClip(frame)) dc.DrawText(label, new Point(frame.X + 4, frame.Y + 4));
            }
            return;
        }
        var dest = Placement(picture.Fit, source.Size, frame).Translate(new Vector(picture.OffsetX, picture.OffsetY));
        using (dc.PushClip(frame))
        {
            switch (source)
            {
                case RasterPicture r:
                    dc.DrawImage(r.Bitmap, new Rect(0, 0, r.Bitmap.Size.Width, r.Bitmap.Size.Height), dest);
                    break;
                case VectorPicture v:
                    dc.Custom(new SvgDraw(v.Picture, v.Cull, dest, frame));
                    break;
                case PostScriptPicture { Shown: { } shown }:
                    dc.DrawImage(shown, new Rect(0, 0, shown.Size.Width, shown.Size.Height), dest);
                    break;
            }
        }
    }

    /// <summary>How a picture's image was shown: for the status bar.</summary>
    public string? Describe(Document doc, string key) => PictureOf(doc, key) switch
    {
        VectorPicture => "an SVG drawing, kept as vector",
        PostScriptPicture eps => "an EPS picture, shown by " + eps.How,
        RasterPicture r => $"a picture of {r.Bitmap.PixelSize.Width} × {r.Bitmap.PixelSize.Height} pixels",
        _ => null,
    };

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

    private PictureSource? PictureOf(Document doc, string key)
    {
        if (key.Length == 0) return null;
        if (_pictures.TryGetValue(key, out var picture)) return picture;
        picture = doc.Images.Get(key) is { } bytes ? Pictures.Load(bytes) : null;
        _pictures[key] = picture;
        return picture;
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
/// <summary>
/// A page on a sheet larger than itself, for printing to the edge: its artwork out to the bleed, and crop marks in the
/// margin round it showing where to cut.
/// </summary>
internal sealed class MarkedPageVisual : Control
{
    private static readonly Pen Mark = new(Brushes.Black, 0.25);
    private readonly PageRenderer _renderer;
    private readonly Document _doc;
    private readonly Sheet _sheet;

    public MarkedPageVisual(Document doc, Sheet sheet, PageRenderer renderer)
    {
        _doc = doc;
        _sheet = sheet;
        _renderer = renderer;
        (Width, Height) = PrintMarks.Sheet(doc.Setup);
        Measure(new Size(Width, Height));
        Arrange(new Rect(0, 0, Width, Height));
    }

    public override void Render(DrawingContext context)
    {
        var slug = PrintMarks.Slug(_doc.Setup);
        using (context.PushTransform(Matrix.CreateTranslation(slug, slug)))
            _renderer.Draw(context, _doc, _sheet, _doc.Setup.Bleed);
        foreach (var (from, to) in PrintMarks.Lines(_doc.Setup))
            context.DrawLine(Mark, new Point(from.X, from.Y), new Point(to.X, to.Y));
    }
}

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

/// <summary>
/// Several pages side by side on one sheet, for a booklet: each drawn at its own place across (a missing page left
/// as blank paper). One visual for the sheet, since Avalonia draws a visual into a canvas from the canvas's corner.
/// </summary>
internal sealed class SheetVisual : Control
{
    private readonly PageRenderer _renderer;
    private readonly Document _doc;
    private readonly IReadOnlyList<(Sheet? Page, double X)> _pages;

    public SheetVisual(Document doc, IReadOnlyList<(Sheet? Page, double X)> pages, double width, PageRenderer renderer)
    {
        _doc = doc;
        _pages = pages;
        _renderer = renderer;
        Width = width;
        Height = doc.Setup.PageHeight;
        Measure(new Size(Width, Height));
        Arrange(new Rect(0, 0, Width, Height));
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Look.Paper, new Rect(0, 0, Width, Height));
        foreach (var (page, x) in _pages)
        {
            if (page is null) continue;
            using (context.PushTransform(Matrix.CreateTranslation(x, 0)))
                _renderer.Draw(context, _doc, page);
        }
    }
}
