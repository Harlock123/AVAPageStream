using Avalonia;
using Avalonia.Media;
using AVAPageStream.Core;
using SkiaSharp;

namespace AVAPageStream;

/// <summary>
/// Text turned into paths: a frame's characters drawn as the page draws them (into a recording), and each glyph of
/// each run replaced by its outline from the font the run was shaped with, so the paths sit exactly where the text did.
/// </summary>
internal static class Outlines
{
    /// <summary>
    /// The outlines of a frame's text (or of its characters from <paramref name="start"/> to <paramref name="end"/>):
    /// one compound path for each colour, in page points, turned as the frame is. Empty if there is nothing to outline.
    /// </summary>
    public static List<PathObject> Of(PageRenderer renderer, Document doc, TextFrame frame, int start = 0, int end = int.MaxValue)
    {
        var recording = new DrawingGroup();
        Dictionary<IBrush, string?> colours;
        using (var dc = recording.Open())
            colours = renderer.DrawCharacters(dc, doc, frame, null, start, end);
        var parts = new Dictionary<string, List<PathNode>>();
        void Add(IBrush? brush, IEnumerable<PathNode> nodes)
        {
            var colour = brush is not null && colours.TryGetValue(brush, out var c) ? c ?? Colours.Black : Colours.Black;
            if (!parts.TryGetValue(colour, out var list)) parts[colour] = list = [];
            list.AddRange(nodes);
        }
        Walk(recording, Matrix.Identity, Add);
        var paths = new List<PathObject>();
        foreach (var (colour, nodes) in parts)
        {
            if (nodes.Count == 0) continue;
            var turned = frame.Rotation == 0 ? nodes : nodes.Select(n => n.Moved(p => Shapes.ToPage(frame, p))).ToList();
            var path = new PathObject { Nodes = [.. turned], Closed = true, NonZero = true, Fill = colour, Stroke = null, Layer = frame.Layer };
            path.Fit();
            paths.Add(path);
        }
        return paths;
    }

    private static void Walk(Drawing drawing, Matrix m, Action<IBrush?, IEnumerable<PathNode>> add)
    {
        switch (drawing)
        {
            case DrawingGroup g:
                var inner = g.Transform is { } t ? t.Value * m : m;
                foreach (var child in g.Children) Walk(child, inner, add);
                break;
            case GlyphRunDrawing { GlyphRun: { } run } gd:
                add(gd.Foreground, Glyphs(run, m));
                break;
            case GeometryDrawing { Geometry: { Bounds.Height: < 1e-6 } line, Pen: { } pen }:
                // An underline (drawn level, as a line): a bar as thick as its pen.
                var half = pen.Thickness / 2;
                var (a, b) = (line.Bounds.TopLeft.Transform(m), line.Bounds.TopRight.Transform(m));
                add(pen.Brush, [new PathNode(a.X, a.Y - half, Start: true), new PathNode(b.X, b.Y - half), new PathNode(b.X, b.Y + half), new PathNode(a.X, a.Y + half)]);
                break;
        }
    }

    private static readonly Dictionary<GlyphTypeface, SKTypeface?> Typefaces = [];

    /// <summary>The font file a run was shaped with, as Skia reads it (so the glyph numbers are the same).</summary>
    private static SKTypeface? Typeface(GlyphTypeface face)
    {
        if (Typefaces.TryGetValue(face, out var known)) return known;
        SKTypeface? typeface = null;
        if (face.PlatformTypeface.TryGetStream(out var stream))
            using (stream)
            {
                var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                typeface = SKTypeface.FromStream(ms);
            }
        return Typefaces[face] = typeface;
    }

    /// <summary>A run's glyphs as path parts: each contour a part, quadratic curves made cubic.</summary>
    private static IEnumerable<PathNode> Glyphs(GlyphRun run, Matrix m)
    {
        if (Typeface(run.GlyphTypeface) is not { } typeface) yield break;
        using var font = new SKFont(typeface, (float)run.FontRenderingEmSize);
        if (run.GlyphTypeface.FontSimulations.HasFlag(FontSimulations.Bold)) font.Embolden = true;
        if (run.GlyphTypeface.FontSimulations.HasFlag(FontSimulations.Oblique)) font.SkewX = -0.2f;
        var x = run.BaselineOrigin.X;
        var y = run.BaselineOrigin.Y;
        foreach (var g in run.GlyphInfos)
        {
            using var glyph = font.GetGlyphPath(g.GlyphIndex);
            var at = new Point(x + g.GlyphOffset.X, y + g.GlyphOffset.Y);
            x += g.GlyphAdvance;
            if (glyph is null) continue;
            foreach (var n in Nodes(glyph, p => new Point(at.X + p.X, at.Y + p.Y).Transform(m))) yield return n;
        }
    }

    /// <summary>A Skia path as nodes: each contour starting a part, its closing point folded into its first.</summary>
    internal static List<PathNode> Nodes(SKPath path, Func<SKPoint, Point> map)
    {
        var all = new List<PathNode>();
        var part = new List<PathNode>();
        void Finish()
        {
            if (part.Count > 1 && Near(part[^1], part[0]))
            {
                // The contour ends where it began: one point, the last one's handle in kept.
                part[0] = part[0] with { InX = part[^1].InX, InY = part[^1].InY };
                part.RemoveAt(part.Count - 1);
            }
            if (part.Count >= 2) all.AddRange(part);
            part = [];
        }
        void Curve(Point c1, Point c2, Point to)
        {
            part[^1] = part[^1] with { OutX = c1.X, OutY = c1.Y };
            part.Add(new PathNode(to.X, to.Y, c2.X, c2.Y));
        }
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Finish();
                    var s = map(pts[0]);
                    part.Add(new PathNode(s.X, s.Y, Start: true));
                    break;
                case SKPathVerb.Line:
                    var l = map(pts[1]);
                    part.Add(new PathNode(l.X, l.Y));
                    break;
                case SKPathVerb.Quad:
                    {
                        var (p0, q, p3) = (map(pts[0]), map(pts[1]), map(pts[2]));
                        Curve(p0 + (q - p0) * (2.0 / 3), p3 + (q - p3) * (2.0 / 3), p3);
                        break;
                    }
                case SKPathVerb.Conic:
                    {
                        var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 2);
                        for (var i = 0; i + 2 < quads.Length; i += 2)
                        {
                            var (p0, q, p3) = (map(quads[i]), map(quads[i + 1]), map(quads[i + 2]));
                            Curve(p0 + (q - p0) * (2.0 / 3), p3 + (q - p3) * (2.0 / 3), p3);
                        }
                        break;
                    }
                case SKPathVerb.Cubic:
                    Curve(map(pts[1]), map(pts[2]), map(pts[3]));
                    break;
                case SKPathVerb.Close:
                    Finish();
                    break;
            }
        }
        Finish();
        if (all.Count > 0) all[0] = all[0] with { Start = true };
        return all;
    }

    private static bool Near(PathNode a, PathNode b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;
}
