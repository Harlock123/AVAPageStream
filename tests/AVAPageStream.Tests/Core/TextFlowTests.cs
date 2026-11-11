using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>
/// Layout with the fixed measurer: at size 10 every character is 5 pt wide, ascent 8, descent 2, and lines are 12 pt
/// apart, so a 100 pt wide area holds 20 characters a line.
/// </summary>
public class TextFlowTests
{
    private static readonly FixedMeasurer M = new();
    private static readonly ParagraphStyle Plain = new("Body", new CharFormat(Size: 10), new ParaFormat());

    private static Story Make(string text)
    {
        var s = Story.Create("s", Plain);
        s.Insert(0, text);
        return s;
    }

    private static Area A(double x, double y, double w, double h, string frame = "f", int column = 0) => new(new Box(x, y, w, h), frame, column);

    private static string LineText(TextLayout l, int i) => l.Story.Text[l.Lines[i].Start..l.Lines[i].End];

    [Fact]
    public void Words_fill_a_line_and_wrap_at_spaces()
    {
        var l = TextFlow.Layout(Make("aaaa bbbb cccc dddd eeee"), [A(0, 0, 100, 200)], M);
        Assert.Equal(2, l.Lines.Count);
        Assert.Equal("aaaa bbbb cccc dddd ", LineText(l, 0)); // 19 visible characters fit; the space hangs
        Assert.Equal("eeee", LineText(l, 1));
        Assert.Equal(8, l.Lines[0].Baseline);
        Assert.Equal(20, l.Lines[1].Baseline);
        Assert.False(l.Overset);
    }

    [Fact]
    public void A_word_too_long_for_any_line_is_broken_between_characters()
    {
        var l = TextFlow.Layout(Make(new string('x', 45)), [A(0, 0, 100, 200)], M);
        Assert.Equal([20, 20, 5], l.Lines.Select(x => x.End - x.Start));
    }

    [Fact]
    public void Text_flows_into_the_next_area_and_what_is_left_is_overset()
    {
        // Each area holds two lines (8 + 12 + 2 = 22 pt of a 30 pt area; a third would need 34).
        var text = string.Join(" ", Enumerable.Repeat("word", 30));
        var l = TextFlow.Layout(Make(text), [A(0, 0, 100, 30, "one"), A(200, 0, 100, 30, "two")], M);
        Assert.Equal(4, l.Lines.Count);
        Assert.Equal([0, 0, 1, 1], l.Lines.Select(x => x.Area));
        Assert.Equal(200, l.Lines[2].Pieces[0].X);
        Assert.Equal(8, l.Lines[2].Baseline); // the top of the second area
        Assert.True(l.Overset);
        Assert.Equal(l.Lines[3].End, l.OversetFrom);
    }

    [Fact]
    public void Paragraph_space_and_leading_set_the_baselines()
    {
        var s = Make("one\ntwo\nthree");
        s.FormatParas(4, 0, p => p with { SpaceBefore = 6, Leading = 20 });
        s.FormatParas(0, 0, p => p with { SpaceAfter = 3, SpaceBefore = 50 });
        var l = TextFlow.Layout(s, [A(0, 0, 100, 200)], M);
        Assert.Equal(8, l.Lines[0].Baseline);              // space before is dropped at the top of an area
        Assert.Equal(8 + 20 + 3 + 6, l.Lines[1].Baseline); // its own leading plus the space after and before
        Assert.Equal(8 + 29 + 12, l.Lines[2].Baseline);
    }

    [Fact]
    public void Empty_paragraphs_take_a_line()
    {
        var l = TextFlow.Layout(Make("a\n\nb"), [A(0, 0, 100, 200)], M);
        Assert.Equal(3, l.Lines.Count);
        Assert.Empty(l.Lines[1].Pieces);
        Assert.Equal(32, l.Lines[2].Baseline);
    }

    [Theory]
    [InlineData(Align.Left, 0)]
    [InlineData(Align.Centre, 40)]
    [InlineData(Align.Right, 80)]
    public void Lines_are_aligned(Align align, double x)
    {
        var s = Make("abcd");
        s.FormatParas(0, 0, p => p with { Align = align });
        var l = TextFlow.Layout(s, [A(0, 0, 100, 100)], M);
        Assert.Equal(x, l.Lines[0].Pieces[0].X);
    }

    [Fact]
    public void Justified_lines_spread_their_spaces_but_not_the_last_line()
    {
        var s = Make("aaa bbb ccc ddd eee fff");
        s.FormatParas(0, 0, p => p with { Align = Align.Justify });
        var l = TextFlow.Layout(s, [A(0, 0, 100, 100)], M);
        var first = l.Lines[0];
        Assert.Equal("aaa bbb ccc ddd eee ", LineText(l, 0));
        var lastPiece = first.Pieces[^1];
        Assert.Equal(100, lastPiece.X + 15, 6); // "eee" ends at the right edge
        Assert.Equal(0, l.Lines[1].Pieces[0].X);
    }

    [Fact]
    public void Indents_narrow_the_lines()
    {
        var s = Make("aaaa bbbb cccc dddd");
        s.FormatParas(0, 0, p => p with { LeftIndent = 10, FirstIndent = 20, RightIndent = 10 });
        var l = TextFlow.Layout(s, [A(0, 0, 100, 100)], M);
        Assert.Equal(30, l.Lines[0].Pieces[0].X);
        Assert.Equal("aaaa bbbb ", LineText(l, 0)); // 60 pt of room on the first line: two words
        Assert.Equal(10, l.Lines[1].Pieces[0].X);
    }

    [Fact]
    public void A_line_takes_the_height_of_its_largest_type()
    {
        var s = Make("small BIG");
        s.FormatChars(6, 3, f => f with { Size = 20 });
        var l = TextFlow.Layout(s, [A(0, 0, 200, 100)], M);
        Assert.Equal(16, l.Lines[0].Ascent);
        Assert.Equal(16, l.Lines[0].Baseline);
        Assert.Equal(2, l.Lines[0].Pieces.Count);
    }

    [Fact]
    public void Tracking_widens_the_text()
    {
        var s = Make("abcd");
        s.FormatChars(0, 4, f => f with { Tracking = 100 }); // a tenth of an em, 1 pt at size 10
        var l = TextFlow.Layout(s, [A(0, 0, 100, 100)], M);
        Assert.Equal(24, l.Lines[0].Pieces[0].Width);
    }

    [Fact]
    public void Carets_find_their_line_and_place_and_points_find_their_character()
    {
        var l = TextFlow.Layout(Make("aaaa bbbb cccc dddd eeee\nff"), [A(0, 0, 100, 200)], M);
        Assert.Equal(0, l.LineOf(0));
        Assert.Equal(0, l.LineOf(19));
        Assert.Equal(1, l.LineOf(20)); // a soft-wrapped line's end is the next line's start
        Assert.Equal(1, l.LineOf(24)); // the end of a paragraph
        Assert.Equal(2, l.LineOf(25));
        Assert.Equal(15, l.CaretX(0, 3));
        Assert.Equal(20, l.CaretX(1, 24));
        Assert.Equal(3, l.IndexAt("f", new Pt(16, 4)));
        Assert.Equal(27, l.IndexAt("f", new Pt(99, 30)));
        Assert.Equal(19, l.IndexAt("f", new Pt(99, 5)));  // past the end of a wrapped line: before its space
        Assert.Null(l.IndexAt("other", new Pt(0, 0)));
    }

    [Fact]
    public void Text_that_does_not_fit_has_no_caret_line()
    {
        var l = TextFlow.Layout(Make("aaaa bbbb cccc dddd eeee"), [A(0, 0, 100, 12)], M);
        Assert.Single(l.Lines);
        Assert.Equal(20, l.OversetFrom);
        Assert.Equal(-1, l.LineOf(22));
    }

    [Fact]
    public void A_story_with_no_frames_is_all_overset()
    {
        var l = TextFlow.Layout(Make("hello"), [], M);
        Assert.Empty(l.Lines);
        Assert.Equal(0, l.OversetFrom);
    }

    [Fact]
    public void Selections_cover_each_line_they_touch()
    {
        var l = TextFlow.Layout(Make("aaaa bbbb cccc dddd eeee"), [A(0, 0, 100, 200)], M);
        var boxes = l.SelectionBoxes(15, 22).ToList();
        Assert.Equal(2, boxes.Count);
        Assert.Equal(new Box(75, 0, 25, 10), boxes[0].Box);
        Assert.Equal(new Box(0, 12, 10, 10), boxes[1].Box);
    }

    [Fact]
    public void Frames_split_into_columns_in_order()
    {
        var doc = Document.New();
        var frame = new TextFrame { Id = "f1", X = 10, Y = 20, W = 220, H = 100, Columns = 2, Gutter = 20, Inset = 0 };
        doc.Pages[0].Objects.Add(frame);
        var story = Chains.NewStory(doc, frame, doc.Style("Body"));
        var areas = Chains.Areas(doc, story);
        Assert.Equal([new Box(10, 20, 100, 100), new Box(130, 20, 100, 100)], areas.Select(a => a.Box));
    }
}
