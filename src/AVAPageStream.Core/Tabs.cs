using System.Globalization;

namespace AVAPageStream.Core;

public enum TabKind { Left, Right, Centre, Decimal }

/// <summary>A tab stop: where across the column it is (in points from the column's left), how text lines up on it, and a leader to fill the gap with.</summary>
public sealed record TabStop(double Position, TabKind Kind = TabKind.Left, char Leader = '\0');

/// <summary>
/// A paragraph's tab stops, kept in its format as text such as "72L 216R. 300C" (points, the kind's letter, and any
/// leader character), so formats compare by value. Past the last stop, tabs fall every half inch.
/// </summary>
public static class Tabs
{
    public const double Every = 36;

    public static List<TabStop> Parse(string? stored)
    {
        var stops = new List<TabStop>();
        foreach (var entry in (stored ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = 0;
            while (i < entry.Length && (char.IsDigit(entry[i]) || entry[i] == '.' && i + 1 < entry.Length && char.IsDigit(entry[i + 1]))) i++;
            if (i == 0 || !double.TryParse(entry[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var at)) continue;
            var kind = i < entry.Length ? entry[i] switch { 'R' => TabKind.Right, 'C' => TabKind.Centre, 'D' => TabKind.Decimal, _ => TabKind.Left } : TabKind.Left;
            var leader = i + 1 < entry.Length ? entry[i + 1] : '\0';
            stops.Add(new TabStop(at, kind, leader));
        }
        return stops.OrderBy(s => s.Position).ToList();
    }

    public static string Store(IEnumerable<TabStop> stops) => string.Join(" ", stops.OrderBy(s => s.Position).Select(s =>
        Math.Round(s.Position, 2).ToString("0.##", CultureInfo.InvariantCulture) + s.Kind switch
        {
            TabKind.Right => "R",
            TabKind.Centre => "C",
            TabKind.Decimal => "D",
            _ => "L",
        } + (s.Leader == '\0' ? "" : s.Leader.ToString())));

    /// <summary>
    /// Reads typed tab stops: comma-separated, each a measure, then optionally left, right, centre or decimal, then
    /// optionally a leader character: "1in, 4in right ., 2.5in decimal".
    /// </summary>
    public static bool TryRead(string? text, Unit unit, out string stored, out string? error)
    {
        stored = "";
        error = null;
        var stops = new List<TabStop>();
        foreach (var raw in (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var kind = TabKind.Left;
            var leader = '\0';
            if (words.Count > 1 && words[^1].Length == 1 && !char.IsLetterOrDigit(words[^1][0])) { leader = words[^1][0]; words.RemoveAt(words.Count - 1); }
            if (words.Count > 1 && Kind(words[^1]) is { } k) { kind = k; words.RemoveAt(words.Count - 1); }
            if (!Units.TryParse(string.Join(" ", words), unit, out var at) || at < 0)
            {
                error = $"\"{raw}\" isn't a tab stop. Type a measure, then left, right, centre or decimal, then a leader: 1in, 4in right .";
                return false;
            }
            stops.Add(new TabStop(at, kind, leader));
        }
        stored = Store(stops);
        return true;
    }

    private static TabKind? Kind(string word) => word.ToLowerInvariant() switch
    {
        "left" or "l" => TabKind.Left,
        "right" or "r" => TabKind.Right,
        "centre" or "center" or "c" => TabKind.Centre,
        "decimal" or "d" => TabKind.Decimal,
        _ => null,
    };

    /// <summary>Tab stops as they are typed, in a unit: "1 in, 4 in right .".</summary>
    public static string Show(string? stored, Unit unit) => string.Join(", ", Parse(stored).Select(s =>
        Units.Format(s.Position, unit) + s.Kind switch { TabKind.Right => " right", TabKind.Centre => " centre", TabKind.Decimal => " decimal", _ => "" } +
        (s.Leader == '\0' ? "" : " " + s.Leader)));

    /// <summary>
    /// The stop a tab at <paramref name="x"/> (from the column's left) goes to: the next of the paragraph's stops, the
    /// left indent of a hanging indent, or the next half inch.
    /// </summary>
    public static TabStop Next(IReadOnlyList<TabStop> stops, double x, double leftIndent)
    {
        const double e = 0.01;
        if (x + e < leftIndent && stops.All(s => s.Position <= x + e || s.Position >= leftIndent)) return new TabStop(leftIndent);
        foreach (var s in stops)
            if (s.Position > x + e) return s;
        return new TabStop((Math.Floor((x + e) / Every) + 1) * Every);
    }
}
