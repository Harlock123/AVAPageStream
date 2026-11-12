using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 4: text on a path, and footnotes.</summary>
public class PathAndNotesTests
{
    private static readonly FixedMeasurer M = new();

    private static Story Text(string text, Align align = Align.Left)
    {
        var s = Story.Create("s", new ParagraphStyle("Body", new CharFormat(Size: 10), new ParaFormat(Align: align)));
        s.Insert(0, text);
        return s;
    }

    private static readonly Pt[] Across = [new(0, 0), new(100, 0)];

    [Fact]
    public void Text_on_a_path_follows_it_from_its_start()
    {
        // At size 10 each character is 5 pt wide.
        var (glyphs, over) = PathText.Place(Text("abc"), Across, M);
        Assert.Equal([0.0, 5, 10], glyphs.Select(g => Math.Round(g.At.X, 6)));
        Assert.All(glyphs, g => Assert.Equal(0, g.Angle));
        Assert.Equal(3, over);

        // Down the page: turned a quarter, going down.
        var (down, _) = PathText.Place(Text("ab"), [new(0, 0), new(0, 100)], M);
        Assert.Equal(90, down[0].Angle, 6);
        Assert.Equal(new Pt(0, 5), new Pt(Math.Round(down[1].At.X, 6), Math.Round(down[1].At.Y, 6)));

        // Round a corner: the second leg's characters turn with it.
        var (corner, _) = PathText.Place(Text("abcdef"), [new(0, 0), new(15, 0), new(15, 100)], M);
        Assert.Equal([0.0, 0, 0, 90, 90, 90], corner.Select(g => Math.Round(g.Angle, 6)));
    }

    [Fact]
    public void Its_alignment_and_offset_place_it_and_what_runs_off_the_end_is_overset()
    {
        Assert.Equal(42.5, PathText.Place(Text("abc", Align.Centre), Across, M).Glyphs[0].At.X, 6); // (100 - 15) / 2
        Assert.Equal(85, PathText.Place(Text("abc", Align.Right), Across, M).Glyphs[0].At.X, 6);
        Assert.Equal(20, PathText.Place(Text("abc"), Across, M, offset: 20).Glyphs[0].At.X, 6);
        var (glyphs, over) = PathText.Place(Text(new string('x', 30)), Across, M);
        Assert.Equal(20, over); // the 21st's middle is past the end
        Assert.Equal(20, glyphs.Count);
        Assert.DoesNotContain(PathText.Place(Text("a b"), Across, M).Glyphs, g => g.Text == " "); // spaces take room, draw nothing
    }

    [Fact]
    public void A_path_with_text_is_copied_and_deleted_with_its_story()
    {
        var e = new Editor(Document.New());
        var path = e.Add(SheetRef.PageAt(0), new PathObject { Nodes = [PathNode.Corner(new Pt(0, 0)), PathNode.Corner(new Pt(200, 50))] });
        Assert.Null(e.PutTextOnPath(path.Id, "Along the wall"));
        var p = (PathObject)e.Doc.Find(path.Id)!;
        Assert.Equal("Along the wall", e.Doc.Stories[p.StoryId!].Text);
        Assert.Equal(path.Id, e.Doc.Stories[p.StoryId!].OnPath);
        Assert.Equal("Text on a path", p.Kind);

        var copy = (PathObject)e.Doc.Find(Assert.Single(e.Duplicate([path.Id])))!;
        Assert.NotEqual(p.StoryId, copy.StoryId);
        Assert.Equal("Along the wall", e.Doc.Stories[copy.StoryId!].Text);
        Assert.Equal(copy.Id, e.Doc.Stories[copy.StoryId!].OnPath);

        var stories = e.Doc.Stories.Count;
        e.Delete([copy.Id]);
        Assert.Equal(stories - 1, e.Doc.Stories.Count);
        e.TakeTextOffPath(path.Id);
        Assert.Null(((PathObject)e.Doc.Find(path.Id)!).StoryId);
        Assert.Equal(stories - 2, e.Doc.Stories.Count);
        e.Undo();
        Assert.NotNull(((PathObject)e.Doc.Find(path.Id)!).StoryId);
    }

    private static (Editor E, string Story) Frame(string text)
    {
        var e = new Editor(Document.New());
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { W = 300, H = 300 });
        e.ReplaceText(f.StoryId, 0, 0, text);
        return (e, f.StoryId);
    }

    [Fact]
    public void Footnotes_are_numbered_through_their_story_in_order()
    {
        var (e, id) = Frame("One two three.");
        var second = e.InsertFootnote(id, 7, "The second.")!;
        var first = e.InsertFootnote(id, 3, "The first.")!; // before it: it becomes 1, the other 2
        var s = e.Doc.Stories[id];
        Assert.Equal(new Dictionary<string, int> { [first] = 1, [second] = 2 }, Footnotes.Numbers(s));
        Assert.Equal(id, e.Doc.Stories[first].NoteOf);
        Assert.Equal(Script.Super, s.FormatAt(3).Script);
        var layout = TextFlow.Layout(s, Chains.Areas(e.Doc, s), M);
        Assert.Equal("One1 two2 three.", layout.Display(0, s.Length));
        Assert.Equal("1 The first.", Fields.Expand(e.Doc.Stories[first].Text, new FieldContext(1, 1, null, 1)));
        Assert.Equal(first, Footnotes.At(s, 4)); // the caret just after its number
        Assert.Contains(e.Doc.Styles, st => st.Name == "Footnote");

        // Typing after a reference makes no other reference.
        e.ReplaceText(id, 4, 0, "x", e.Doc.Stories[id].TypingFormat(4));
        Assert.Null(e.Doc.Stories[id].FormatAt(4).Note);
        Assert.Equal(Script.Normal, e.Doc.Stories[id].FormatAt(4).Script);
    }

    [Fact]
    public void Deleting_a_reference_deletes_its_note_and_copying_it_copies_the_note()
    {
        var (e, id) = Frame("Text here.");
        var note = e.InsertFootnote(id, 4, "A note.")!;
        var frame = e.Doc.Pages[0].Objects[0];
        var copy = (TextFrame)e.Doc.Find(Assert.Single(e.Duplicate([frame.Id])))!;
        var copied = e.Doc.Stories[copy.StoryId];
        var copiedNote = Assert.Single(Footnotes.Numbers(copied).Keys);
        Assert.NotEqual(note, copiedNote);
        Assert.Equal(e.Doc.Stories[note].Text, e.Doc.Stories[copiedNote].Text);
        Assert.Equal(copied.Id, e.Doc.Stories[copiedNote].NoteOf);

        e.ReplaceText(id, 4, 1, "");
        Assert.False(e.Doc.Stories.ContainsKey(note));
        Assert.True(e.Doc.Stories.ContainsKey(copiedNote));
        e.Undo();
        Assert.True(e.Doc.Stories.ContainsKey(note));
    }

    [Fact]
    public void A_footnote_is_set_at_the_foot_of_the_column_its_reference_is_in()
    {
        var doc = Document.New();
        var story = Text("a\nb\nc\nd");
        story.Id = "s";
        doc.Stories["s"] = story;
        var note = Footnotes.NewNote(doc, "s", "N");
        story.Insert(1, Fields.Footnote.ToString(), story.FormatAt(0) with { Note = note.Id, Script = Script.Super });
        var areas = new[] { new Area(new Box(0, 0, 100, 60), "f", 0), new Area(new Box(200, 0, 100, 60), "f", 1) };

        // Without room for notes, four lines of 12 pt fit in 60.
        Assert.All(TextFlow.Layout(story, areas, M).Lines, l => Assert.Equal(0, l.Area));

        var h = Footnotes.Height(note, 100, M);
        Assert.True(h > 4);
        var layout = TextFlow.Layout(story, areas, M, noteHeight: Footnotes.Heights(doc, M));
        var box = Assert.Single(layout.Notes);
        Assert.Equal((0, note.Id, 1), (box.Area, box.Id, box.Number));
        Assert.Equal(60 - h, box.Box.Y, 6);
        // The fourth line (its foot at 46) no longer fits above the note and its rule.
        Assert.Equal([0, 0, 0, 1], layout.Lines.Select(l => l.Area));

        // A reference that falls in the next column takes its note there.
        var later = Text("a\nb\nc\nd\ne\nf");
        later.Id = "t";
        doc.Stories["t"] = later;
        var n2 = Footnotes.NewNote(doc, "t", "M");
        later.Insert(11, Fields.Footnote.ToString(), later.FormatAt(10) with { Note = n2.Id, Script = Script.Super });
        var l2 = TextFlow.Layout(later, areas, M, noteHeight: Footnotes.Heights(doc, M));
        Assert.Equal(1, Assert.Single(l2.Notes).Area);
    }
}
