using System.Globalization;

namespace AVAPageStream.Core;

/// <summary>How an object's colours mix with what is under it.</summary>
public enum BlendMode { Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference, Exclusion, Hue, Saturation, Color, Luminosity }

/// <summary>A colour of a gradient, and where along it (0 to 1) it comes.</summary>
public sealed record ColourStop(double At, string Colour);

/// <summary>
/// A gradient fill: linear (at <see cref="Angle"/> degrees, 0 left to right, 90 top to bottom, through the point
/// <see cref="X"/> and <see cref="Y"/> across and down the object from 0 to 1, and <see cref="Size"/> times as long as
/// it takes to cross the object at that angle) or radial (out from <see cref="X"/>, <see cref="Y"/> to <see cref="Size"/>
/// times the distance to the object's furthest corner; <see cref="Angle"/> is only where its handle is shown).
/// </summary>
public sealed record Gradient(bool Radial, IReadOnlyList<ColourStop> Stops, double Angle = 0, double X = 0.5, double Y = 0.5, double Size = 1)
{
    public bool Equals(Gradient? other) => other is not null && Radial == other.Radial && Angle == other.Angle && X == other.X && Y == other.Y && Size == other.Size && Stops.SequenceEqual(other.Stops);
    public override int GetHashCode() => HashCode.Combine(Radial, Angle, X, Y, Size, Stops.Count);

    /// <summary>
    /// Where the gradient runs in a box: from its start to its end, for a linear one; from its centre to its edge, for a
    /// radial one. These are the ends of the line dragged on the page to set it.
    /// </summary>
    public (Pt From, Pt To) Ends(Box r)
    {
        var mid = new Pt(r.X + r.W * X, r.Y + r.H * Y);
        var a = Angle * Math.PI / 180;
        var (dx, dy) = (Math.Cos(a), Math.Sin(a));
        if (Radial)
        {
            var radius = Math.Max(0.01, Furthest(r, mid) * Size);
            return (mid, new Pt(mid.X + dx * radius, mid.Y + dy * radius));
        }
        var half = Crossing(r, dx, dy) * Size;
        return (new Pt(mid.X - dx * half, mid.Y - dy * half), new Pt(mid.X + dx * half, mid.Y + dy * half));
    }

    /// <summary>The gradient set by a line dragged from <paramref name="from"/> to <paramref name="to"/> in a box.</summary>
    public Gradient Dragged(Box r, Pt from, Pt to)
    {
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.5 || r.W <= 0 || r.H <= 0) return this;
        var angle = Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI, 2);
        if (Radial)
            return this with { X = (from.X - r.X) / r.W, Y = (from.Y - r.Y) / r.H, Angle = angle, Size = length / Math.Max(0.01, Furthest(r, from)) };
        var mid = new Pt((from.X + to.X) / 2, (from.Y + to.Y) / 2);
        return this with { X = (mid.X - r.X) / r.W, Y = (mid.Y - r.Y) / r.H, Angle = angle, Size = length / 2 / Math.Max(0.01, Crossing(r, dx / length, dy / length)) };
    }

    /// <summary>Half the distance across a box in a direction, so a gradient of that angle just spans it.</summary>
    private static double Crossing(Box r, double dx, double dy) => Math.Abs(r.W / 2 * dx) + Math.Abs(r.H / 2 * dy);

    private static double Furthest(Box r, Pt c) =>
        new[] { new Pt(r.X, r.Y), new Pt(r.Right, r.Y), new Pt(r.X, r.Bottom), new Pt(r.Right, r.Bottom) }.Max(p => Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y)));

    /// <summary>
    /// Reads stops as typed: colours separated by commas, each with "at" and a per cent if it isn't evenly spaced:
    /// "Red, Gold, Blue" or "Navy at 0, Navy 40% at 70, White at 100". Returns why not, or null.
    /// </summary>
    public static string? ReadStops(string? text, IEnumerable<Swatch> swatches, out List<ColourStop> stops)
    {
        stops = [];
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "A gradient needs two colours or more, such as Red, Blue.";
        var given = new double?[parts.Length];
        var colours = new string[parts.Length];
        for (var k = 0; k < parts.Length; k++)
        {
            var p = parts[k];
            var at = p.LastIndexOf(" at ", StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                var pos = p[(at + 4)..].Trim().TrimEnd('%');
                if (!double.TryParse(pos, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || v > 100) return $"\"{p[(at + 1)..]}\" isn't a place from 0 to 100.";
                given[k] = v / 100;
                p = p[..at].Trim();
            }
            if (!Colours.TryRead(p, swatches, out var c) || c is null) return $"\"{p}\" isn't a colour: type a swatch's name, or a value such as #2050D0.";
            colours[k] = c;
        }
        given[0] ??= 0;
        given[^1] ??= 1;
        // Stops without a place are spread evenly between those with one.
        for (var k = 0; k < parts.Length; k++)
        {
            if (given[k] is not null) continue;
            var before = k - 1;
            var after = Array.FindIndex(given, k, g => g is not null);
            var (a, b) = (given[before]!.Value, given[after]!.Value);
            given[k] = a + (b - a) * (k - before) / (after - before);
        }
        for (var k = 0; k < parts.Length; k++) stops.Add(new ColourStop(Math.Clamp(given[k]!.Value, 0, 1), colours[k]));
        stops.Sort((x, y) => x.At.CompareTo(y.At));
        return null;
    }

    /// <summary>The stops as they are typed.</summary>
    public string Describe(IEnumerable<Swatch> swatches) =>
        string.Join(", ", Stops.Select(s => $"{Colours.Describe(s.Colour, swatches)} at {Math.Round(s.At * 100):0}"));
}

/// <summary>A soft shadow behind an object (or, as a glow, all round it): its colour, opacity, offset and blur, in points.</summary>
public sealed record Shadow(string Colour = Colours.Black, double Opacity = 0.5, double X = 4, double Y = 4, double Blur = 4, bool Glow = false);

public enum CornerStyle { Round, Cut }

/// <summary>A rectangle's corners: each one's size, rounded or cut off.</summary>
public sealed record Corners(double TopLeft, double TopRight, double BottomRight, double BottomLeft, CornerStyle Style = CornerStyle.Round)
{
    public static Corners All(double size, CornerStyle style = CornerStyle.Round) => new(size, size, size, size, style);

    public bool IsSquare => TopLeft <= 0 && TopRight <= 0 && BottomRight <= 0 && BottomLeft <= 0;

    /// <summary>Each corner's size kept to half the side it is on, so corners never cross.</summary>
    public Corners Fitted(double w, double h)
    {
        var m = Math.Max(0, Math.Min(w, h) / 2);
        return this with { TopLeft = Math.Min(TopLeft, m), TopRight = Math.Min(TopRight, m), BottomRight = Math.Min(BottomRight, m), BottomLeft = Math.Min(BottomLeft, m) };
    }

    /// <summary>Reads sizes as typed: one measure for every corner, or four (top left, top right, bottom right, bottom left).</summary>
    public static bool TryRead(string? text, Unit unit, out double[] sizes)
    {
        sizes = [];
        var parts = (text ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (1 or 4)) return false;
        var list = new List<double>();
        foreach (var p in parts)
        {
            if (!Units.TryParse(p, unit, out var v) || v < 0) return false;
            list.Add(v);
        }
        sizes = list.Count == 1 ? [list[0], list[0], list[0], list[0]] : [.. list];
        return true;
    }

    public string Describe(Unit unit) =>
        TopLeft == TopRight && TopRight == BottomRight && BottomRight == BottomLeft
            ? Units.Format(TopLeft, unit)
            : string.Join(" ", new[] { TopLeft, TopRight, BottomRight, BottomLeft }.Select(v => Units.Format(v, unit)));
}
