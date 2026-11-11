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

/// <summary>A box text may fill: one column of one frame in a story's chain.</summary>
public sealed record Area(Box Box, string FrameId, int Column);

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

    internal TextLayout(Story story, IReadOnlyList<Area> areas, List<Line> lines, int oversetFrom, ITextMeasurer measurer)
    {
        Story = story;
        Areas = areas;
        Lines = lines;
        OversetFrom = oversetFrom;
        _measurer = measurer;
    }

    public Story Story { get; }
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
                return piece.X + TextFlow.Measure(_measurer, Story.Slice(piece.Start, index - piece.Start), piece.Format);
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
        var nearestDistance = double.MaxValue;
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var area = Areas[line.Area];
            if (area.FrameId != frameId) continue;
            var top = line.Baseline - line.Ascent;
            var bottom = line.Baseline + line.Descent;
            var dy = p.Y < top ? top - p.Y : p.Y > bottom ? p.Y - bottom : 0;
            var dx = p.X < area.Box.X ? area.Box.X - p.X : p.X > area.Box.Right ? p.X - area.Box.Right : 0;
            var d = dy * 4 + dx;
            if (d < nearestDistance) { nearestDistance = d; nearest = line; nearestIndex = i; }
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

    public static TextLayout Layout(Story story, IReadOnlyList<Area> areas, ITextMeasurer measurer)
    {
        var lines = new List<Line>();
        var text = story.Text;
        var areaIndex = 0;
        double? previousBaseline = null; // in the current area
        var pendingSpace = 0.0;          // space after the previous paragraph and before this one
        var oversetFrom = text.Length;
        var done = false;

        for (var p = 0; p < story.ParagraphCount && !done; p++)
        {
            var para = story.Paras[p];
            var (pStart, pEnd) = story.ParagraphRange(p);
            var words = Words(story, pStart, pEnd, measurer);
            pendingSpace += para.SpaceBefore;
            var w = 0;
            var firstLine = true;
            var charSplit = 0; // characters of words[w] already set, when a word is broken across lines

            do
            {
                // Find an area this line fits in, re-breaking it if the area is a different width.
                while (true)
                {
                    if (areaIndex >= areas.Count)
                    {
                        oversetFrom = w < words.Count ? words[w].Start + charSplit : pStart;
                        done = true;
                        break;
                    }
                    var box = areas[areaIndex].Box;
                    var left = box.X + para.LeftIndent + (firstLine ? para.FirstIndent : 0);
                    var width = box.W - para.LeftIndent - para.RightIndent - (firstLine ? para.FirstIndent : 0);
                    var (pieces, nextWord, nextSplit, visible) = Fill(story, words, w, charSplit, left, width, measurer);
                    var formats = pieces.Count > 0 ? pieces.Select(x => x.Format) : [story.FormatAt(pEnd)];
                    var ascent = 0.0;
                    var descent = 0.0;
                    var largest = 0.0;
                    foreach (var f in formats)
                    {
                        var fm = measurer.Metrics(f);
                        ascent = Math.Max(ascent, fm.Ascent + Math.Max(0, f.Shift));
                        descent = Math.Max(descent, fm.Descent - Math.Min(0, f.Shift));
                        largest = Math.Max(largest, f.Size);
                    }
                    var baseline = previousBaseline is { } pb ? pb + Leading(para, largest) + pendingSpace : box.Y + ascent;
                    if (baseline + descent > box.Bottom + Slack || width <= 0)
                    {
                        areaIndex++;
                        previousBaseline = null;
                        continue;
                    }

                    var last = nextWord >= words.Count;
                    var start = w < words.Count ? words[w].Start + charSplit : pStart;
                    var end = last ? pEnd : words[nextWord].Start + nextSplit;
                    pieces = Align(pieces, para, left, width, visible, last, measurer, story);
                    var x = pieces.Count > 0 ? pieces[0].X : para.Align switch
                    {
                        Core.Align.Centre => left + width / 2,
                        Core.Align.Right => left + width,
                        _ => left,
                    };
                    lines.Add(new Line(areaIndex, start, end, baseline, ascent, descent, x, p, last, pieces));
                    previousBaseline = baseline;
                    pendingSpace = 0;
                    w = nextWord;
                    charSplit = nextSplit;
                    firstLine = false;
                    break;
                }
            } while (!done && w < words.Count);

            pendingSpace = para.SpaceAfter;
        }

        return new TextLayout(story, areas, lines, oversetFrom, measurer);
    }

    /// <summary>Breaks a paragraph into words, each with the spaces that follow it, split where the format changes.</summary>
    private static List<Word> Words(Story story, int start, int end, ITextMeasurer m)
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
                var width = Measure(m, chunkText, run.Format);
                word.Chunks.Add(new Chunk(s, e - s, run.Format, width));
                word.Width += width;
                var visibleChars = Math.Max(0, Math.Min(e, visibleEnd) - s);
                word.Visible = visibleChars > 0 ? word.Width - width + Measure(m, chunkText[..visibleChars], run.Format) : word.Visible;
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
    /// they fit, and a word wider than a whole line broken between characters.
    /// </summary>
    private static (List<Piece> Pieces, int NextWord, int NextSplit, double Visible) Fill(
        Story story, List<Word> words, int w, int split, double left, double width, ITextMeasurer m)
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
                var wd = Measure(m, story.Slice(s, len), f);
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

    private static double MeasureRange(Story story, int start, int length, ITextMeasurer m) =>
        Formats(story, start, length).Sum(x => Measure(m, story.Slice(x.Start, x.Length), x.Format));

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
        bool last, ITextMeasurer m, Story story)
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
