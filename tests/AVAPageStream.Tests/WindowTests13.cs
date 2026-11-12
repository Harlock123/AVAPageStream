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

public partial class WindowTests
{
    [AvaloniaFact]
    public void A_table_runs_on_to_the_next_page_and_is_typed_into_there()
    {
        var w = Open();
        var t = w.Editor.AddTable(SheetRef.PageAt(0), new Box(72, 640, 300, 160), 8, 2);
        w.View.Select([t.Id]);
        w.RunOnTable();
        Assert.Contains("runs on to page 2", w.Message.Text);
        var next = (TableObject)w.Editor.Doc.Pages[1].Objects.Single();
        Assert.Equal(t.Id, next.Continues);

        w.ShowSheet(SheetRef.PageAt(1));
        Pump();
        // The panel shows the table's rows, from either frame.
        w.View.Select([next.Id]);
        w.Panel.SelectedIndex = 0;
        w.Refresh();
        Assert.Equal(8, w.Panel.TableRowsBox.Value);

        // Into a cell of a row set on this page.
        var placed = Tables.Place(w.Editor.Doc, next)!;
        var cell = Enumerable.Range(0, 16).First(i => placed.FrameOf(i)?.Id == next.Id && i >= 2);
        var box = placed.CellBox(cell)!.Value;
        var spot = w.View.TextAt(new Pt(box.X + 10, box.Y + box.H / 2))!;
        Assert.Equal(next.Id, spot.Object.Id);
        Assert.Equal(t.Cells[cell], spot.StoryId);
        w.View.StartEditing(spot);
        Type(w, "Here");
        Assert.Equal("Here", w.Editor.Doc.Stories[t.Cells[cell]].Text);

        // The header row, drawn again at the top of this page's frame.
        var doc = w.Editor.Doc;
        var renderer = new PageRenderer(new Layouts(FontMeasurer.Shared)) { Screen = false };
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(612, 792));
        bitmap.Render(new PageVisual(doc, doc.Pages[1], renderer));
        var pixels = new byte[612 * 792 * 4];
        unsafe { fixed (byte* p = pixels) bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, 612, 792), (nint)p, pixels.Length, 612 * 4); }
        var at = ((int)next.Y + 5) * 612 + (int)next.X + 75;
        Assert.Equal(0xD0, pixels[at * 4]); // the header's light grey
        Done(w);
    }
}
