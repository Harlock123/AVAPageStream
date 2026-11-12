namespace AVAPageStream.Core;

/// <summary>A stretch of a story's text that a search found.</summary>
public sealed record Match(string StoryId, int Start, int Length);

/// <summary>How to compare: with or without case, and anywhere or only as a whole word.</summary>
public sealed record FindOptions(bool MatchCase = false, bool WholeWord = false);

/// <summary>Finding text through every story, in reading order: the pages' stories first, then the masters'.</summary>
public static class Search
{
    /// <summary>The stories in the order their first frames come: page by page, top-most object last, then the master pages.</summary>
    public static List<string> StoryOrder(Document doc)
    {
        var order = new List<string>();
        var seen = new HashSet<string>();
        foreach (var sheet in doc.Pages.Cast<Sheet>().Concat(doc.Sheets.Where(s => s is not Page)))
            foreach (var frame in sheet.All.OfType<TextFrame>())
                if (doc.Stories.ContainsKey(frame.StoryId) && seen.Add(frame.StoryId)) order.Add(frame.StoryId);
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

    /// <summary>Every place the query is, story by story in reading order.</summary>
    public static List<Match> FindAll(Document doc, string query, FindOptions o)
    {
        var found = new List<Match>();
        if (query.Length == 0) return found;
        foreach (var id in StoryOrder(doc))
        {
            var text = doc.Stories[id].Text;
            for (var i = text.IndexOf(query, Comparison(o)); i >= 0; i = text.IndexOf(query, i + Math.Max(1, query.Length), Comparison(o)))
                if (IsMatch(text, i, query, o)) found.Add(new Match(id, i, query.Length));
        }
        return found;
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
