using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 2, first part: text wrap, page-number fields, find and replace, placing text, the grid.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Text_flows_round_an_object_set_to_wrap_from_the_object_tab()
    {
        var w = Open();
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 400 });
        w.Editor.ReplaceText(frame.StoryId, 0, 0, Lorem + " " + Lorem + " " + Lorem);
        var box = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 200, Y = 100, W = 200, H = 100, Fill = "#D0D0D0" });
        w.View.Select([box.Id]);
        Pump();
        w.Panel.WrapGapBox.Focus();
        w.Panel.WrapGapBox.Text = "10";
        Press(w, Key.Enter);
        w.Panel.WrapBox.IsChecked = true;
        Pump();
        Assert.True(w.Editor.Doc.Find(box.Id)!.Wrap);
        Assert.Equal(10, w.Editor.Doc.Find(box.Id)!.WrapGap);

        var layout = w.View.LayoutOf(frame.StoryId)!;
        var keep = new Box(190, 90, 220, 120);
        foreach (var line in layout.Lines)
        {
            var top = line.Baseline - line.Ascent;
            var bottom = line.Baseline + line.Descent;
            if (bottom <= keep.Y || top >= keep.Bottom) continue;
            // What shows of each piece: a space at the end of a line may hang into the gap, as at a column's edge.
            Assert.All(line.Pieces, p =>
            {
                var visible = TextFlow.Measure(w.View.Layouts.Measurer, layout.Display(p.Start, p.Length).TrimEnd(), p.Format);
                Assert.True(p.X + visible <= keep.X + 0.5 || p.X >= keep.Right - 0.5,
                    $"\"{layout.Display(p.Start, p.Length)}\" at {p.X:0.#} overlaps the wrapped box.");
            });
        }
        Assert.Contains(layout.Lines, l => l.Baseline > keep.Bottom && l.Pieces[^1].X + l.Pieces[^1].Width > keep.X); // full width again below

        w.Undo(); // the wrap goes, and the text with it
        Assert.DoesNotContain(w.View.LayoutOf(frame.StoryId)!.Lines,
            l => l.Baseline > 100 && l.Baseline < 200 && l.Pieces[^1].X + l.Pieces[^1].Width < 195);
        Done(w);
    }

    [AvaloniaFact]
    public void A_page_number_on_the_master_shows_each_pages_own()
    {
        var w = Open();
        w.AddPage(after: true);
        w.AddPage(after: true);
        w.ShowSheet(SheetRef.MasterAt(0));
        var footer = w.Editor.Add(SheetRef.MasterAt(0), new TextFrame { X = 36, Y = 36, W = 300, H = 30 });
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(footer.Id)!);
        Type(w, "Page ");
        Press(w, Key.D3, RawInputModifiers.Control | RawInputModifiers.Shift);
        Type(w, " of ");
        w.View.InsertField(Fields.PageCount);
        Pump();
        var story = w.Editor.Doc.Stories[footer.StoryId];
        Assert.Equal($"Page {Fields.PageNumber} of {Fields.PageCount}", story.Text);

        var layouts = w.View.Layouts;
        string Shown(int? page)
        {
            var l = layouts.Of(w.Editor.Doc, footer.StoryId, page)!;
            return l.Display(0, story.Length);
        }
        Assert.Equal("Page # of 3", Shown(null)); // on the master itself
        Assert.Equal("Page 1 of 3", Shown(1));
        Assert.Equal("Page 3 of 3", Shown(3));
        // Each page draws it with its own number.
        w.ShowSheet(SheetRef.PageAt(1));
        Pump();
        Assert.Equal("Page 2 of 3", Shown(2));
        Done(w);
    }

    [AvaloniaFact]
    public void Find_shows_each_match_in_turn_and_replace_all_changes_them_together()
    {
        var w = Open();
        var a = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 100 });
        w.Editor.ReplaceText(a.StoryId, 0, 0, "The harbour wall, and the harbour lamps.");
        w.AddPage(after: true);
        var b = w.Editor.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 300, H = 100 });
        w.Editor.ReplaceText(b.StoryId, 0, 0, "Harbour office hours.");
        w.ShowSheet(SheetRef.PageAt(0));

        Press(w, Key.F, RawInputModifiers.Control);
        Assert.True(w.FindBar.IsVisible);
        Assert.True(w.FindBox.IsFocused);
        w.FindBox.Text = "harbour";
        Press(w, Key.Enter);
        Assert.Equal("1 of 3", w.FindStatus.Text);
        Assert.Equal((4, 11), (w.View.Editing!.Start, w.View.Editing.Start + w.View.Editing.Length));
        Press(w, Key.Enter);
        Press(w, Key.Enter);
        Assert.Equal("3 of 3", w.FindStatus.Text);
        Assert.Equal(b.StoryId, w.View.Editing!.StoryId);
        Assert.Equal("Page 2 of 2", w.PageInfo.Text); // it went to the page
        Press(w, Key.Enter, RawInputModifiers.Shift);
        Assert.Equal("2 of 3", w.FindStatus.Text);

        w.MatchCase.IsChecked = true;
        w.ReplaceBox.Text = "marina";
        w.ReplaceAll();
        Assert.Equal("Replaced 2.", w.FindStatus.Text);
        Assert.Equal("The marina wall, and the marina lamps.", w.Editor.Doc.Stories[a.StoryId].Text);
        Assert.Equal("Harbour office hours.", w.Editor.Doc.Stories[b.StoryId].Text);

        w.MatchCase.IsChecked = false;
        w.FindBox.Text = "office";
        w.ReplaceBox.Text = "office's";
        w.FindNext();
        w.ReplaceNext(); // replaces the one found
        Assert.Equal("Harbour office's hours.", w.Editor.Doc.Stories[b.StoryId].Text);
        w.FindBox.Text = "nothing like it";
        Assert.False(w.FindNext());
        Assert.Equal("Not found.", w.FindStatus.Text);
        Press(w, Key.Escape);
        Done(w);
    }

    [AvaloniaFact]
    public void Markdown_is_placed_into_the_selected_frame_with_its_styles()
    {
        var dir = Folder();
        var md = Path.Combine(dir, "notes.md");
        File.WriteAllText(md, "# Notes from the quay\n\nThe **new** lamps.\n\n- steps\n- swimmers\n");
        var w = Open();
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        w.View.Select([frame.Id]);
        w.PickOpen = kind => Task.FromResult<string?>(kind == "text" ? md : null);
        Wait(w.PlaceText());
        var story = w.Editor.Doc.Stories[frame.StoryId];
        Assert.Equal("Notes from the quay\nThe new lamps.\n•\tsteps\n•\tswimmers", story.Text);
        Assert.Equal("Heading", story.Paras[0].Style);
        Assert.True(story.FormatAt(story.Text.IndexOf("new", StringComparison.Ordinal)).Bold);
        Assert.Contains("Placed 4 paragraphs from notes.md", w.Message.Text);
        Assert.NotNull(w.View.Editing); // the caret is after what was placed
        Assert.Equal(story.Length, w.View.Editing!.Caret);

        w.View.StopEditing();
        w.View.Select([]);
        Wait(w.PlaceText());
        Assert.Contains("Select a text frame", w.Message.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void Drawing_snaps_to_the_grid_when_it_is_on()
    {
        var w = Open();
        w.View.Snap = false; // only the grid
        w.SetGrid(show: true, snap: true);
        Tool(w, AVAPageStream.Tool.Rectangle);
        Drag(w, new Pt(56, 107), new Pt(161, 199)); // each within reach (3.75 pt at 100%) of an 18 pt grid line
        var r = Assert.Single(w.Editor.Doc.Pages[0].Objects);
        Assert.Equal(new Box(54, 108, 108, 90), r.Bounds);
        Assert.True(w.Settings.SnapGrid);
        Done(w);
    }
}
