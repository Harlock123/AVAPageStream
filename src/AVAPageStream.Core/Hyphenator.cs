namespace AVAPageStream.Core;

/// <summary>
/// Where words may be hyphenated, by Frank Liang's algorithm (the one TeX uses) with the American English patterns
/// and exceptions of the hyph-utf8 package, bundled in Hyphenation/. At least two letters stay before a break and
/// three after it.
/// </summary>
public sealed class Hyphenator
{
    private readonly Dictionary<string, int[]> _patterns = [];
    private readonly Dictionary<string, int[]> _exceptions = [];
    private readonly int _longest;

    public const int LeftMin = 2;
    public const int RightMin = 3;

    private static readonly Lazy<Hyphenator> LazyEnglish = new(() => new Hyphenator(Read("hyph-en-us.pat.txt"), Read("hyph-en-us.hyp.txt")));

    /// <summary>American English.</summary>
    public static Hyphenator English => LazyEnglish.Value;

    private static IEnumerable<string> Read(string name)
    {
        using var stream = typeof(Hyphenator).Assembly.GetManifestResourceStream("Hyphenation." + name)
            ?? throw new InvalidOperationException($"The hyphenation file {name} is missing from the program.");
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '%') lines.Add(line.Trim());
        return lines;
    }

    public Hyphenator(IEnumerable<string> patterns, IEnumerable<string> exceptions)
    {
        foreach (var pattern in patterns)
        {
            // "a1b" is the letters "ab" with 1 between them: a value before each letter and after the last.
            var letters = new System.Text.StringBuilder();
            var values = new List<int> { 0 };
            foreach (var c in pattern)
            {
                if (char.IsDigit(c)) values[^1] = c - '0';
                else
                {
                    letters.Append(c);
                    values.Add(0);
                }
            }
            _patterns[letters.ToString()] = [.. values];
            _longest = Math.Max(_longest, letters.Length);
        }
        foreach (var e in exceptions)
        {
            var word = e.Replace("-", "");
            var points = new int[word.Length + 1];
            var at = 0;
            foreach (var c in e)
            {
                if (c == '-') points[at] = 1;
                else at++;
            }
            _exceptions[word] = points;
        }
    }

    /// <summary>
    /// The places in a word (counted in characters from its start) where it may be broken with a hyphen, in order.
    /// Words that aren't all letters, or are too short, have none.
    /// </summary>
    public IReadOnlyList<int> Points(string word)
    {
        if (word.Length < LeftMin + RightMin || !word.All(char.IsLetter)) return [];
        var lower = word.ToLowerInvariant();
        var result = new List<int>();
        if (_exceptions.TryGetValue(lower, out var known))
        {
            for (var i = LeftMin; i <= word.Length - RightMin; i++)
                if (known[i] == 1) result.Add(i);
            return result;
        }
        var w = "." + lower + ".";
        var points = new int[w.Length + 1];
        for (var i = 0; i < w.Length; i++)
            for (var j = i + 1; j <= Math.Min(w.Length, i + _longest); j++)
                if (_patterns.TryGetValue(w[i..j], out var values))
                    for (var k = 0; k < values.Length; k++)
                        points[i + k] = Math.Max(points[i + k], values[k]);
        // points[i + 1] is the value between the word's characters i - 1 and i.
        for (var i = LeftMin; i <= word.Length - RightMin; i++)
            if (points[i + 1] % 2 == 1) result.Add(i);
        return result;
    }

    /// <summary>A word with its hyphenation points shown, such as "hy-phen-ation" (for tests and the curious).</summary>
    public string Show(string word)
    {
        var points = Points(word);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < word.Length; i++)
        {
            if (points.Contains(i)) sb.Append('-');
            sb.Append(word[i]);
        }
        return sb.ToString();
    }
}
