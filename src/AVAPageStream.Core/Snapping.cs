namespace AVAPageStream.Core;

/// <summary>
/// The lines on a page that moving, resizing and drawing snap to: the page's edges and centre, the margins, the
/// column edges and the ruler guides.
/// </summary>
public sealed class SnapTargets
{
    public List<double> Xs { get; } = [];
    public List<double> Ys { get; } = [];

    public static SnapTargets For(Setup setup, Sheet? sheet, Sheet? master = null)
    {
        var t = new SnapTargets();
        t.Xs.AddRange([0, setup.PageWidth, setup.PageWidth / 2]);
        t.Ys.AddRange([0, setup.PageHeight, setup.PageHeight / 2]);
        var live = setup.Live;
        t.Xs.AddRange([live.X, live.Right]);
        t.Ys.AddRange([live.Y, live.Bottom]);
        foreach (var c in setup.ColumnBoxes()) t.Xs.AddRange([c.X, c.Right]);
        foreach (var g in (sheet?.Guides ?? []).Concat(master?.Guides ?? []))
            (g.Vertical ? t.Xs : t.Ys).Add(g.Position);
        return t;
    }

    /// <summary>The nearest target within <paramref name="tolerance"/> of <paramref name="value"/>, or the value itself.</summary>
    public static double Snap(double value, IEnumerable<double> targets, double tolerance)
    {
        var best = value;
        var bestDistance = tolerance;
        foreach (var t in targets)
        {
            var d = Math.Abs(t - value);
            if (d <= bestDistance) { bestDistance = d; best = t; }
        }
        return best;
    }

    public Pt Snap(Pt p, double tolerance) => new(Snap(p.X, Xs, tolerance), Snap(p.Y, Ys, tolerance));

    /// <summary>
    /// How far to move a box so its left, centre or right edge (whichever is nearest a target) and its top, middle
    /// or bottom land on targets; zero on an axis with nothing within reach.
    /// </summary>
    public (double Dx, double Dy) SnapBox(Box b, double tolerance) =>
        (Best([b.X, b.CentreX, b.Right], Xs, tolerance), Best([b.Y, b.CentreY, b.Bottom], Ys, tolerance));

    private static double Best(double[] edges, List<double> targets, double tolerance)
    {
        var best = 0.0;
        var bestDistance = tolerance;
        foreach (var e in edges)
            foreach (var t in targets)
            {
                var d = Math.Abs(t - e);
                if (d < bestDistance) { bestDistance = d; best = t - e; }
            }
        return best;
    }
}
