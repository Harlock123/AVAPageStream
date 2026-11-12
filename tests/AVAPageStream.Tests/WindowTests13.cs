using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 4: text on a path, and footnotes, in the window.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Text_is_put_on_a_path_and_moved_along_it()
    {
        var w = Open();
        var path = w.Editor.Add(SheetRef.PageAt(0), new PathObject
        {
            Nodes = [PathNode.Corner(new Pt(72, 300)), new PathNode(300, 200, InX: 200, InY: 150), PathNode.Corner(new Pt(500, 300))],
            Stroke = null,
        });
        w.View.Select([path.Id]);
        w.TextOnPath();
        Pump();
        var p = (PathObject)w.Editor.Doc.Find(path.Id)!;
        Assert.NotNull(p.StoryId);
        Assert.Contains("story editor", w.Message.Text);
        w.Panel.SelectedIndex = 0;
        w.Refresh();
        Pump();
        w.Panel.TextOffsetBox.Focus();
        w.Panel.TextOffsetBox.Text = "36";
        Press(w, Key.Enter);
        Assert.Equal(36, ((PathObject)w.Editor.Doc.Find(path.Id)!).TextOffset);

        // The Text tab, with the path selected, sets its text's type.
        w.View.Select([path.Id]);
        w.View.ApplyChar(c => c with { Size = 24 }, "Size");
        Assert.Equal(24, w.Editor.Doc.Stories[p.StoryId!].FormatAt(0).Size);
        Assert.NotNull(w.CaptureRenderedFrame()); // draws
        Done(w);
    }

    [AvaloniaFact]
    public void A_footnote_is_inserted_at_the_caret_and_set_at_the_foot_of_its_column()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(400, 400));
        TypeLines(w, "The harbour wall was finished in the spring.");
        var id = w.View.Editing!.StoryId;
        w.View.Editing.Caret = w.View.Editing.Anchor = 17; // after "wall"
        Press(w, Key.F, RawInputModifiers.Control | RawInputModifiers.Alt);
        var s = w.Editor.Doc.Stories[id];
        var note = Assert.Single(Footnotes.Numbers(s).Keys);
        Assert.Equal(18, w.View.Editing!.Caret);
        w.Editor.ReplaceText(note, w.Editor.Doc.Stories[note].Length, 0, "Two years late.");
        var layout = w.View.Layouts.Of(w.Editor.Doc, id)!;
        var box = Assert.Single(layout.Notes);
        Assert.True(box.Box.Y > 380 && box.Box.Bottom <= 400.01); // at the foot of the frame
        Assert.Equal("1 Two years late.", w.View.Layouts.Of(w.Editor.Doc, note)!.Display(0, w.Editor.Doc.Stories[note].Length));
        Assert.Equal(note, w.View.NoteAt(new Pt(100, box.Box.Y + box.Box.H / 2)));
        Assert.Null(w.View.NoteAt(new Pt(100, 100)));
        Assert.NotNull(w.EditFootnote());
        Assert.NotNull(w.CaptureRenderedFrame());
        Done(w);
    }
}
