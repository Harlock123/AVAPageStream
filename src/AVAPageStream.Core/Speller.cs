using System.IO.Compression;

namespace AVAPageStream.Core;

/// <summary>
/// Checking spelling against a word list: SCOWL's American or British English at size 60 (the lists Hunspell's English
/// dictionaries are made from, every inflected form included), bundled in Spelling/ with SCOWL's copyright notice.
/// </summary>
public sealed class Speller
{
    private readonly HashSet<string> _words;
    private readonly HashSet<string> _lower;
    private readonly Dictionary<string, int> _rank = []; // lower case → its SCOWL level: 10 the commonest words, 60 the rarest

    public static readonly (string Code, string Name)[] Languages = [("en-US", "English (American)"), ("en-GB", "English (British)")];

    private static readonly Dictionary<string, Lazy<Speller>> Loaded = Languages.ToDictionary(l => l.Code, l => new Lazy<Speller>(() => new Speller(Read(l.Code))));

    /// <summary>The speller for a language code ("en-US", "en-GB"); American English for one it doesn't know.</summary>
    public static Speller For(string? language) => (Loaded.TryGetValue(language ?? "", out var s) ? s : Loaded["en-US"]).Value;

    private static IEnumerable<string> Read(string code)
    {
        using var stream = typeof(Speller).Assembly.GetManifestResourceStream($"Spelling.{code}.words.gz")
            ?? throw new InvalidOperationException($"The word list for {code} is missing from the program.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var words = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) words.Add(line);
        return words;
    }

    public Speller(IEnumerable<string> words)
    {
        // The lists come a level at a time, each after a "#level" line.
        _words = new HashSet<string>(StringComparer.Ordinal);
        var level = 0;
        foreach (var line in words)
        {
            if (line.StartsWith('#')) { int.TryParse(line[1..], out level); continue; }
            _words.Add(line);
            _rank.TryAdd(line.ToLowerInvariant(), level);
        }
        _lower = new HashSet<string>(_words.Select(w => w.ToLowerInvariant()), StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a word is spelt right: in the list as it is, or capitalised or in capitals where the list has it in
    /// lower case; or in <paramref name="also"/> (ignored words, the user's own). Words with digits pass, and each
    /// part of a hyphenated word is checked.
    /// </summary>
    public bool Check(string word, ISet<string>? also = null)
    {
        word = Normal(word);
        if (word.Length == 0 || word.Any(char.IsDigit)) return true;
        if (also is not null && (also.Contains(word) || also.Contains(word.ToLowerInvariant()))) return true;
        if (word.Contains('-')) return word.Split('-', StringSplitOptions.RemoveEmptyEntries).All(p => Check(p, also));
        if (_words.Contains(word)) return true;
        var lower = word.ToLowerInvariant();
        var capitalised = char.IsUpper(word[0]) && word.Skip(1).All(c => !char.IsUpper(c));
        var capitals = word.All(c => !char.IsLower(c));
        if ((capitalised || capitals) && _words.Contains(lower)) return true;
        if (capitals && _lower.Contains(lower)) return true; // NASA's list form is "NASA"; "Harbour" in capitals
        return false;
    }

    /// <summary>A word as it is looked up: curly apostrophes made straight, quotes and stray apostrophes round it taken off.</summary>
    public static string Normal(string word) => word.Replace('’', '\'').Replace('‘', '\'').Trim('\'');

    private const string Letters = "abcdefghijklmnopqrstuvwxyz'";

    /// <summary>
    /// Words from the list one or two changes away (a letter dropped, added, changed, or two swapped), the nearest
    /// first and, as near as each other, the commonest first; in the word's own case.
    /// </summary>
    public List<string> Suggest(string word, int count = 6)
    {
        var w = Normal(word);
        if (w.Length == 0) return [];
        var lower = w.ToLowerInvariant();
        var one = Edits(lower).Where(Known).Distinct().ToList();
        // The commonest first; among words as common, two letters swapped (the commonest slip) before other changes.
        var found = one.OrderBy(Rank).ThenBy(c => Swapped(c, lower) ? 0 : 1).ToList();
        if (found.Count < count && lower.Length <= 14)
        {
            var near = one.ToHashSet();
            var two = Edits(lower).Distinct().SelectMany(Edits).Where(e => e != lower && Known(e) && !near.Contains(e)).Distinct().OrderBy(Rank);
            found.AddRange(two.Take(count));
        }
        return found.Take(count).Select(s => Case(s, w)).Distinct().ToList();
    }

    private int Rank(string lower) => _rank.GetValueOrDefault(lower, int.MaxValue);

    private static bool Swapped(string a, string b) =>
        a.Length == b.Length && a.Order().SequenceEqual(b.Order()) && a.Zip(b).Count(p => p.First != p.Second) == 2;

    private bool Known(string lower) => _lower.Contains(lower);

    /// <summary>A suggestion in the case of the word it is for: capitals, capitalised, or as the list has it.</summary>
    private string Case(string lower, string like)
    {
        if (like.All(c => !char.IsLower(c)) && like.Length > 1) return lower.ToUpperInvariant();
        var listed = _words.Contains(lower) ? lower : _words.FirstOrDefault(x => x.Equals(lower, StringComparison.OrdinalIgnoreCase)) ?? lower;
        return char.IsUpper(like[0]) ? char.ToUpperInvariant(listed[0]) + listed[1..] : listed;
    }

    private static IEnumerable<string> Edits(string w)
    {
        for (var i = 0; i <= w.Length; i++)
        {
            var (a, b) = (w[..i], w[i..]);
            if (b.Length > 0) yield return a + b[1..];                                     // a letter dropped
            if (b.Length > 1) yield return a + b[1] + b[0] + b[2..];                       // two swapped
            foreach (var c in Letters)
            {
                if (b.Length > 0 && c != b[0]) yield return a + c + b[1..];                // one changed
                yield return a + c + b;                                                    // one added
            }
        }
    }

    /// <summary>The words of a stretch of text: letters, with apostrophes and hyphens inside them, and where each starts.</summary>
    public static IEnumerable<(int Start, string Word)> Words(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i])) { i++; continue; }
            var j = i;
            while (j < text.Length && (char.IsLetterOrDigit(text[j]) ||
                   (text[j] is '\'' or '’' or '-' && j + 1 < text.Length && char.IsLetter(text[j + 1])))) j++;
            yield return (i, text[i..j]);
            i = j;
        }
    }

    /// <summary>The misspelt words of a story: where each is and how long.</summary>
    public IEnumerable<Match> Misspelt(Story story, ISet<string>? also = null) =>
        Words(story.Text).Where(w => !Check(w.Word, also)).Select(w => new Match(story.Id, w.Start, w.Word.Length));

    /// <summary>
    /// The next misspelt word after <paramref name="from"/> in a story, on through the stories in reading order and
    /// round to the start; null if there are none.
    /// </summary>
    public Match? Next(Document doc, ISet<string>? also, string? storyId, int from)
    {
        var order = Search.StoryOrder(doc);
        if (order.Count == 0) return null;
        var first = storyId is null ? 0 : Math.Max(0, order.IndexOf(storyId));
        // This story from the caret on, then each story after it (round the end to the start).
        for (var k = 0; k < order.Count; k++)
        {
            var story = doc.Stories[order[(first + k) % order.Count]];
            var hit = k == 0 && storyId is not null ? Misspelt(story, also).FirstOrDefault(m => m.Start >= from) : Misspelt(story, also).FirstOrDefault();
            if (hit is not null) return hit;
        }
        // Then this story before the caret.
        return storyId is null ? null : Misspelt(doc.Stories[order[first]], also).FirstOrDefault(m => m.Start < from);
    }
}
