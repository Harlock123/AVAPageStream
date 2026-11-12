using AVAPageStream.Core;
using Avalonia.Headless.XUnit;

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
}
