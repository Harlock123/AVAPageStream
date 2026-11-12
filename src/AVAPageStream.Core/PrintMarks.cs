namespace AVAPageStream.Core;

/// <summary>
/// Crop marks for printing to the edge: the page is set on a larger sheet, its artwork running out past the trim by
/// the bleed, with short lines in the margin round it that show the printer where to cut.
/// </summary>
public static class PrintMarks
{
    /// <summary>How long each mark is.</summary>
    public const double Length = 12;

    /// <summary>The gap between the trim's corner and the start of a mark, at least clear of the bleed.</summary>
    public static double Offset(Setup s) => Math.Max(s.Bleed, 3) + 3;

    /// <summary>How far the sheet runs past the trim on each side: room for the bleed and the marks.</summary>
    public static double Slug(Setup s) => Offset(s) + Length + 6;

    /// <summary>The sheet the marked page is set on.</summary>
    public static (double Width, double Height) Sheet(Setup s) => (s.PageWidth + 2 * Slug(s), s.PageHeight + 2 * Slug(s));

    /// <summary>
    /// The eight marks, in the sheet's points: at each corner of the trim one across and one down, in line with the
    /// trim's edges and kept off the page and its bleed.
    /// </summary>
    public static IReadOnlyList<(Pt From, Pt To)> Lines(Setup s)
    {
        var slug = Slug(s);
        var off = Offset(s);
        var (l, t) = (slug, slug);
        var (r, b) = (slug + s.PageWidth, slug + s.PageHeight);
        var lines = new List<(Pt, Pt)>();
        foreach (var x in new[] { l, r })
            foreach (var y in new[] { t, b })
            {
                var dx = x == l ? -1 : 1;
                var dy = y == t ? -1 : 1;
                lines.Add((new Pt(x + dx * off, y), new Pt(x + dx * (off + Length), y))); // across, beside the corner
                lines.Add((new Pt(x, y + dy * off), new Pt(x, y + dy * (off + Length)))); // down, above or below it
            }
        return lines;
    }
}
