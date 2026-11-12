namespace AVAPageStream.Core;

/// <summary>One side of a printed sheet: the page on its left and on its right (counting from 0), or null for a blank.</summary>
public sealed record SheetSide(int? Left, int? Right);

/// <summary>Arranging pages for printing.</summary>
public static class Imposition
{
    /// <summary>
    /// A saddle-stitched booklet: pages in pairs on sheets printed both sides, folded and nested, so they read in
    /// order. The count is made up to a multiple of four with blank pages at the end. For eight pages: 8|1, 2|7, 6|3,
    /// 4|5 (the first side of each sheet, then its back).
    /// </summary>
    public static List<SheetSide> Booklet(int pages)
    {
        var n = Math.Max(4, (pages + 3) / 4 * 4);
        int? Page(int i) => i < pages ? i : null;
        var sides = new List<SheetSide>();
        for (var i = 0; i < n / 2; i++)
            sides.Add(i % 2 == 0 ? new SheetSide(Page(n - 1 - i), Page(i)) : new SheetSide(Page(i), Page(n - 1 - i)));
        return sides;
    }
}
