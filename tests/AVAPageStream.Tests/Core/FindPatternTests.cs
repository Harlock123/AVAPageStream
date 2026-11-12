using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 9, stage 4: finding with patterns, and by style.</summary>
public class FindPatternTests
{
    private static (Editor E, string Id) OneStory(string text)
    {
        var e = new Editor(Document.New());
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { W = 300, H = 300 });
        e.ReplaceText(f.StoryId, 0, 0, text);
        return (e, f.StoryId);
    }

    private static List<string> Texts(Editor e, string query, FindOptions o) =>
        Search.FindAll(e.Doc, query, o).Select(m => e.Doc.Stories[m.StoryId].Text.Substring(m.Start, m.Length)).ToList();

    [Fact]
    public void A_pattern_finds_and_its_groups_go_in_the_replacement()
    {
        var (e, id) = OneStory("High water 12:40, low water 6:55.\nNext high water 1:20.");
        var o = new FindOptions(Regex: true);
        Assert.Equal(["12:40", "6:55", "1:20"], Texts(e, @"\d+:\d\d", o));
        Assert.Equal(["High", "high"], Texts(e, "high", o));                         // without case
        Assert.Equal(["high"], Texts(e, "high", o with { MatchCase = true }));
        Assert.Equal(["High water", "Next high water"], Texts(e, "^[^,.\n]*water", o)); // ^ at each paragraph
        Assert.Equal(["low"], Texts(e, "lo.", o with { WholeWord = true }));
        Assert.Empty(Texts(e, "[", o));                                               // a pattern that doesn't read finds nothing
        Assert.Contains("regular expression", Search.Problem(e.Doc, "[", o));
        Assert.Null(Search.Problem(e.Doc, "[", new FindOptions()));                   // as plain text, it's fine
        Assert.Empty(Texts(e, @"\d+", new FindOptions()));                            // not a pattern without asking
        // Groups: 12:40 becomes 12.40, and so on, as one undo step.
        Assert.Equal(3, e.ReplaceAll(@"(\d+):(\d\d)", "$1.$2", o));
        Assert.Equal("High water 12.40, low water 6.55.\nNext high water 1.20.", e.Doc.Stories[id].Text);
        e.Undo();
        Assert.Equal("High water 12:40, low water 6:55.\nNext high water 1:20.", e.Doc.Stories[id].Text);
        // \n in the replacement starts a paragraph; $$ is a dollar.
        e.ReplaceAll(@",\s*", @"\n", o);
        Assert.Equal(3, e.Doc.Stories[id].ParagraphCount);
        Assert.True(e.Doc.Stories[id].IsWellFormed());
        var m = Search.FindAll(e.Doc, @"(\d+):(\d\d)", o)[0];
        Assert.Equal("$12", Search.Replacement(e.Doc, m, @"(\d+):(\d\d)", "$$$1", o));
        Assert.True(Search.IsFound(e.Doc, m, @"(\d+):(\d\d)", o));
        Assert.False(Search.IsFound(e.Doc, m with { Length = 2 }, @"(\d+):(\d\d)", o));
    }

    [Fact]
    public void Text_is_found_in_a_style_or_by_its_style_alone()
    {
        var (e, id) = OneStory("Tides\nThe tide turns at noon, and the tide tables say so.\nMoorings\nNo tide here.");
        e.ApplyStyle(id, 0, 0, "Heading");
        var story = e.Doc.Stories[id];
        e.ApplyStyle(id, story.Text.IndexOf("Moorings", StringComparison.Ordinal), 0, "Heading");
        var tables = story.Text.IndexOf("tide tables", StringComparison.Ordinal);
        e.ApplyCharStyle(id, tables, "tide tables".Length, "Emphasis");
        // In a paragraph style.
        Assert.Equal(["Tide"], Texts(e, "tide", new FindOptions(ParaStyle: "Heading")));
        Assert.Equal(["tide", "tide", "tide"], Texts(e, "tide", new FindOptions(ParaStyle: "Body")));
        // In a character style, with a pattern.
        Assert.Equal(["tide"], Texts(e, "t.de", new FindOptions(Regex: true, CharStyle: "Emphasis")));
        // Nothing to find: each stretch in the style.
        Assert.Equal(["Tides", "Moorings"], Texts(e, "", new FindOptions(ParaStyle: "Heading")));
        Assert.Equal(["tide tables"], Texts(e, "", new FindOptions(CharStyle: "Emphasis")));
        Assert.NotNull(Search.Problem(e.Doc, "", new FindOptions()));
        Assert.Contains("no paragraph style", Search.Problem(e.Doc, "x", new FindOptions(ParaStyle: "Nope")));
        // Replacing every heading's text.
        Assert.Equal(2, e.ReplaceAll("", "Section", new FindOptions(ParaStyle: "Heading")));
        Assert.StartsWith("Section\n", e.Doc.Stories[id].Text);
        Assert.Contains("\nSection\n", e.Doc.Stories[id].Text);
        Assert.Equal("Heading", e.Doc.Stories[id].Paras[0].Style);
    }
}
