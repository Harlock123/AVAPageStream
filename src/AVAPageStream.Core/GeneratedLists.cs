namespace AVAPageStream.Core;

public enum ListKind { Contents, Index }

/// <summary>
/// A table of contents or an index, as the document remembers it: what kind, the paragraph styles a contents lists
/// (the first the top level), and the width its page numbers' tab goes to. Its text starts at a marker
/// (<see cref="Fields.ListStart"/>, whose format names it) and runs on through the paragraphs after in its own styles.
/// </summary>
public sealed record GeneratedList(string Id, ListKind Kind, IReadOnlyList<string> Styles, double Width = 300)
{
    public bool Equals(GeneratedList? other) => other is not null && Id == other.Id && Kind == other.Kind && Width == other.Width && Styles.SequenceEqual(other.Styles);
    public override int GetHashCode() => HashCode.Combine(Id, Kind, Width, Styles.Count);
}

/// <summary>Finding a document's contents and indexes, and what each would say if it were made again now.</summary>
public static class GeneratedLists
{
    /// <summary>The paragraph styles a list's own text is set in.</summary>
    public static IEnumerable<string> StylesOf(GeneratedList list) => list.Kind == ListKind.Index
        ? [Indexes.HeadingStyle, Indexes.StyleFor(0), Indexes.StyleFor(1)]
        : Enumerable.Range(0, Math.Max(1, list.Styles.Count)).Select(Contents.StyleFor);

    /// <summary>
    /// Where a list's text is: its story, from the start of the paragraph its marker is in to the end of the last
    /// paragraph after it in the list's styles (not taking that last paragraph's end). Null if its marker is gone.
    /// </summary>
    public static (string StoryId, int Start, int End)? Where(Document doc, GeneratedList list)
    {
        foreach (var (id, story) in doc.Stories)
        {
            var at = 0;
            foreach (var run in story.Runs)
            {
                if (run.Format.List == list.Id)
                    for (var i = at; i < at + run.Length && i < story.Text.Length; i++)
                    {
                        if (story.Text[i] != Fields.ListStart) continue;
                        var styles = StylesOf(list).ToHashSet();
                        var first = story.ParagraphAt(i);
                        var last = first;
                        while (last + 1 < story.ParagraphCount && styles.Contains(story.Paras[last + 1].Style)) last++;
                        return (id, story.ParagraphRange(first).Start, story.ParagraphRange(last).End);
                    }
                at += run.Length;
            }
        }
        return null;
    }

    /// <summary>What a list would say if it were made now, as paragraphs to place (one saying so, if there is nothing to list).</summary>
    public static List<PlacedPara> Make(Document doc, GeneratedList list, ITextMeasurer measurer)
    {
        var paras = list.Kind == ListKind.Index
            ? Indexes.Paragraphs(doc, Indexes.Entries(doc, measurer))
            : Contents.Paragraphs(Contents.Find(doc, list.Styles, measurer));
        if (paras.Count == 0) paras.Add(new PlacedPara(StylesOf(list).ElementAt(list.Kind == ListKind.Index ? 1 : 0), [new PlacedRun(list.Kind == ListKind.Index ? "(No index entries are marked.)" : "(No headings to list.)")]));
        return paras;
    }

    /// <summary>A list's text as it is now, paragraph by paragraph with their styles, to compare with what it would be.</summary>
    public static List<(string Style, string Text)> Now(Document doc, GeneratedList list)
    {
        if (Where(doc, list) is not var (id, start, end)) return [];
        var story = doc.Stories[id];
        var found = new List<(string, string)>();
        for (var p = story.ParagraphAt(start); p <= story.ParagraphAt(end); p++)
        {
            var (s, e) = story.ParagraphRange(p);
            found.Add((story.Paras[p].Style, Fields.Expand(story.Text[s..e], null)));
        }
        return found;
    }

    /// <summary>Whether a list would say something else if it were made now.</summary>
    public static bool IsStale(Document doc, GeneratedList list, ITextMeasurer measurer) =>
        !Now(doc, list).SequenceEqual(Make(doc, list, measurer).Select(p => (p.Style, string.Concat(p.Runs.Select(r => r.Text)))));

    /// <summary>The list a place in a story is in, if it is in one.</summary>
    public static GeneratedList? At(Document doc, string storyId, int index) =>
        doc.Lists.FirstOrDefault(l => Where(doc, l) is var (id, s, e) && id == storyId && index >= s && index <= e);
}
