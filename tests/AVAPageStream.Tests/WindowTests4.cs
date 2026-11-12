using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 2: groups, the Pen and editing points, rotation.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Objects_grouped_move_as_one_and_a_grouped_frame_still_takes_text()
    {
        var w = Open();
        var box = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 100, Y = 100, W = 100, H = 100, Fill = "#D0D0D0" });
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 220, Y = 100, W = 150, H = 100 });
        w.View.Select([box.Id, frame.Id]);
        Press(w, Key.G, RawInputModifiers.Control);
        var group = Assert.IsType<GroupObject>(Assert.Single(w.Editor.Doc.Pages[0].Objects));
        Assert.Equal("Group of 2", w.Panel.ObjectTitle.Text);

        Tool(w, AVAPageStream.Tool.Select);
        w.View.Select([]);
        Click(w, new Pt(150, 150)); // on the box: the group is picked
        Assert.Equal([group.Id], w.View.SelectedIds);
        Drag(w, new Pt(150, 150), new Pt(170, 190));
        Assert.Equal(new Box(120, 140, 100, 100), w.Editor.Doc.Find(box.Id)!.Bounds);
        Assert.Equal(new Box(240, 140, 150, 100), w.Editor.Doc.Find(frame.Id)!.Bounds);

        // A double-click reaches the frame inside to type in it.
        var at = At(w, 300, 180);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Pump();
        Assert.NotNull(w.View.Editing);
        Type(w, "Grouped text");
        Assert.Equal("Grouped text", w.Editor.Doc.Stories[frame.StoryId].Text);
        Press(w, Key.Escape);

        w.View.Select([group.Id]);
        Press(w, Key.G, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(2, w.Editor.Doc.Pages[0].Objects.Count);
        Assert.Equal(2, w.View.SelectedIds.Count);
        Done(w);
    }

    [AvaloniaFact]
    public void The_pen_draws_a_closed_shape_and_a_curved_path()
    {
        var w = Open();
        w.View.Snap = false;
        Tool(w, AVAPageStream.Tool.Pen);
        Click(w, new Pt(100, 100));
        Click(w, new Pt(200, 100));
        Click(w, new Pt(150, 180));
        Click(w, new Pt(100, 100)); // back on the first point: closed
        var tri = Assert.IsType<PathObject>(Assert.Single(w.Editor.Doc.Pages[0].Objects));
        Assert.True(tri.Closed);
        Assert.Equal(3, tri.Nodes.Count);
        Assert.Equal("Polygon", tri.Kind);
        Assert.Equal(new Box(100, 100, 100, 80), tri.Bounds);

        // A drag makes a smooth point; Enter ends the path open.
        Click(w, new Pt(250, 300));
        Drag(w, new Pt(350, 300), new Pt(400, 250));
        Press(w, Key.Enter);
        var curve = Assert.IsType<PathObject>(w.Editor.Doc.Pages[0].Objects[1]);
        Assert.False(curve.Closed);
        Assert.Equal(new Pt(400, 250), curve.Nodes[1].Out);
        Assert.Equal(new Pt(300, 350), curve.Nodes[1].In);
        Assert.Equal("Path", curve.Kind);

        // Escape gives up a path half drawn.
        Click(w, new Pt(450, 450));
        Press(w, Key.Escape);
        Assert.Equal(2, w.Editor.Doc.Pages[0].Objects.Count);
        Done(w);
    }

    [AvaloniaFact]
    public void A_paths_points_are_dragged_after_a_double_click()
    {
        var w = Open();
        w.View.Snap = false;
        var path = new PathObject { Nodes = [PathNode.Corner(new Pt(100, 100)), PathNode.Corner(new Pt(200, 100)), PathNode.Corner(new Pt(150, 200))], Closed = true, Fill = "#2050D0" };
        path.Fit();
        w.Editor.Add(SheetRef.PageAt(0), path);
        Tool(w, AVAPageStream.Tool.Select);
        var inside = At(w, 150, 130);
        w.MouseDown(inside, MouseButton.Left);
        w.MouseUp(inside, MouseButton.Left);
        w.MouseDown(inside, MouseButton.Left);
        w.MouseUp(inside, MouseButton.Left);
        Pump();
        Assert.Equal(path.Id, w.View.EditingPoints);
        Drag(w, new Pt(150, 200), new Pt(150, 260));
        var edited = (PathObject)w.Editor.Doc.Find(path.Id)!;
        Assert.Equal(new Pt(150, 260), edited.Nodes[2].At);
        Assert.Equal(160, edited.H);
        Assert.Equal("Edit points", w.Editor.UndoLabel);
        Press(w, Key.Escape);
        Assert.Null(w.View.EditingPoints);
        Done(w);
    }

    [AvaloniaFact]
    public void Objects_turn_with_the_rotate_handle_and_the_object_tab()
    {
        var w = Open();
        var box = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 200, Y = 200, W = 100, H = 60, Fill = "#FFD800" });
        Tool(w, AVAPageStream.Tool.Select);
        w.View.Select([box.Id]);
        Pump();
        // The handle sits above the top's middle (250, 200); drag it round to the right of the centre (250, 230), Shift for 15° steps.
        var knob = w.View.TranslatePoint(w.View.ToView(new Pt(250, 200 - 24 / w.View.Scale)), w)!.Value;
        var right = At(w, 400, 232);
        w.MouseMove(knob);
        w.MouseDown(knob, MouseButton.Left);
        w.MouseMove(At(w, 330, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
        w.MouseMove(right, RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
        w.MouseUp(right, MouseButton.Left, RawInputModifiers.Shift);
        Pump();
        Assert.Equal(90, w.Editor.Doc.Find(box.Id)!.Rotation);
        Assert.Equal("90°", w.Panel.RotationBox.Text);

        // Turned, it is hit where it now is, not where its frame was.
        w.View.Select([]);
        Click(w, new Pt(250, 270)); // below the old frame (200-260), inside the turned one (180-280)
        Assert.Equal([box.Id], w.View.SelectedIds);
        Click(w, new Pt(205, 230)); // in the old frame's corner, outside the turned one
        Assert.Empty(w.View.SelectedIds);

        w.View.Select([box.Id]);
        Pump();
        w.Panel.RotationBox.Focus();
        w.Panel.RotationBox.Text = "-30";
        Press(w, Key.Enter);
        Assert.Equal(-30, w.Editor.Doc.Find(box.Id)!.Rotation);
        Done(w);
    }

    [AvaloniaFact]
    public void Text_in_a_turned_frame_is_typed_and_clicked_in_its_own_frame()
    {
        var w = Open();
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 100, Y = 100, W = 200, H = 100, Rotation = 90 });
        w.Editor.ReplaceText(frame.StoryId, 0, 0, "Turned text");
        // Its first line runs down the page from the turned frame's top-right corner (250, 50).
        Tool(w, AVAPageStream.Tool.Text);
        Click(w, new Pt(240, 60));
        Assert.NotNull(w.View.Editing);
        Assert.InRange(w.View.Editing!.Caret, 0, 2);
        Press(w, Key.End);
        Type(w, "!");
        Assert.Equal("Turned text!", w.Editor.Doc.Stories[frame.StoryId].Text);
        Shot(w, "turned");
        Done(w);
    }
}
