using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class EditorTests
{
    private static (Editor Editor, SheetRef Page) Fresh() => (new Editor(Document.New()), SheetRef.PageAt(0));

    [Fact]
    public void Changes_undo_and_redo_one_step_at_a_time()
    {
        var (e, page) = Fresh();
        var box = e.Add(page, new RectangleObject { X = 10, Y = 10, W = 50, H = 50 });
        e.Change("Move", d => d.Find(box.Id)!.X = 100);
        Assert.Equal(100, e.Doc.Find(box.Id)!.X);
        e.Undo();
        Assert.Equal(10, e.Doc.Find(box.Id)!.X);
        e.Undo();
        Assert.Null(e.Doc.Find(box.Id));
        Assert.False(e.CanUndo);
        e.Redo();
        e.Redo();
        Assert.Equal(100, e.Doc.Find(box.Id)!.X);
        Assert.Equal("Move", e.UndoLabel);
    }

    [Fact]
    public void Typing_runs_together_into_one_step()
    {
        var (e, page) = Fresh();
        var frame = e.Add(page, new TextFrame { W = 200, H = 100 });
        var caret = 0;
        foreach (var c in "hello")
            caret = e.ReplaceText(frame.StoryId, caret, 0, c.ToString(), merge: "type:" + frame.StoryId);
        Assert.Equal("hello", e.Doc.Stories[frame.StoryId].Text);
        e.Undo();
        Assert.Equal("", e.Doc.Stories[frame.StoryId].Text);
    }

    [Fact]
    public void A_drag_is_one_step_and_cancelling_puts_it_back()
    {
        var (e, page) = Fresh();
        var box = e.Add(page, new EllipseObject { W = 20, H = 20 });
        e.Begin();
        for (var i = 1; i <= 5; i++) e.Doc.Find(box.Id)!.X = i * 10;
        e.Commit("Move");
        Assert.Equal(50, e.Doc.Find(box.Id)!.X);
        e.Undo();
        Assert.Equal(0, e.Doc.Find(box.Id)!.X);

        e.Begin();
        e.Doc.Find(box.Id)!.X = 99;
        e.Cancel();
        Assert.Equal(0, e.Doc.Find(box.Id)!.X);
        Assert.Equal("New ellipse", e.UndoLabel);
    }

    [Fact]
    public void Dirty_follows_undo_back_to_the_saved_state()
    {
        var (e, page) = Fresh();
        Assert.False(e.Dirty);
        e.Add(page, new RectangleObject());
        Assert.True(e.Dirty);
        e.MarkSaved();
        Assert.False(e.Dirty);
        e.Undo();
        Assert.True(e.Dirty);
        e.Redo();
        Assert.False(e.Dirty);
    }

    [Fact]
    public void Linking_frames_makes_one_story_flow_through_both()
    {
        var (e, page) = Fresh();
        var a = e.Add(page, new TextFrame { W = 100, H = 100 });
        var b = e.Add(page, new TextFrame { X = 200, W = 100, H = 100 });
        string? why = e.Change("Link", d => Chains.Link(d, (TextFrame)d.Find(a.Id)!, (TextFrame)d.Find(b.Id)!));
        Assert.Null(why);
        var story = e.Doc.Stories[((TextFrame)e.Doc.Find(a.Id)!).StoryId];
        Assert.Equal([a.Id, b.Id], story.Frames);
        Assert.Single(e.Doc.Stories); // b's empty story went
        Assert.Equal(story.Id, ((TextFrame)e.Doc.Find(b.Id)!).StoryId);

        Assert.NotNull(Chains.Link(e.Doc, (TextFrame)e.Doc.Find(a.Id)!, (TextFrame)e.Doc.Find(b.Id)!));
    }

    [Fact]
    public void A_frame_holding_text_will_not_join_another_chain()
    {
        var (e, page) = Fresh();
        var a = e.Add(page, new TextFrame { W = 100, H = 100 });
        var b = e.Add(page, new TextFrame { W = 100, H = 100 });
        e.ReplaceText(b.StoryId, 0, 0, "mine");
        Assert.NotNull(Chains.Link(e.Doc, (TextFrame)e.Doc.Find(a.Id)!, (TextFrame)e.Doc.Find(b.Id)!));
    }

    [Fact]
    public void Unlinking_and_deleting_frames_keep_the_text_in_the_rest_of_the_chain()
    {
        var (e, page) = Fresh();
        var a = e.Add(page, new TextFrame { W = 100, H = 100 });
        var b = e.Add(page, new TextFrame { W = 100, H = 100 });
        var c = e.Add(page, new TextFrame { W = 100, H = 100 });
        e.Change("Link", d =>
        {
            Chains.Link(d, (TextFrame)d.Find(a.Id)!, (TextFrame)d.Find(b.Id)!);
            Chains.Link(d, (TextFrame)d.Find(b.Id)!, (TextFrame)d.Find(c.Id)!);
        });
        var storyId = a.StoryId;
        e.ReplaceText(storyId, 0, 0, "text");
        e.Change("Unlink", d => Chains.Unlink(d, (TextFrame)d.Find(b.Id)!));
        Assert.Equal([a.Id, c.Id], e.Doc.Stories[storyId].Frames);
        Assert.NotEqual(storyId, ((TextFrame)e.Doc.Find(b.Id)!).StoryId);

        e.Delete([a.Id]);
        Assert.Equal([c.Id], e.Doc.Stories[storyId].Frames);
        e.Delete([c.Id]);
        Assert.False(e.Doc.Stories.ContainsKey(storyId));
    }

    [Fact]
    public void Locked_objects_are_not_deleted()
    {
        var (e, page) = Fresh();
        var a = e.Add(page, new RectangleObject { Locked = true });
        Assert.Equal(0, e.Delete([a.Id]));
        Assert.NotNull(e.Doc.Find(a.Id));
    }

    [Fact]
    public void Duplicating_a_text_frame_copies_its_story()
    {
        var (e, page) = Fresh();
        var a = e.Add(page, new TextFrame { W = 100, H = 100 });
        e.ReplaceText(a.StoryId, 0, 0, "copy me");
        var ids = e.Duplicate([a.Id]);
        var copy = (TextFrame)e.Doc.Find(ids.Single())!;
        Assert.Equal(12, copy.X);
        Assert.NotEqual(a.StoryId, copy.StoryId);
        Assert.Equal("copy me", e.Doc.Stories[copy.StoryId].Text);
        Assert.Equal([copy.Id], e.Doc.Stories[copy.StoryId].Frames);
    }

    [Fact]
    public void Arranging_changes_the_drawing_order()
    {
        var (e, page) = Fresh();
        var a = e.Add(page, new RectangleObject());
        var b = e.Add(page, new RectangleObject());
        var c = e.Add(page, new RectangleObject());
        string Order() => string.Join(",", e.Doc.Pages[0].Objects.Select(o => o.Id == a.Id ? "a" : o.Id == b.Id ? "b" : "c"));
        e.Arrange([a.Id], Editor.Order.Front);
        Assert.Equal("b,c,a", Order());
        e.Arrange([a.Id], Editor.Order.Backward);
        Assert.Equal("b,a,c", Order());
        e.Arrange([c.Id], Editor.Order.Back);
        Assert.Equal("c,b,a", Order());
        e.Arrange([c.Id], Editor.Order.Forward);
        Assert.Equal("b,c,a", Order());
    }

    [Fact]
    public void Aligning_lines_objects_up()
    {
        var (e, page) = Fresh();
        var a = e.Add(page, new RectangleObject { X = 10, Y = 0, W = 20, H = 20 });
        var b = e.Add(page, new RectangleObject { X = 50, Y = 40, W = 40, H = 40 });
        e.AlignObjects([a.Id, b.Id], Editor.Alignment.Right);
        Assert.Equal(70, e.Doc.Find(a.Id)!.X);
        e.AlignObjects([a.Id, b.Id], Editor.Alignment.Middle);
        Assert.Equal(30, e.Doc.Find(a.Id)!.Y);
        e.AlignObjects([a.Id], Editor.Alignment.Centre); // one object: the page's centre
        Assert.Equal(296, e.Doc.Find(a.Id)!.X);
    }

    [Fact]
    public void Pages_are_added_moved_and_deleted_but_never_the_last()
    {
        var (e, _) = Fresh();
        e.AddPage(1);
        e.AddPage(2);
        Assert.Equal(3, e.Doc.Pages.Count);
        Assert.All(e.Doc.Pages, p => Assert.Equal("A-Master", p.Master));
        e.Add(SheetRef.PageAt(2), new RectangleObject { Name = "on three" });
        e.MovePage(2, 0);
        Assert.Equal("on three", e.Doc.Pages[0].Objects[0].Name);
        Assert.Null(e.DeletePage(0));
        Assert.Null(e.DeletePage(0));
        Assert.NotNull(e.DeletePage(0));
        Assert.Single(e.Doc.Pages);
    }

    [Fact]
    public void Masters_get_new_names_and_renaming_follows_the_pages()
    {
        var (e, _) = Fresh();
        Assert.Equal("B-Master", e.AddMaster());
        Assert.Null(e.RenameMaster("A-Master", "Body pages"));
        Assert.Equal("Body pages", e.Doc.Pages[0].Master);
        Assert.NotNull(e.RenameMaster("B-Master", "Body pages"));
    }

    [Fact]
    public void Defining_a_style_restyles_its_paragraphs()
    {
        var (e, page) = Fresh();
        var a = e.Add(page, new TextFrame { W = 100, H = 100 });
        e.ReplaceText(a.StoryId, 0, 0, "text");
        var body = e.Doc.Style("Body");
        e.DefineStyle(body with { Char = body.Char with { Size = 20 } });
        Assert.Equal(20, e.Doc.Stories[a.StoryId].FormatAt(0).Size);
    }

    [Fact]
    public void Snapping_finds_the_nearest_target_in_reach()
    {
        var setup = new Setup(Margins: new Margins(36, 36, 36, 36));
        var page = new Page { Guides = [new Guide(true, 200)] };
        var t = SnapTargets.For(setup, page);
        Assert.Equal(new Pt(36, 36), t.Snap(new Pt(38, 33), 5));
        Assert.Equal(200, t.Snap(new Pt(197, 100), 5).X);
        Assert.Equal(100, t.Snap(new Pt(197, 100), 5).Y);
        Assert.Equal((-2.0, 0.0), t.SnapBox(new Box(150, 100, 52, 10), 5)); // its right edge onto the guide
    }
}
