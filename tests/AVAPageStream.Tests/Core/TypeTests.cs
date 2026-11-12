using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Tabs, drop caps, hyphenation, kerning and wrap by shape, with the fixed measurer (5 pt a character at size 10).</summary>
public class TypeTests
{
    private static readonly FixedMeasurer M = new();
    private static readonly ParagraphStyle Plain = new("Body", new CharFormat(Size: 10), new ParaFormat());

    private static Story Make(string text, Func<ParaFormat, ParaFormat>? para = null)
    {
        var s = Story.Create("s", Plain);
        s.Insert(0, text);
        if (para is not null) s.FormatParas(0, s.Length, para);
        return s;
    }

    private static TextLayout Set(Story s, double w = 400, double h = 400) => TextFlow.Layout(s, [new Area(new Box(0, 0, w, h), "f", 0)], M);

    [Theory]
    [InlineData("hyphenation", "hy-phen-ation")]
    [InlineData("typesetting", "type-set-ting")]
    [InlineData("associate", "as-so-ciate")]  // from the exceptions
    [InlineData("table", "ta-ble")]
    [InlineData("cat", "cat")]
    [InlineData("Harbour", "Har-bour")]
    public void Words_hyphenate_where_the_patterns_allow(string word, string shown) =>
        Assert.Equal(shown, Hyphenator.English.Show(word));

    [Fact]
    public void Hyphenation_keeps_two_letters_before_and_three_after()
    {
        foreach (var word in new[] { "information", "extraordinary", "photographic", "relationships" })
            Assert.All(Hyphenator.English.Points(word), p => Assert.InRange(p, 2, word.Length - 3));
        Assert.Empty(Hyphenator.English.Points("don't"));
    }

    [Fact]
    public void Tab_stops_are_read_shown_and_kept_by_value()
    {
        Assert.True(Tabs.TryRead("1in, 4in right ., 2.5in decimal, 3in centre", Unit.Point, out var stored, out _));
        Assert.Equal("72L 180D 216C 288R.", stored);
        Assert.Equal("1 in, 2.5 in decimal, 3 in centre, 4 in right .", Tabs.Show(stored, Unit.Inch));
        Assert.Equal([new TabStop(72), new TabStop(180, TabKind.Decimal), new TabStop(216, TabKind.Centre), new TabStop(288, TabKind.Right, '.')], Tabs.Parse(stored));
        Assert.False(Tabs.TryRead("wide", Unit.Point, out _, out var why));
        Assert.Contains("isn't a tab stop", why);
        Assert.True(Tabs.TryRead("", Unit.Point, out var none, out _));
        Assert.Equal("", none);
    }

    [Fact]
    public void Past_the_stops_tabs_fall_every_half_inch_and_a_hanging_indent_is_a_stop()
    {
        Assert.Equal(36, Tabs.Next([], 5, 0).Position);
        Assert.Equal(72, Tabs.Next([], 36, 0).Position);
        Assert.Equal(100, Tabs.Next([new TabStop(100)], 40, 0).Position);
        Assert.Equal(14, Tabs.Next([], 5, 14).Position); // the hanging indent of a list item
    }

    [Fact]
    public void Tabs_reach_their_stops_and_right_and_decimal_tabs_line_up_the_text()
    {
        var s = Make("a\tb\tc\t12.5", p => p with { Tabs = "100L 200R 300D" });
        var l = Set(s);
        var pieces = l.Lines[0].Pieces;
        Assert.Equal(100, pieces.Single(p => p.Start == 2).X);              // b at the left tab
        Assert.Equal(200, pieces.Single(p => p.Start == 4).X + 5);           // c ends at the right tab
        Assert.Equal(300, pieces.Single(p => p.Start == 6).X + 10);          // "12" before the point at the decimal tab
        Assert.True(pieces.Single(p => p.Start == 1).Tab);
        Assert.Equal(100, l.CaretX(0, 2));
    }

    [Fact]
    public void A_bullet_and_tab_hang_in_a_hanging_indent()
    {
        var s = Make("•\tlist item that runs on to a second line", p => p with { LeftIndent = 20, FirstIndent = -20 });
        var l = Set(s, w: 120);
        Assert.Equal(0, l.Lines[0].Pieces[0].X);  // the bullet at the edge
        Assert.Equal(20, l.Lines[0].Pieces[2].X); // the text at the indent, after the tab
        Assert.Equal(20, l.Lines[1].Pieces[0].X); // and the next line under it
    }

    [Fact]
    public void A_tab_leader_is_kept_on_the_tab()
    {
        var l = Set(Make("Chapter\t9", p => p with { Tabs = "200R." }));
        var tab = l.Lines[0].Pieces.Single(p => p.Tab);
        Assert.Equal('.', tab.Leader);
        Assert.Equal(35, tab.X);
        Assert.Equal(200 - 5 - 35, tab.Width);
    }

    [Fact]
    public void A_drop_cap_reaches_down_its_lines_and_the_lines_beside_it_are_indented()
    {
        var s = Make(string.Join(" ", Enumerable.Repeat("word", 40)), p => p with { DropLines = 3, DropChars = 1 });
        var l = Set(s, w: 200);
        var cap = l.Lines[0].Pieces[0];
        Assert.Equal((0, 1), (cap.Start, cap.Length));
        Assert.Equal(24, cap.Drop);                       // two lines of 12 pt below the first
        var caps = 8 * 0.72; // the fixed measurer's capitals: 72% of its ascent
        Assert.Equal(Math.Round(10 * (24 + caps) / caps, 2), cap.Format.Size); // capitals from the third baseline up to the first line's
        var indent = cap.Width + 2.5;
        Assert.Equal(indent, l.Lines[0].Pieces[1].X, 6);
        Assert.Equal(indent, l.Lines[2].Pieces[0].X, 6);
        Assert.Equal(0, l.Lines[3].Pieces[0].X);          // the fourth line is clear of it
        Assert.Equal(0, l.LineOf(0));
    }

    [Fact]
    public void Hyphenation_fills_a_line_with_the_first_part_of_the_next_word()
    {
        var text = "aaaa bbbb hyphenation";
        var off = Set(Make(text), w: 100);
        Assert.Equal(10, off.Lines[1].Start); // without: the whole word goes down
        var on = Set(Make(text, p => p with { Hyphenate = true }), w: 100);
        var first = on.Lines[0];
        Assert.True(first.Pieces[^1].Hyphen);
        Assert.Equal("aaaa bbbb hyphen", on.Display(first.Start, first.End - first.Start)); // 50 + "hyphen-" 35 fits in 100
        Assert.Equal(first.End, on.Lines[1].Start);
        Assert.Equal("ation", on.Display(on.Lines[1].Start, on.Lines[1].End - on.Lines[1].Start));

        // The rest of the word starts the next line, and the words after it follow on that line.
        var more = Set(Make(text + " and more words", p => p with { Hyphenate = true }), w: 100);
        Assert.Equal("ation and more words", more.Display(more.Lines[1].Start, more.Lines[1].End - more.Lines[1].Start));
    }

    /// <summary>"AV" set closer than A and V apart, as real fonts kern it.</summary>
    private sealed class KerningMeasurer : ITextMeasurer
    {
        public double Width(string text, CharFormat f) => text.Length * f.Size * 0.5 - (text.Contains("AV") ? 1 : 0);
        public FontMetrics Metrics(CharFormat f) => new(f.Size * 0.8, f.Size * 0.2);
    }

    [Fact]
    public void A_pair_split_only_by_colour_keeps_its_kerning()
    {
        var s = Make("AV");
        s.FormatChars(1, 1, f => f with { Colour = "#FF0000" });
        var l = TextFlow.Layout(s, [new Area(new Box(0, 0, 200, 100), "f", 0)], new KerningMeasurer());
        Assert.Equal(2, l.Lines[0].Pieces.Count);
        Assert.Equal(4, l.Lines[0].Pieces[1].X); // 5 for the A, less the kern of 1
        s.FormatChars(1, 1, f => f with { Bold = true }); // another face: no kerning across it
        l = TextFlow.Layout(s, [new Area(new Box(0, 0, 200, 100), "f", 0)], new KerningMeasurer());
        Assert.Equal(5, l.Lines[0].Pieces[1].X);
    }

    [Fact]
    public void Text_follows_a_round_shape_closer_than_its_box()
    {
        var e = new Editor(Document.New());
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { X = 0, Y = 0, W = 300, H = 300 });
        var circle = e.Add(SheetRef.PageAt(0), new EllipseObject { X = 0, Y = 0, W = 100, H = 100, Wrap = true, WrapGap = 0, WrapShape = true });
        var keep = Assert.Single(Chains.Areas(e.Doc, e.Doc.Stories[frame.StoryId])[0].Keep);
        Assert.NotNull(keep.Outline);
        // Near the circle's top it is narrow, through its middle it is all of it.
        var top = keep.Span(0, 2)!.Value;
        var middle = keep.Span(49, 51)!.Value;
        Assert.True(top.Right - top.Left < 40, $"{top}");
        Assert.True(middle.Right > 99, $"{middle}");
        Assert.Null(keep.Span(120, 130));
        e.Change("Frame wrap", d => d.Find(circle.Id)!.WrapShape = false);
        Assert.Null(Chains.Areas(e.Doc, e.Doc.Stories[frame.StoryId])[0].Keep[0].Outline);
    }

    [Fact]
    public void A_turned_frame_wraps_by_its_turned_outline()
    {
        var o = new RectangleObject { X = 0, Y = 0, W = 100, H = 100, Rotation = 45, WrapGap = 0 };
        var keep = Shapes.KeepOf(o);
        var tip = keep.Span(-20, -19)!.Value; // near the top corner of the diamond
        Assert.True(tip.Right - tip.Left < 10, $"{tip}");
    }
}

public class FragmentTests
{
    private static Story Make(string text)
    {
        var s = Story.Create("s", Document.DefaultStyles()[0]);
        s.Insert(0, text);
        return s;
    }

    [Fact]
    public void A_copied_stretch_keeps_its_formats_and_paragraphs()
    {
        var s = Make("Title\nSome bold words");
        s.ApplyStyle(0, 0, Document.DefaultStyles()[1]);
        s.FormatChars(11, 4, f => f with { Bold = true });
        var copy = s.Extract(3, 13); // "le\nSome bold "
        Assert.Equal("le\nSome bold ", copy.Text);
        Assert.Equal(13, copy.Runs.Sum(r => r.Length));
        Assert.Equal(["Heading", "Body"], copy.Paras.Select(p => p.Style));

        var into = Make("start end");
        into.InsertFragment(6, copy);
        Assert.Equal("start le\nSome bold end", into.Text);
        Assert.Equal(24, into.FormatAt(6).Size);     // "le" as it was, a heading's size
        Assert.True(into.FormatAt(14).Bold);         // "bold"
        Assert.Equal("Body", into.Paras[0].Style);   // the paragraph pasted into keeps its own
        Assert.Equal("Body", into.Paras[1].Style);   // the new one takes the copied paragraph's
        Assert.True(into.IsWellFormed());
    }
}
