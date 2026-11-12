namespace AVAPageStream.Core;

/// <summary>A font's height above and below the baseline at a format's size, in points.</summary>
public readonly record struct FontMetrics(double Ascent, double Descent);

/// <summary>
/// Measures text in a format. The program measures with the real fonts; tests use fixed widths. Widths are the
/// advance of the whole string, trailing spaces included, before tracking.
/// </summary>
public interface ITextMeasurer
{
    double Width(string text, CharFormat format);
    FontMetrics Metrics(CharFormat format);
}

/// <summary>
/// A box text may fill: one column of one frame in a story's chain, and the boxes in it text must keep out of
/// (objects with text wrap, already widened by their standoff).
/// </summary>
public sealed record Area(Box Box, string FrameId, int Column, IReadOnlyList<Box>? Exclusions = null)
{
    public IReadOnlyList<Box> Keep => Exclusions ?? [];

    public bool Equals(Area? other) =>
        other is not null && Box == other.Box && FrameId == other.FrameId && Column == other.Column && Keep.SequenceEqual(other.Keep);

    public override int GetHashCode() => HashCode.Combine(Box, FrameId, Column, Keep.Count);
}

/// <summary>What the page-number and page-count fields show: the page a story is drawn on (0 on a master page itself), and how many pages there are.</summary>
public sealed record FieldContext(int Page, int Pages);

/// <summary>
/// Fields: characters in a story that stand for something worked out when it is set, such as the number of the page
/// a master's footer is drawn on.
/// </summary>
public static class Fields
{
    public const char PageNumber = '\uE000';
    public const char PageCount = '\uE001';

    public static bool IsField(char c) => c is PageNumber or PageCount;

    /// <summary>Text with its fields replaced by what they show ("#" for a page number on a master page itself).</summary>
    public static string Expand(string text, FieldContext? context)
    {
        if (text.IndexOfAny([PageNumber, PageCount]) < 0) return text;
        var page = context is { Page: > 0 } c ? c.Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : "#";
        var pages = context is { Pages: > 0 } d ? d.Pages.ToString(System.Globalization.CultureInfo.InvariantCulture) : "#";
        return text.Replace(PageNumber.ToString(), page).Replace(PageCount.ToString(), pages);
    }
}

/// <summary>A piece of a line in one format: the characters from <see cref="Start"/>, set at <see cref="X"/>.</summary>
public sealed record Piece(int Start, int Length, double X, double Width, CharFormat Format);

/// <summary>
/// One line of text: in <see cref="Area"/>, holding the characters from <see cref="Start"/> to <see cref="End"/>
/// (its paragraph's '\n' not counted), set on <see cref="Baseline"/>.
/// </summary>
public sealed record Line(
    int Area,
    int Start,
    int End,
    double Baseline,
    double Ascent,
    double Descent,
    double X,
    int Paragraph,
    bool LastOfParagraph,
    IReadOnlyList<Piece> Pieces);

/// <summary>
/// How a story is set in its areas: its lines in order, and where the text that did not fit starts. Also answers where
/// a caret goes and which character a point is over.
/// </summary>
public sealed class TextLayout
{
    private readonly ITextMeasurer _measurer;

    internal TextLayout(Story story, IReadOnlyList<Area> areas, List<Line> lines, int oversetFrom, ITextMeasurer measurer, FieldContext? fields)
    {
        Story = story;
        Fields = fields;
        Areas = areas;
        Lines = lines;
        OversetFrom = oversetFrom;
        _measurer = measurer;
    }

    public Story Story { get; }

    /// <summary>What this layout's fields show.</summary>
    public FieldContext? Fields { get; }

    /// <summary>A stretch of the story as it is set: its fields replaced by what they show.</summary>
    public string Display(int start, int length) => Core.Fields.Expand(Story.Slice(start, length), Fields);

    public IReadOnlyList<Area> Areas { get; }
    public IReadOnlyList<Line> Lines { get; }

    /// <summary>Where the text that did not fit starts: the story's length when it all fits.</summary>
    public int OversetFrom { get; }

    public bool Overset => OversetFrom < Story.Length;

    /// <summary>
    /// The last caret position on a line. A soft-wrapped line's end is the next line's start, so it stops before
    /// that, except on the last line set.
    /// </summary>
    private int LastPosition(int lineIndex)
    {
        var line = Lines[lineIndex];
        return line.LastOfParagraph || lineIndex == Lines.Count - 1 ? line.End : Math.Max(line.Start, line.End - 1);
    }

    /// <summary>The line a caret at <paramref name="index"/> sits on, or -1 if that text is overset (or there are no lines).</summary>
    public int LineOf(int index)
    {
        for (var i = 0; i < Lines.Count; i++)
            if (index >= Lines[i].Start && index <= LastPosition(i)) return i;
        return -1;
    }

    /// <summary>The X of a caret at <paramref name="index"/> on line <paramref name="lineIndex"/>.</summary>
    public double CaretX(int lineIndex, int index)
    {
        var line = Lines[lineIndex];
        if (line.Pieces.Count == 0) return line.X;
        foreach (var piece in line.Pieces)
            if (index >= piece.Start && index < piece.Start + piece.Length)
                return piece.X + TextFlow.Measure(_measurer, Display(piece.Start, index - piece.Start), piece.Format);
        if (index < line.Pieces[0].Start) return line.Pieces[0].X;
        var last = line.Pieces[^1];
        return last.X + last.Width;
    }

    /// <summary>The caret position on a line nearest to <paramref name="x"/>.</summary>
    public int IndexAtX(int lineIndex, double x)
    {
        var line = Lines[lineIndex];
        var best = line.Start;
        var bestDistance = double.MaxValue;
        for (var i = line.Start; i <= LastPosition(lineIndex); i++)
        {
            var d = Math.Abs(CaretX(lineIndex, i) - x);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>The character a point in one of the story's frames is nearest to, or null if the frame has no lines.</summary>
    public int? IndexAt(string frameId, Pt p)
    {
        Line? nearest = null;
        var nearestIndex = -1;
        var nearestDistance = (double.MaxValue, double.MaxValue);
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var area = Areas[line.Area];
            if (area.FrameId != frameId) continue;
            var top = line.Baseline - line.Ascent;
            var bottom = line.Baseline + line.Descent;
            var dy = p.Y < top ? top - p.Y : p.Y > bottom ? p.Y - bottom : 0;
            // Beside text wrap a line may be one of several at a baseline, so measure to the line's own ends.
            var x0 = line.Pieces.Count > 0 ? line.Pieces[0].X : area.Box.X;
            var x1 = line.Pieces.Count > 0 ? line.Pieces[^1].X + line.Pieces[^1].Width : area.Box.Right;
            var dx = p.X < x0 ? x0 - p.X : p.X > x1 ? p.X - x1 : 0;
            // Up and down first; across only between lines at the same height (either side of text wrap).
            var d = (dy, dx);
            if (d.CompareTo(nearestDistance) < 0) { nearestDistance = d; nearest = line; nearestIndex = i; }
        }
        return nearest is null ? null : IndexAtX(nearestIndex, p.X);
    }

    /// <summary>The boxes a selection covers, line by line, with the area each is in.</summary>
    public IEnumerable<(int Area, Box Box)> SelectionBoxes(int start, int end)
    {
        if (end < start) (start, end) = (end, start);
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var lineEnd = line.LastOfParagraph ? line.End + 1 : (i + 1 < Lines.Count ? Lines[i + 1].Start : line.End);
            if (end <= line.Start || start >= lineEnd) continue;
            var from = Math.Max(start, line.Start);
            var to = Math.Min(end, line.End);
            var x0 = CaretX(i, from);
            var x1 = CaretX(i, to);
            // A selected paragraph end shows as a sliver of space after the line.
            if (line.LastOfParagraph && end > line.End) x1 += line.Ascent * 0.4;
            yield return (line.Area, new Box(x0, line.Baseline - line.Ascent, Math.Max(0, x1 - x0), line.Ascent + line.Descent));
        }
    }
}

/// <summary>Sets a story's text in lines through its areas.</summary>
public static class TextFlow
{
    private const double Slack = 0.01;

    /// <summary>A string's width in a format, tracking included.</summary>
    public static double Measure(ITextMeasurer m, string text, CharFormat f) =>
        text.Length == 0 ? 0 : m.Width(text, f) + text.Length * f.Tracking / 1000 * f.Size;

    /// <summary>The leading of a line: the paragraph's, or 120% of its largest size.</summary>
    public static double Leading(ParaFormat para, double largestSize) => para.Leading > 0 ? para.Leading : largestSize * 1.2;

    private sealed record Chunk(int Start, int Length, CharFormat Format, double Width);

    /// <summary>A word and the spaces after it, in one or more chunks (one per format).</summary>
    private sealed class Word
    {
        public List<Chunk> Chunks { get; } = [];
        public double Width;    // spaces included
        public double Visible;  // spaces left out
        public int Start => Chunks[0].Start;
        public int End => Chunks[^1].Start + Chunks[^1].Length;
    }

    /// <summary>Measuring through a story's fields.</summary>
    private readonly record struct Ctx(ITextMeasurer M, FieldContext? Fields)
    {
        public double Measure(string text, CharFormat f) => TextFlow.Measure(M, Core.Fields.Expand(text, Fields), f);
    }

    private readonly record struct Slot(double X, double W);

    private sealed record Segment(List<Piece> Pieces, int Start, int NextWord, int NextSplit, double Visible, Slot Slot);

    /// <summary>
    /// Sets a story through its areas. Lines keep out of each area's exclusions: a line beside one may be set in
    /// pieces on either side of it, and where no word fits beside it the text moves on below it.
    /// </summary>
    public static TextLayout Layout(Story story, IReadOnlyList<Area> areas, ITextMeasurer measurer, FieldContext? fields = null)
    {
        var ctx = new Ctx(measurer, fields);
        var lines = new List<Line>();
        var text = story.Text;
        var areaIndex = 0;
        double? previousBaseline = null; // in the current area
        double? jumpTo = null;           // a baseline below something in the way
        var pendingSpace = 0.0;          // space after the previous paragraph and before this one
        var oversetFrom = text.Length;
        var done = false;

        for (var p = 0; p < story.ParagraphCount && !done; p++)
        {
            var para = story.Paras[p];
            var (pStart, pEnd) = story.ParagraphRange(p);
            var words = Words(story, pStart, pEnd, ctx);
            pendingSpace += para.SpaceBefore;
            var w = 0;
            var firstLine = true;
            var charSplit = 0; // characters of words[w] already set, when a word is broken across lines

            do
            {
                while (true)
                {
                    if (areaIndex >= areas.Count)
                    {
                        oversetFrom = w < words.Count ? words[w].Start + charSplit : pStart;
                        done = true;
                        break;
                    }
                    var area = areas[areaIndex];
                    var box = area.Box;
                    var left = box.X + para.LeftIndent + (firstLine ? para.FirstIndent : 0);
                    var width = box.W - para.LeftIndent - para.RightIndent - (firstLine ? para.FirstIndent : 0);
                    if (width <= 0) { NextArea(); continue; }

                    // Where the line would go, judged by the format it starts in, and the room there.
                    var startIndex = w < words.Count ? words[w].Start + charSplit : pEnd;
                    var f0 = story.FormatAt(startIndex);
                    var (a0, d0) = Height([f0], measurer);
                    var trial = previousBaseline is { } pb0 ? pb0 + Leading(para, f0.Size) + pendingSpace : box.Y + a0;
                    if (jumpTo is { } j0 && j0 > trial) trial = j0;
                    if (trial + d0 > box.Bottom + Slack) { NextArea(); continue; }
                    var slots = Room(area.Keep, left, width, trial - a0, trial + d0);
                    var clear = slots.Count == 1 && slots[0] == new Slot(left, width);

                    var made = new List<Segment>();
                    int cw = w, cs = charSplit;
                    if (words.Count == 0)
                    {
                        if (slots.Count > 0) made.Add(new Segment([], pStart, 0, 0, 0, slots[0]));
                    }
                    else
                    {
                        foreach (var slot in slots)
                        {
                            var segmentStart = words[cw].Start + cs;
                            var (pieces, nw, ns, vis) = Fill(story, words, cw, cs, slot.X, slot.W, ctx, breakWords: clear);
                            if (pieces.Count == 0) continue;
                            made.Add(new Segment(pieces, segmentStart, nw, ns, vis, slot));
                            (cw, cs) = (nw, ns);
                            if (cw >= words.Count) break;
                        }
                    }
                    if (made.Count == 0)
                    {
                        // Nothing fits beside what is in the way here: go on below it.
                        jumpTo = Below(area.Keep, left, width, trial - a0, trial + d0) + a0;
                        continue;
                    }

                    var formats = made.SelectMany(m => m.Pieces).Select(x => x.Format).ToList();
                    if (formats.Count == 0) formats.Add(story.FormatAt(pEnd));
                    var (ascent, descent) = Height(formats, measurer);
                    var largest = formats.Max(f => f.Size);
                    var baseline = previousBaseline is { } pb ? pb + Leading(para, largest) + pendingSpace : box.Y + ascent;
                    if (jumpTo is { } j && j > baseline) baseline = j - a0 + ascent;
                    if (baseline + descent > box.Bottom + Slack) { NextArea(); continue; }

                    for (var k = 0; k < made.Count; k++)
                    {
                        var seg = made[k];
                        var last = seg.NextWord >= words.Count && k == made.Count - 1;
                        var end = seg.NextWord >= words.Count ? pEnd : words[seg.NextWord].Start + seg.NextSplit;
                        var aligned = Align(seg.Pieces, para, seg.Slot.X, seg.Slot.W, seg.Visible, last || seg.NextWord >= words.Count, story);
                        var x = aligned.Count > 0 ? aligned[0].X : para.Align switch
                        {
                            Core.Align.Centre => seg.Slot.X + seg.Slot.W / 2,
                            Core.Align.Right => seg.Slot.X + seg.Slot.W,
                            _ => seg.Slot.X,
                        };
                        lines.Add(new Line(areaIndex, seg.Start, end, baseline, ascent, descent, x, p, last, aligned));
                    }
                    previousBaseline = baseline;
                    jumpTo = null;
                    pendingSpace = 0;
                    (w, charSplit) = (cw, cs);
                    firstLine = false;
                    break;
                }
            } while (!done && w < words.Count);

            pendingSpace = para.SpaceAfter;
        }

        return new TextLayout(story, areas, lines, oversetFrom, measurer, fields);

        void NextArea()
        {
            areaIndex++;
            previousBaseline = null;
            jumpTo = null;
        }
    }

    private static (double Ascent, double Descent) Height(IEnumerable<CharFormat> formats, ITextMeasurer m)
    {
        double ascent = 0, descent = 0;
        foreach (var f in formats)
        {
            var fm = m.Metrics(f);
            ascent = Math.Max(ascent, fm.Ascent + Math.Max(0, f.Shift));
            descent = Math.Max(descent, fm.Descent - Math.Min(0, f.Shift));
        }
        return (ascent, descent);
    }

    /// <summary>The stretches of a line from <paramref name="left"/> that nothing in the way covers, between two heights.</summary>
    private static List<Slot> Room(IReadOnlyList<Box> keep, double left, double width, double top, double bottom)
    {
        var slots = new List<Slot> { new(left, width) };
        foreach (var k in keep)
        {
            if (k.Y >= bottom || k.Bottom <= top) continue;
            var next = new List<Slot>();
            foreach (var s in slots)
            {
                var r = s.X + s.W;
                if (k.Right <= s.X || k.X >= r) { next.Add(s); continue; }
                if (k.X > s.X) next.Add(new Slot(s.X, k.X - s.X));
                if (k.Right < r) next.Add(new Slot(k.Right, r - k.Right));
            }
            slots = next;
        }
        return slots;
    }

    /// <summary>The lowest point a line between two heights must drop to so that something in its way is passed: the nearest bottom.</summary>
    private static double Below(IReadOnlyList<Box> keep, double left, double width, double top, double bottom)
    {
        var lowest = double.MaxValue;
        foreach (var k in keep)
            if (k.Y < bottom && k.Bottom > top && k.Right > left && k.X < left + width) lowest = Math.Min(lowest, k.Bottom);
        return lowest == double.MaxValue ? bottom : lowest + Slack;
    }

    /// <summary>Breaks a paragraph into words, each with the spaces that follow it, split where the format changes.</summary>
    private static List<Word> Words(Story story, int start, int end, Ctx m)
    {
        var words = new List<Word>();
        var text = story.Text;
        var i = start;
        while (i < end)
        {
            var j = i;
            while (j < end && text[j] != ' ' && text[j] != '\t' && !(text[j] == '-' && j > i)) j++;
            if (j < end && text[j] == '-') j++;
            var visibleEnd = j;
            while (j < end && (text[j] == ' ' || text[j] == '\t')) j++;
            var word = new Word();
            var at = i;
            foreach (var (runStart, run) in RunsBetween(story, i, j))
            {
                var s = Math.Max(at, runStart);
                var e = Math.Min(j, runStart + run.Length);
                if (e <= s) continue;
                var chunkText = text[s..e].Replace('\t', ' ');
                var width = m.Measure(chunkText, run.Format);
                word.Chunks.Add(new Chunk(s, e - s, run.Format, width));
                word.Width += width;
                var visibleChars = Math.Max(0, Math.Min(e, visibleEnd) - s);
                word.Visible = visibleChars > 0 ? word.Width - width + m.Measure(chunkText[..visibleChars], run.Format) : word.Visible;
                at = e;
            }
            words.Add(word);
            i = j;
        }
        return words;
    }

    private static IEnumerable<(int Start, Run Run)> RunsBetween(Story story, int start, int end)
    {
        var at = 0;
        foreach (var run in story.Runs)
        {
            if (at >= end) yield break;
            if (at + run.Length > start) yield return (at, run);
            at += run.Length;
        }
    }

    /// <summary>
    /// Fills one line from word <paramref name="w"/> (from character <paramref name="split"/> of it): whole words while
    /// they fit, and (if <paramref name="breakWords"/>) a word wider than a whole line broken between characters.
    /// </summary>
    private static (List<Piece> Pieces, int NextWord, int NextSplit, double Visible) Fill(
        Story story, List<Word> words, int w, int split, double left, double width, Ctx m, bool breakWords = true)
    {
        var pieces = new List<Piece>();
        var x = left;
        var visible = 0.0;
        while (w < words.Count)
        {
            var word = words[w];
            if (split == 0 && x - left + word.Visible <= width + Slack)
            {
                foreach (var c in word.Chunks)
                {
                    pieces.Add(new Piece(c.Start, c.Length, x, c.Width, c.Format));
                    x += c.Width;
                }
                visible = x - left - (word.Width - word.Visible);
                w++;
                continue;
            }
            if (pieces.Count > 0) break;
            if (!breakWords) return (pieces, w, split, 0); // beside text wrap: let the word go where it fits whole

            // The rest of a long word, from character `split`, as much as fits (at least one character).
            var take = 0;
            var used = 0.0;
            var from = word.Start + split;
            var total = word.End - from;
            for (var n = 1; n <= total; n++)
            {
                var wd = MeasureRange(story, from, n, m);
                if (wd > width + Slack && n > 1) break;
                take = n;
                used = wd;
            }
            foreach (var (s, len, f) in Formats(story, from, take))
            {
                var wd = m.Measure(story.Slice(s, len), f);
                pieces.Add(new Piece(s, len, x, wd, f));
                x += wd;
            }
            visible = used;
            if (split + take >= word.End - word.Start) { w++; split = 0; }
            else split += take;
            return (pieces, w, split, visible);
        }
        return (pieces, w, 0, visible);
    }

    private static double MeasureRange(Story story, int start, int length, Ctx m) =>
        Formats(story, start, length).Sum(x => m.Measure(story.Slice(x.Start, x.Length), x.Format));

    private static IEnumerable<(int Start, int Length, CharFormat Format)> Formats(Story story, int start, int length)
    {
        foreach (var (runStart, run) in RunsBetween(story, start, start + length))
        {
            var s = Math.Max(start, runStart);
            var e = Math.Min(start + length, runStart + run.Length);
            if (e > s) yield return (s, e - s, run.Format);
        }
    }

    /// <summary>Moves a line's pieces for the paragraph's alignment; justified lines spread their spaces.</summary>
    private static List<Piece> Align(List<Piece> pieces, ParaFormat para, double left, double width, double visible,
        bool last, Story story)
    {
        if (pieces.Count == 0) return pieces;
        var free = width - visible;
        switch (para.Align)
        {
            case Core.Align.Centre: return Shift(pieces, free / 2);
            case Core.Align.Right: return Shift(pieces, free);
            case Core.Align.Justify when !last && free > 0:
                // Gaps are the places where a piece ends in a space and another piece follows.
                var gaps = 0;
                for (var i = 0; i < pieces.Count - 1; i++)
                    if (EndsInSpace(story, pieces[i])) gaps++;
                if (gaps == 0) return pieces;
                var extra = free / gaps;
                var result = new List<Piece>(pieces.Count);
                var add = 0.0;
                for (var i = 0; i < pieces.Count; i++)
                {
                    result.Add(pieces[i] with { X = pieces[i].X + add });
                    if (i < pieces.Count - 1 && EndsInSpace(story, pieces[i])) add += extra;
                }
                return result;
            default: return pieces;
        }
    }

    private static bool EndsInSpace(Story story, Piece p) => p.Length > 0 && story.Text[p.Start + p.Length - 1] is ' ' or '\t';

    private static List<Piece> Shift(List<Piece> pieces, double dx) => pieces.Select(p => p with { X = p.X + dx }).ToList();
}
