namespace AVAPageStream.Core;

/// <summary>A character set on a path: from where its baseline starts, turned to follow the path there.</summary>
public sealed record PathGlyph(int Index, string Text, CharFormat Format, Pt At, double Angle, double Width);

/// <summary>
/// Text on a path: a story set as one line along a path, each character turned to the direction of the path at its
/// middle. The first paragraph's alignment places it (left from the start, centred, or right to the end), moved along by
/// the path's <see cref="PathObject.TextOffset"/>; what runs past the end is overset.
/// </summary>
public static class PathText
{
    public static List<Pt> Points(PathObject path) => Shapes.Flatten(path);

    public static double Length(IReadOnlyList<Pt> pts)
    {
        var total = 0.0;
        for (var i = 1; i < pts.Count; i++) total += Distance(pts[i - 1], pts[i]);
        return total;
    }

    /// <summary>The point at a distance along the path, and the direction there in degrees (0 is to the right, down is 90).</summary>
    public static (Pt At, double Angle) At(IReadOnlyList<Pt> pts, double distance)
    {
        if (pts.Count == 0) return (new Pt(0, 0), 0);
        if (pts.Count == 1) return (pts[0], 0);
        var gone = 0.0;
        for (var i = 1; i < pts.Count; i++)
        {
            var (a, b) = (pts[i - 1], pts[i]);
            var len = Distance(a, b);
            if (len == 0) continue;
            if (gone + len >= distance || i == pts.Count - 1)
            {
                var t = (distance - gone) / len;
                return (new Pt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t), Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI);
            }
            gone += len;
        }
        return (pts[^1], 0);
    }

    /// <summary>
    /// Sets a story along the points. Returns the characters that fit on the path, and the index of the first that
    /// didn't (the story's length if all did).
    /// </summary>
    public static (List<PathGlyph> Glyphs, int OversetFrom) Place(Story story, IReadOnlyList<Pt> pts, ITextMeasurer m, double offset = 0, FieldContext? fields = null)
    {
        var glyphs = new List<PathGlyph>();
        var length = Length(pts);
        var text = story.Text;
        var widths = new double[text.Length];
        var shown = new string[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            shown[i] = text[i] == '\n' ? " " : Fields.Expand(text[i].ToString(), fields);
            widths[i] = TextFlow.Measure(m, shown[i], story.FormatAt(i));
        }
        var total = widths.Sum();
        var start = (story.Paras.Count > 0 ? story.Paras[0].Align : Align.Left) switch
        {
            Align.Centre => (length - total) / 2,
            Align.Right => length - total,
            _ => 0,
        } + offset;
        var d = start;
        for (var i = 0; i < text.Length; i++)
        {
            var w = widths[i];
            var middle = d + w / 2;
            if (middle > length + 0.01) return (glyphs, i);
            if (middle >= -0.01 && shown[i].Trim().Length > 0)
            {
                var (at, angle) = At(pts, middle);
                var r = angle * Math.PI / 180;
                glyphs.Add(new PathGlyph(i, shown[i], story.FormatAt(i), new Pt(at.X - Math.Cos(r) * w / 2, at.Y - Math.Sin(r) * w / 2), angle, w));
            }
            d += w;
        }
        return (glyphs, text.Length);
    }

    private static double Distance(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
