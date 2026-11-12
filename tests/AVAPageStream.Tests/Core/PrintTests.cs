using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 3, first part: small caps and scripts, the table of contents, booklets, templates.</summary>
public class PrintTests
{
    private static readonly FixedMeasurer M = new();

    [Fact]
    public void Small_caps_set_lower_case_as_smaller_capitals()
    {
        var f = new CharFormat(Size: 10, SmallCaps: true);
        var parts = TextFlow.SmallCapsPieces("Harbour Notes", f).ToList();
        Assert.Equal(["H", "ARBOUR", " N", "OTES"], parts.Select(p => p.Text));
        Assert.Equal(10, parts[0].Format.Size);
        Assert.Equal(7.2, parts[1].Format.Size);
        Assert.Equal(2 * 5 + 10 * 3.6 + 1 * 5, TextFlow.Measure(M, "Harbour Notes", f), 6); // 3 at full size, 10 small
    }

    [Fact]
    public void Super_and_subscripts_are_smaller_and_raised_or_lowered()
    {
        var sup = TextFlow.AsSet(new CharFormat(Size: 10, Script: Script.Super));
        Assert.Equal(6, sup.Size);
        Assert.Equal(3.3, sup.Shift, 6);
        var sub = TextFlow.AsSet(new CharFormat(Size: 10, Script: Script.Sub, Shift: 1));
        Assert.Equal(6, sub.Size);
        Assert.Equal(1 - 1.6, sub.Shift, 6);
        Assert.Equal(3, TextFlow.Measure(M, "2", new CharFormat(Size: 10, Script: Script.Super))); // half of 6
        Assert.Equal(sup, TextFlow.AsSet(sup)); // setting twice changes nothing
    }

    [Fact]
    public void A_raised_character_lifts_its_line()
    {
        var s = Story.Create("s", new ParagraphStyle("Body", new CharFormat(Size: 10), new ParaFormat()));
        s.Insert(0, "E=mc2");
        s.FormatChars(4, 1, f => f with { Script = Script.Super });
        var l = TextFlow.Layout(s, [new Area(new Box(0, 0, 200, 100), "f", 0)], M);
        Assert.Equal(3.3 + 6 * 0.8, l.Lines[0].Ascent, 6); // the superscript's ascent, raised, is above the 8 of the rest
        Assert.Equal(4 * 5 + 3, l.CaretX(0, 5));
    }

    /// <summary>
    /// Five pages, a 30 pt frame on each, linked: each frame holds one line before a heading pushes on to the next.
    /// "One" is on page 1, "Two" (a subheading) on page 3, "Three" on page 5, and "Four" doesn't fit at all.
    /// </summary>
    private static Editor Book()
    {
        var e = new Editor(Document.New());
        for (var i = 1; i < 5; i++) e.AddPage(i);
        string? previous = null;
        for (var i = 0; i < 5; i++)
        {
            var f = e.Add(SheetRef.PageAt(i), new TextFrame { X = 72, Y = 72, W = 400, H = 30 });
            if (previous is { } p) e.Change("Link", d => Chains.Link(d, (TextFrame)d.Find(p)!, (TextFrame)d.Find(f.Id)!));
            previous = f.Id;
        }
        var story = ((TextFrame)e.Doc.Pages[0].Objects[0]).StoryId;
        e.ReplaceText(story, 0, 0, "One\nbody\nTwo\nbody\nThree\nFour");
        e.ApplyStyle(story, 0, 0, "Heading");
        e.ApplyStyle(story, 9, 0, "Subheading");
        e.ApplyStyle(story, 18, 0, "Heading");
        e.ApplyStyle(story, 24, 0, "Heading");
        return e;
    }

    [Fact]
    public void The_contents_list_headings_by_level_with_their_pages()
    {
        var e = Book();
        var entries = Contents.Find(e.Doc, ["Heading", "Subheading"], M);
        Assert.Equal([("One", 0, (int?)1), ("Two", 1, 3), ("Three", 0, 5), ("Four", 0, null)], entries.Select(x => (x.Text, x.Level, x.Page)));
    }

    [Fact]
    public void The_contents_go_in_as_styled_paragraphs_with_a_leader_to_the_page()
    {
        var e = Book();
        var toc = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 400, W = 300, H = 300 });
        e.InsertContents(toc.StoryId, 0, ["Heading", "Subheading"], 300, M);
        var story = e.Doc.Stories[toc.StoryId];
        Assert.Equal("One\t1\nTwo\t3\nThree\t5\nFour\t–", Fields.Expand(story.Text, null));
        Assert.Equal(["Contents 1", "Contents 2", "Contents 1", "Contents 1"], story.Paras.Select(p => p.Style));
        Assert.Equal([new TabStop(300, TabKind.Right, '.')], Tabs.Parse(e.Doc.Style("Contents 2").Para.Tabs));
        Assert.Equal(14, e.Doc.Style("Contents 2").Para.LeftIndent);
        // The page numbers end at the tab.
        var line = TextFlow.Layout(story, Chains.Areas(e.Doc, story), M).Lines[0];
        Assert.Equal(72 + 300, line.Pieces[^1].X + line.Pieces[^1].Width, 6);
        e.Undo();
        Assert.Equal("", e.Doc.Stories[toc.StoryId].Text);
        Assert.DoesNotContain(e.Doc.Styles, s => s.Name.StartsWith("Contents"));
    }

    [Theory]
    [InlineData(8, "7|0 1|6 5|2 3|4")]
    [InlineData(4, "3|0 1|2")]
    [InlineData(6, "-|0 1|- 5|2 3|4")] // made up to eight with two blank pages at the end
    [InlineData(1, "-|0 -|-")]
    public void Booklets_pair_the_pages_in_folding_order(int pages, string expected)
    {
        static string Show(int? p) => p?.ToString() ?? "-";
        Assert.Equal(expected, string.Join(" ", Imposition.Booklet(pages).Select(s => $"{Show(s.Left)}|{Show(s.Right)}")));
    }

    [Fact]
    public void The_built_in_templates_make_working_documents()
    {
        var news = Templates.BuiltIn.Single(t => t.Name == "Newsletter").Make();
        Assert.Single(news.Pages);
        Assert.Contains(news.Masters[0].All.OfType<TextFrame>(), f => news.Stories[f.StoryId].Text.Contains(Fields.PageNumber));
        Assert.Contains(news.Stories.Values, s => s.Frames.Count == 2);

        var book = Templates.BuiltIn.Single(t => t.Name == "Booklet").Make();
        Assert.True(book.Setup.Facing);
        Assert.Equal(8, book.Pages.Count);
        var story = Assert.Single(book.Stories.Values, s => s.Frames.Count == 8); // one story through every page
        Assert.True(story.IsWellFormed());
        Assert.NotEmpty(book.Masters[0].Left!.Objects);
        // Each page's frame sits inside its own margins: the inside is wider, so left and right pages differ.
        Assert.NotEqual(book.Pages[0].Objects[0].X, book.Pages[1].Objects[0].X);
        Assert.Equal(book.GuidesFor(book.Pages[1]).Live.X, book.Pages[1].Objects[0].X);
    }

    [Fact]
    public void Templates_saved_in_a_folder_are_found()
    {
        var dir = Directory.CreateTempSubdirectory("avapt-").FullName;
        DocumentFile.Save(Document.New(), Path.Combine(dir, "b report.avpt"));
        DocumentFile.Save(Document.New(), Path.Combine(dir, "A letter.avpt"));
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a template");
        Assert.Equal(["A letter", "b report"], Templates.InFolder(dir).Select(Path.GetFileNameWithoutExtension));
        Assert.Empty(Templates.InFolder(Path.Combine(dir, "none")));
    }
}
