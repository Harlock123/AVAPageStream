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

    [Fact]
    public void Replacing_can_put_text_in_a_style_keeping_or_changing_it()
    {
        var (e, id) = OneStory("The Moon pulls the sea.\nNote: watch the Moon.\nThe moon rises.");
        // Kept, and put in a character style: only the words found.
        Assert.Equal(3, e.ReplaceAll("moon", "", new FindOptions(), new StyleChange(CharStyle: "Emphasis")));
        var story = e.Doc.Stories[id];
        Assert.Equal("The Moon pulls the sea.\nNote: watch the Moon.\nThe moon rises.", story.Text);
        Assert.Equal(["Moon", "Moon", "moon"], Texts(e, "", new FindOptions(CharStyle: "Emphasis")));
        Assert.True(story.FormatAt(4).Italic);
        Assert.False(story.FormatAt(0).Italic);
        Assert.Equal("Restyle 3", e.UndoLabel);
        // Out of it again.
        e.ReplaceAll("", "", new FindOptions(CharStyle: "Emphasis"), new StyleChange(CharStyle: ""));
        Assert.Empty(Texts(e, "", new FindOptions(CharStyle: "Emphasis")));
        Assert.False(e.Doc.Stories[id].FormatAt(4).Italic);
        // A pattern's paragraphs into a paragraph style.
        Assert.Equal(1, e.ReplaceAll("^Note:", "", new FindOptions(Regex: true, MatchCase: true), new StyleChange(ParaStyle: "Caption")));
        story = e.Doc.Stories[id];
        Assert.Equal(["Body", "Caption", "Body"], story.Paras.Select(p => p.Style));
        Assert.True(story.FormatAt(story.Text.IndexOf("watch", StringComparison.Ordinal)).Italic); // the whole paragraph, in Caption's italic
        // Replaced, and the new text in a style.
        e.ReplaceAll("sea", "ocean", new FindOptions(WholeWord: true), new StyleChange(CharStyle: "Strong"));
        story = e.Doc.Stories[id];
        Assert.Equal(["ocean"], Texts(e, "", new FindOptions(CharStyle: "Strong")));
        Assert.True(story.IsWellFormed());
        // One at a time.
        var m = Search.FindAll(e.Doc, "rises", new FindOptions())[0];
        var end = e.ReplaceOne(m, "", new StyleChange(CharStyle: "Strong"));
        Assert.Equal(m.Start + m.Length, end);
        Assert.Equal(["ocean", "rises"], Texts(e, "", new FindOptions(CharStyle: "Strong")));
    }

    [Fact]
    public void A_pattern_finds_across_paragraphs_and_joins_or_splits_them()
    {
        var (e, id) = OneStory("Tides\nThe tide turns\nat noon, and\nagain at midnight.\nMoorings");
        e.ApplyStyle(id, 0, 0, "Heading");
        var o = new FindOptions(Regex: true, MatchCase: true);
        Assert.Equal(["turns\nat", "and\nagain"], Texts(e, @"\w+\n[a-z]+", o));
        Assert.Equal(["Tides\nThe"], Texts(e, @"^Tides\n\w+", o));
        // Lines broken in the middle of a sentence: joined, the paragraph before keeping its format.
        Assert.Equal(2, e.ReplaceAll(@"\n(?=[a-z])", " ", o));
        var story = e.Doc.Stories[id];
        Assert.Equal("Tides\nThe tide turns at noon, and again at midnight.\nMoorings", story.Text);
        Assert.Equal(["Heading", "Body", "Body"], story.Paras.Select(p => p.Style));
        Assert.True(story.IsWellFormed());
        // And split again, each new paragraph in the format of the one it came from.
        e.ReplaceAll(@", ", @",\n", o);
        Assert.Equal(4, e.Doc.Stories[id].ParagraphCount);
        Assert.Equal("Body", e.Doc.Stories[id].Paras[2].Style);
        // In a style, a match across paragraphs needs both in it.
        Assert.Empty(Texts(e, @"Tides\nThe", o with { ParaStyle = "Heading" }));
    }
}
