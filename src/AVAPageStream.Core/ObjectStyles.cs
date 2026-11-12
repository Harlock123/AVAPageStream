namespace AVAPageStream.Core;

/// <summary>
/// A named look for objects: fill (or gradient), stroke, opacity and blend, shadow, corners and text wrap. Applying it
/// sets those on an object; redefining it sets them again on every object in it, as a paragraph style does its text.
/// </summary>
public sealed record ObjectStyle(
    string Name,
    string? Fill = null,
    string? Stroke = Colours.Black,
    double StrokeWidth = 1,
    Gradient? Gradient = null,
    double Opacity = 1,
    double FillOpacity = 1,
    double StrokeOpacity = 1,
    BlendMode Blend = BlendMode.Normal,
    Shadow? Shadow = null,
    Corners? Corners = null,
    bool Wrap = false,
    double WrapGap = 6,
    bool WrapShape = false)
{
    /// <summary>A style of how an object looks now.</summary>
    public static ObjectStyle From(string name, PageObject o) => new(name, o.Fill, o.Stroke, o.StrokeWidth, o.Gradient,
        o.Opacity, o.FillOpacity, o.StrokeOpacity, o.Blend, o.Shadow, CornersOf(o), o.Wrap, o.WrapGap, o.WrapShape);

    /// <summary>Gives an object this style's look, and puts it in the style.</summary>
    public void ApplyTo(PageObject o)
    {
        (o.Fill, o.Stroke, o.StrokeWidth, o.Gradient) = (Fill, Stroke, StrokeWidth, Gradient);
        (o.Opacity, o.FillOpacity, o.StrokeOpacity, o.Blend, o.Shadow) = (Opacity, FillOpacity, StrokeOpacity, Blend, Shadow);
        if (HasCorners(o))
        {
            o.Corners = Corners;
            if (o is RectangleObject r) r.CornerRadius = 0;
        }
        (o.Wrap, o.WrapGap, o.WrapShape) = (Wrap, WrapGap, WrapShape);
        o.ObjectStyle = Name;
    }

    /// <summary>Whether an object looks as this style says: if not, it has been changed since the style was applied.</summary>
    public bool Matches(PageObject o) => this == From(Name, o) with { Corners = HasCorners(o) ? CornersOf(o) : Corners };

    /// <summary>A short account of the style, for the Object tab.</summary>
    public string Describe(IEnumerable<Swatch> swatches)
    {
        var parts = new List<string>();
        parts.Add(Gradient is { } g ? (g.Radial ? "radial gradient " : "gradient ") + g.Describe(swatches) : "fill " + Colours.Describe(Fill, swatches));
        parts.Add(Stroke is null ? "no stroke" : $"{StrokeWidth:0.##} pt {Colours.Describe(Stroke, swatches)} stroke");
        if (Opacity < 1) parts.Add($"{Opacity * 100:0}% opaque");
        if (Blend != BlendMode.Normal) parts.Add(Blend.ToString().ToLowerInvariant());
        if (Shadow is { } s) parts.Add(s.Glow ? "a glow" : "a drop shadow");
        if (Corners is { IsSquare: false } c) parts.Add(c.Style == CornerStyle.Cut ? "cut corners" : "round corners");
        if (Wrap) parts.Add("text wraps");
        return string.Join(", ", parts);
    }

    private static bool HasCorners(PageObject o) => o is RectangleObject or TextFrame or PictureObject;

    private static Corners? CornersOf(PageObject o) =>
        o.Corners ?? (o is RectangleObject { CornerRadius: > 0 } r ? Corners.All(r.CornerRadius) : null);
}
