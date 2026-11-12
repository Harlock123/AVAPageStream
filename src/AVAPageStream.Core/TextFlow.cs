namespace AVAPageStream.Core;

/// <summary>
/// A font's height above and below the baseline at a format's size, in points, and how tall its capitals stand (0:
/// not known, taken as 72% of the ascent).
/// </summary>
public readonly record struct FontMetrics(double Ascent, double Descent, double CapHeight = 0)
{
    public double Caps => CapHeight > 0 ? CapHeight : Ascent * 0.72;
}

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
/// Somewhere text keeps out of: a wrapping object's box, already widened by its standoff, and for wrap by shape the
/// outline inside it (a polygon round an ellipse, a path or a turned frame).
/// </summary>
public sealed record Keep(Box Bounds, IReadOnlyList<Pt>? Outline = null)
{
    public static implicit operator Keep(Box b) => new(b);

    public bool Equals(Keep? other) =>
        other is not null && Bounds == other.Bounds && (Outline ?? []).SequenceEqual(other.Outline ?? []);

    public override int GetHashCode() => HashCode.Combine(Bounds, Outline?.Count ?? 0);

    /// <summary>How far across it reaches between two heights, or null if it doesn't reach them.</summary>
    public (double Left, double Right)? Span(double top, double bottom)
    {
        if (Bounds.Y >= bottom || Bounds.Bottom <= top) return null;
        if (Outline is not { Count: > 2 } o) return (Bounds.X, Bounds.Right);
        double l = double.MaxValue, r = double.MinValue;
        void Take(double x) { l = Math.Min(l, x); r = Math.Max(r, x); }
        for (var i = 0; i < o.Count; i++)
        {
            var a = o[i];
            var b = o[(i + 1) % o.Count];
            if (a.Y >= top && a.Y <= bottom) Take(a.X);
            // Where the edge crosses the band's top and bottom.
            foreach (var y in new[] { top, bottom })
                if ((a.Y - y) * (b.Y - y) < 0) Take(a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y));
        }
        return l > r ? null : (l, r);
    }
}

/// <summary>
/// The lines text set on the grid sits on: every <see cref="Step"/> points from <see cref="Start"/>, down from the top
/// of the page, so baselines line up across columns and frames.
/// </summary>
public readonly record struct BaselineGrid(double Start, double Step)
{
    /// <summary>The first grid line at or below a baseline.</summary>
    public double Snap(double baseline) => Step <= 0 ? baseline : Start + Math.Ceiling((baseline - Start - 0.001) / Step) * Step;
}

/// <summary>
/// A box text may fill: one column of one frame in a story's chain, and what in it text must keep out of (objects
/// with text wrap).
/// </summary>
public sealed record Area(Box Box, string FrameId, int Column, IReadOnlyList<Keep>? Exclusions = null)
{
    public IReadOnlyList<Keep> Keep => Exclusions ?? [];

    public bool Equals(Area? other) =>
        other is not null && Box == other.Box && FrameId == other.FrameId && Column == other.Column && Keep.SequenceEqual(other.Keep);

    public override int GetHashCode() => HashCode.Combine(Box, FrameId, Column, Keep.Count);
}

/// <summary>
/// What the page-number and page-count fields show: the page a story is drawn on (0 on a master page itself), how
/// many pages there are, and the page's number as printed, if its section numbers it otherwise (iv, A-3).
/// </summary>
public sealed record FieldContext(int Page, int Pages, string? Label = null);

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
        var page = context is { Page: > 0 } c ? c.Label ?? c.Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : "#";
        var pages = context is { Pages: > 0 } d ? d.Pages.ToString(System.Globalization.CultureInfo.InvariantCulture) : "#";
        return text.Replace(PageNumber.ToString(), page).Replace(PageCount.ToString(), pages);
    }
}

/// <summary>
/// A piece of a line in one format: the characters from <see cref="Start"/>, set at <see cref="X"/>. A tab is a piece
/// of its own, as wide as the gap to its stop (filled with <see cref="Leader"/>, if it has one); a piece that ends a
/// hyphenated line shows a hyphen after it (counted in its width); a drop cap sits <see cref="Drop"/> below the line.
/// </summary>
public sealed record Piece(int Start, int Length, double X, double Width, CharFormat Format, bool Tab = false, char Leader = '\0', bool Hyphen = false, double Drop = 0);

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
                return piece.Tab ? piece.X : piece.X + TextFlow.Measure(_measurer, Display(piece.Start, index - piece.Start), piece.Format);
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

    /// <summary>A string's width in a format, tracking, small caps and super- or subscript included.</summary>
    public static double Measure(ITextMeasurer m, string text, CharFormat f)
    {
        if (text.Length == 0) return 0;
        f = AsSet(f);
        var tracking = text.Length * f.Tracking / 1000 * f.Size;
        if (!f.SmallCaps) return m.Width(text, f) + tracking;
        return SmallCapsPieces(text, f).Sum(p => m.Width(p.Text, p.Format)) + tracking;
    }

    /// <summary>The size small capitals are set at, of the format's size.</summary>
    public const double SmallCapsScale = 0.72;

    /// <summary>
    /// A format as it is set: superscript and subscript at 60% of the size, raised by a third of the size or lowered
    /// by a sixth (on top of any baseline shift).
    /// </summary>
    public static CharFormat AsSet(CharFormat f) => f.Script switch
    {
        Script.Super => f with { Script = Script.Normal, Size = Math.Round(f.Size * 0.6, 2), Shift = f.Shift + f.Size * 0.33 },
        Script.Sub => f with { Script = Script.Normal, Size = Math.Round(f.Size * 0.6, 2), Shift = f.Shift - f.Size * 0.16 },
        _ => f,
    };

    /// <summary>
    /// Small caps as runs to draw: capitals and everything that isn't a lower-case letter as they are, lower-case
    /// letters as capitals at <see cref="SmallCapsScale"/> of the size. Each run keeps its offset into the text.
    /// </summary>
    public static IEnumerable<(string Text, CharFormat Format, int Offset)> SmallCapsPieces(string text, CharFormat f)
    {
        var small = f with { SmallCaps = false, Size = Math.Round(f.Size * SmallCapsScale, 2) };
        var big = f with { SmallCaps = false };
        var i = 0;
        while (i < text.Length)
        {
            var lower = char.IsLower(text[i]);
            var j = i;
            while (j < text.Length && char.IsLower(text[j]) == lower) j++;
            yield return lower ? (text[i..j].ToUpperInvariant(), small, i) : (text[i..j], big, i);
            i = j;
        }
    }

    /// <summary>The leading of a line: the paragraph's, or 120% of its largest size.</summary>
    public static double Leading(ParaFormat para, double largestSize) => para.Leading > 0 ? para.Leading : largestSize * 1.2;

    private sealed record Chunk(int Start, int Length, CharFormat Format, double Width);

    /// <summary>A word and the spaces after it, in one or more chunks (one per format).</summary>
    private sealed class Word
    {
        public List<Chunk> Chunks { get; } = [];
        public double Width;    // spaces included
        public double Visible;  // spaces left out
        public bool Tab;        // a tab, set as wide as the gap to its stop
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
    public static TextLayout Layout(Story story, IReadOnlyList<Area> areas, ITextMeasurer measurer, FieldContext? fields = null, BaselineGrid? grid = null)
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

        // Keeping text together sets a paragraph again: from where things stood before it (a snapshot of the state),
        // starting in the next area, or with a break forced after so many of its lines.
        var snaps = new (int Lines, int Area, double? Baseline, double? Jump, double Pending, int Overset, bool Done)[story.ParagraphCount];
        var plans = new Dictionary<int, (bool NextArea, int BreakAfter)>();
        var tries = new Dictionary<int, int>();

        var back = 0; // the paragraph a retry goes back to
        for (var p = 0; p < story.ParagraphCount && !done; p++)
        {
            snaps[p] = (lines.Count, areaIndex, previousBaseline, jumpTo, pendingSpace, oversetFrom, done);
            (bool NextArea, int BreakAfter) plan = plans.GetValueOrDefault(p, (false, -1));
            if (plan.NextArea && areaIndex < areas.Count) NextArea();
            var para = story.Paras[p];
            var (pStart, pEnd) = story.ParagraphRange(p);
            var stops = Tabs.Parse(para.Tabs);

            // A drop cap: its first characters set large enough to reach down DropLines lines, the lines beside it indented.
            var drop = para.DropLines >= 2 && pEnd > pStart ? Math.Min(Math.Max(1, para.DropChars), pEnd - pStart) : 0;
            CharFormat? dropFormat = null;
            double dropWidth = 0, dropGap = 0, dropDown = 0;
            if (drop > 0)
            {
                var basis = story.FormatAt(pStart);
                // Its capital stands from the last line's baseline up to the top of the first line's capitals.
                var caps = Math.Max(0.01, measurer.Metrics(basis).Caps);
                var lead = Leading(para, basis.Size);
                dropDown = lead * (para.DropLines - 1);
                dropFormat = basis with { Size = Math.Round(basis.Size * (dropDown + caps) / caps, 2) };
                dropWidth = ctx.Measure(text[pStart..(pStart + drop)], dropFormat);
                dropGap = basis.Size * 0.25;
            }
            var linesInPara = 0;
            var words = Words(story, pStart + drop, pEnd, ctx);
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
                    var indent = drop > 0 ? (linesInPara < para.DropLines ? dropWidth + dropGap : 0) : firstLine ? para.FirstIndent : 0;
                    var left = box.X + para.LeftIndent + indent;
                    var width = box.W - para.LeftIndent - para.RightIndent - indent;
                    if (width <= 0) { NextArea(); continue; }
                    var rules = new Rules(box.X, para.LeftIndent, stops, para.Hyphenate);

                    // Where the line would go, judged by the format it starts in, and the room there.
                    var startIndex = w < words.Count ? words[w].Start + charSplit : pEnd;
                    var f0 = story.FormatAt(startIndex);
                    var (a0, d0) = Height([f0], measurer);
                    var trial = previousBaseline is { } pb0 ? pb0 + Leading(para, f0.Size) + pendingSpace : box.Y + a0;
                    if (jumpTo is { } j0 && j0 > trial) trial = j0;
                    if (para.OnGrid && grid is { } g0) trial = g0.Snap(trial);
                    if (trial + d0 > box.Bottom + Slack) { NextArea(); continue; }
                    var slots = Room(area.Keep, left, width, trial - a0, trial + d0);
                    var clear = slots.Count == 1 && slots[0] == new Slot(left, width);

                    var made = new List<Segment>();
                    int cw = w, cs = charSplit;
                    if (words.Count == 0)
                    {
                        if (slots.Count > 0) made.Add(new Segment([], pStart + drop, 0, 0, 0, slots[0]));
                    }
                    else
                    {
                        foreach (var slot in slots)
                        {
                            var segmentStart = words[cw].Start + cs;
                            var (pieces, nw, ns, vis) = Fill(story, words, cw, cs, slot.X, slot.W, ctx, rules, breakWords: clear);
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
                    if (para.OnGrid && grid is { } g) baseline = g.Snap(baseline);
                    if (baseline + descent > box.Bottom + Slack) { NextArea(); continue; }

                    if (drop > 0 && linesInPara == 0)
                    {
                        // The drop cap goes on the first line, in front, set down to the last line it spans.
                        var first = made[0];
                        var cap = new Piece(pStart, drop, box.X + para.LeftIndent, dropWidth, dropFormat!, Drop: dropDown);
                        made[0] = first with { Pieces = [cap, .. first.Pieces], Start = pStart };
                    }
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
                    linesInPara++;
                    if (plan.BreakAfter > 0 && linesInPara == plan.BreakAfter && w < words.Count) NextArea();
                    break;
                }
            } while (!done && w < words.Count);

            if (Retry(p, para))
            {
                p = back - 1; // the loop's own increment brings it to back
                continue;
            }
            pendingSpace = para.SpaceAfter;
        }

        return new TextLayout(story, areas, lines, oversetFrom, measurer, fields);

        // Whether paragraph p broke the rules for keeping text together; if so, things are put back as they were before
        // it (or before the paragraph kept with it), with a plan for setting it again, and the loop goes back to it.
        bool Retry(int p, ParaFormat para)
        {
            var start = snaps[p].Lines;
            if (lines.Count == start) return false;
            var mine = lines.Skip(start).ToList();
            var atTop = start == 0 || lines[start - 1].Area != mine[0].Area;
            // Counted by baseline: beside text wrap, one line can be in several pieces.
            var rows = mine.Select(l => (l.Area, l.Baseline)).Distinct().ToList();
            var firstArea = rows.Count(r => r.Area == mine[0].Area);
            var lastArea = rows.Count(r => r.Area == mine[^1].Area);
            var spans = mine[0].Area != mine[^1].Area;

            // The paragraph before, kept with this one, but left behind in the column before.
            if (p > 0 && story.Paras[p - 1].KeepWithNext && snaps[p].Lines > snaps[p - 1].Lines &&
                lines[start - 1].Area != mine[0].Area && TryAgain(p - 1, (true, -1)))
                return true;
            if (!spans) return false;
            if ((para.KeepTogether || firstArea < para.Orphans) && !atTop && TryAgain(p, (true, -1))) return true;
            if (lastArea < para.Widows && rows.Count >= para.Widows + Math.Max(1, para.Orphans))
            {
                // Break earlier, so enough lines go on to the next column; if that leaves too few behind, move it all.
                var before = rows.Count - lastArea;
                var breakAfter = before - (para.Widows - lastArea);
                if (breakAfter >= Math.Max(1, para.Orphans) && TryAgain(p, (false, breakAfter))) return true;
                if (!atTop && TryAgain(p, (true, -1))) return true;
            }
            return false;
        }

        bool TryAgain(int p, (bool NextArea, int BreakAfter) plan)
        {
            if (tries.GetValueOrDefault(p) >= 2) return false;
            tries[p] = tries.GetValueOrDefault(p) + 1;
            plans[p] = plan;
            var s = snaps[p];
            lines.RemoveRange(s.Lines, lines.Count - s.Lines);
            (areaIndex, previousBaseline, jumpTo, pendingSpace, oversetFrom, done) = (s.Area, s.Baseline, s.Jump, s.Pending, s.Overset, s.Done);
            back = p;
            return true;
        }

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
        foreach (var given in formats)
        {
            var f = AsSet(given);
            var fm = m.Metrics(f with { SmallCaps = false });
            ascent = Math.Max(ascent, fm.Ascent + Math.Max(0, f.Shift));
            descent = Math.Max(descent, fm.Descent - Math.Min(0, f.Shift));
        }
        return (ascent, descent);
    }

    /// <summary>The stretches of a line from <paramref name="left"/> that nothing in the way covers, between two heights.</summary>
    private static List<Slot> Room(IReadOnlyList<Keep> keep, double left, double width, double top, double bottom, Keep? drop = null)
    {
        var slots = new List<Slot> { new(left, width) };
        foreach (var k in drop is null ? keep : keep.Append(drop))
        {
            if (k.Span(top, bottom) is not var (kx, kr)) continue;
            var next = new List<Slot>();
            foreach (var s in slots)
            {
                var r = s.X + s.W;
                if (kr <= s.X || kx >= r) { next.Add(s); continue; }
                if (kx > s.X) next.Add(new Slot(s.X, kx - s.X));
                if (kr < r) next.Add(new Slot(kr, r - kr));
            }
            slots = next;
        }
        return slots;
    }

    /// <summary>
    /// The lowest point a line between two heights must drop to so that something in its way is passed: the nearest
    /// bottom of a box, or a little further down a shape (whose room opens up as it curves away).
    /// </summary>
    private static double Below(IReadOnlyList<Keep> keep, double left, double width, double top, double bottom)
    {
        var lowest = double.MaxValue;
        foreach (var k in keep)
        {
            if (k.Span(top, bottom) is not var (kx, kr) || kr <= left || kx >= left + width) continue;
            lowest = Math.Min(lowest, k.Outline is null ? k.Bounds.Bottom : Math.Min(k.Bounds.Bottom, top + 2));
        }
        return lowest == double.MaxValue ? bottom : lowest + Slack;
    }

    /// <summary>
    /// Breaks a paragraph into words, each with the spaces that follow it, split where the format changes; a tab is a
    /// word of its own. Where the format changes inside a word but the face doesn't (only the colour, say), the pair of
    /// letters either side keeps its kerning.
    /// </summary>
    private static List<Word> Words(Story story, int start, int end, Ctx m)
    {
        var words = new List<Word>();
        var text = story.Text;
        var i = start;
        while (i < end)
        {
            if (text[i] == '\t')
            {
                var tab = new Word { Tab = true };
                tab.Chunks.Add(new Chunk(i, 1, story.FormatAt(i), 0));
                words.Add(tab);
                i++;
                continue;
            }
            var j = i;
            while (j < end && text[j] != ' ' && text[j] != '\t' && !(text[j] == '-' && j > i)) j++;
            if (j < end && text[j] == '-') j++;
            var visibleEnd = j;
            while (j < end && text[j] == ' ') j++;
            var word = new Word();
            var at = i;
            foreach (var (runStart, run) in RunsBetween(story, i, j))
            {
                var s = Math.Max(at, runStart);
                var e = Math.Min(j, runStart + run.Length);
                if (e <= s) continue;
                var chunkText = text[s..e];
                var width = m.Measure(chunkText, run.Format);
                if (word.Chunks.Count > 0 && word.Chunks[^1] is var before && SameFace(before.Format, run.Format) && s < visibleEnd)
                {
                    // The kern between the last letter before and the first after.
                    var pair = text[(s - 1)..(s + 1)];
                    var kern = m.Measure(pair, run.Format) - m.Measure(pair[..1], run.Format) - m.Measure(pair[1..], run.Format);
                    word.Chunks[^1] = before with { Width = before.Width + kern };
                    word.Width += kern;
                    word.Visible += kern;
                }
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

    private static bool SameFace(CharFormat a, CharFormat b) =>
        a.Font == b.Font && a.Size == b.Size && a.Bold == b.Bold && a.Italic == b.Italic && a.Tracking == 0 && b.Tracking == 0 && a.Shift == b.Shift;

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

    /// <summary>What a paragraph sets its lines by: where its column starts, its left indent, its tab stops, and whether it hyphenates.</summary>
    private readonly record struct Rules(double ColumnLeft, double LeftIndent, IReadOnlyList<TabStop> Stops, bool Hyphenate);

    /// <summary>
    /// Fills one line from word <paramref name="w"/> (from character <paramref name="split"/> of it): whole words while
    /// they fit, then the first part of the next with a hyphen if the paragraph hyphenates, and (if
    /// <paramref name="breakWords"/>) a word wider than a whole line broken between characters. A tab reaches to its
    /// stop; text after a right, centre or decimal tab lines up on it.
    /// </summary>
    private static (List<Piece> Pieces, int NextWord, int NextSplit, double Visible) Fill(
        Story story, List<Word> words, int w, int split, double left, double width, Ctx m, Rules rules, bool breakWords = true)
    {
        var pieces = new List<Piece>();
        var x = left;
        var visible = 0.0;
        while (w < words.Count)
        {
            var word = words[w];
            if (word.Tab)
            {
                var stop = Tabs.Next(rules.Stops, x - rules.ColumnLeft, rules.LeftIndent);
                var stopX = rules.ColumnLeft + stop.Position;
                if (stopX > left + width + Slack)
                {
                    if (pieces.Count > 0) break; // no stop left on this line: the tab starts the next
                    stopX = x;
                }
                var target = stopX;
                if (stop.Kind != TabKind.Left)
                {
                    var (follow, toPoint) = Following(story, words, w + 1, m);
                    target = stop.Kind switch
                    {
                        TabKind.Right => stopX - follow,
                        TabKind.Centre => stopX - follow / 2,
                        _ => stopX - toPoint,
                    };
                }
                var gap = Math.Max(0, target - x);
                pieces.Add(new Piece(word.Start, 1, x, gap, word.Chunks[0].Format, Tab: true, Leader: stop.Leader));
                x += gap;
                visible = x - left;
                w++;
                continue;
            }
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
            if (split == 0 && rules.Hyphenate && Hyphenated(story, word, x, left, width, m) is { } h)
            {
                pieces.AddRange(h.Pieces);
                return (pieces, w, h.Split, h.Right - left);
            }
            if (pieces.Count > 0) break;
            var from = word.Start + split;
            var total = word.End - from;
            if (split > 0)
            {
                // The rest of a hyphenated (or broken) word: if it fits, it starts the line and the words after it follow.
                var visibleEnd = word.End;
                while (visibleEnd > from && story.Text[visibleEnd - 1] == ' ') visibleEnd--;
                if (MeasureRange(story, from, visibleEnd - from, m) <= width + Slack)
                {
                    foreach (var (s, len, f) in Formats(story, from, total))
                    {
                        var wd = m.Measure(story.Slice(s, len), f);
                        pieces.Add(new Piece(s, len, x, wd, f));
                        x += wd;
                    }
                    visible = x - left - (MeasureRange(story, from, total, m) - MeasureRange(story, from, visibleEnd - from, m));
                    w++;
                    split = 0;
                    continue;
                }
            }
            if (!breakWords) return (pieces, w, split, 0); // beside text wrap: let the word go where it fits whole

            // The rest of a long word, from character `split`, as much as fits (at least one character).
            var take = 0;
            var used = 0.0;
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

    /// <summary>
    /// The first part of a word that fits the rest of a line with a hyphen after it, broken where the hyphenation
    /// patterns allow (the longest that fits), or null.
    /// </summary>
    private static (List<Piece> Pieces, int Split, double Right)? Hyphenated(Story story, Word word, double x, double left, double width, Ctx m)
    {
        var text = story.Text;
        var end = word.Start;
        while (end < word.End && text[end] != ' ') end++;
        var lead = 0;
        while (word.Start + lead < end && !char.IsLetter(text[word.Start + lead])) lead++;
        var trail = 0;
        while (end - trail > word.Start + lead && !char.IsLetter(text[end - trail - 1])) trail++;
        var letters = text[(word.Start + lead)..(end - trail)];
        var points = Hyphenator.English.Points(letters);
        for (var k = points.Count - 1; k >= 0; k--)
        {
            var length = lead + points[k];
            var last = story.FormatAt(word.Start + length - 1);
            var hyphen = m.Measure("-", last);
            var wd = MeasureRange(story, word.Start, length, m) + hyphen;
            if (x - left + wd > width + Slack) continue;
            var pieces = new List<Piece>();
            var at = x;
            foreach (var (s, len, f) in Formats(story, word.Start, length))
            {
                var pw = m.Measure(story.Slice(s, len), f);
                pieces.Add(new Piece(s, len, at, pw, f));
                at += pw;
            }
            pieces[^1] = pieces[^1] with { Width = pieces[^1].Width + hyphen, Hyphen = true };
            return (pieces, length, at + hyphen);
        }
        return null;
    }

    /// <summary>How wide the text after a tab is, up to the next tab or the paragraph's end, and up to its first decimal point.</summary>
    private static (double Width, double ToPoint) Following(Story story, List<Word> words, int from, Ctx m)
    {
        double width = 0, toPoint = -1;
        for (var i = from; i < words.Count && !words[i].Tab; i++)
        {
            var word = words[i];
            if (toPoint < 0)
            {
                var text = story.Slice(word.Start, word.End - word.Start);
                var dot = text.IndexOf('.');
                if (dot >= 0) toPoint = width + MeasureRange(story, word.Start, dot, m);
            }
            width += i + 1 < words.Count && !words[i + 1].Tab ? word.Width : word.Visible;
        }
        return (width, toPoint < 0 ? width : toPoint);
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
            case Core.Align.Justify when !last && free > 0 && !pieces.Any(p => p.Tab):
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
