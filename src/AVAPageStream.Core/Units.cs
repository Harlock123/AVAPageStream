using System.Globalization;
using System.Text.RegularExpressions;

namespace AVAPageStream.Core;

/// <summary>The units measures are shown and typed in. Everything is kept in points (1/72 inch).</summary>
public enum Unit { Point, Pica, Inch, Millimetre, Centimetre }

/// <summary>Converting, showing and reading measures.</summary>
public static partial class Units
{
    public static readonly Unit[] All = [Unit.Point, Unit.Pica, Unit.Inch, Unit.Millimetre, Unit.Centimetre];

    /// <summary>How many points one of the unit is.</summary>
    public static double Points(Unit unit) => unit switch
    {
        Unit.Point => 1,
        Unit.Pica => 12,
        Unit.Inch => 72,
        Unit.Millimetre => 72 / 25.4,
        Unit.Centimetre => 720 / 25.4,
        _ => 1,
    };

    public static string Suffix(Unit unit) => unit switch
    {
        Unit.Point => "pt",
        Unit.Pica => "pc",
        Unit.Inch => "in",
        Unit.Millimetre => "mm",
        Unit.Centimetre => "cm",
        _ => "pt",
    };

    public static string Name(Unit unit) => unit switch
    {
        Unit.Point => "Points",
        Unit.Pica => "Picas",
        Unit.Inch => "Inches",
        Unit.Millimetre => "Millimetres",
        Unit.Centimetre => "Centimetres",
        _ => "Points",
    };

    public static double ToPoints(double value, Unit unit) => value * Points(unit);

    public static double FromPoints(double points, Unit unit) => points / Points(unit);

    /// <summary>
    /// A measure in a unit, rounded to what is worth showing: "54 pt", "0.75 in", "19.05 mm". Picas are shown as
    /// picas and points, the way they are spoken: "4p6".
    /// </summary>
    public static string Format(double points, Unit unit, bool suffix = true)
    {
        if (unit == Unit.Pica)
        {
            var negative = points < 0;
            var total = Math.Round(Math.Abs(points), 2);
            var picas = Math.Floor(total / 12 + 1e-9);
            var rest = Math.Round(total - picas * 12, 2);
            if (rest >= 12) { picas++; rest = 0; }
            return (negative ? "-" : "") + picas.ToString(CultureInfo.InvariantCulture) + "p" + Number(rest, 2);
        }
        var digits = unit switch { Unit.Point => 2, Unit.Millimetre => 2, Unit.Centimetre => 3, _ => 4 };
        var text = Number(FromPoints(points, unit), digits);
        return suffix ? text + " " + Suffix(unit) : text;
    }

    private static string Number(double value, int digits) =>
        Math.Round(value, digits).ToString("0.####", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\G\s*(?<sign>[+-])?\s*(?<num>\d+(?:\.\d*)?|\.\d+)\s*(?:(?<unit>pt|pc|in|mm|cm|"")|(?<p>p)(?<pts>\d+(?:\.\d*)?|\.\d+)?)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Term();

    /// <summary>
    /// Reads a typed measure: a number in the current unit ("54"), or with its own unit ("3in", "25 mm", "12p6" for
    /// 12 picas and 6 points, "18pt"), or a sum of them ("1in + 6pt", "2in-0.25in").
    /// </summary>
    public static bool TryParse(string? text, Unit current, out double points)
    {
        points = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var at = 0;
        var any = false;
        while (at < text.Length)
        {
            var m = Term().Match(text, at);
            if (!m.Success || m.Length == 0) return false;
            if (any && !m.Groups["sign"].Success) return false;
            var value = double.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
            double term;
            if (m.Groups["p"].Success)
                term = value * 12 + (m.Groups["pts"].Success ? double.Parse(m.Groups["pts"].Value, CultureInfo.InvariantCulture) : 0);
            else if (m.Groups["unit"].Success)
                term = ToPoints(value, m.Groups["unit"].Value.ToLowerInvariant() switch
                {
                    "pt" => Unit.Point,
                    "pc" => Unit.Pica,
                    "in" or "\"" => Unit.Inch,
                    "mm" => Unit.Millimetre,
                    _ => Unit.Centimetre,
                });
            else
                term = ToPoints(value, current);
            points += m.Groups["sign"].Value == "-" ? -term : term;
            at += m.Length;
            any = true;
        }
        return any;
    }
}
