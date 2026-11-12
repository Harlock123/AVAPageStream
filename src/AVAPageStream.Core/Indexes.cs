using System.Globalization;

namespace AVAPageStream.Core;

/// <summary>How far an index entry reaches: only where it is, to the end of its paragraph, or to a named place.</summary>
public enum IndexCovers { Here, Paragraph, ToPlace }

/// <summary>
/// An index entry marked in the text: a topic, a sub-topic under it and a sub-sub-topic under that (or none), another
/// topic to see instead (or none), and how far it reaches (<see cref="Place"/> naming the place, for ToPlace).
/// </summary>
public sealed record IndexMark(string Topic, string Sub = "", string See = "", string Sub2 = "", IndexCovers Covers = IndexCovers.Here, string Place = "")
{
    /// <summary>As it is kept in the marker's format: topic, sub-topic, see, sub-sub-topic and reach, separated by tabs.</summary>
    public string Key
    {
        get
        {
            var covers = Covers switch { IndexCovers.Paragraph => "¶", IndexCovers.ToPlace => "@" + Place, _ => "" };
            var key = Topic + "\t" + Sub + "\t" + See;
            return Sub2.Length == 0 && covers.Length == 0 ? key : key + "\t" + Sub2 + "\t" + covers;
        }
    }

    public static IndexMark Parse(string key)
    {
        var parts = key.Split('\t');
        string At(int i) => parts.Length > i ? parts[i] : "";
        var covers = At(4);
        return new IndexMark(At(0), At(1), At(2), At(3),
            covers == "¶" ? IndexCovers.Paragraph : covers.StartsWith('@') ? IndexCovers.ToPlace : IndexCovers.Here,
            covers.StartsWith('@') ? covers[1..] : "");
    }

    /// <summary>The topic and the sub-topics under it, as far down as they go.</summary>
    public IReadOnlyList<string> Path
    {
        get
        {
            var (t, s, s2) = (Topic.Trim(), Sub.Trim(), Sub2.Trim());
            return s.Length == 0 ? [t] : s2.Length == 0 ? [t, s] : [t, s, s2];
        }
    }
}

/// <summary>An index entry as found: its mark, the page (from 0) it starts on, and the page it reaches to (null if overset).</summary>
public sealed record IndexHit(IndexMark Mark, int? Page, int? To = null)
{
    public IEnumerable<int> Pages => Page is not { } p ? [] : To is { } t && t > p ? Enumerable.Range(p, t - p + 1) : [p];
}

/// <summary>
/// An index: made from the entries marked in the text, alphabetical under a heading for each letter, each topic (and
/// sub-topic, and sub-sub-topic) with the pages its entries are set on, consecutive pages joined into ranges, and its
/// see references.
/// </summary>
public static class Indexes
{
    public const string HeadingStyle = "Index Heading";
    public const int Levels = 3;
    public static string StyleFor(int level) => $"Index {level + 1}";

    /// <summary>Every marked entry in the document's stories, with the pages it is set on.</summary>
    public static List<IndexHit> Entries(Document doc, ITextMeasurer measurer)
    {
        var pages = new Pager(doc, measurer);
        var places = References.Places(doc);
        var found = new List<IndexHit>();
        foreach (var id in Search.StoryOrder(doc))
        {
            var story = doc.Stories[id];
            if (!story.Text.Contains(Fields.IndexEntry)) continue;
            var at = 0;
            foreach (var run in story.Runs)
            {
                if (run.Format.Index is { } key)
                    for (var i = at; i < at + run.Length && i < story.Text.Length; i++)
                    {
                        if (story.Text[i] != Fields.IndexEntry) continue;
                        var mark = IndexMark.Parse(key);
                        var page = pages.At(id, i);
                        int? to = mark.Covers switch
                        {
                            IndexCovers.Paragraph => pages.At(id, Math.Max(i, story.ParagraphRange(story.ParagraphAt(i)).End - 1)),
                            IndexCovers.ToPlace when places.FirstOrDefault(p => p.Name == mark.Place) is { } place => pages.At(place.StoryId, place.Index),
                            _ => null,
                        };
                        // A range that runs backwards (a place before the entry) runs from the place instead.
                        if (page is { } a && to is { } b && b < a) (page, to) = (b, a);
                        found.Add(new IndexHit(mark, page, to));
                    }
                at += run.Length;
            }
        }
        return found;
    }

    /// <summary>The page (from 0) a place in a story is set on, laying each story out once.</summary>
    private sealed class Pager(Document doc, ITextMeasurer m)
    {
        private readonly Dictionary<string, TextLayout> _layouts = [];
        private readonly Dictionary<string, int> _pageOf = Map(doc);

        private static Dictionary<string, int> Map(Document doc)
        {
            var map = new Dictionary<string, int>();
            for (var i = 0; i < doc.Pages.Count; i++)
                foreach (var o in doc.Pages[i].All) map[o.Id] = i;
            return map;
        }

        public int? At(string storyId, int index)
        {
            if (!doc.Stories.TryGetValue(storyId, out var story)) return null;
            if (!_layouts.TryGetValue(storyId, out var layout))
                _layouts[storyId] = layout = TextFlow.Layout(story, Chains.Areas(doc, story), m, null, story.Table is null ? doc.Setup.Baselines : null, Footnotes.Heights(doc, m));
            if (layout.LineOf(index) is var li and >= 0 && _pageOf.TryGetValue(Chains.ObjectOf(layout.Areas[layout.Lines[li].Area].FrameId), out var p)) return p;
            // A footnote's text is on the page its reference is.
            if (story.NoteOf is { } parent && doc.Stories.TryGetValue(parent, out var ps))
            {
                var at = 0;
                foreach (var run in ps.Runs)
                {
                    if (run.Format.Note == storyId) return At(parent, at);
                    at += run.Length;
                }
            }
            return null;
        }
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

    /// <summary>The index as paragraphs to place: letter headings, topics, and the sub-topics under them.</summary>
    public static List<PlacedPara> Paragraphs(Document doc, IEnumerable<IndexHit> entries)
    {
        var compare = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        var paras = new List<PlacedPara>();
        string? letter = null;
        void Level(List<IndexHit> hits, int depth)
        {
            foreach (var group in hits.GroupBy(h => h.Mark.Path[depth], compare).OrderBy(g => g.Key, compare))
            {
                if (depth == 0 && Letter(group.Key) is var l && l != letter)
                {
                    letter = l;
                    paras.Add(new PlacedPara(HeadingStyle, [new PlacedRun(l)]));
                }
                var here = group.Where(h => h.Mark.Path.Count == depth + 1).ToList();
                var deeper = group.Where(h => h.Mark.Path.Count > depth + 1).ToList();
                var pages = Pages(doc, here.Where(h => h.Mark.See.Trim().Length == 0).SelectMany(h => h.Pages));
                var sees = here.Select(h => h.Mark.See.Trim()).Where(s => s.Length > 0).Distinct(compare).Order(compare).ToList();
                // Spelt as most of its entries spell it.
                var name = group.GroupBy(h => h.Mark.Path[depth]).OrderByDescending(g => g.Count()).First().Key;
                var line = name + (pages.Length > 0 ? ", " + pages : "");
                if (sees.Count > 0) line += (pages.Length > 0 || deeper.Count > 0 ? ". See also " : ". See ") + string.Join("; ", sees) + ".";
                paras.Add(new PlacedPara(StyleFor(depth), [new PlacedRun(line)]));
                if (deeper.Count > 0) Level(deeper, depth + 1);
            }
        }
        Level(entries.Where(e => e.Mark.Topic.Trim().Length > 0).ToList(), 0);
        return paras;
    }

    /// <summary>The index's styles, from the body text's: headings bold, topics hanging, sub-topics indented a step a level.</summary>
    public static IEnumerable<ParagraphStyle> MakeStyles(Document doc)
    {
        var body = doc.Style("Body");
        yield return new ParagraphStyle(HeadingStyle, body.Char with { Font = Fonts.Sans, Bold = true, Size = body.Char.Size + 2 },
            body.Para with { Style = HeadingStyle, SpaceBefore = 10, SpaceAfter = 3, LeftIndent = 0, FirstIndent = 0, KeepWithNext = true });
        for (var level = 0; level < Levels; level++)
            yield return new ParagraphStyle(StyleFor(level), body.Char,
                body.Para with { Style = StyleFor(level), LeftIndent = 12 * (level + 1), FirstIndent = -12, SpaceBefore = 0, SpaceAfter = 1, Align = Align.Left });
    }
}
