using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 9, stage 2: cross-references.</summary>
public class ReferenceTests
{
    private static readonly FixedMeasurer M = new();

    /// <summary>A document of two pages: a frame on each, the second's text starting with a heading.</summary>
    private static (Editor E, TextFrame First, TextFrame Second) TwoPages()
    {
        var e = new Editor(Document.New());
        e.AddPage(1);
        var a = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        var b = e.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        e.ReplaceText(a.StoryId, 0, 0, "See ");
        e.ReplaceText(b.StoryId, 0, 0, "Tide tables\nHigh water is at noon.");
        e.ApplyStyle(b.StoryId, 0, 0, "Heading");
        return (e, a, b);
    }

    private static TextLayout? Lay(Document doc, Story s) => TextFlow.Layout(s, Chains.Areas(doc, s), M);

    [Fact]
    public void A_reference_shows_its_places_page_its_text_or_both()
    {
        var (e, a, b) = TwoPages();
        var name = e.PlaceForParagraph(b.StoryId, 0);
        Assert.Equal("Tide tables", name);
        Assert.Equal(name, e.PlaceForParagraph(b.StoryId, 0)); // the same place again, not another
        Assert.Single(References.Places(e.Doc));
        foreach (var kind in new[] { RefKind.Page, RefKind.Text, RefKind.Both }) e.InsertReference(a.StoryId, e.Doc.Stories[a.StoryId].Length, name, kind);
        e.InsertReference(a.StoryId, 0, "Nowhere", RefKind.Page);
        var story = e.Doc.Stories[a.StoryId];
        var refs = References.Resolve(e.Doc, story, s => Lay(e.Doc, s))!;
        Assert.Equal("2", refs.Shown[References.Key(name, RefKind.Page)]);
        Assert.Equal("Tide tables", refs.Shown[References.Key(name, RefKind.Text)]);
        Assert.Equal("“Tide tables” on page 2", refs.Shown[References.Key(name, RefKind.Both)]);
        Assert.Equal("??", refs.Shown[References.Key("Nowhere", RefKind.Page)]);
        // Laid out with them: the text shows what they show.
        var layout = TextFlow.Layout(story, Chains.Areas(e.Doc, story), M, new FieldContext(1, 2, null, 0, refs));
        Assert.Equal("??See 2Tide tables“Tide tables” on page 2", layout.Display(0, story.Length));
        // A page label of its own (a section's numbering) is what it shows.
        e.Change("Section", d => d.Pages[1].Section = new PageNumbering(5, NumberStyle.LowerRoman));
        Assert.Equal("v", References.Resolve(e.Doc, story, s => Lay(e.Doc, s))!.Shown[References.Key(name, RefKind.Page)]);
    }

    [Fact]
    public void Places_need_names_of_their_own()
    {
        var (e, a, _) = TwoPages();
        Assert.Null(e.NamePlace(a.StoryId, 0, "Start"));
        Assert.Contains("already", e.NamePlace(a.StoryId, 2, "Start"));
        Assert.NotNull(e.NamePlace(a.StoryId, 2, " "));
        Assert.Equal("Start (2)", References.NameFor(e.Doc, "Start"));
    }
}
