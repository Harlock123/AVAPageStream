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
    private sealed record Entry(string Text, Run[] Runs, ParaFormat[] Paras, Area[] Areas, FieldContext Fields, BaselineGrid? Grid, string Notes, TextLayout Layout);

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
        Func<string, double, double>? heights = null;
        var notes = "";
        if (story.NoteOf is { } parentId)
        {
            // A footnote: set in the box its story's layout keeps for it, numbered as it numbers it.
            if (Of(doc, parentId, page) is not { } parent || parent.Notes.FirstOrDefault(n => n.Id == storyId) is not { } box) return null;
            areas = [new Area(box.Box, parent.Areas[box.Area].FrameId, 0)];
            fields = fields with { Note = box.Number };
            grid = null;
        }
        else if (story.OnPath is { } pathId)
        {
            // Text on a path is drawn along it (PathText); this layout, one long line, is for moving the caret through it.
            areas = [new Area(new Box(0, 0, 1_000_000, 1_000_000), pathId, 0)];
            grid = null;
        }
        else if (story.Table is null && story.Text.Contains(Fields.Footnote))
        {
            heights = Footnotes.Heights(doc, measurer);
            // The layout depends on its notes' text too.
            notes = string.Join("\u0001", Footnotes.Numbers(story).Keys.Select(id => doc.Stories.TryGetValue(id, out var n)
                ? n.Text + string.Concat(n.Runs) + string.Concat(n.Paras) : ""));
        }
        if (_layouts.TryGetValue(key, out var e) && e.Text == story.Text && e.Fields == fields && e.Grid == grid && e.Notes == notes &&
            e.Runs.AsSpan().SequenceEqual(story.Runs.ToArray()) && e.Paras.AsSpan().SequenceEqual(story.Paras.ToArray()) &&
            e.Areas.AsSpan().SequenceEqual(areas.ToArray()))
            return e.Layout;
        var layout = TextFlow.Layout(story, areas, measurer, fields, grid, heights);
        LaidOut++;
        _layouts[key] = new Entry(story.Text, [.. story.Runs], [.. story.Paras], [.. areas], fields, grid, notes, layout);
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
        // See-through, blended or with a shadow: drawn as a layer of its own.
        using var effects = Effects.Begin(dc, o, c => Colours.Resolve(c, doc.Swatches) is { } v && Colours.TryParse(v, out var rgba) ? new SkiaSharp.SKColor(rgba.R, rgba.G, rgba.B, rgba.A) : null);
        var rect = new Rect(o.X, o.Y, Math.Max(0, o.W), Math.Max(0, o.H));
        var fill = FillOf(doc, o, rect);
        var stroke = o.Stroke is not null && o.StrokeWidth > 0 ? BrushOf(doc, o.Stroke) : null;
        if (stroke is not null && o.StrokeOpacity < 1) stroke = new SolidColorBrush(((ISolidColorBrush)stroke).Color, o.StrokeOpacity);
        var pen = stroke is not null ? new Pen(stroke, o.StrokeWidth) : null;
        // Rounded or cut corners, for the rectangles: its outline.
        var corners = o is RectangleObject or TextFrame or PictureObject && o.Corners is { IsSquare: false } c ? Effects.Outline(rect, c) : null;
        switch (o)
        {
            case RectangleObject r:
                if (corners is not null) dc.DrawGeometry(fill, pen, corners);
                else dc.DrawRectangle(fill, pen, rect, r.CornerRadius, r.CornerRadius);
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
                if (corners is not null && (fill is not null || pen is not null)) dc.DrawGeometry(fill, pen, corners);
                else if (fill is not null || pen is not null) dc.DrawRectangle(fill, pen, rect);
                DrawStory(dc, doc, frame.StoryId, id => id == frame.Id, page);
                DrawNotes(dc, doc, frame, page);
                break;
            case TableObject table:
                DrawTable(dc, doc, table, fill, pen, page);
                break;
            case GroupObject group:
                foreach (var child in group.Children) DrawObject(dc, doc, child, page);
                break;
            case PathObject path:
                if (path.Nodes.Count > 1) dc.DrawGeometry(path.Closed ? fill : null, pen is null ? null : new Pen(pen.Brush, pen.Thickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), PathGeometry(path));
                if (path.StoryId is { } onPath) DrawPathText(dc, doc, path, onPath, page);
                break;
            case PictureObject picture:
                if (corners is not null)
                {
                    // The picture inside its rounded or cut corners.
                    using (dc.PushGeometryClip(corners))
                    {
                        if (fill is not null) dc.FillRectangle(fill, rect);
                        DrawPicture(dc, doc, picture, rect);
                    }
                    if (pen is not null) dc.DrawGeometry(null, pen, corners);
                    break;
                }
                if (fill is not null) dc.FillRectangle(fill, rect);
                DrawPicture(dc, doc, picture, rect);
                if (pen is not null) dc.DrawRectangle(null, pen, rect);
                break;
        }
    }

    /// <summary>
    /// A table frame: its fill and header rows' fill, each cell's text, then the rules round and between the cells. A
    /// frame a table runs on into shows the rows set in it, under its header rows drawn again.
    /// </summary>
    private void DrawTable(DrawingContext dc, Document doc, TableObject frame, IBrush? fill, IPen? pen, int? page)
    {
        if (Tables.Place(doc, frame) is not { } placed) return;
        var t = placed.Head;
        var index = placed.Frames.ToList().FindIndex(f => f.Id == frame.Id);
        var rows = placed.RowsIn(index).ToList();
        if (rows.Count == 0 || t.Columns == 0) return;
        var height = rows.Max(r => r.Y + t.RowHeights[r.Row]);
        if (fill is not null) dc.FillRectangle(fill, new Rect(frame.X, frame.Y, frame.W, height));
        if (t.HeaderRows > 0 && BrushOf(doc, t.HeaderFill) is { } header && rows.Where(r => r.Row < t.HeaderRows).ToList() is { Count: > 0 } heads)
            dc.FillRectangle(header, new Rect(frame.X, frame.Y + heads.Min(r => r.Y), frame.W, heads.Sum(r => t.RowHeights[r.Row])));
        foreach (var (row, y, repeated) in rows)
        {
            var x = frame.X;
            for (var c = 0; c < t.Columns; c++)
            {
                var i = row * t.Columns + c;
                var box = new Rect(x, frame.Y + y, t.ColumnWidths[c], t.RowHeights[row]);
                x += t.ColumnWidths[c];
                using (dc.PushClip(box))
                {
                    if (!repeated)
                    {
                        DrawStory(dc, doc, t.Cells[i], id => id == frame.CellId(i), page);
                        continue;
                    }
                    // A header row again: its cell's text, from where it is set in the first frame.
                    if (placed.CellBox(i) is not { } real) continue;
                    using (dc.PushTransform(Matrix.CreateTranslation(box.X - real.X, box.Y - real.Y)))
                        DrawStory(dc, doc, t.Cells[i], id => id == t.CellId(i), page);
                }
            }
        }
        if (pen is null) return;
        dc.DrawRectangle(null, pen, new Rect(frame.X, frame.Y, frame.W, height));
        var cx = frame.X;
        foreach (var w in t.ColumnWidths.SkipLast(1)) { cx += w; dc.DrawLine(pen, new Point(cx, frame.Y), new Point(cx, frame.Y + height)); }
        foreach (var (_, y, _) in rows.Skip(1)) dc.DrawLine(pen, new Point(frame.X, frame.Y + y), new Point(frame.X + frame.W, frame.Y + y));
    }

    /// <summary>A paragraph's rules: above its first line and below its last, in the area the line is in.</summary>
    private void DrawRules(DrawingContext dc, Document doc, TextLayout layout, Line line)
    {
        var story = layout.Story;
        var para = story.Paras[line.Paragraph];
        if (para.RuleAbove is null && para.RuleBelow is null) return;
        var area = layout.Areas[line.Area].Box;
        var (start, _) = story.ParagraphRange(line.Paragraph);
        void Rule(ParaRule r, double y)
        {
            if (r.Weight <= 0 || BrushOf(doc, r.Colour) is not { } brush) return;
            var left = area.X + para.LeftIndent;
            var right = r.Length switch
            {
                RuleLength.Text => line.Pieces.Count > 0 ? line.Pieces.Max(p => p.X + p.Width) : left,
                RuleLength.Width => left + r.Width,
                _ => area.Right - para.RightIndent,
            };
            if (r.Length == RuleLength.Text && line.Pieces.Count > 0) left = line.Pieces.Min(p => p.X);
            dc.DrawLine(new Pen(brush, r.Weight), new Point(left, y), new Point(Math.Max(left, right), y));
        }
        if (para.RuleAbove is { } above && line.Start == start) Rule(above, line.Baseline - line.Ascent - above.Offset - above.Weight / 2);
        if (para.RuleBelow is { } below && line.LastOfParagraph) Rule(below, line.Baseline + line.Descent + below.Offset + below.Weight / 2);
    }

    /// <summary>The brushes used while drawing text for outlines, and the colour (as the document has it) each is for.</summary>
    private Dictionary<IBrush, string?>? _outlining;

    /// <summary>
    /// Draws only a frame's characters (or those of them from <paramref name="start"/> to <paramref name="end"/>), with
    /// no rules or leaders, for turning into outlines. Gives the colour each brush drawn with stands for.
    /// </summary>
    internal Dictionary<IBrush, string?> DrawCharacters(DrawingContext dc, Document doc, TextFrame frame, int? page, int start, int end)
    {
        _outlining = new Dictionary<IBrush, string?>(ReferenceEqualityComparer.Instance);
        try
        {
            DrawStory(dc, doc, frame.StoryId, id => id == frame.Id, page, (start, end));
            return _outlining;
        }
        finally
        {
            _outlining = null;
        }
    }

    /// <summary>The lines of a story that are set in the areas <paramref name="inArea"/> picks (a frame, or a table's cell).</summary>
    private void DrawStory(DrawingContext dc, Document doc, string storyId, Func<string, bool> inArea, int? page, (int Start, int End)? only = null)
    {
        if (Layouts.Of(doc, storyId, page) is not { } layout) return;
        foreach (var line in layout.Lines)
        {
            if (!inArea(layout.Areas[line.Area].FrameId)) continue;
            if (only is null) DrawRules(dc, doc, layout, line);
            foreach (var piece in line.Pieces)
            {
                var given = piece.Format;
                var f = TextFlow.AsSet(given);
                var brush = BrushOf(doc, f.Colour) ?? Brushes.Black;
                _outlining?.TryAdd(brush, f.Colour ?? Colours.Black);
                var baseline = line.Baseline - f.Shift + piece.Drop;
                if (piece.Tab)
                {
                    if (piece.Leader != '\0' && only is null) DrawLeader(dc, piece, baseline, brush);
                    continue;
                }
                var text = layout.Display(piece.Start, piece.Length) + (piece.Hyphen ? "-" : "");
                var x0 = piece.X;
                if (only is var (from, to))
                {
                    if (piece.Start >= to || piece.Start + piece.Length <= from) continue;
                    // Part of the piece: its characters from where the range starts, placed where they were set.
                    if (text.Length == piece.Length + (piece.Hyphen ? 1 : 0) && (from > piece.Start || to < piece.Start + piece.Length))
                    {
                        var a = Math.Max(0, from - piece.Start);
                        var b = Math.Min(piece.Length, to - piece.Start);
                        x0 += TextFlow.Measure(Layouts.Measurer, text[..a], f);
                        text = text[a..b] + (piece.Hyphen && b == piece.Length ? "-" : "");
                    }
                }
                if (f.SmallCaps)
                {
                    foreach (var (part, pf, offset) in TextFlow.SmallCapsPieces(text, f))
                    {
                        var ft = FontSupport.Format(part, pf, brush);
                        var x = x0 + TextFlow.Measure(Layouts.Measurer, text[..offset], f);
                        dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
                    }
                }
                else if (f.Tracking == 0)
                {
                    var ft = FontSupport.Format(text, f, brush);
                    dc.DrawText(ft, new Point(x0, baseline - ft.Baseline));
                }
                else
                {
                    for (var i = 0; i < text.Length; i++)
                    {
                        if (text[i] == ' ') continue;
                        var ft = FontSupport.Format(text[i].ToString(), f, brush);
                        var x = x0 + TextFlow.Measure(Layouts.Measurer, text[..i], f);
                        dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
                    }
                }
                if (f.Underline)
                {
                    var visible = text.TrimEnd();
                    var w = TextFlow.Measure(Layouts.Measurer, visible, f);
                    var y = baseline + f.Size * 0.12;
                    dc.DrawLine(new Pen(brush, Math.Max(0.5, f.Size * 0.06)), new Point(x0, y), new Point(x0 + w, y));
                }
            }
        }
    }

    /// <summary>A frame's footnotes, at the foot of each of its columns that has any, under a short rule.</summary>
    private void DrawNotes(DrawingContext dc, Document doc, TextFrame frame, int? page)
    {
        if (Layouts.Of(doc, frame.StoryId, page) is not { Notes.Count: > 0 } layout) return;
        var rule = new Pen(Brushes.Black, 0.5);
        foreach (var group in layout.Notes.GroupBy(n => n.Area))
        {
            var area = layout.Areas[group.Key];
            if (area.FrameId != frame.Id) continue;
            var top = group.Min(n => n.Box.Y) - Footnotes.Gap / 2;
            dc.DrawLine(rule, new Point(area.Box.X, top), new Point(area.Box.X + Math.Min(72, area.Box.W / 3), top));
            foreach (var note in group) DrawStory(dc, doc, note.Id, _ => true, page);
        }
    }

    /// <summary>The text set along a path, each character turned to the path where it sits.</summary>
    private void DrawPathText(DrawingContext dc, Document doc, PathObject path, string storyId, int? page)
    {
        if (!doc.Stories.TryGetValue(storyId, out var story)) return;
        var number = page ?? doc.Pages.FindIndex(p => p.All.Any(o => o.Id == path.Id)) + 1;
        var fields = new FieldContext(number, doc.Pages.Count, number > 0 ? doc.PageLabel(number - 1) : null);
        var (glyphs, _) = PathText.Place(story, PathText.Points(path), Layouts.Measurer, path.TextOffset, fields);
        foreach (var g in glyphs)
        {
            var f = TextFlow.AsSet(g.Format);
            var brush = BrushOf(doc, f.Colour) ?? Brushes.Black;
            using (dc.PushTransform(Matrix.CreateRotation(g.Angle * Math.PI / 180) * Matrix.CreateTranslation(g.At.X, g.At.Y)))
            {
                if (f.SmallCaps)
                    foreach (var (part, pf, offset) in TextFlow.SmallCapsPieces(g.Text, f))
                    {
                        var ft = FontSupport.Format(part, pf, brush);
                        dc.DrawText(ft, new Point(TextFlow.Measure(Layouts.Measurer, g.Text[..offset], f), -f.Shift - ft.Baseline));
                    }
                else
                {
                    var ft = FontSupport.Format(g.Text, f, brush);
                    dc.DrawText(ft, new Point(0, -f.Shift - ft.Baseline));
                }
                if (f.Underline) dc.DrawLine(new Pen(brush, Math.Max(0.5, f.Size * 0.06)), new Point(0, f.Size * 0.12), new Point(g.Width, f.Size * 0.12));
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
        var source = PictureOf(doc, picture.Shown);
        if (source is null || source is PostScriptPicture { Shown: null })
        {
            if (!Screen) return;
            var pen = new Pen(new SolidColorBrush(Color.Parse("#A0A8B8")), 0.75);
            dc.DrawRectangle(new SolidColorBrush(Color.Parse("#F0F2F5")), pen, frame);
            dc.DrawLine(pen, frame.TopLeft, frame.BottomRight);
            dc.DrawLine(pen, frame.TopRight, frame.BottomLeft);
            if (source is PostScriptPicture eps)
            {
                var label = FontSupport.Format(eps.Kind + ": " + eps.How, new CharFormat(Fonts.Sans, 8), new SolidColorBrush(Color.Parse("#5A6478")));
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
        VectorPicture v => v.What + ", kept as vector",
        PostScriptPicture { Kind: "PDF" } pdf => "a PDF page, not drawn: " + pdf.How,
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
        c.SetFillRule(path.NonZero ? FillRule.NonZero : FillRule.EvenOdd);
        if (nodes.Count == 0) return g;
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
        var first = 0;
        for (var end = 1; end <= nodes.Count; end++)
        {
            if (end < nodes.Count && !nodes[end].Start) continue;
            c.BeginFigure(new Point(nodes[first].X, nodes[first].Y), close);
            for (var i = first + 1; i < end; i++) Segment(nodes[i - 1], nodes[i]);
            if (close && end - first >= 2) Segment(nodes[end - 1], nodes[first]);
            c.EndFigure(close);
            first = end;
        }
        return g;
    }

    /// <summary>Where an image of <paramref name="size"/> goes in its frame for a fit.</summary>
    public static Rect Placement(Fit fit, Size size, Rect frame)
    {
        var b = Fitting.Place(fit, size.Width, size.Height, new Box(frame.X, frame.Y, frame.Width, frame.Height));
        return new Rect(b.X, b.Y, b.W, b.H);
    }

    /// <summary>A bitmap picture's pixels and its size in points (null for a drawing, which prints sharp at any size).</summary>
    public Core.PictureSize? PixelsOf(Document doc, string key) => PictureOf(doc, key) switch
    {
        RasterPicture r => new Core.PictureSize(r.Bitmap.PixelSize.Width, r.Bitmap.PixelSize.Height, r.Size.Width, r.Size.Height),
        // An EPS with pictures in it: as many pixels as its coarsest picture would have over the whole of it.
        VectorPicture { PixelsPerPoint: { } ppp } v => new Core.PictureSize((int)Math.Round(ppp * v.Size.Width), (int)Math.Round(ppp * v.Size.Height), v.Size.Width, v.Size.Height),
        PostScriptPicture { Shown: null } => null,
        _ => null,
    };

    /// <param name="key">The image's key, with "#" and a page after it for a PDF's page other than its first.</param>
    private PictureSource? PictureOf(Document doc, string key)
    {
        if (key.Length == 0) return null;
        if (_pictures.TryGetValue(key, out var picture)) return picture;
        var hash = key.IndexOf('#');
        var page = hash > 0 && int.TryParse(key[(hash + 1)..], out var n) ? n : 1;
        picture = doc.Images.Get(hash > 0 ? key[..hash] : key) is { } bytes ? Pictures.Load(bytes, page) : null;
        _pictures[key] = picture;
        return picture;
    }

    /// <summary>An object's fill: its gradient across its frame, or its colour, at its fill's opacity.</summary>
    private IBrush? FillOf(Document doc, PageObject o, Rect rect)
    {
        if (o.Gradient is { Stops.Count: >= 2 } g)
            return Effects.Brush(g, rect, c => Colours.Resolve(c, doc.Swatches) is { } v && Colours.TryParse(v, out var rgba) ? Color.FromArgb(rgba.A, rgba.R, rgba.G, rgba.B) : null, o.FillOpacity);
        var fill = BrushOf(doc, o.Fill);
        return fill is ISolidColorBrush solid && o.FillOpacity < 1 ? new SolidColorBrush(solid.Color, o.FillOpacity) : fill;
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
