namespace AVAPageStream.Core;

/// <summary>A paragraph for a table of contents: its text, its level (by the styles chosen), and the page it is on (null if it is overset).</summary>
public sealed record ContentsEntry(string Text, int Level, int? Page);

/// <summary>Building a table of contents from the paragraphs in chosen styles, with the pages they are set on.</summary>
public static class Contents
{
    /// <summary>
    /// The paragraphs on the pages (not the masters) in <paramref name="styles"/>, the first style level 0, in page
    /// order and then reading order, each with the page it starts on, as the stories are set now.
    /// </summary>
    public static List<ContentsEntry> Find(Document doc, IReadOnlyList<string> styles, ITextMeasurer measurer)
    {
        var order = Search.StoryOrder(doc);
        var pageOf = new Dictionary<string, int>();
        for (var i = 0; i < doc.Pages.Count; i++)
            foreach (var f in doc.Pages[i].All.OfType<TextFrame>()) pageOf[f.Id] = i + 1;
        var found = new List<(ContentsEntry Entry, int Story, int Start)>();
        for (var k = 0; k < order.Count; k++)
        {
            var story = doc.Stories[order[k]];
            if (story.Frames.Count == 0 || !pageOf.ContainsKey(story.Frames[0])) continue; // a master's story
            var fields = new FieldContext(Chains.PageOf(doc, story), doc.Pages.Count);
            var layout = TextFlow.Layout(story, Chains.Areas(doc, story), measurer, fields);
            for (var p = 0; p < story.ParagraphCount; p++)
            {
                var level = IndexOf(styles, story.Paras[p].Style);
                if (level < 0) continue;
                var (start, end) = story.ParagraphRange(p);
                var text = Fields.Expand(story.Text[start..end], fields).Replace('\t', ' ').Trim();
                if (text.Length == 0) continue;
                var line = layout.LineOf(start);
                int? page = line >= 0 && pageOf.TryGetValue(layout.Areas[layout.Lines[line].Area].FrameId, out var n) ? n : null;
                found.Add((new ContentsEntry(text, level, page), k, start));
            }
        }
        return found.OrderBy(f => f.Entry.Page ?? int.MaxValue).ThenBy(f => f.Story).ThenBy(f => f.Start).Select(f => f.Entry).ToList();
    }

    private static int IndexOf(IReadOnlyList<string> styles, string style)
    {
        for (var i = 0; i < styles.Count; i++)
            if (styles[i] == style) return i;
        return -1;
    }

    /// <summary>The name of the style a table of contents sets its entries of a level in: "Contents 1" for level 0.</summary>
    public static string StyleFor(int level) => $"Contents {level + 1}";

    /// <summary>
    /// The style for a level of contents: the body text's, indented a step a level, with a right tab and dot leader
    /// at <paramref name="width"/> (the column's width) for the page number.
    /// </summary>
    public static ParagraphStyle MakeStyle(Document doc, int level, double width)
    {
        var body = doc.Style("Body");
        return new ParagraphStyle(StyleFor(level), body.Char with { Bold = level == 0 },
            body.Para with { Style = StyleFor(level), LeftIndent = 14 * level, FirstIndent = 0, SpaceBefore = level == 0 ? 6 : 0, SpaceAfter = 2,
                Align = Align.Left, Tabs = Tabs.Store([new TabStop(Math.Max(36, width), TabKind.Right, '.')]) });
    }

    /// <summary>The entries as paragraphs to place: the text, a tab, the page number.</summary>
    public static List<PlacedPara> Paragraphs(IEnumerable<ContentsEntry> entries) =>
        entries.Select(e => new PlacedPara(StyleFor(e.Level), [new PlacedRun(e.Text + "\t" + (e.Page?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "–"))])).ToList();
}
