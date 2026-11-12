using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>The rest of milestone 1: scroll bars, Page Up/Down through a chain, the export dialog, dragging pages,
/// renaming styles, cropping pictures, and laying out only the stories a change reaches.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void The_scroll_bars_follow_the_view_and_scroll_it()
    {
        var w = Open();
        var v = w.Scroller.Vertical;
        var h = w.Scroller.Horizontal;
        Assert.True(v.IsVisible && h.IsVisible);
        Assert.Equal(PageView.RulerSize - w.View.Origin.Y, v.Value, 3);
        Assert.True(v.Maximum > v.Value, "At 100% there is more page below.");

        // Dragging the bar scrolls the page.
        var before = w.View.Origin;
        v.Value += 300;
        Pump();
        Assert.Equal(before.Y - 300, w.View.Origin.Y, 3);
        Assert.Equal(before.X, w.View.Origin.X, 3);

        // The wheel moves the bar.
        var wheelAt = At(w, 300, 300);
        w.MouseWheel(wheelAt, new Vector(0, -1));
        Pump();
        Assert.Equal(PageView.RulerSize - w.View.Origin.Y, v.Value, 3);

        // Zooming in widens the range: the page is bigger.
        var range = h.Maximum - h.Minimum;
        w.View.SetZoom(4);
        Pump();
        Assert.True(h.Maximum - h.Minimum > range);
        Done(w);
    }

    [AvaloniaFact]
    public void Page_down_moves_the_caret_into_the_next_frame_on_the_next_page()
    {
        var w = Open();
        var a = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 120, H = 40 });
        w.AddPage(after: true);
        var b = w.Editor.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        w.Editor.Change("Link", d => Chains.Link(d, (TextFrame)d.Find(a.Id)!, (TextFrame)d.Find(b.Id)!));
        w.Editor.ReplaceText(a.StoryId, 0, 0, Lorem);
        w.ShowSheet(SheetRef.PageAt(0));
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(a.Id)!);
        Pump();
        var layout = w.View.LayoutOf(a.StoryId)!;
        var firstInB = layout.Lines.First(l => layout.Areas[l.Area].FrameId == b.Id).Start;

        Press(w, Key.PageDown);
        Assert.Equal(firstInB, w.View.Editing!.Caret);
        Assert.Equal("Page 2 of 2", w.PageInfo.Text); // the caret took the view with it
        Press(w, Key.PageDown);
        Assert.Equal(Lorem.Length, w.View.Editing!.Caret);
        Press(w, Key.PageUp, RawInputModifiers.Shift);
        Assert.Equal(0, w.View.Editing!.Caret);
        Assert.Equal(Lorem.Length, w.View.Editing!.Anchor); // Shift kept the selection's other end
        Assert.Equal("Page 1 of 2", w.PageInfo.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void The_export_dialog_asks_for_pages_and_resolution_and_checks_the_range()
    {
        var w = Open();
        w.AddPage(after: true);
        w.AddPage(after: true);
        w.AskExport = kind => ExportDialog.Show(w, kind == "png", w.Editor.Doc.Pages.Count, 1, 150);
        var asked = w.AskExport("png");
        Pump();
        var dialog = Assert.IsType<ExportDialog>(Assert.Single(w.OwnedWindows));
        dialog.Range.Focus();
        dialog.Range.Text = "2-9";
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.True(dialog.Error.IsVisible);
        Assert.Contains("from 1 to 3", dialog.Error.Text);
        Assert.False(asked.IsCompleted);

        dialog.Range.Text = "1, 3";
        Pump();
        Assert.False(dialog.Error.IsVisible); // editing the range clears the complaint
        dialog.Resolution.Value = 300;
        if (Shots is { } dir)
        {
            Pump();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(dir, "AVAPageStream-export.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        dialog.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(asked);
        Assert.Equal(new[] { 0, 2 }, asked.Result!.Pages);
        Assert.Equal(300, asked.Result.Dpi);
        Done(w);
    }

    [AvaloniaFact]
    public void Pages_are_moved_by_dragging_them_in_the_pages_tab()
    {
        var w = Open();
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { Name = "first", W = 10, H = 10 });
        w.AddPage(after: true);
        w.AddPage(after: true);
        w.Panel.SelectedIndex = 2; // Pages
        Pump();
        Point Row(int i)
        {
            var row = (Control)w.Panel.PageList.ContainerFromIndex(i)!;
            return row.TranslatePoint(new Point(40, row.Bounds.Height / 2), w)!.Value;
        }
        var from = Row(0);
        var to = Row(2);
        w.MouseMove(from);
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X, (from.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton);
        w.MouseMove(to, RawInputModifiers.LeftMouseButton);
        w.MouseUp(to, MouseButton.Left);
        Pump();
        Assert.Equal("first", Assert.Single(w.Editor.Doc.Pages[2].Objects).Name);
        Assert.Empty(w.Editor.Doc.Pages[0].Objects);
        Assert.Equal("Page 3 of 3", w.PageInfo.Text);
        Assert.Contains("Moved page 1 to page 3", w.Message.Text);
        w.Undo();
        Assert.Equal("first", Assert.Single(w.Editor.Doc.Pages[0].Objects).Name);
        Done(w);
    }

    [AvaloniaFact]
    public void A_style_is_renamed_from_the_text_tab()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(400, 200));
        Type(w, "Caption text");
        var storyId = w.View.Editing!.StoryId;
        w.Panel.SelectedIndex = 1;
        Pump();
        w.Panel.StyleChoice.SelectedItem = "Caption";
        Pump();
        w.Panel.StyleNameBox.Focus();
        w.Panel.StyleNameBox.Text = "Photo caption";
        w.Panel.RenameStyleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal("Photo caption", w.Editor.Doc.Stories[storyId].Paras[0].Style);
        Assert.Contains("Photo caption", (IEnumerable<string>)w.Panel.StyleChoice.ItemsSource!);
        Assert.Equal("Photo caption", w.Panel.StyleChoice.SelectedItem);
        Done(w);
    }

    [AvaloniaFact]
    public void A_picture_is_cropped_by_alt_dragging_it_or_from_the_object_tab()
    {
        var w = Open();
        var pic = w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 100, Y = 100, W = 200, H = 150, Fit = Fit.Fill });
        var key = w.Editor.Doc.Images.Add(Png);
        w.Editor.Change("Place picture", d => ((PictureObject)d.Find(pic.Id)!).Image = key);
        Tool(w, AVAPageStream.Tool.Select);
        Drag(w, new Pt(200, 175), new Pt(230, 165), RawInputModifiers.Alt);
        var moved = (PictureObject)w.Editor.Doc.Find(pic.Id)!;
        Assert.Equal(new Box(100, 100, 200, 150), moved.Bounds); // the frame stays put
        Assert.Equal(30, moved.OffsetX, 1);
        Assert.Equal(-10, moved.OffsetY, 1);
        Assert.Equal("Crop picture", w.Editor.UndoLabel);

        Assert.Equal("30 pt", w.Panel.OffsetXBox.Text);
        w.Panel.OffsetYBox.Focus();
        w.Panel.OffsetYBox.Text = "0.25in";
        Press(w, Key.Enter);
        Assert.Equal(18, ((PictureObject)w.Editor.Doc.Find(pic.Id)!).OffsetY);
        Done(w);
    }

    [AvaloniaFact]
    public void Typing_in_one_story_lays_out_no_other()
    {
        var w = Open();
        var a = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 200, H = 200 });
        var b = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 300, Y = 72, W = 200, H = 200 });
        w.Editor.ReplaceText(b.StoryId, 0, 0, Lorem);
        var box = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 72, Y = 400, W = 50, H = 50 });
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(a.Id)!);
        Pump();
        var layouts = w.View.Layouts;
        var start = layouts.LaidOut;
        foreach (var c in "abc") w.KeyTextInput(c.ToString());
        Pump();
        Assert.Equal(3, layouts.LaidOut - start); // story a, once per letter; never b

        Press(w, Key.Escape);
        w.View.Select([box.Id]);
        start = layouts.LaidOut;
        Press(w, Key.Right);
        Press(w, Key.Down);
        Assert.Equal(0, layouts.LaidOut - start); // moving a rectangle lays out nothing

        start = layouts.LaidOut;
        w.Editor.Change("Resize", d => d.Find(b.Id)!.W = 150);
        w.View.InvalidateVisual();
        Pump();
        Assert.Equal(1, layouts.LaidOut - start); // resizing b's frame lays out b
        Done(w);
    }
}
