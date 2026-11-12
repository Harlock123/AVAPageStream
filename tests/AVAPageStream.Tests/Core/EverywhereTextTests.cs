using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 5: find and spelling go through footnotes and text on paths.</summary>
public class EverywhereTextTests
{
    private static (Editor E, string Story, string Note, string Path) Doc()
    {
        var e = new Editor(Document.New());
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { W = 300, H = 300 });
        e.ReplaceText(f.StoryId, 0, 0, "The harbour wall.");
        var note = e.InsertFootnote(f.StoryId, 11, "The old harbour, that is.")!;
        var path = e.Add(SheetRef.PageAt(0), new PathObject { Nodes = [PathNode.Corner(new Pt(0, 400)), PathNode.Corner(new Pt(400, 400))] });
        e.PutTextOnPath(path.Id, "Harbour lamps");
        return (e, f.StoryId, note, ((PathObject)e.Doc.Find(path.Id)!).StoryId!);
    }

    [Fact]
    public void A_storys_footnotes_come_after_it_and_text_on_a_path_where_the_path_is()
    {
        var (e, story, note, path) = Doc();
        Assert.Equal([story, note, path], Search.StoryOrder(e.Doc));
        Assert.Equal([story, note, path], Search.FindAll(e.Doc, "harbour", new FindOptions()).Select(m => m.StoryId));
        Assert.Equal(note, Search.Next(e.Doc, "harbour", new FindOptions(), story, 6)!.StoryId);
        // Contents leaves them out: they have no frames of their own.
        Assert.DoesNotContain(Contents.Find(e.Doc, ["Body"], new FixedMeasurer()), c => c.Text.Contains("old", StringComparison.Ordinal));
    }

    [Fact]
    public void The_spelling_check_goes_into_footnotes_and_along_paths()
    {
        var (e, story, note, path) = Doc();
        e.ReplaceText(note, e.Doc.Stories[note].Length, 0, " Qwzzx.");
        e.ReplaceText(path, 0, 0, "Blarfh ");
        var speller = Speller.For("en-GB"); // "harbour" is right in British English
        var first = speller.Next(e.Doc, null, story, 0)!;
        Assert.Equal((note, "Qwzzx"), (first.StoryId, e.Doc.Stories[note].Slice(first.Start, first.Length)));
        Assert.Equal(path, speller.Next(e.Doc, null, note, first.Start + first.Length)!.StoryId);
    }
}
