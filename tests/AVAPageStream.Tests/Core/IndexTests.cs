using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 9, stage 3: an index.</summary>
public class IndexTests
{
    private static readonly FixedMeasurer M = new();

    /// <summary>Four pages, a frame on each with a line of text.</summary>
    private static (Editor E, List<TextFrame> Frames) FourPages()
    {
        var e = new Editor(Document.New());
        for (var i = 1; i < 4; i++) e.AddPage(i);
        var frames = new List<TextFrame>();
        for (var i = 0; i < 4; i++)
        {
            var f = e.Add(SheetRef.PageAt(i), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
            e.ReplaceText(f.StoryId, 0, 0, $"Text on page {i + 1}.");
            frames.Add(f);
        }
        return (e, frames);
    }

    private static string Lines(List<PlacedPara> paras) => string.Join("\n", paras.Select(p => p.Style + ": " + string.Concat(p.Runs.Select(r => r.Text))));

    [Fact]
    public void An_index_files_topics_under_letters_with_their_pages_joined()
    {
        var (e, f) = FourPages();
        void Mark(int page, IndexMark m) => Assert.Null(e.MarkIndex(f[page].StoryId, 0, m));
        Mark(0, new("tides"));
        Mark(1, new("Tides"));
        Mark(2, new("Tides"));
        Mark(3, new("Tides"));
        Mark(0, new("Tides", "neap"));
        Mark(2, new("Tides", "spring"));
        Mark(1, new("Anchors"));
        Mark(3, new("Anchors"));
        Mark(2, new("Moon", See: "Tides"));
        Mark(1, new("Bearings", See: "Compass"));
        Mark(1, new("Bearings"));
        Mark(0, new("1990s storm"));
        var entries = Indexes.Entries(e.Doc, M);
        Assert.Equal(12, entries.Count);
        Assert.All(entries, x => Assert.NotNull(x.Page));
        Assert.Equal("""
            Index Heading: #
            Index 1: 1990s storm, 1
            Index Heading: A
            Index 1: Anchors, 2, 4
            Index Heading: B
            Index 1: Bearings, 2. See also Compass.
            Index Heading: M
            Index 1: Moon. See Tides.
            Index Heading: T
            Index 1: Tides, 1–4
            Index 2: neap, 1
            Index 2: spring, 3
            """, Lines(Indexes.Paragraphs(e.Doc, entries)));
    }

    [Fact]
    public void Entries_are_invisible_in_the_text_and_need_a_topic()
    {
        var (e, f) = FourPages();
        Assert.NotNull(e.MarkIndex(f[0].StoryId, 0, new(" ")));
        Assert.NotNull(e.MarkIndex(f[0].StoryId, 0, new("a\tb")));
        Assert.Null(e.MarkIndex(f[0].StoryId, 5, new("Text")));
        var story = e.Doc.Stories[f[0].StoryId];
        Assert.Equal(Fields.IndexEntry, story.Text[5]);
        var layout = TextFlow.Layout(story, Chains.Areas(e.Doc, story), M);
        Assert.Equal("Text on page 1.", layout.Display(0, story.Length));
        Assert.Equal("Text on page 1.", Fields.Expand(story.Text, null));
        // Survives saving.
        var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms);
        Assert.Equal("Text", Indexes.Entries(back, M).Single().Mark.Topic);
    }

    [Fact]
    public void Making_the_index_adds_its_styles_and_uses_page_labels()
    {
        var (e, f) = FourPages();
        e.MarkIndex(f[3].StoryId, 0, new("Charts"));
        e.Change("Section", d => d.Pages[2].Section = new PageNumbering(1, NumberStyle.UpperRoman));
        var paras = Indexes.Paragraphs(e.Doc, Indexes.Entries(e.Doc, M));
        Assert.Equal("Index 1: Charts, II", Lines(paras).Split('\n')[1]);
        var end = e.InsertIndex(f[0].StoryId, e.Doc.Stories[f[0].StoryId].Length, M);
        Assert.Contains(e.Doc.Styles, s => s.Name == Indexes.HeadingStyle);
        Assert.Contains(e.Doc.Styles, s => s.Name == "Index 1");
        Assert.Contains("Charts, II", e.Doc.Stories[f[0].StoryId].Text);
        Assert.True(end > 0);
        Assert.True(e.Doc.Stories[f[0].StoryId].IsWellFormed());
        e.Undo();
        Assert.DoesNotContain(e.Doc.Styles, s => s.Name == Indexes.HeadingStyle);
    }

    [Fact]
    public void A_footnotes_entry_is_on_its_references_page()
    {
        var (e, f) = FourPages();
        var note = e.InsertFootnote(f[2].StoryId, 4);
        Assert.NotNull(note);
        e.MarkIndex(note!, e.Doc.Stories[note!].Length, new("Footnotes"));
        Assert.Equal(2, Indexes.Entries(e.Doc, M).Single().Page);
    }
}
