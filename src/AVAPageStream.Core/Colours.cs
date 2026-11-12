using System.Globalization;

namespace AVAPageStream.Core;

/// <summary>A colour in sRGB with alpha.</summary>
public readonly record struct Rgba(byte A, byte R, byte G, byte B)
{
    public string Hex => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>A colour as printing inks: cyan, magenta, yellow and black, each from 0 to 100 per cent.</summary>
public readonly record struct Cmyk(double C, double M, double Y, double K)
{
    /// <summary>
    /// The colour on screen and in an RGB PDF, by the plain conversion (no colour profile): each of red, green and blue
    /// is what its opposite ink and the black leave of white.
    /// </summary>
    public Rgba ToRgb()
    {
        var black = 1 - Math.Clamp(K, 0, 100) / 100;
        byte Of(double ink) => (byte)Math.Round(255 * (1 - Math.Clamp(ink, 0, 100) / 100) * black);
        return new Rgba(255, Of(C), Of(M), Of(Y));
    }

    public override string ToString() => FormattableString.Invariant($"C{C:0.#} M{M:0.#} Y{Y:0.#} K{K:0.#}");
}

/// <summary>
/// A named colour the document offers: its colour on screen, and for print, its CMYK inks if it is made of them, and
/// whether it is a spot colour (an ink of its own, such as a Pantone colour, its CMYK only how it looks).
/// </summary>
public sealed record Swatch(string Name, string Colour, Cmyk? Process = null, bool Spot = false)
{
    /// <summary>A swatch made of CMYK inks: its screen colour worked out from them.</summary>
    public static Swatch FromCmyk(string name, Cmyk ink, bool spot = false) => new(name, ink.ToRgb().Hex, ink, spot);
}

/// <summary>
/// Colours are kept in the document as text: "#RRGGBB" or "#AARRGGBB", "@Name" for the document's swatch of that name
/// (so changing the swatch changes everything using it), or null for none.
/// </summary>
public static class Colours
{
    public const string Black = "#000000";
    public const string White = "#FFFFFF";

    public static List<Swatch> DefaultSwatches() =>
    [
        new("Black", Black), new("White", White), new("Grey", "#808080"), new("Light grey", "#D0D0D0"),
        new("Red", "#E02020"), new("Green", "#1E9E40"), new("Blue", "#2050D0"),
        new("Cyan", "#00A0E0"), new("Magenta", "#D0008A"), new("Yellow", "#FFD800"),
    ];

    /// <summary>
    /// Reads a swatch's colour as typed: "#RRGGBB", or CMYK as four per cents ("0 100 100 0", "C0 M100 Y100 K0",
    /// "cmyk 0,100,100,0"). Gives the screen colour, and the inks if CMYK was typed.
    /// </summary>
    public static bool TryReadSwatch(string? text, out string colour, out Cmyk? inks)
    {
        colour = "";
        inks = null;
        var s = (text ?? "").Trim();
        if (TryParse(s, out var rgba)) { colour = rgba.Hex; return true; }
        var numbers = System.Text.RegularExpressions.Regex.Matches(s.Replace("cmyk", "", StringComparison.OrdinalIgnoreCase), @"-?\d+(\.\d+)?")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (numbers.Count != 4 || numbers.Any(n => n < 0 || n > 100)) return false;
        var ink = new Cmyk(numbers[0], numbers[1], numbers[2], numbers[3]);
        inks = ink;
        colour = ink.ToRgb().Hex;
        return true;
    }

    /// <summary>A colour that refers to a swatch by name.</summary>
    public static string Ref(string swatch) => "@" + swatch;

    public static bool IsRef(string? colour) => colour is { Length: > 1 } && colour[0] == '@';

    /// <summary>A reference to a swatch at a tint: "@Navy 40%" (100 is the swatch itself, "@Navy").</summary>
    public static string Ref(string swatch, double tint) => tint >= 100 ? Ref(swatch) : FormattableString.Invariant($"@{swatch} {Math.Round(tint, 1):0.#}%");

    /// <summary>A reference's swatch name and tint (100 for none).</summary>
    public static (string Name, double Tint) Parse(string reference)
    {
        var body = reference.TrimStart('@');
        var space = body.LastIndexOf(' ');
        if (space > 0 && body.EndsWith('%') && double.TryParse(body[(space + 1)..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tint))
            return (body[..space], Math.Clamp(tint, 0, 100));
        return (body, 100);
    }

    /// <summary>
    /// The value a colour stands for: a swatch's colour (lightened toward white for a tint) for a reference, null if
    /// there is no such swatch; otherwise itself.
    /// </summary>
    public static string? Resolve(string? colour, IEnumerable<Swatch> swatches)
    {
        if (!IsRef(colour)) return colour;
        var (name, tint) = Parse(colour!);
        if (swatches.FirstOrDefault(w => w.Name == name) is not { } swatch) return null;
        if (tint >= 100 || !TryParse(swatch.Colour, out var c)) return swatch.Colour;
        byte Mix(byte v) => (byte)Math.Round(255 - (255 - v) * tint / 100);
        return new Rgba(c.A, Mix(c.R), Mix(c.G), Mix(c.B)).Hex;
    }

    /// <summary>
    /// The inks a document prints with: whether it uses process (CMYK, or plain RGB, which prints as process) colour,
    /// and the spot inks it uses, by name.
    /// </summary>
    public static (bool Process, List<string> Spots) Inks(Document doc)
    {
        var used = new List<string?>();
        foreach (var o in doc.AllObjects) used.AddRange([o.Fill, o.Stroke]);
        foreach (var story in doc.Stories.Values) used.AddRange(story.Runs.Select(r => r.Format.Colour));
        var spots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var process = false;
        foreach (var c in used.Where(c => c is not null).Distinct())
        {
            if (IsRef(c) && doc.Swatches.FirstOrDefault(w => w.Name == Parse(c!).Name) is { Spot: true } spot) spots.Add(spot.Name);
            else if (Resolve(c, doc.Swatches) is { } v && TryParse(v, out var rgba) && rgba.A > 0) process = true;
        }
        return (process, [.. spots]);
    }

    public static bool TryParse(string? text, out Rgba colour)
    {
        colour = default;
        if (text is null) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length is not (6 or 8) || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        if (s.Length == 6) v |= 0xFF000000;
        colour = new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>
    /// Reads a typed colour: "none" (or nothing) is no colour, a swatch's name refers to the swatch, otherwise a hex value.
    /// Returns false, leaving <paramref name="colour"/> null, for anything else.
    /// </summary>
    public static bool TryRead(string? text, IEnumerable<Swatch> swatches, out string? colour)
    {
        colour = null;
        var s = text?.Trim() ?? "";
        if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        var (name, tint) = Parse(s);
        if (swatches.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } swatch)
        {
            colour = Ref(swatch.Name, tint);
            return true;
        }
        if (!TryParse(s, out var rgba)) return false;
        colour = rgba.Hex;
        return true;
    }

    /// <summary>How a colour is shown in a box: the swatch's name for a reference, the hex value otherwise, or "none".</summary>
    public static string Describe(string? colour, IEnumerable<Swatch> swatches)
    {
        if (colour is null) return "none";
        if (IsRef(colour)) return colour[1..]; // "Navy" or "Navy 40%"
        return TryParse(colour, out var c) ? c.Hex : colour;
    }
}
