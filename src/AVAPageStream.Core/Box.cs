namespace AVAPageStream.Core;

/// <summary>A point on a page, in points from its top-left corner.</summary>
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>A rectangle on a page, in points: its top-left corner and size.</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CentreX => X + W / 2;
    public double CentreY => Y + H / 2;

    /// <summary>The rectangle with these two corners, whichever way round they are.</summary>
    public static Box FromCorners(Pt a, Pt b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public bool Contains(Pt p, double slack = 0) =>
        p.X >= X - slack && p.X <= Right + slack && p.Y >= Y - slack && p.Y <= Bottom + slack;

    public bool Intersects(Box b) => b.X <= Right && b.Right >= X && b.Y <= Bottom && b.Bottom >= Y;

    /// <summary>Whether all of this lies inside <paramref name="b"/>.</summary>
    public bool Inside(Box b) => X >= b.X && Y >= b.Y && Right <= b.Right && Bottom <= b.Bottom;

    public Box Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);

    public Box Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    public static Box Union(IEnumerable<Box> boxes)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var b in boxes)
        {
            x0 = Math.Min(x0, b.X);
            y0 = Math.Min(y0, b.Y);
            x1 = Math.Max(x1, b.Right);
            y1 = Math.Max(y1, b.Bottom);
        }
        return x0 > x1 ? default : new Box(x0, y0, x1 - x0, y1 - y0);
    }
}
