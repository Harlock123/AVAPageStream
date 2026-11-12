using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>
/// Text wrap and fields, with the fixed measurer: at size 10 a character is 5 pt wide, lines are 12 pt apart, and a
/// line's band runs from 8 above its baseline to 2 below.
/// </summary>
public class WrapAndFieldTests
{
    private static readonly FixedMeasurer M = new();
    private static readonly ParagraphStyle Plain = new("Body", new CharFormat(Size: 10), new ParaFormat());

    private static Story Make(string text)
    {
        var s = Story.Create("s", Plain);
        s.Insert(0, text);
        return s;
    }

    private static string Words(int n) => string.Join(" ", Enumerable.Repeat("word", n));

    private static Area A(double w, double h, params Box[] keep) => new(new Box(0, 0, w, h), "f", 0, keep);

    [Fact]
    public void Lines_beside_something_in_the_way_are_narrower()
    {
        // The right half of a 100 pt column is taken for its first 30 pt: lines 1 and 2 (bands 0-10 and 12-22) get
        // 50 pt, line 3 (24-34) touches it too, line 4 (36-46) is clear.
        var l = TextFlow.Layout(Make(Words(20)), [A(100, 200, new Box(50, 0, 50, 30))], M);
        Assert.Equal("word word ", l.Display(l.Lines[0].Start, l.Lines[0].End - l.Lines[0].Start)); // 2 words in 50 pt
        Assert.All(l.Lines.Take(3), x => Assert.True(x.Pieces[^1].X + x.Pieces[^1].Width <= 50 + 0.01));
        Assert.Equal(4, l.Lines[3].Pieces.Count); // a full line again
    }

    [Fact]
    public void Text_flows_on_both_sides_of_something_in_the_middle()
    {
        var l = TextFlow.Layout(Make(Words(30)), [A(200, 200, new Box(80, 0, 40, 20))], M);
        // Two lines at the first baseline: one either side.
        Assert.Equal(l.Lines[0].Baseline, l.Lines[1].Baseline);
        Assert.Equal(0, l.Lines[0].Pieces[0].X);
        Assert.Equal(120, l.Lines[1].Pieces[0].X);
        Assert.False(l.Lines[0].LastOfParagraph);
        Assert.Equal(l.Lines[0].End, l.Lines[1].Start); // the text runs on from one side to the other
        Assert.Equal(1, l.LineOf(l.Lines[1].Start));
        Assert.Equal(l.Lines[1].Start + 1, l.IndexAt("f", new Pt(126, 4))); // nearest the boundary at 125
    }

    [Fact]
    public void Where_nothing_fits_beside_it_the_text_goes_below()
    {
        // Across the whole column from 10 to 50 pt: the second line jumps below it.
        var l = TextFlow.Layout(Make(Words(12)), [A(100, 200, new Box(0, 10, 100, 40))], M);
        Assert.Equal(8, l.Lines[0].Baseline);
        Assert.True(l.Lines[1].Baseline - 8 >= 50, $"The second line's top is {l.Lines[1].Baseline - 8}.");
        Assert.Equal(12, l.Lines[2].Baseline - l.Lines[1].Baseline, 6); // then the usual 12 pt leading
        Assert.False(l.Overset);
    }

    [Fact]
    public void A_long_word_waits_for_room_rather_than_being_broken_beside_wrap()
    {
        var l = TextFlow.Layout(Make(new string('x', 15)), [A(100, 200, new Box(30, 0, 70, 20))], M);
        Assert.Single(l.Lines);
        Assert.True(l.Lines[0].Baseline > 20);
        Assert.Equal(15, l.Lines[0].End - l.Lines[0].Start);
    }

    [Fact]
    public void Wrap_that_fills_the_rest_of_an_area_sends_the_text_on_to_the_next()
    {
        var l = TextFlow.Layout(Make(Words(12)), [A(100, 30, new Box(0, 10, 100, 40)), new Area(new Box(0, 100, 100, 100), "g", 0)], M);
        Assert.Equal(0, l.Lines[0].Area);
        Assert.Equal(1, l.Lines[1].Area);
    }

    [Fact]
    public void Wrapping_objects_on_the_page_and_its_master_become_the_areas_exclusions()
    {
        var e = new Editor(Document.New());
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { X = 0, Y = 0, W = 200, H = 200 });
        e.Add(SheetRef.PageAt(0), new RectangleObject { X = 150, Y = 10, W = 100, H = 50, Wrap = true, WrapGap = 5 });
        e.Add(SheetRef.PageAt(0), new RectangleObject { X = 10, Y = 10, W = 10, H = 10 }); // no wrap
        e.Add(SheetRef.MasterAt(0), new EllipseObject { X = 0, Y = 150, W = 20, H = 20, Wrap = true, WrapGap = 0 });
        e.Add(SheetRef.PageAt(0), new RectangleObject { X = 500, Y = 500, W = 10, H = 10, Wrap = true }); // nowhere near
        var area = Assert.Single(Chains.Areas(e.Doc, e.Doc.Stories[frame.StoryId]));
        Assert.Equal([new Box(0, 150, 20, 20), new Box(145, 5, 110, 60)], area.Keep);

        // Wrap affects only the text under it: a frame brought to the front is clear of the page's wrapping objects.
        e.Arrange([frame.Id], Editor.Order.Front);
        Assert.Equal([new Box(0, 150, 20, 20)], Assert.Single(Chains.Areas(e.Doc, e.Doc.Stories[frame.StoryId])).Keep);
        e.Arrange([frame.Id], Editor.Order.Back);

        // A frame never wraps its own text.
        e.Change("Wrap", d => d.Find(frame.Id)!.Wrap = true);
        Assert.Equal(2, Chains.Areas(e.Doc, e.Doc.Stories[frame.StoryId])[0].Keep.Count);
    }

    [Fact]
    public void Areas_with_the_same_exclusions_are_equal()
    {
        Assert.Equal(A(10, 10, new Box(1, 2, 3, 4)), A(10, 10, new Box(1, 2, 3, 4)));
        Assert.NotEqual(A(10, 10, new Box(1, 2, 3, 4)), A(10, 10, new Box(1, 2, 3, 5)));
        Assert.Equal(new Area(new Box(0, 0, 1, 1), "f", 0), new Area(new Box(0, 0, 1, 1), "f", 0, []));
    }

    [Fact]
    public void Fields_show_the_page_number_and_count()
    {
        var s = Make($"Page {Fields.PageNumber} of {Fields.PageCount}");
        var l = TextFlow.Layout(s, [A(200, 100)], M, new FieldContext(12, 30));
        Assert.Equal("Page 12 of 30", l.Display(0, s.Length));
        Assert.Equal(13 * 5, l.CaretX(0, s.Length)); // measured as shown: 13 characters
        Assert.Equal(7 * 5, l.CaretX(0, 6));        // after the field: "Page 12"
        Assert.Equal("Page # of #", Fields.Expand(s.Text, null));
        Assert.Equal("Page # of 4", Fields.Expand(s.Text, new FieldContext(0, 4)));
    }

    [Fact]
    public void A_storys_fields_count_from_the_page_its_first_frame_is_on()
    {
        var e = new Editor(Document.New());
        e.AddPage(1);
        e.AddPage(2);
        var frame = e.Add(SheetRef.PageAt(2), new TextFrame { W = 100, H = 100 });
        var footer = e.Add(SheetRef.MasterAt(0), new TextFrame { W = 100, H = 100 });
        Assert.Equal(3, Chains.PageOf(e.Doc, e.Doc.Stories[frame.StoryId]));
        Assert.Equal(0, Chains.PageOf(e.Doc, e.Doc.Stories[footer.StoryId]));
    }
}
