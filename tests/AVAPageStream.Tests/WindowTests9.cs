using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 3: SVG and EPS pictures.</summary>
public partial class WindowTests
{
    private const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 200 100">
          <rect x="0" y="0" width="100" height="100" fill="#000000"/>
          <circle cx="150" cy="50" r="40" fill="#E02020"/>
        </svg>
        """;

    /// <summary>The colour of a pixel of a PNG.</summary>
    private static (byte R, byte G, byte B) PixelAt(string png, int x, int y)
    {
        using var bitmap = new Bitmap(png);
        var buffer = new byte[4];
        unsafe { fixed (byte* p = buffer) bitmap.CopyPixels(new Avalonia.PixelRect(x, y, 1, 1), (nint)p, 4, 4); }
        return (buffer[2], buffer[1], buffer[0]);
    }

    [AvaloniaFact]
    public void An_svg_is_placed_drawn_and_kept_as_vector_in_a_pdf()
    {
        var dir = Folder();
        var file = Path.Combine(dir, "mark.svg");
        File.WriteAllText(file, Svg);
        var w = Open();
        w.PickOpen = kind => Task.FromResult<string?>(kind == "image" ? file : null);
        Tool(w, AVAPageStream.Tool.Picture);
        Drag(w, new Pt(100, 100), new Pt(300, 200));
        Pump();
        Assert.Contains("an SVG drawing, kept as vector", w.Message.Text);
        var picture = Assert.IsType<PictureObject>(Assert.Single(w.Editor.Doc.Pages[0].Objects));
        Assert.Equal("svg", ImageStore.Extension(w.Editor.Doc.Images.Get(picture.Image)!));
        Shot(w, "svg");

        // In a PNG of the page, its square and circle land in the frame (at 72 dpi a pixel is a point).
        var png = Path.Combine(dir, "page.png");
        w.PickSave = (_, _) => Task.FromResult<string?>(png);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 72));
        Wait(w.ExportPng());
        Assert.Equal((0, 0, 0), PixelAt(png, 150, 150));      // the black square
        var red = PixelAt(png, 250, 150);                     // the red circle
        Assert.True(red.R > 200 && red.G < 80, $"{red}");
        Assert.Equal((255, 255, 255), PixelAt(png, 350, 150)); // outside the frame: paper

        // In a PDF, it is drawn, not pasted in as a picture.
        var pdf = Path.Combine(dir, "page.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 150));
        Wait(w.ExportPdf());
        Assert.DoesNotContain("/Subtype /Image", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)));
        if (Shots is { } shots) File.Copy(pdf, Path.Combine(shots, "AVAPageStream-svg.pdf"), overwrite: true);
        Done(w);
    }

    [AvaloniaFact]
    public void An_eps_shows_its_preview_at_its_own_size()
    {
        var dir = Folder();
        var file = Path.Combine(dir, "logo.eps");
        File.WriteAllBytes(file, Core.EpsTests.Sample());
        var before = Pictures.Ghostscript;
        Pictures.Ghostscript = _ => null; // as on a computer without Ghostscript
        try
        {
            var w = Open();
            var pic = w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 100, Y = 100, W = 81, H = 40.5, Fit = Fit.Actual });
            w.PickOpen = _ => Task.FromResult<string?>(file);
            Wait(w.View.ReplaceImage(pic.Id));
            Assert.Contains("an EPS picture, shown by its preview", w.Message.Text);
            var png = Path.Combine(dir, "eps.png");
            w.PickSave = (_, _) => Task.FromResult<string?>(png);
            w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 72));
            Wait(w.ExportPng());
            // At its own size, 40.5 × 20.25 pt: the preview's black left half, then white.
            Assert.Equal((0, 0, 0), PixelAt(png, 105, 110));
            Assert.Equal((255, 255, 255), PixelAt(png, 135, 110));
            Done(w);
        }
        finally
        {
            Pictures.Ghostscript = before;
        }
    }
}
