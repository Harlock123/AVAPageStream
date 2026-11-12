using System.Globalization;

namespace AVAPageStream.Core;

/// <summary>A colour in sRGB with alpha.</summary>
public readonly record struct Rgba(byte A, byte R, byte G, byte B)
{
    public string Hex => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>A named colour the document offers.</summary>
public sealed record Swatch(string Name, string Colour);

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

    /// <summary>A colour that refers to a swatch by name.</summary>
    public static string Ref(string swatch) => "@" + swatch;

    public static bool IsRef(string? colour) => colour is { Length: > 1 } && colour[0] == '@';

    /// <summary>The value a colour stands for: a swatch's colour for a reference (null if there is no such swatch), else itself.</summary>
    public static string? Resolve(string? colour, IEnumerable<Swatch> swatches)
    {
        if (!IsRef(colour)) return colour;
        var name = colour![1..];
        return swatches.FirstOrDefault(w => w.Name == name)?.Colour;
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
        if (swatches.FirstOrDefault(w => w.Name.Equals(s.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) is { } swatch)
        {
            colour = Ref(swatch.Name);
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
        if (IsRef(colour)) return colour[1..];
        return TryParse(colour, out var c) ? c.Hex : colour;
    }
}
