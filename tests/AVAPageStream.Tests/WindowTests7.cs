using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 3, first part: printing, booklets, the table of contents, templates, small caps and scripts.</summary>
public partial class WindowTests
{
    private static int PdfPages(string path) =>
        System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)), @"/Type\s*/Page\b").Count;

    private static (double W, double H) PdfSize(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)), @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)");
        return (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public void Printing_sends_a_pdf_of_the_pages_to_the_printer()
    {
        var w = Open();
        w.AddPage(after: true);
        string? sent = null;
        w.SendToPrinter = path => { sent = path; return Task.FromResult<string?>(null); };
        w.AskExport = kind => Task.FromResult<ExportChoice?>(kind == "print" ? new ExportChoice([0, 1], 150) : null);
        Press(w, Key.P, RawInputModifiers.Control);
        for (var i = 0; i < 200 && sent is null; i++) { Thread.Sleep(5); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
        Assert.NotNull(sent);
        Assert.Equal(2, PdfPages(sent!));
        Assert.Contains("Sent 2 pages to the printer", w.Message.Text);

        // When the printer can't, the window says why.
        w.SendToPrinter = _ => Task.FromResult<string?>("The printer is out of paper.");
        Wait(w.Print());
        Assert.Equal("The printer is out of paper.", w.Message.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void A_booklet_puts_two_pages_on_each_sheet_in_folding_order()
    {
        var dir = Folder();
        var w = Open();
        w.Editor.Change("Size", d => d.Setup = d.Setup with { PageWidth = 420, PageHeight = 595 });
        for (var i = 1; i < 6; i++) w.AddPage(after: true);
        for (var i = 0; i < 6; i++)
        {
            var f = w.Editor.Add(SheetRef.PageAt(i), new TextFrame { X = 100, Y = 250, W = 220, H = 100 });
            w.Editor.ReplaceText(f.StoryId, 0, 0, $"Page {i + 1}", new CharFormat(Fonts.Sans, 48, Bold: true));
            w.Editor.FormatParas(f.StoryId, 0, 0, p => p with { Align = Align.Centre });
        }
        var pdf = Path.Combine(Shots ?? dir, "AVAPageStream-booklet.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0, 1, 2, 3, 4, 5], 150, Booklet: true));
        Wait(w.ExportPdf());
        Assert.Equal(4, PdfPages(pdf));        // six pages make two sheets, both sides
        Assert.Equal((840, 595), PdfSize(pdf)); // each side two pages wide
        Assert.Contains("booklet of 2 sheets", w.Message.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void The_table_of_contents_lists_the_headings_at_the_caret()
    {
        var w = Open();
        var body = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 300, W = 400, H = 300 });
        w.Editor.ReplaceText(body.StoryId, 0, 0, "Chapter one\nText.\nA section\nMore text.");
        w.Editor.ApplyStyle(body.StoryId, 0, 0, "Heading");
        w.Editor.ApplyStyle(body.StoryId, 18, 0, "Subheading");
        var toc = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 200 });
        w.View.Select([toc.Id]);
        IReadOnlyList<string>? offered = null;
        w.AskContents = styles => { offered = styles; return Task.FromResult<List<string>?>(["Heading", "Subheading"]); };
        Wait(w.MakeContents());
        Assert.Contains("Body", offered!);
        var story = w.Editor.Doc.Stories[toc.StoryId];
        Assert.Equal("Chapter one\t1\nA section\t1", Fields.Expand(story.Text, null));
        Assert.Contains("Made a table of contents: 2 entries", w.Message.Text);
        Assert.Equal("Contents 2", story.Paras[1].Style);

        // Asked again, Contents styles aren't offered as headings.
        Wait(w.MakeContents());
        Assert.DoesNotContain(offered!, s => s.StartsWith("Contents"));
        // And made again where it is (the frame's end is in it), not a second time.
        Assert.Contains("Made the table of contents again", w.Message.Text);
        Assert.Single(w.Editor.Doc.Lists);
        Assert.Equal("Chapter one\t1\nA section\t1", Fields.Expand(w.Editor.Doc.Stories[toc.StoryId].Text, null));
        Done(w);
    }

    [AvaloniaFact]
    public void Documents_start_from_the_built_in_templates_and_from_saved_ones()
    {
        var dir = Folder();
        var w = Open();
        w.TemplatesFolder = dir;
        Wait(w.NewFrom(Templates.BuiltIn.Single(t => t.Name == "Booklet")));
        Assert.Equal("Untitled — AVAPageStream", w.Title);
        Assert.Equal(8, w.Editor.Doc.Pages.Count);
        Assert.True(w.Editor.Doc.Setup.Facing);
        Assert.Equal(w.Editor.Doc.Setup.PageWidth, Assert.Single(w.View.Spread()).X); // page 1 alone, on the right of its spread
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { Name = "logo" });
        var template = Path.Combine(dir, "Parish magazine");
        w.PickSave = (kind, _) => Task.FromResult<string?>(kind == "template" ? template : null);
        Assert.True(Wait(w.SaveAsTemplate()));
        Assert.True(File.Exists(template + Templates.Extension));

        w.Ask = (_, _, _) => Task.FromResult(1); // don't save the changes
        Wait(w.NewFrom(null, template + Templates.Extension));
        Assert.Contains(w.Editor.Doc.Pages[0].Objects, o => o.Name == "logo");
        Assert.Null(w.FilePath); // a new document, not the template itself
        Assert.Contains("Parish magazine template", w.Message.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void Small_caps_and_superscript_are_set_from_the_text_tab()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(400, 200));
        Type(w, "Notes on E=mc2");
        var t = w.View.Editing!;
        w.Panel.SelectedIndex = 1;
        t.Anchor = 0;
        t.Caret = 5;
        w.Refresh();
        w.Panel.SmallCapsToggle.IsChecked = true;
        Pump();
        t.Anchor = 13;
        t.Caret = 14;
        w.Refresh();
        w.Panel.SuperToggle.IsChecked = true;
        Pump();
        var story = w.Editor.Doc.Stories[t.StoryId];
        Assert.True(story.FormatAt(0).SmallCaps);
        Assert.Equal(Script.Super, story.FormatAt(13).Script);
        Assert.Equal(Script.Normal, story.FormatAt(12).Script);
        Shot(w, "scripts");
        Done(w);
    }

    [AvaloniaFact]
    public void A_booklet_sheet_draws_each_page_in_its_own_half()
    {
        var doc = Document.New(new Setup(100, 100));
        doc.Pages[0].Objects.Add(new RectangleObject { Id = "r", X = 0, Y = 0, W = 100, H = 100, Fill = "#000000", Stroke = null });
        var renderer = new PageRenderer(new Layouts(FontMeasurer.Shared)) { Screen = false };
        var sheet = new SheetVisual(doc, [(null, 0), (doc.Pages[0], 100)], 200, renderer);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(200, 100));
        bitmap.Render(sheet);
        var pixels = new byte[200 * 100 * 4];
        unsafe { fixed (byte* p = pixels) bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, 200, 100), (nint)p, pixels.Length, 200 * 4); }
        byte Grey(int x, int y) => pixels[(y * 200 + x) * 4];
        Assert.Equal(255, Grey(50, 50));  // the blank left half is paper
        Assert.Equal(0, Grey(150, 50));   // the page in the right half is drawn there
    }

    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.Result;
    }
}
