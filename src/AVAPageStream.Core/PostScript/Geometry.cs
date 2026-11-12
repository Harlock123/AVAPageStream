namespace AVAPageStream.Core.PostScript;

/// <summary>An affine transformation [a b c d e f]: x' = a x + c y + e, y' = b x + d y + f.</summary>
public readonly record struct PsMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly PsMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    public (double X, double Y) Delta(double x, double y) => (A * x + C * y, B * x + D * y);

    /// <summary>This, then <paramref name="m"/>.</summary>
    public PsMatrix Then(PsMatrix m) => new(
        A * m.A + B * m.C, A * m.B + B * m.D,
        C * m.A + D * m.C, C * m.B + D * m.D,
        E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);

    public double Determinant => A * D - B * C;

    public PsMatrix? Inverse()
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-12) return null;
        return new PsMatrix(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }
}

public enum SegKind { Move, Line, Curve, Close }

/// <summary>A piece of a path: a move, a line or a Bézier curve to (X3, Y3), or the closing of the subpath.</summary>
public readonly record struct PsSeg(SegKind Kind, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0, double X3 = 0, double Y3 = 0);

/// <summary>A path, in whatever space its points were put in.</summary>
public sealed class PsPath
{
    public List<PsSeg> Segs { get; } = [];

    public bool IsEmpty => Segs.Count == 0;

    public PsPath Clone()
    {
        var p = new PsPath();
        p.Segs.AddRange(Segs);
        return p;
    }

    public PsPath Transformed(PsMatrix m)
    {
        var p = new PsPath();
        foreach (var s in Segs)
        {
            var (x1, y1) = m.Apply(s.X1, s.Y1);
            var (x2, y2) = m.Apply(s.X2, s.Y2);
            var (x3, y3) = m.Apply(s.X3, s.Y3);
            p.Segs.Add(s with { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, X3 = x3, Y3 = y3 });
        }
        return p;
    }

    /// <summary>The box round its points (with the curves' control points), or null for an empty path.</summary>
    public (double X0, double Y0, double X1, double Y1)? Bounds()
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double x, double y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        foreach (var s in Segs)
        {
            if (s.Kind == SegKind.Close) continue;
            if (s.Kind == SegKind.Curve) { Add(s.X1, s.Y1); Add(s.X2, s.Y2); }
            Add(s.X3, s.Y3);
        }
        return x0 == double.MaxValue ? null : (x0, y0, x1, y1);
    }
}

/// <summary>A colour as it is drawn.</summary>
public readonly record struct PsColour(double R, double G, double B)
{
    public static readonly PsColour Black = new(0, 0, 0);
}

/// <summary>A region drawing is kept inside: a path (in device space) and its fill rule.</summary>
public sealed record PsClip(PsPath Path, bool EvenOdd);

/// <summary>How a stroke is drawn.</summary>
public sealed record PsPen(double Width, int Cap, int Join, double Miter, double[] Dash, double DashOffset);

/// <summary>Something an EPS draws, in device space (points, y up), inside its clips.</summary>
public abstract record PsOp(IReadOnlyList<PsClip> Clips);

public sealed record PsFill(PsPath Path, PsColour Colour, bool EvenOdd, IReadOnlyList<PsClip> Clips) : PsOp(Clips);

/// <summary>A stroke: its path in user space, drawn through <see cref="Matrix"/> so the pen turns and stretches with it.</summary>
public sealed record PsStroke(PsPath Path, PsMatrix Matrix, PsPen Pen, PsColour Colour, IReadOnlyList<PsClip> Clips) : PsOp(Clips);

/// <summary>A picture's pixels, drawn into the unit square through <see cref="Matrix"/> (image space to device space).</summary>
public sealed record PsImage(int Width, int Height, byte[] Rgba, PsMatrix Matrix, IReadOnlyList<PsClip> Clips) : PsOp(Clips);

/// <summary>
/// A smooth shading: axial (along a line from Coords[0..1] to Coords[2..3]) or radial (between the circles Coords[0..2]
/// and Coords[3..5]), its colours at positions from 0 to 1, drawn through <see cref="Matrix"/> (shading space to
/// device space), inside its clips and, for a pattern, inside <see cref="Area"/>.
/// </summary>
public sealed record PsShading(bool Radial, double[] Coords, PsColour[] Colours, double[] Positions, bool ExtendStart, bool ExtendEnd,
    PsMatrix Matrix, PsPath? Area, bool EvenOdd, IReadOnlyList<PsClip> Clips) : PsOp(Clips);

/// <summary>What an EPS draws: its bounding box (in its own points) and the drawing, in order.</summary>
public sealed record PsDrawing(Box BoundingBox, IReadOnlyList<PsOp> Ops);
