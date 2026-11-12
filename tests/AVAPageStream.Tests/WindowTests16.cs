using Avalonia.Headless.XUnit;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>The small fixes and the earlier milestones' limits, in the window.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Exporting_a_document_with_problems_asks_first()
    {
        var w = Open();
        w.Settings.PreflightFirst = true;
        w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 72, Y = 72, W = 100, H = 100 }); // empty: it will show
        var pdf = Path.Combine(Folder(), "asked.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 150));
        string? question = null;
        w.Ask = (title, message, _) => { question = message; return Task.FromResult(2); }; // Cancel
        Wait(w.ExportPdf());
        Assert.Contains("a picture frame has no picture in it", question);
        Assert.False(File.Exists(pdf));
        w.Ask = (_, _, _) => Task.FromResult(0); // Go on anyway
        Wait(w.ExportPdf());
        Assert.True(File.Exists(pdf));
        w.Settings.PreflightFirst = false;
        Done(w);
    }

    [AvaloniaFact]
    public void Preflight_checks_the_pictures_inside_an_eps()
    {
        // A 4 by 4 picture over a 100-point square: 4 pixels over 1.4 inches, about 3 dots an inch.
        var eps = System.Text.Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 100 100\n100 100 scale 4 4 8 [4 0 0 -4 0 4] <00FF00FF00FF00FF00FF00FF00FF00FF> image\n");
        var file = Path.Combine(Folder(), "small.eps");
        File.WriteAllBytes(file, eps);
        var w = Open();
        var pic = w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 72, Y = 72, W = 100, H = 100 });
        w.PickOpen = _ => Task.FromResult<string?>(file);
        Wait(w.View.ReplaceImage(pic.Id));
        var issue = Assert.Single(w.Preflight(), i => i.Kind == Problem.LowResolution);
        Assert.Contains("only 3 dots an inch", issue.Message);
        Done(w);
    }

    [AvaloniaFact]
    public void A_cmyk_pdf_has_its_swatches_inks_spot_colours_and_pictures_in_cmyk()
    {
        var w = Open();
        Assert.Null(w.Editor.AddSwatch("Navy", "100 80 0 20", spot: false));
        Assert.Null(w.Editor.AddSwatch("Gold", "0 20 100 0", spot: true));
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 72, Y = 72, W = 200, H = 100, Fill = "@Navy", Stroke = null });
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 72, Y = 200, W = 200, H = 100, Fill = "@Gold 50%", Stroke = null });
        var key = w.Editor.Doc.Images.Add(Png);
        w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 300, Y = 72, W = 100, H = 100, Image = key, Fit = Fit.Stretch });
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 400, W = 300, H = 50 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, "Harbour News");
        var pdf = Path.Combine(Shots ?? Folder(), "AVAPageStream-cmyk.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 150, Cmyk: true));
        Wait(w.ExportPdf());
        Assert.Contains("In CMYK", w.Message.Text);
        var file = PdfFile.Read(File.ReadAllBytes(pdf))!;
        var content = string.Join("\n", file.Pages().Select(p => file.Objects[p].Body)
            .SelectMany(b => System.Text.RegularExpressions.Regex.Matches(b, @"/Contents\s+(\d+)").Select(m => int.Parse(m.Groups[1].Value)))
            .Select(n => System.Text.Encoding.Latin1.GetString(file.Objects[n].Decoded()!)));
        Assert.DoesNotContain(" rg", content);                    // no RGB colours left
        Assert.Contains("1 0.8 0 0.2 k", content);               // the Navy swatch's own inks
        Assert.Contains("0 0 0 1 k", content);                    // black text in black ink only
        Assert.Matches(@"/AVS\d+ cs 0\.5 scn", content);          // Gold, a spot ink, at half
        Assert.Contains(file.Objects.Values, o => o.Body.Contains("/Separation /Gold /DeviceCMYK"));
        Assert.Contains(file.Objects.Values, o => o.Body.Contains("/Subtype /Image") && o.Body.Contains("/DeviceCMYK"));
        Done(w);
    }
}
