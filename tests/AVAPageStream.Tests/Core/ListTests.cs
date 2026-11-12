using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 10, stage 1: contents and indexes that keep up; and markers that survive styling.</summary>
public class ListTests
{
    private static readonly FixedMeasurer M = new();

    [Fact]
    public void Markers_keep_what_they_mark_when_their_paragraph_is_styled()
    {
        var e = new Editor(Document.New());
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { W = 300, H = 300 });
        e.ReplaceText(f.StoryId, 0, 0, "Tides and moorings");
        e.MarkIndex(f.StoryId, 0, new IndexMark("Tides"));
        e.NamePlace(f.StoryId, 6, "Here");
        e.InsertReference(f.StoryId, 10, "Here", RefKind.Page);
        var note = e.InsertFootnote(f.StoryId, 3)!;
        e.ApplyStyle(f.StoryId, 0, 0, "Heading");
        var story = e.Doc.Stories[f.StoryId];
        Assert.Single(Indexes.Entries(e.Doc, M));
        Assert.Single(References.Places(e.Doc));
        Assert.Contains(story.Runs, r => r.Format.Ref is not null);
        Assert.Contains(story.Runs, r => r.Format.Note == note && r.Format.Script == Script.Super);
        // And when the style changes.
        e.Change("Heading", d => { var i = d.Styles.FindIndex(s => s.Name == "Heading"); d.Styles[i] = d.Styles[i] with { Char = d.Styles[i].Char with { Size = 30 } }; d.Stories[f.StoryId].Restyle(d.Styles[i], d.CharStyle); });
        Assert.Single(Indexes.Entries(e.Doc, M));
        Assert.Contains(e.Doc.Stories[f.StoryId].Runs, r => r.Format.Note == note);
    }

    /// <summary>Headings on pages 2 and 3, and a frame on page 1 for the contents.</summary>
    private static (Editor E, string Toc, string Body) Book()
    {
        var e = new Editor(Document.New());
        e.AddPage(1);
        e.AddPage(2);
        var toc = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        var a = e.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        var b = e.Add(SheetRef.PageAt(2), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        e.Change("Link", d => Chains.Link(d, (TextFrame)d.Find(a.Id)!, (TextFrame)d.Find(b.Id)!));
        e.ReplaceText(toc.StoryId, 0, 0, "Contents\n");
        e.ApplyStyle(toc.StoryId, 0, 0, "Heading");
        e.ReplaceText(a.StoryId, 0, 0, "Tides\nText.\nMoorings\nMore.");
        e.ApplyStyle(a.StoryId, 0, 0, "Heading");
        var story = e.Doc.Stories[a.StoryId];
        e.ApplyStyle(a.StoryId, story.Text.IndexOf("Moorings", StringComparison.Ordinal), 0, "Heading");
        return (e, toc.StoryId, a.StoryId);
    }

    private static string Shown(Editor e, string id) => Fields.Expand(e.Doc.Stories[id].Text, null);

    [Fact]
    public void A_contents_is_remembered_and_made_again_where_it_is()
    {
        var (e, toc, body) = Book();
        Assert.True(e.InsertContents(toc, e.Doc.Stories[toc].Length, ["Heading"], 300, M) > 0);
        var list = Assert.Single(e.Doc.Lists);
        Assert.Equal(ListKind.Contents, list.Kind);
        Assert.Equal("Contents\nContents\t1\nTides\t2\nMoorings\t2", Shown(e, toc));
        Assert.False(GeneratedLists.IsStale(e.Doc, list, M));
        Assert.Equal(0, e.UpdateLists(M)); // nothing to do: no undo step
        Assert.Equal("Table of contents", e.UndoLabel);
        // Moorings moves on to page 3; a page goes in before them all.
        var story = e.Doc.Stories[body];
        e.ReplaceText(body, story.Text.IndexOf("\nMoorings", StringComparison.Ordinal), 0, "\n" + string.Concat(Enumerable.Repeat("Words words words words words. ", 40)));
        e.AddPage(0);
        Assert.True(GeneratedLists.IsStale(e.Doc, list, M));
        Assert.Equal(1, e.UpdateLists(M));
        Assert.Equal("Contents\nContents\t2\nTides\t3\nMoorings\t4", Shown(e, toc));
        Assert.Equal("Heading", e.Doc.Stories[toc].Paras[0].Style);           // what's before it is left alone
        Assert.Equal(Contents.StyleFor(0), e.Doc.Stories[toc].Paras[3].Style);
        e.Undo();
        Assert.Equal("Contents\nContents\t1\nTides\t2\nMoorings\t2", Shown(e, toc));
        // Saved with the document.
        var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        Assert.Equal(list, Assert.Single(DocumentFile.Read(ms).Lists));
        // Deleted, it is forgotten.
        e.Redo();
        var (_, s, end) = GeneratedLists.Where(e.Doc, list)!.Value;
        e.ReplaceText(toc, s, end - s, "");
        e.UpdateLists(M);
        Assert.Empty(e.Doc.Lists);
    }

    [Fact]
    public void Text_after_a_list_is_kept_and_the_list_settles_when_it_pushes_its_own_headings_on()
    {
        var e = new Editor(Document.New());
        e.AddPage(1);
        var a = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 34 });
        var b = e.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        e.Change("Link", d => Chains.Link(d, (TextFrame)d.Find(a.Id)!, (TextFrame)d.Find(b.Id)!));
        e.ReplaceText(a.StoryId, 0, 0, "Tides\nText.");
        e.ApplyStyle(a.StoryId, 0, 0, "Heading");
        Assert.Equal(1, Contents.Find(e.Doc, ["Heading"], M).Single().Page); // on page 1 before the contents
        e.InsertContents(a.StoryId, 0, ["Heading"], 300, M);
        Assert.Equal("Tides\t2\nTides\nText.", Shown(e, a.StoryId));       // the contents pushed it to page 2
        Assert.False(GeneratedLists.IsStale(e.Doc, e.Doc.Lists[0], M));
    }

    [Fact]
    public void An_index_keeps_up_and_preflight_says_when_a_list_is_out_of_date()
    {
        var (e, toc, body) = Book();
        e.MarkIndex(body, 0, new IndexMark("Tides"));
        Assert.True(e.InsertIndex(toc, e.Doc.Stories[toc].Length, M) > 0);
        Assert.EndsWith("T\nTides, 2", Shown(e, toc));
        var options = new PreflightOptions(M, _ => null, _ => FontState.Bundled);
        Assert.DoesNotContain(Preflight.Check(e.Doc, options), i => i.Kind == Problem.ListStale);
        e.AddPage(0);
        var issue = Assert.Single(Preflight.Check(e.Doc, options), i => i.Kind == Problem.ListStale);
        Assert.Contains("The index is out of date", issue.Message);
        Assert.False(issue.Serious);
        e.UpdateLists(M);
        Assert.EndsWith("T\nTides, 3", Shown(e, toc));
        // Made again with the caret in it: still one index.
        e.InsertIndex(toc, e.Doc.Stories[toc].Length - 2, M);
        Assert.Single(e.Doc.Lists);
        Assert.Single(Regex(Shown(e, toc), "Tides, 3"));
    }

    private static IEnumerable<int> Regex(string s, string what)
    {
        for (var i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal)) yield return i;
    }
}
