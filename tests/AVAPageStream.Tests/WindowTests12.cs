using Avalonia.Headless.XUnit;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 4: autosave and recovery, bleed and crop marks, the baseline grid and linked pictures in the window.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Unsaved_work_is_copied_and_offered_back_after_a_crash()
    {
        var home = Folder();
        var settings = Path.Combine(home, "settings.json");
        var w = new MainWindow(settings) { UserWordsPath = Path.Combine(home, "words.txt"), TemplatesFolder = Path.Combine(home, "templates") };
        w.Ask = (_, _, _) => throw new Xunit.Sdk.XunitException("Nothing to recover yet.");
        w.Show();
        Pump();
        w.Autosave();
        Assert.Empty(w.Recovery.Found()); // nothing changed, nothing copied
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { Name = "work" });
        w.Autosave();
        Assert.Single(w.Recovery.Found());
        // The window goes without closing (a crash): the copy stays.

        var again = new MainWindow(settings) { UserWordsPath = Path.Combine(home, "words.txt"), TemplatesFolder = Path.Combine(home, "templates") };
        string? asked = null;
        again.Ask = (title, message, _) => { asked = message; return Task.FromResult(0); }; // Recover
        again.Show();
        Pump();
        Assert.Contains("stopped before a new document was saved", asked);
        Assert.Equal("work", Assert.Single(again.Editor.Doc.Pages[0].Objects).Name);
        Assert.True(again.Editor.Dirty);
        Assert.Equal([again.RecoveryId], again.Recovery.Found().Select(r => r.Id)); // now this window's copy

        // Saved, the copy goes.
        Assert.True(again.SaveTo(Path.Combine(home, "saved.avps")));
        Assert.Empty(again.Recovery.Found());
        Done(again);
    }

    [AvaloniaFact]
    public void Closing_without_saving_leaves_no_copy()
    {
        var w = Open();
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject());
        w.Autosave();
        Assert.Single(w.Recovery.Found());
        Done(w); // answers "Don't save"
        Assert.Empty(w.Recovery.Found());
    }

    [AvaloniaFact]
    public void A_pdf_with_crop_marks_puts_the_page_and_its_bleed_on_a_larger_sheet()
    {
        var dir = Folder();
        var w = Open();
        w.Editor.Change("Setup", d => d.Setup = d.Setup with { PageWidth = 300, PageHeight = 400, Bleed = 9 });
        // A box printed to the edge, running out into the bleed.
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = -9, Y = -9, W = 159, H = 100, Fill = "#000000", Stroke = null });
        var pdf = Path.Combine(dir, "marks.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 150, Marks: true));
        Wait(w.ExportPdf());
        var slug = PrintMarks.Slug(w.Editor.Doc.Setup);
        Assert.Equal((300 + 2 * slug, 400 + 2 * slug), PdfSize(pdf));

        // Drawn: the box out to the bleed and no further, and a crop mark.
        var doc = w.Editor.Doc;
        var renderer = new PageRenderer(new Layouts(FontMeasurer.Shared)) { Screen = false };
        var (sw, sh) = PrintMarks.Sheet(doc.Setup);
        var visual = new MarkedPageVisual(doc, doc.Pages[0], renderer);
        var (pw, ph) = ((int)Math.Ceiling(sw), (int)Math.Ceiling(sh));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(pw, ph));
        bitmap.Render(visual);
        var pixels = new byte[pw * ph * 4];
        unsafe { fixed (byte* p = pixels) bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, pw, ph), (nint)p, pixels.Length, pw * 4); }
        int Alpha(double x, double y) => pixels[((int)y * pw + (int)x) * 4 + 3];
        int Grey(double x, double y) => pixels[((int)y * pw + (int)x) * 4];
        Assert.True(Alpha(slug - 5, slug + 20) > 200 && Grey(slug - 5, slug + 20) < 40);  // in the bleed
        Assert.Equal(0, Alpha(slug - 12, slug + 20));                                       // past it, nothing
        var (from, to) = PrintMarks.Lines(doc.Setup)[0];
        Assert.True(Alpha((from.X + to.X) / 2, from.Y) > 0);                               // a mark
        Done(w);
    }

    [AvaloniaFact]
    public void The_document_tab_sets_the_bleed_and_baseline_grid_and_the_text_tab_puts_text_on_it()
    {
        var w = Open();
        w.Panel.SelectedIndex = 3;
        Pump();
        w.Panel.BleedBox.Text = "9pt";
        w.Panel.BaselineBox.Text = "15pt";
        w.Panel.BaselineFromBox.Text = "";
        w.Panel.ApplySetup();
        var s = w.Editor.Doc.Setup;
        Assert.Equal((9.0, 15.0, (double?)null), (s.Bleed, s.BaselineGrid, s.BaselineFrom));
        w.Panel.BaselineBox.Text = "1pt";
        w.Panel.ApplySetup();
        Assert.Contains("at least 2 pt", w.Message.Text);
        Assert.Equal(15, w.Editor.Doc.Setup.BaselineGrid);

        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(400, 300));
        TypeLines(w, "One", "Two");
        var id = w.View.Editing!.StoryId;
        w.Panel.SelectedIndex = 1;
        Pump();
        w.Panel.OnGridBox.IsChecked = true;
        Pump();
        Assert.True(w.Editor.Doc.Stories[id].Paras[1].OnGrid);
        var layout = w.View.Layouts.Of(w.Editor.Doc, id)!;
        var last = layout.Lines[^1].Baseline;
        Assert.Equal(0, (last - 36) % 15, 6); // the paragraph the caret is in sits on the grid, from the top margin
        Done(w);
    }

    [AvaloniaFact]
    public void A_placed_picture_can_be_linked_and_follows_its_file()
    {
        var dir = Folder();
        var file = Path.Combine(dir, "photo.png");
        File.WriteAllBytes(file, Png);
        var w = Open();
        var pic = w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 72, Y = 72, W = 100, H = 100 });
        w.PickOpen = _ => Task.FromResult<string?>(file);
        Wait(w.View.ReplaceImage(pic.Id));
        w.Panel.SelectedIndex = 0;
        w.Refresh();
        Pump();
        Assert.Equal(file, ((PictureObject)w.Editor.Doc.Find(pic.Id)!).Source);
        Assert.Contains("Kept in the document (placed from photo.png)", w.Panel.LinkInfo.Text);
        w.Panel.LinkedBox.IsChecked = true;
        Pump();
        Assert.True(((PictureObject)w.Editor.Doc.Find(pic.Id)!).Linked);
        w.Refresh();
        Assert.Equal("Linked to photo.png.", w.Panel.LinkInfo.Text);

        File.WriteAllBytes(file, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABc3UBGAAAAABJRU5ErkJggg=="));
        w.UpdateLinks();
        Assert.Contains("Updated 1 linked picture", w.Message.Text);
        Done(w);
    }
}
