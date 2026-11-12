using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 5: footnotes and text on paths edited on the page, and found.</summary>
public partial class WindowTests
{
    private static (TextFrame Frame, string Note) FrameWithNote(MainWindow w)
    {
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 400, H = 300 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, "The harbour wall was finished in the spring.");
        var note = w.Editor.InsertFootnote(f.StoryId, 16, "Two years late")!;
        return (f, note);
    }

    [AvaloniaFact]
    public void A_footnote_is_typed_into_at_the_foot_of_its_column()
    {
        var w = Open();
        var (f, note) = FrameWithNote(w);
        var box = Assert.Single(w.View.Layouts.Of(w.Editor.Doc, f.StoryId)!.Notes).Box;
        var at = At(w, box.X + 30, box.Y + box.H / 2);
        Assert.Equal(note, w.View.TextAt(new Pt(box.X + 30, box.Y + box.H / 2))?.StoryId);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        w.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        w.MouseUp(at, MouseButton.Left);
        Pump();
        Assert.Equal(note, w.View.Editing?.StoryId);
        w.View.Editing!.Caret = w.View.Editing.Anchor = w.Editor.Doc.Stories[note].Length;
        Type(w, ", and over budget.");
        Assert.EndsWith("Two years late, and over budget.", w.Editor.Doc.Stories[note].Text);
        Assert.Equal(f.StoryId, w.Editor.Doc.Stories[note].NoteOf);
        Assert.NotNull(w.CaptureRenderedFrame());
        Press(w, Key.Escape);
        Assert.Equal([f.Id], w.View.SelectedIds); // out of the note, its frame selected
        Done(w);
    }

    [AvaloniaFact]
    public void Find_goes_into_footnotes_and_along_paths()
    {
        var w = Open();
        var (f, note) = FrameWithNote(w);
        var path = w.Editor.Add(SheetRef.PageAt(0), new PathObject { Nodes = [PathNode.Corner(new Pt(72, 500)), PathNode.Corner(new Pt(500, 500))] });
        w.Editor.PutTextOnPath(path.Id, "Late again");
        var onPath = ((PathObject)w.Editor.Doc.Find(path.Id)!).StoryId!;
        w.FindBox.Text = "late";
        Assert.True(w.FindNext());
        Assert.Equal((note, 12), (w.View.Editing!.StoryId, w.View.Editing.Start)); // "1 Two years late"
        Assert.True(w.FindNext());
        Assert.Equal(onPath, w.View.Editing!.StoryId);
        Assert.Equal([path.Id], w.View.SelectedIds);
        Shot(w, "path-selection"); // the selection drawn along the path
        Assert.True(w.FindNext());
        Assert.Equal(note, w.View.Editing!.StoryId); // round again
        Done(w);
    }

    [AvaloniaFact]
    public void Text_on_a_path_is_clicked_into_and_typed_along()
    {
        var w = Open();
        var path = w.Editor.Add(SheetRef.PageAt(0), new PathObject { Nodes = [PathNode.Corner(new Pt(100, 300)), PathNode.Corner(new Pt(500, 300))], Stroke = null });
        w.Editor.PutTextOnPath(path.Id, "Lamps");
        var id = ((PathObject)w.Editor.Doc.Find(path.Id)!).StoryId!;
        var p = (PathObject)w.Editor.Doc.Find(path.Id)!;
        // In the second letter's right half, a little above the path: the caret goes after it.
        var glyph = PathText.Place(w.Editor.Doc.Stories[id], PathText.Points(p), w.View.Layouts.Measurer).Glyphs[1];
        Assert.Equal(2, w.View.PathIndexAt(p, new Pt(glyph.At.X + glyph.Width * 0.8, 296)));
        Assert.Null(w.View.PathIndexAt(p, new Pt(300, 200)));        // nowhere near
        Assert.Equal(5, w.View.PathIndexAt(p, new Pt(480, 300), near: true));

        var at = At(w, glyph.At.X + glyph.Width * 0.8, 296);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Pump();
        Assert.Equal(id, w.View.Editing?.StoryId);
        w.View.Editing!.Caret = w.View.Editing.Anchor = 5;
        Type(w, " lit");
        Assert.Equal("Lamps lit", w.Editor.Doc.Stories[id].Text);
        Press(w, Key.Home);
        Assert.Equal(0, w.View.Editing!.Caret);
        Assert.Null(w.View.EditingPoints); // into its text, not its points
        Assert.NotNull(w.CaptureRenderedFrame());
        Done(w);
    }
}
