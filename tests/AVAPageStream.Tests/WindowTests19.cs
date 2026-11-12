using AVAPageStream.Core;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace AVAPageStream.Tests;

/// <summary>Milestone 10.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Contents_and_index_keep_up_when_updated_and_when_exported()
    {
        var w = Open();
        var e = w.Editor;
        e.AddPage(1);
        var toc = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        var body = e.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        e.ReplaceText(body.StoryId, 0, 0, "Tides\nText.");
        e.ApplyStyle(body.StoryId, 0, 0, "Heading");
        w.View.Select([toc.Id]);
        w.AskContents = _ => Task.FromResult<List<string>?>(["Heading"]);
        Wait(w.MakeContents());
        string Shown() => Fields.Expand(e.Doc.Stories[toc.StoryId].Text, null);
        Assert.Equal("Tides\t2", Shown());
        // Nothing has moved: it says so.
        w.UpdateLists();
        Assert.Contains("up to date", w.Message.Text);
        // A page in front: Update brings it up to date, as a step to undo.
        e.AddPage(0);
        w.UpdateLists();
        Assert.Equal("Tides\t3", Shown());
        Assert.Contains("Brought 1 list up to date", w.Message.Text);
        Assert.Equal("Update contents and index", e.UndoLabel);
        // Exporting does it first, and says so.
        e.AddPage(0);
        var pdf = Path.Combine(Folder(), "lists.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 150));
        Wait(w.ExportPdf());
        Assert.Equal("Tides\t4", Shown());
        Assert.Contains("up to date first", w.Message.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void The_find_bar_puts_what_it_finds_in_a_style()
    {
        var w = Open();
        var e = w.Editor;
        var a = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 200 });
        e.ReplaceText(a.StoryId, 0, 0, "The Moon and the moon.");
        Press(w, Key.F, RawInputModifiers.Control);
        w.FindBox.Text = "moon";
        Pump();
        Assert.Contains("a Emphasis", (IEnumerable<string>)w.ChangeStyle.ItemsSource!);
        Assert.Contains("¶ Caption", (IEnumerable<string>)w.ChangeStyle.ItemsSource!);
        w.ChangeStyle.SelectedItem = "a Emphasis";
        w.ReplaceAll();
        Assert.Equal("Restyled 2.", w.FindStatus.Text);
        Assert.Contains("Put 2 of \"moon\" in Emphasis", w.Message.Text);
        Assert.Equal("The Moon and the moon.", e.Doc.Stories[a.StoryId].Text);
        Assert.True(e.Doc.Stories[a.StoryId].FormatAt(5).Italic);
        // With a replacement, both.
        w.FindBox.Text = "and";
        w.ReplaceBox.Text = "or";
        w.ChangeStyle.SelectedItem = "a Strong";
        Pump();
        w.FindNext();
        w.ReplaceNext();
        Assert.Equal("The Moon or the moon.", e.Doc.Stories[a.StoryId].Text);
        Assert.True(e.Doc.Stories[a.StoryId].FormatAt(9).Bold);
        Done(w);
    }

    [AvaloniaFact]
    public void Index_entries_can_go_three_deep_and_run_to_a_place()
    {
        var w = Open();
        var e = w.Editor;
        var a = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 200 });
        e.ReplaceText(a.StoryId, 0, 0, "Spring tides are the highest of all.");
        e.NamePlace(a.StoryId, 10, "Highest");
        w.View.StartEditing((TextFrame)e.Doc.Find(a.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = 0;
        IReadOnlyList<string>? offered = null;
        w.AskIndex = (_, places) => { offered = places; return Task.FromResult<IndexMark?>(new IndexMark("Tides", "spring", "", "highest", IndexCovers.ToPlace, "Highest")); };
        Wait(w.MarkIndexEntry());
        Assert.Equal(["Highest"], offered);
        Assert.Contains("Tides, spring, highest", w.Message.Text);
        w.View.Editing!.Caret = w.View.Editing.Anchor = e.Doc.Stories[a.StoryId].Length;
        w.MakeIndex();
        Assert.EndsWith("Tides\nspring\nhighest, 1", Fields.Expand(e.Doc.Stories[a.StoryId].Text, null));
        Assert.Equal("Index 3", e.Doc.Stories[a.StoryId].Paras[^1].Style);
        Done(w);
    }

    [AvaloniaFact]
    public void Objects_are_anchored_in_table_cells_and_footnotes()
    {
        var w = Open();
        var e = w.Editor;
        var table = e.AddTable(SheetRef.PageAt(0), new Box(72, 72, 300, 120), 2, 2);
        var cell = table.Cells[3]; // the second row's second cell
        e.ReplaceText(cell, 0, 0, "Lamps ");
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 300, W = 300, H = 300 });
        e.ReplaceText(frame.StoryId, 0, 0, "The harbour.");
        var note = e.InsertFootnote(frame.StoryId, 11, "See the chart ")!;
        // Pasted at the caret, in the cell and in the note, as a user would.
        var red = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 500, Y = 500, W = 12, H = 12, Fill = "#FF0000", Stroke = null });
        var blue = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 520, Y = 500, W = 12, H = 12, Fill = "#0000FF", Stroke = null });
        w.View.Select([red.Id]);
        Wait(w.Copy());
        w.View.StartEditing((TableObject)e.Doc.Find(table.Id)!, 3);
        w.View.Editing!.Caret = w.View.Editing.Anchor = e.Doc.Stories[cell].Length;
        Wait(w.Paste());
        Assert.Contains("Anchored in the text", w.Message.Text);
        w.View.StopEditing();
        w.View.Select([blue.Id]);
        Wait(w.Copy());
        w.View.Reveal(note, e.Doc.Stories[note].Length, 0);
        Wait(w.Paste());
        w.View.StopEditing();
        e.Delete([red.Id, blue.Id]);
        var inCell = Assert.Single(e.Doc.Stories[cell].Anchored);
        var inNote = Assert.Single(e.Doc.Stories[note].Anchored);
        // Placed where their text is, and selectable there.
        var here = w.View.AnchoredHere();
        Assert.Contains(here, o => o.Id == inCell.Id);
        Assert.Contains(here, o => o.Id == inNote.Id);
        var c = e.Doc.Find(inCell.Id)!;
        var n = e.Doc.Find(inNote.Id)!;
        Assert.Equal(table.Id, c.PlacedIn);
        Assert.Equal(frame.Id, n.PlacedIn);
        var cellBox = ((TableObject)e.Doc.Find(table.Id)!).CellBox(3)!;
        Assert.True(cellBox.Contains(new Pt(c.X + 1, c.Y + 1)), $"{c.Bounds} in {cellBox}");
        Assert.True(n.Y > 300 + 20, "in the note at the foot of the frame");
        using var page = DrawPage(e.Doc);
        Assert.Equal((255, 0, 0), Pixel(page, (int)(c.X + 6), (int)(c.Y + 6)));
        Assert.Equal((0, 0, 255), Pixel(page, (int)(n.X + 6), (int)(n.Y + 6)));
        Done(w);
    }
}
