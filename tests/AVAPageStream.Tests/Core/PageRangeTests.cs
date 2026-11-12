using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class PageRangeTests
{
    [Theory]
    [InlineData("1-3, 5", 6, new[] { 0, 1, 2, 4 })]
    [InlineData("5, 1-3, 2", 6, new[] { 0, 1, 2, 4 })]
    [InlineData("4-", 6, new[] { 3, 4, 5 })]
    [InlineData("-2", 6, new[] { 0, 1 })]
    [InlineData(" 2 ", 3, new[] { 1 })]
    public void Ranges_name_their_pages_once_each_in_order(string text, int count, int[] expected)
    {
        Assert.True(PageRanges.TryParse(text, count, out var pages, out var error));
        Assert.Null(error);
        Assert.Equal(expected, pages);
    }

    [Theory]
    [InlineData("", 3, "Type the pages")]
    [InlineData("0", 3, "from 1 to 3")]
    [InlineData("2-9", 3, "from 1 to 3")]
    [InlineData("3-1", 3, "from 1 to 3")]
    [InlineData("two", 3, "isn't a page")]
    [InlineData("1-2-3", 3, "isn't a page")]
    [InlineData("2", 1, "only page 1")]
    public void Bad_ranges_say_what_is_wrong(string text, int count, string reason)
    {
        Assert.False(PageRanges.TryParse(text, count, out _, out var error));
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Renaming_a_style_renames_its_paragraphs_too()
    {
        var e = new Editor(Document.New());
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { W = 200, H = 100 });
        e.ReplaceText(frame.StoryId, 0, 0, "Title\nbody");
        e.ApplyStyle(frame.StoryId, 0, 0, "Heading");
        Assert.Null(e.RenameStyle("Heading", "Headline"));
        Assert.Equal("Headline", e.Doc.Stories[frame.StoryId].Paras[0].Style);
        Assert.Equal("Body", e.Doc.Stories[frame.StoryId].Paras[1].Style);
        Assert.Contains(e.Doc.Styles, s => s.Name == "Headline" && s.Para.Style == "Headline");
        Assert.DoesNotContain(e.Doc.Styles, s => s.Name == "Heading");
        Assert.NotNull(e.RenameStyle("Headline", "Body"));
        Assert.NotNull(e.RenameStyle("Headline", " "));
        Assert.NotNull(e.RenameStyle("Nothing", "Else"));
        e.Undo();
        Assert.Equal("Heading", e.Doc.Stories[frame.StoryId].Paras[0].Style);
    }
}
