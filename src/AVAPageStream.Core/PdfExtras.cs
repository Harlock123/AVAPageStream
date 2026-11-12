using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace AVAPageStream.Core;

/// <summary>A heading as a PDF bookmark: its text, level, the page it is on (from 0) and how far down it its top is.</summary>
public sealed record Bookmark(string Title, int Level, int Page, double Y);

/// <summary>A part of a page that links somewhere: to a web address, or to a bookmark (by its number).</summary>
public sealed record PdfLink(Box Box, string? Url, int? Bookmark);

/// <summary>An outline entry as written into a PDF: its title and level, the PDF page (from 0) and the top in PDF units.</summary>
public sealed record OutlineEntry(string Title, int Level, int Page, double Top);

/// <summary>
/// What a PDF gets beyond its pages: bookmarks made from the headings, links from a table of contents to the headings
/// and from web addresses to the web, and (by an update appended to the finished file, since SkiaSharp writes
/// neither) the outline of bookmarks and the trim and bleed boxes printers use.
/// </summary>
public static partial class PdfExtras
{
    /// <summary>The styles headings are in, by level: Heading, then Subheading, then any "Heading 2", "Heading 3"…</summary>
    public static List<string> OutlineStyles(Document doc)
    {
        var styles = new List<string>();
        foreach (var name in new[] { "Heading", "Subheading" })
            if (doc.Styles.Any(s => s.Name == name)) styles.Add(name);
        styles.AddRange(doc.Styles.Select(s => s.Name).Where(n => HeadingN().IsMatch(n)).OrderBy(n => int.Parse(n[8..], CultureInfo.InvariantCulture)));
        return styles;
    }

    [GeneratedRegex(@"^Heading \d$")]
    private static partial Regex HeadingN();

    [GeneratedRegex(@"(https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    /// <summary>The document's headings, in page order, as bookmarks (those set on no page are left out).</summary>
    public static List<Bookmark> Bookmarks(Document doc, ITextMeasurer m) =>
        Contents.Placed(doc, OutlineStyles(doc), m).Where(p => p.Entry.Page is not null)
            .Select(p => new Bookmark(p.Entry.Text, p.Entry.Level, p.Entry.Page!.Value - 1, p.Y)).ToList();

    /// <summary>
    /// The links on a page: web addresses in its text, and the entries of a table of contents (paragraphs in the
    /// "Contents" styles), each to the heading of the same words. Boxes are in the page's points.
    /// </summary>
    public static List<PdfLink> Links(Document doc, int page, IReadOnlyList<Bookmark> marks, Func<string, TextLayout?> layoutOf)
    {
        var links = new List<PdfLink>();
        var frames = doc.Pages[page].All.OfType<TextFrame>().Where(f => f.Rotation == 0).Select(f => f.Id).ToHashSet();
        var stories = doc.Pages[page].All.OfType<TextFrame>().Select(f => f.StoryId).Distinct();
        var used = new HashSet<int>();
        foreach (var id in stories)
        {
            if (!doc.Stories.TryGetValue(id, out var story) || layoutOf(id) is not { } layout) continue;
            IEnumerable<Box> Boxes(int start, int end) =>
                layout.SelectionBoxes(start, end).Where(b => frames.Contains(Chains.ObjectOf(layout.Areas[b.Area].FrameId))).Select(b => b.Box);
            foreach (RegexMatch m in Address().Matches(story.Text))
            {
                var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"');
                var target = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
                foreach (var b in Boxes(m.Index, m.Index + url.Length)) links.Add(new PdfLink(b, target, null));
            }
            for (var p = 0; p < story.ParagraphCount; p++)
            {
                if (!story.Paras[p].Style.StartsWith("Contents ", StringComparison.Ordinal)) continue;
                var (start, end) = story.ParagraphRange(p);
                var text = layout.Display(start, end - start);
                var tab = text.LastIndexOf('\t');
                var title = (tab >= 0 ? text[..tab] : text).Trim();
                var to = Enumerable.Range(0, marks.Count).FirstOrDefault(i => marks[i].Title == title && !used.Contains(i), -1);
                if (to < 0) continue;
                used.Add(to);
                foreach (var b in Boxes(start, end)) links.Add(new PdfLink(b, null, to));
            }
        }
        return links;
    }

    /// <summary>
    /// Adds an outline of bookmarks and trim and bleed boxes to a finished PDF, as an update appended to it (the file
    /// before it is left as it was). Boxes are in PDF units from the page's bottom-left, one pair per page or none.
    /// Returns the PDF unchanged if there is nothing to add, or if it isn't laid out as expected.
    /// </summary>
    public static byte[] Finish(byte[] pdf, IReadOnlyList<OutlineEntry> outline, IReadOnlyList<(Box Trim, Box Bleed)>? boxes = null)
    {
        if (outline.Count == 0 && boxes is null) return pdf;
        var t = Encoding.Latin1.GetString(pdf);
        var startxref = Regex.Matches(t, @"startxref\s+(\d+)").LastOrDefault();
        var trailer = Regex.Matches(t, @"trailer\s*<<(.*?)>>\s*startxref", RegexOptions.Singleline).LastOrDefault();
        if (startxref is null || trailer is null) return pdf;
        int? Ref(string dict, string key) => Regex.Match(dict, @"/" + key + @"\s+(\d+)\s+0\s+R") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var size = Regex.Match(trailer.Groups[1].Value, @"/Size\s+(\d+)") is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        if (Ref(trailer.Groups[1].Value, "Root") is not { } root || size == 0) return pdf;
        var info = Ref(trailer.Groups[1].Value, "Info");

        var objects = new Dictionary<int, string>();
        foreach (RegexMatch m in Regex.Matches(t, @"(?<=^|[\r\n])(\d+) 0 obj\s*(.*?)endobj", RegexOptions.Singleline))
            objects[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Groups[2].Value.TrimEnd();
        if (!objects.TryGetValue(root, out var catalog) || Ref(catalog, "Pages") is not { } pagesRoot) return pdf;
        var pages = new List<int>();
        void Walk(int n, int depth)
        {
            if (depth > 32 || !objects.TryGetValue(n, out var o)) return;
            if (Regex.IsMatch(o, @"/Type\s*/Pages\b"))
            {
                var kids = Regex.Match(o, @"/Kids\s*\[(.*?)\]", RegexOptions.Singleline).Groups[1].Value;
                foreach (RegexMatch k in Regex.Matches(kids, @"(\d+)\s+0\s+R")) Walk(int.Parse(k.Groups[1].Value, CultureInfo.InvariantCulture), depth + 1);
            }
            else if (Regex.IsMatch(o, @"/Type\s*/Page\b")) pages.Add(n);
        }
        Walk(pagesRoot, 0);
        if (pages.Count == 0) return pdf;

        var written = new SortedDictionary<int, string>();
        var next = size;
        static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string Insert(string dict, string more) => dict.EndsWith(">>", StringComparison.Ordinal) ? dict[..^2] + more + ">>" : dict;

        if (outline.Count > 0)
        {
            // Each entry under the last one before it of a lower level.
            var ids = outline.Select(_ => next++).ToArray();
            var top = next++;
            var parent = new int[outline.Count];
            var stack = new List<int>();
            for (var i = 0; i < outline.Count; i++)
            {
                while (stack.Count > 0 && outline[stack[^1]].Level >= outline[i].Level) stack.RemoveAt(stack.Count - 1);
                parent[i] = stack.Count > 0 ? stack[^1] : -1;
                stack.Add(i);
            }
            int ParentId(int i) => parent[i] < 0 ? top : ids[parent[i]];
            List<int> Children(int p) => Enumerable.Range(0, outline.Count).Where(i => parent[i] == p).ToList();
            int Descendants(int p) => Children(p).Sum(c => 1 + Descendants(c));
            for (var i = 0; i < outline.Count; i++)
            {
                var e = outline[i];
                var siblings = Children(parent[i]);
                var at = siblings.IndexOf(i);
                var kids = Children(i);
                var page = pages[Math.Clamp(e.Page, 0, pages.Count - 1)];
                var sb = new StringBuilder($"<</Title {PdfString(e.Title)}\n/Parent {ParentId(i)} 0 R\n/Dest [{page} 0 R /XYZ 0 {Num(e.Top)} null]");
                if (at > 0) sb.Append($"\n/Prev {ids[siblings[at - 1]]} 0 R");
                if (at < siblings.Count - 1) sb.Append($"\n/Next {ids[siblings[at + 1]]} 0 R");
                if (kids.Count > 0) sb.Append($"\n/First {ids[kids[0]]} 0 R\n/Last {ids[kids[^1]]} 0 R\n/Count {Descendants(i)}");
                written[ids[i]] = sb.Append(">>").ToString();
            }
            var firsts = Children(-1);
            written[top] = $"<</Type /Outlines\n/First {ids[firsts[0]]} 0 R\n/Last {ids[firsts[^1]]} 0 R\n/Count {outline.Count}>>";
            written[root] = Insert(catalog, $"\n/Outlines {top} 0 R\n/PageMode /UseOutlines");
        }
        if (boxes is not null)
            for (var i = 0; i < Math.Min(boxes.Count, pages.Count); i++)
            {
                var (trim, bleed) = boxes[i];
                string Arr(Box b) => $"[{Num(b.X)} {Num(b.Y)} {Num(b.Right)} {Num(b.Bottom)}]";
                written[pages[i]] = Insert(objects[pages[i]], $"\n/TrimBox {Arr(trim)}\n/BleedBox {Arr(bleed)}");
            }

        // The update: the objects, a cross-reference section for them, and a trailer pointing back at the last.
        var body = new StringBuilder("\n");
        var offsets = new Dictionary<int, long>();
        foreach (var (n, text) in written)
        {
            offsets[n] = pdf.Length + Encoding.Latin1.GetByteCount(body.ToString());
            body.Append(CultureInfo.InvariantCulture, $"{n} 0 obj\n{text}\nendobj\n");
        }
        var xref = pdf.Length + Encoding.Latin1.GetByteCount(body.ToString());
        body.Append("xref\n");
        foreach (var (n, at) in offsets) body.Append(CultureInfo.InvariantCulture, $"{n} 1\n{at:D10} 00000 n \n");
        body.Append(CultureInfo.InvariantCulture, $"trailer\n<</Size {Math.Max(size, next)}\n/Root {root} 0 R{(info is { } i2 ? $"\n/Info {i2} 0 R" : "")}\n/Prev {startxref.Groups[1].Value}>>\nstartxref\n{xref}\n%%EOF\n");
        return [.. pdf, .. Encoding.Latin1.GetBytes(body.ToString())];
    }

    /// <summary>Text as a PDF string: plain where it can be, UTF-16 otherwise.</summary>
    public static string PdfString(string s)
    {
        if (s.All(c => c is >= ' ' and <= '~'))
            return "(" + s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        var bytes = Encoding.BigEndianUnicode.GetBytes(s);
        return "<FEFF" + Convert.ToHexString(bytes) + ">";
    }
}
