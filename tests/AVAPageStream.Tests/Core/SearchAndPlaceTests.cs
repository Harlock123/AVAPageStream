using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class SearchAndPlaceTests
{
    /// <summary>Two pages: a frame on page 2 holding "The cat sat", a frame on page 1 holding "Cats and a cat."</summary>
    private static (Editor E, string First, string Second) TwoStories()
    {
        var e = new Editor(Document.New());
        e.AddPage(1);
        var second = e.Add(SheetRef.PageAt(1), new TextFrame { W = 200, H = 100 });
        e.ReplaceText(second.StoryId, 0, 0, "The cat sat");
        var first = e.Add(SheetRef.PageAt(0), new TextFrame { W = 200, H = 100 });
        e.ReplaceText(first.StoryId, 0, 0, "Cats and a cat.");
        return (e, first.StoryId, second.StoryId);
    }

    [Fact]
    public void Stories_are_searched_in_page_order()
    {
        var (e, first, second) = TwoStories();
        Assert.Equal([first, second], Search.StoryOrder(e.Doc));
        var all = Search.FindAll(e.Doc, "cat", new FindOptions());
        Assert.Equal([new Match(first, 0, 3), new Match(first, 11, 3), new Match(second, 4, 3)], all);
        Assert.Equal(2, Search.FindAll(e.Doc, "cat", new FindOptions(MatchCase: true)).Count);
        Assert.Equal(2, Search.FindAll(e.Doc, "cat", new FindOptions(WholeWord: true)).Count); // not "Cats"
        Assert.Empty(Search.FindAll(e.Doc, "", new FindOptions()));
    }

    [Fact]
    public void Next_goes_on_from_the_caret_round_every_story_and_back()
    {
        var (e, first, second) = TwoStories();
        var o = new FindOptions();
        Assert.Equal(new Match(first, 0, 3), Search.Next(e.Doc, "cat", o, null, 0));
        Assert.Equal(new Match(first, 11, 3), Search.Next(e.Doc, "cat", o, first, 3));
        Assert.Equal(new Match(second, 4, 3), Search.Next(e.Doc, "cat", o, first, 14));
        Assert.Equal(new Match(first, 0, 3), Search.Next(e.Doc, "cat", o, second, 7)); // round to the start
        Assert.Equal(new Match(first, 11, 3), Search.Next(e.Doc, "cat", o, second, 4, forward: false));
        Assert.Equal(new Match(second, 4, 3), Search.Next(e.Doc, "cat", o, first, 0, forward: false)); // round to the end
        Assert.Null(Search.Next(e.Doc, "dog", o, null, 0));
    }

    [Fact]
    public void Replace_all_is_one_step_and_keeps_each_formats()
    {
        var (e, first, second) = TwoStories();
        e.FormatChars(second, 4, 3, f => f with { Bold = true });
        Assert.Equal(3, e.ReplaceAll("cat", "dog", new FindOptions()));
        Assert.Equal("dogs and a dog.", e.Doc.Stories[first].Text);
        Assert.Equal("The dog sat", e.Doc.Stories[second].Text);
        Assert.True(e.Doc.Stories[second].FormatAt(4).Bold);
        Assert.False(e.Doc.Stories[second].FormatAt(7).Bold);
        e.Undo();
        Assert.Equal("Cats and a cat.", e.Doc.Stories[first].Text);
        Assert.Equal("The cat sat", e.Doc.Stories[second].Text);
    }

    [Fact]
    public void Plain_text_is_placed_a_paragraph_a_line()
    {
        var paras = TextImport.PlainText("one\r\ntwo\n\nfour\n");
        Assert.Equal(["one", "two", "", "four"], paras.Select(p => p.Text));
        Assert.All(paras, p => Assert.Equal("Body", p.Style));
    }

    [Fact]
    public void Markdown_becomes_styled_paragraphs()
    {
        var md = """
            # The harbour

            The wall was **finished** in the *spring*,
            two years late.

            ## Steps
            - one with `code`
            - two with a [link](http://example.com)
            1. first
            > a quote that
            > runs on

            ```
            keep   this
            ```
            ---
            Escaped \*stars\* and snake_case_words.
            """;
        var paras = TextImport.Markdown(md);
        Assert.Equal(["The harbour", "The wall was finished in the spring, two years late.", "Steps",
            "•\tone with code", "•\ttwo with a link", "1.\tfirst", "a quote that runs on", "keep   this",
            "Escaped *stars* and snake_case_words."], paras.Select(p => p.Text));
        Assert.Equal(["Heading", "Body", "Subheading"], paras.Take(3).Select(p => p.Style));
        var body = paras[1].Runs;
        Assert.Contains(body, r => r.Text == "finished" && r.Bold && !r.Italic);
        Assert.Contains(body, r => r.Text == "spring" && r.Italic);
        Assert.Equal((14, -14), (paras[3].LeftIndent, paras[3].FirstIndent));
        Assert.All(paras[6].Runs, r => Assert.True(r.Italic));
        Assert.All(paras[8].Runs, r => Assert.False(r.Italic || r.Bold));
    }

    [Fact]
    public void An_unclosed_marker_stays_as_it_is()
    {
        Assert.Equal("2 * 3 = 6", string.Concat(TextImport.Inline("2 * 3 = 6").Select(r => r.Text)));
        Assert.Equal("**not bold", string.Concat(TextImport.Inline("**not bold").Select(r => r.Text)));
    }

    [Fact]
    public void Placed_paragraphs_take_their_styles_in_one_step()
    {
        var e = new Editor(Document.New());
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { W = 200, H = 300 });
        e.ReplaceText(frame.StoryId, 0, 0, "before after");
        var caret = e.Place(frame.StoryId, 7, TextImport.Markdown("# Title\n\nSome **bold** text"));
        var story = e.Doc.Stories[frame.StoryId];
        Assert.Equal("before \nTitle\nSome bold text\nafter", story.Text);
        Assert.Equal(8 + 5 + 1 + 14, caret);
        Assert.Equal("Heading", story.Paras[1].Style);
        Assert.Equal(24, story.FormatAt(8).Size);
        Assert.True(story.FormatAt(19).Bold);  // "bold"
        Assert.False(story.FormatAt(15).Bold); // "ome"
        Assert.Equal("Body", story.Paras[2].Style);
        Assert.True(story.IsWellFormed());
        e.Undo();
        Assert.Equal("before after", e.Doc.Stories[frame.StoryId].Text);
    }

    [Fact]
    public void Placing_into_an_empty_story_leaves_no_blank_paragraphs()
    {
        var e = new Editor(Document.New());
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { W = 200, H = 300 });
        e.Place(frame.StoryId, 0, TextImport.PlainText("a\nb"));
        Assert.Equal("a\nb", e.Doc.Stories[frame.StoryId].Text);
    }

    [Fact]
    public void Distributing_spaces_objects_evenly_between_the_outermost()
    {
        var e = new Editor(Document.New());
        var a = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 0, Y = 0, W = 10, H = 10 });
        var b = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 15, Y = 50, W = 20, H = 10 });
        var c = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 90, Y = 100, W = 10, H = 30 });
        Assert.Null(e.Distribute([a.Id, b.Id, c.Id], across: true));
        Assert.Equal(40, e.Doc.Find(b.Id)!.X); // gaps of 30 either side
        Assert.Equal(90, e.Doc.Find(c.Id)!.X);
        Assert.Null(e.Distribute([a.Id, b.Id, c.Id], across: false));
        Assert.Equal(50, e.Doc.Find(b.Id)!.Y); // 130 tall, 50 of objects: gaps of 40, after the first's 10
        Assert.Equal(100, e.Doc.Find(c.Id)!.Y);
        Assert.NotNull(e.Distribute([a.Id, b.Id], across: true));
    }

    [Fact]
    public void The_grid_is_snapped_to_when_on()
    {
        var setup = new Setup(Grid: 18);
        var off = SnapTargets.For(setup, null, guides: false);
        Assert.Equal(new Pt(55, 70), off.Snap(new Pt(55, 70), 3));
        var on = SnapTargets.For(setup, null, guides: false, grid: true);
        Assert.Equal(new Pt(54, 72), on.Snap(new Pt(55, 70), 3));
        Assert.Equal((-1.0, 2.0), on.SnapBox(new Box(55, 70, 10, 10), 3));
    }
}
