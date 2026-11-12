using Avalonia.Headless.XUnit;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 5: preflight, packaging and the PDF's bookmarks and links, in the window.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Preflight_lists_the_problems_and_goes_to_each()
    {
        var w = Open();
        w.AddPage(after: true);
        var f = w.Editor.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 100, H = 20 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, string.Join(" ", Enumerable.Repeat("harbour", 30)));
        var empty = w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 72, Y = 300, W = 100, H = 100 });
        var pre = w.OpenPreflight();
        Pump();
        Assert.Equal(2, pre.Issues.Count);
        Assert.Contains("2 things to look at, 2 of them (in red) will show in print", pre.Summary.Text);

        pre.List.SelectedItem = pre.Issues.Single(i => i.Kind == Problem.Overset);
        Pump();
        Assert.Equal(SheetRef.PageAt(1), w.View.Sheet);
        Assert.Equal(f.StoryId, w.View.Editing?.StoryId); // the caret where the text stops fitting
        pre.List.SelectedItem = pre.Issues.Single(i => i.Kind == Problem.NoPicture);
        Pump();
        Assert.Equal(SheetRef.PageAt(0), w.View.Sheet);
        Assert.Equal([empty.Id], w.View.SelectedIds);

        w.Editor.Delete([empty.Id]);
        pre.Check();
        Assert.Single(pre.Issues);
        pre.Close();

        // Exporting says what preflight found.
        var pdf = Path.Combine(Folder(), "out.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0, 1], 150));
        Wait(w.ExportPdf());
        Assert.Contains("Preflight found 1 problem that will show", w.Message.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void A_package_is_made_in_a_new_folder()
    {
        var parent = Folder();
        var w = Open();
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { Fill = "#FF0000" });
        w.PickFolder = () => Task.FromResult<string?>(parent);
        var made = Wait(w.MakePackage())!;
        Assert.Equal(Path.Combine(parent, "Untitled package"), made.Folder);
        Assert.True(File.Exists(Path.Combine(made.Folder, "Untitled.avps")));
        Assert.True(File.Exists(Path.Combine(made.Folder, "Report.txt")));
        Assert.Contains("Packaged Untitled", w.Message.Text);
        Assert.Equal(Path.Combine(parent, "Untitled package 2"), Wait(w.MakePackage())!.Folder); // never over one already there
        Done(w);
    }

    [AvaloniaFact]
    public void The_pdf_has_its_title_author_bookmarks_and_links()
    {
        var w = Open();
        w.AddPage(after: true);
        var contents = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 400, H = 200 });
        w.Editor.Change("Contents style", d => d.Styles.Add(Contents.MakeStyle(d, 0, 300)));
        w.Editor.ReplaceText(contents.StoryId, 0, 0, "Tide tables\t2\nMore at https://harlock123.com/tides here.");
        w.Editor.ApplyStyle(contents.StoryId, 0, 0, "Contents 1");
        var body = w.Editor.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 400, H = 400 });
        w.Editor.ReplaceText(body.StoryId, 0, 0, "Tide tables\nHigh and low water through the year.\nSpring tides");
        w.Editor.ApplyStyle(body.StoryId, 0, 0, "Heading");
        w.Editor.ApplyStyle(body.StoryId, 50, 0, "Subheading");
        w.Editor.Change("Who", d => { d.Title = "Harbour News"; d.Author = "Lonnie Watson"; d.Setup = d.Setup with { Bleed = 9 }; });

        var pdf = Path.Combine(Shots ?? Folder(), "AVAPageStream-news.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0, 1], 150, Marks: true));
        Wait(w.ExportPdf());
        var t = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Assert.Contains("(Harbour News)", t);
        Assert.Contains("(Lonnie Watson)", t);
        Assert.Contains("/Title (Tide tables)", t);
        Assert.Contains("/Title (Spring tides)", t);
        Assert.Contains("/PageMode /UseOutlines", t);
        Assert.Contains("/URI (https://harlock123.com/tides)", t);
        Assert.Matches(@"/Subtype /Link", t);
        Assert.Contains("/TrimBox [", t);
        Assert.Contains("/BleedBox [", t);
        Assert.Contains("/Count 2", t); // two pages (the update writes the page objects again, with their boxes)
        Done(w);
    }

    [AvaloniaFact]
    public void An_eps_without_ghostscript_shows_its_colour_tiff_preview()
    {
        var was = Pictures.Ghostscript;
        Pictures.Ghostscript = _ => null; // as on a computer without it
        try
        {
            var ps = System.Text.Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 80 40\n");
            var tiff = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Core", "Samples", "rgb-lzw.tif"));
            var header = new byte[30];
            (header[0], header[1], header[2], header[3]) = (0xC5, 0xD0, 0xD3, 0xC6);
            BitConverter.GetBytes(30).CopyTo(header, 4);
            BitConverter.GetBytes(ps.Length).CopyTo(header, 8);
            BitConverter.GetBytes(30 + ps.Length).CopyTo(header, 20);
            BitConverter.GetBytes(tiff.Length).CopyTo(header, 24);
            var picture = Assert.IsType<PostScriptPicture>(Pictures.Load([.. header, .. ps, .. tiff]));
            Assert.Contains("colour preview", picture.How);
            Assert.Equal((8, 4), (picture.Shown!.PixelSize.Width, picture.Shown.PixelSize.Height));
            Assert.Equal((80.0, 40.0), (picture.Size.Width, picture.Size.Height)); // sized by its bounding box, not the preview
        }
        finally
        {
            Pictures.Ghostscript = was;
        }
    }
}
