using System.Globalization;

namespace AVAPageStream.Core;

/// <summary>An index entry marked in the text: a topic, a sub-topic under it (or none), and another topic to see (or none).</summary>
public sealed record IndexMark(string Topic, string Sub = "", string See = "")
{
    /// <summary>As it is kept in the marker's format: the three, separated by tabs.</summary>
    public string Key => Topic + "\t" + Sub + "\t" + See;

    public static IndexMark Parse(string key)
    {
        var parts = key.Split('\t');
        return new IndexMark(parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
    }
}

/// <summary>
/// An index: made from the entries marked in the text, alphabetical under a heading for each letter, each topic (and
/// sub-topic) with the pages its entries are set on, consecutive pages joined into ranges, and its see references.
/// </summary>
public static class Indexes
{
    public const string HeadingStyle = "Index Heading";
    public static string StyleFor(int level) => $"Index {level + 1}";

    /// <summary>Every marked entry in the document's pages, with the page (from 0) it is set on (null if overset).</summary>
    public static List<(IndexMark Mark, int? Page)> Entries(Document doc, ITextMeasurer measurer)
    {
        var pageOf = new Dictionary<string, int>();
        for (var i = 0; i < doc.Pages.Count; i++)
            foreach (var o in doc.Pages[i].All) pageOf[o.Id] = i;
        var found = new List<(IndexMark, int?)>();
        foreach (var id in Search.StoryOrder(doc))
        {
            var story = doc.Stories[id];
            if (!story.Text.Contains(Fields.IndexEntry)) continue;
            TextLayout? layout = null;
            var at = 0;
            foreach (var run in story.Runs)
            {
                if (run.Format.Index is { } key)
                    for (var i = at; i < at + run.Length && i < story.Text.Length; i++)
                    {
                        if (story.Text[i] != Fields.IndexEntry) continue;
                        layout ??= TextFlow.Layout(story, Chains.Areas(doc, story), measurer, null, story.Table is null ? doc.Setup.Baselines : null, Footnotes.Heights(doc, measurer));
                        int? page = layout.LineOf(i) is var li and >= 0 && pageOf.TryGetValue(Chains.ObjectOf(layout.Areas[layout.Lines[li].Area].FrameId), out var p) ? p : null;
                        // A footnote's entry is on the page its reference is.
                        if (page is null && story.NoteOf is { } parent && doc.Stories.TryGetValue(parent, out var ps) && ps.Runs.Any(r => r.Format.Note == id))
                            page = NotePage(doc, ps, id, measurer, pageOf);
                        found.Add((IndexMark.Parse(key), page));
                    }
                at += run.Length;
            }
        }
        return found;
    }

    private static int? NotePage(Document doc, Story parent, string noteId, ITextMeasurer m, Dictionary<string, int> pageOf)
    {
        var layout = TextFlow.Layout(parent, Chains.Areas(doc, parent), m, null, null, Footnotes.Heights(doc, m));
        var at = 0;
        foreach (var run in parent.Runs)
        {
            if (run.Format.Note == noteId && layout.LineOf(at) is var li and >= 0 && pageOf.TryGetValue(Chains.ObjectOf(layout.Areas[layout.Lines[li].Area].FrameId), out var p)) return p;
            at += run.Length;
        }
        return null;
    }

    /// <summary>Pages (from 0) as the index shows them: by their labels, consecutive ones joined ("4–6").</summary>
    public static string Pages(Document doc, IEnumerable<int> pages)
    {
        var sorted = pages.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? doc.PageLabel(sorted[i]) : doc.PageLabel(sorted[i]) + "–" + doc.PageLabel(sorted[j]));
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>The letter a topic is filed under: its first letter, upper case, or # for one starting otherwise.</summary>
    public static string Letter(string topic)
    {
        var t = topic.TrimStart();
        if (t.Length == 0 || !char.IsLetter(t[0])) return "#";
        var d = t[..1].Normalize(System.Text.NormalizationForm.FormD);
        return char.ToUpper(d[0], CultureInfo.CurrentCulture).ToString();
    }

    /// <summary>The index as paragraphs to place: letter headings, topics, and their sub-topics.</summary>
    public static List<PlacedPara> Paragraphs(Document doc, IEnumerable<(IndexMark Mark, int? Page)> entries)
    {
        var compare = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        var topics = entries.GroupBy(e => e.Mark.Topic.Trim(), compare).Where(g => g.Key.Length > 0)
            .OrderBy(g => g.Key, compare).ToList();
        var paras = new List<PlacedPara>();
        string? letter = null;
        foreach (var topic in topics)
        {
            var l = Letter(topic.Key);
            if (l != letter)
            {
                letter = l;
                paras.Add(new PlacedPara(HeadingStyle, [new PlacedRun(l)]));
            }
            var direct = topic.Where(e => e.Mark.Sub.Trim().Length == 0).ToList();
            var pages = Pages(doc, direct.Where(e => e.Mark.See.Length == 0).Select(e => e.Page).OfType<int>());
            var sees = topic.Select(e => e.Mark.See.Trim()).Where(s => s.Length > 0).Distinct(compare).Order(compare).ToList();
            // Spelt as most of its entries spell it.
            var name = topic.GroupBy(e => e.Mark.Topic.Trim()).OrderByDescending(g => g.Count()).First().Key;
            var line = name + (pages.Length > 0 ? ", " + pages : "");
            if (sees.Count > 0) line += (pages.Length > 0 || topic.Any(e => e.Mark.Sub.Trim().Length > 0) ? ". See also " : ". See ") + string.Join("; ", sees) + ".";
            paras.Add(new PlacedPara(StyleFor(0), [new PlacedRun(line)]));
            foreach (var sub in topic.Where(e => e.Mark.Sub.Trim().Length > 0).GroupBy(e => e.Mark.Sub.Trim(), compare).OrderBy(g => g.Key, compare))
            {
                var subPages = Pages(doc, sub.Select(e => e.Page).OfType<int>());
                paras.Add(new PlacedPara(StyleFor(1), [new PlacedRun(sub.Key + (subPages.Length > 0 ? ", " + subPages : ""))]));
            }
        }
        return paras;
    }

    /// <summary>The index's styles, from the body text's: headings bold, topics hanging, sub-topics indented.</summary>
    public static IEnumerable<ParagraphStyle> MakeStyles(Document doc)
    {
        var body = doc.Style("Body");
        yield return new ParagraphStyle(HeadingStyle, body.Char with { Font = Fonts.Sans, Bold = true, Size = body.Char.Size + 2 },
            body.Para with { Style = HeadingStyle, SpaceBefore = 10, SpaceAfter = 3, LeftIndent = 0, FirstIndent = 0, KeepWithNext = true });
        yield return new ParagraphStyle(StyleFor(0), body.Char, body.Para with { Style = StyleFor(0), LeftIndent = 12, FirstIndent = -12, SpaceBefore = 0, SpaceAfter = 1, Align = Align.Left });
        yield return new ParagraphStyle(StyleFor(1), body.Char, body.Para with { Style = StyleFor(1), LeftIndent = 24, FirstIndent = -12, SpaceBefore = 0, SpaceAfter = 1, Align = Align.Left });
    }
}
