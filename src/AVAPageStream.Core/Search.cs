using System.Text.RegularExpressions;

namespace AVAPageStream.Core;

/// <summary>A stretch of a story's text that a search found.</summary>
public sealed record Match(string StoryId, int Start, int Length);

/// <summary>
/// How to compare: with or without case, anywhere or only as a whole word, as plain text or a regular expression;
/// and only in text of a paragraph style or a character style, if one is named (with nothing to find, every stretch
/// of text in it is found).
/// </summary>
public sealed record FindOptions(bool MatchCase = false, bool WholeWord = false, bool Regex = false, string? ParaStyle = null, string? CharStyle = null)
{
    public bool ByStyle => ParaStyle is not null || CharStyle is not null;
}

/// <summary>Finding text through every story, in reading order: the pages' stories first, then the masters'.</summary>
public static class Search
{
    /// <summary>
    /// The stories in the order their first frames come: page by page, top-most object last, then the master pages;
    /// each story's footnotes after it, and text on paths where its path is.
    /// </summary>
    public static List<string> StoryOrder(Document doc)
    {
        var order = new List<string>();
        var seen = new HashSet<string>();
        foreach (var sheet in doc.Pages.Cast<Sheet>().Concat(doc.Sheets.Where(s => s is not Page)))
            foreach (var o in sheet.All)
            {
                IEnumerable<string> stories = o switch { TextFrame f => [f.StoryId], TableObject t => t.Cells, PathObject { StoryId: { } p } => [p], _ => [] };
                foreach (var id in stories)
                {
                    if (!doc.Stories.TryGetValue(id, out var story) || !seen.Add(id)) continue;
                    order.Add(id);
                    // Its footnotes after it, in the order of their numbers.
                    foreach (var note in Footnotes.Numbers(story).Keys)
                        if (doc.Stories.ContainsKey(note) && seen.Add(note)) order.Add(note);
                }
            }
        return order;
    }

    private static StringComparison Comparison(FindOptions o) => o.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Whether the text at <paramref name="start"/> is the query, as the options compare it.</summary>
    public static bool IsMatch(string text, int start, string query, FindOptions o)
    {
        if (query.Length == 0 || start < 0 || start + query.Length > text.Length) return false;
        if (string.Compare(text, start, query, 0, query.Length, Comparison(o)) != 0) return false;
        if (!o.WholeWord) return true;
        var before = start == 0 || !IsWord(text[start - 1]);
        var after = start + query.Length == text.Length || !IsWord(text[start + query.Length]);
        return before && after;
    }

    /// <summary>A regular expression's pattern as the options make it (whole words in \b), or null if there is none to find.</summary>
    private static Regex? Pattern(string query, FindOptions o)
    {
        if (!o.Regex || query.Length == 0) return null;
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | (o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(o.WholeWord ? $@"\b(?:{query})\b" : query, options, TimeSpan.FromSeconds(2));
    }

    /// <summary>What is wrong with a search (a pattern that doesn't read, nothing to find), or null if it can be made.</summary>
    public static string? Problem(Document doc, string query, FindOptions o)
    {
        if (query.Length == 0 && !o.ByStyle) return "Type something to find, or pick a style to find text in.";
        if (o.ParaStyle is { } p && doc.Styles.All(s => s.Name != p)) return $"There is no paragraph style called {p}.";
        if (o.CharStyle is { } c && doc.CharStyles.All(s => s.Name != c)) return $"There is no character style called {c}.";
        try { Pattern(query, o); }
        catch (ArgumentException e) { return "That pattern doesn't read as a regular expression: " + e.Message.Split(" - ")[^1]; }
        return null;
    }

    /// <summary>Every place the query is, story by story in reading order (nothing, if the search can't be made).</summary>
    public static List<Match> FindAll(Document doc, string query, FindOptions o)
    {
        var found = new List<Match>();
        if (Problem(doc, query, o) is not null) return found;
        foreach (var id in StoryOrder(doc)) found.AddRange(InStory(doc, id, query, o));
        return found;
    }

    /// <summary>Every place the query is in one story.</summary>
    public static List<Match> InStory(Document doc, string id, string query, FindOptions o)
    {
        var found = new List<Match>();
        if (!doc.Stories.TryGetValue(id, out var story) || Problem(doc, query, o) is not null) return found;
        var text = story.Text;
        if (query.Length == 0)
        {
            // Every stretch of text in the style, within a paragraph.
            for (var i = 0; i < text.Length;)
            {
                if (text[i] == '\n' || !InStyle(doc, story, i, o)) { i++; continue; }
                var j = i;
                while (j < text.Length && text[j] != '\n' && InStyle(doc, story, j, o)) j++;
                found.Add(new Match(id, i, j - i));
                i = j;
            }
            return found;
        }
        if (Pattern(query, o) is { } regex)
        {
            try
            {
                foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
                    if (m.Length > 0 && Styled(doc, story, m.Index, m.Length, o)) found.Add(new Match(id, m.Index, m.Length));
            }
            catch (RegexMatchTimeoutException) { }
            return found;
        }
        for (var i = text.IndexOf(query, Comparison(o)); i >= 0; i = text.IndexOf(query, i + Math.Max(1, query.Length), Comparison(o)))
            if (IsMatch(text, i, query, o) && Styled(doc, story, i, query.Length, o)) found.Add(new Match(id, i, query.Length));
        return found;
    }

    private static bool InStyle(Document doc, Story story, int i, FindOptions o)
    {
        if (o.ParaStyle is { } p && story.Paras[story.ParagraphAt(i)].Style != p) return false;
        if (o.CharStyle is { } c && story.FormatAt(i).CharStyle != c) return false;
        return true;
    }

    /// <summary>Whether every character of a stretch is in the styles asked for.</summary>
    private static bool Styled(Document doc, Story story, int start, int length, FindOptions o)
    {
        if (!o.ByStyle) return true;
        for (var i = start; i < start + length; i++)
            if (story.Text[i] != '\n' && !InStyle(doc, story, i, o)) return false;
        return true;
    }

    /// <summary>Whether a stretch of a story is exactly one of the places the query is.</summary>
    public static bool IsFound(Document doc, Match m, string query, FindOptions o) => InStory(doc, m.StoryId, query, o).Contains(m);

    /// <summary>
    /// What a found stretch is replaced with: the replacement as it is, or, for a pattern, with its groups ($1, ${name},
    /// $0 for the whole) filled in and \n and \t as a new paragraph and a tab.
    /// </summary>
    public static string Replacement(Document doc, Match m, string query, string replacement, FindOptions o)
    {
        if (Pattern(query, o) is not { } regex || !doc.Stories.TryGetValue(m.StoryId, out var story)) return replacement;
        var found = regex.Match(story.Text, m.Start);
        if (!found.Success || found.Index != m.Start) return replacement;
        var escaped = replacement.Replace(@"\\", "\u0001").Replace(@"\n", "\n").Replace(@"\t", "\t").Replace("\u0001", @"\");
        return found.Result(escaped);
    }

    /// <summary>
    /// The next place the query is after <paramref name="from"/> in a story (or before it, going back), on round the
    /// other stories and back to the start: null if it is nowhere.
    /// </summary>
    public static Match? Next(Document doc, string query, FindOptions o, string? storyId, int from, bool forward = true)
    {
        var all = FindAll(doc, query, o);
        if (all.Count == 0) return null;
        var order = StoryOrder(doc);
        var at = storyId is null ? -1 : order.IndexOf(storyId);
        int Rank(Match m) => order.IndexOf(m.StoryId);
        if (forward)
            return all.FirstOrDefault(m => Rank(m) > at || (Rank(m) == at && m.Start >= from)) ?? all[0];
        return all.LastOrDefault(m => Rank(m) < at || (Rank(m) == at && m.Start + m.Length <= from && m.Start < from)) ?? all[^1];
    }
}
