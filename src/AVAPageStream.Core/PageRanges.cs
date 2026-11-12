namespace AVAPageStream.Core;

/// <summary>Reading a typed list of pages to export: "1-3, 5, 8-" (an open end runs to the last page).</summary>
public static class PageRanges
{
    /// <summary>
    /// The pages a range names, counted from 0, in order and each once; or false with a reason. Pages are typed
    /// counting from 1, as the window shows them.
    /// </summary>
    public static bool TryParse(string? text, int count, out List<int> pages, out string? error)
    {
        pages = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Type the pages to export, such as 1-3, 5."; return false; }
        var chosen = new SortedSet<int>();
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('-', StringSplitOptions.TrimEntries);
            int from, to;
            if (parts.Length == 1 && int.TryParse(parts[0], out from)) to = from;
            else if (parts.Length == 2 && Bound(parts[0], 1, out from) && Bound(parts[1], count, out to)) { }
            else { error = $"\"{raw}\" isn't a page or a range of pages."; return false; }
            if (from < 1 || to > count || from > to)
            {
                error = count == 1 ? "The document has only page 1." : $"Pages go from 1 to {count}; \"{raw}\" is outside that.";
                return false;
            }
            for (var p = from; p <= to; p++) chosen.Add(p - 1);
        }
        if (chosen.Count == 0) { error = "Type the pages to export, such as 1-3, 5."; return false; }
        pages = [.. chosen];
        return true;
    }

    /// <summary>One end of a range: a page number, or nothing for the first or last page.</summary>
    private static bool Bound(string text, int open, out int page)
    {
        if (text.Length == 0) { page = open; return true; }
        return int.TryParse(text, out page);
    }
}
