using Avalonia.Headless.XUnit;
using AVAPageStream.Core;
using SkiaSharp;

namespace AVAPageStream.Tests;

/// <summary>
/// EPS files from real producers, drawn by AVAPageStream's interpreter and compared with the picture their producer
/// drew of the same thing (rsvg-convert's PNG of the SVG they were made from).
/// </summary>
public partial class WindowTests
{
    private static string EpsSample(string name) => Path.Combine(AppContext.BaseDirectory, "Core", "Samples", "eps", name);

    /// <summary>An EPS drawn at the size of a reference PNG, white behind it.</summary>
    internal static SKBitmap DrawEps(byte[] eps, int width, int height)
    {
        EpsRender.Ready();
        var d = Eps.Draw(eps, out var why) ?? throw new Xunit.Sdk.XunitException("Not drawn: " + why);
        using var picture = EpsRender.Record(d);
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale((float)(width / d.BoundingBox.W), (float)(height / d.BoundingBox.H));
        canvas.DrawPicture(picture);
        return bitmap;
    }

    /// <summary>The share of pixels that differ by more than a little (anti-aliasing at edges differs between renderers).</summary>
    internal static double Difference(SKBitmap a, SKBitmap b)
    {
        var differ = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var (p, q) = (a.GetPixel(x, y), b.GetPixel(x, y));
                if (Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue))) > 80) differ++;
            }
        return differ / (double)(a.Width * a.Height);
    }

    [AvaloniaTheory]
    [InlineData("shapes-cairo.eps", "shapes.png")]
    [InlineData("shapes-pdftocairo.eps", "shapes.png")]
    [InlineData("shapes-poppler.eps", "shapes.png")]
    [InlineData("chart-cairo.eps", "chart.png")]
    [InlineData("chart-poppler.eps", "chart.png")]
    [InlineData("gradient-poppler.eps", "gradient.png")] // Poppler writes a gradient as many thin fills
    [InlineData("gradient-cairo.eps", "gradient.png")]   // Cairo, as axial shading
    [InlineData("radial-cairo.eps", "radial.png")]       // and radial
    [InlineData("radial-poppler.eps", "radial.png")]
    [InlineData("image-cairo.eps", "image.png")]         // a picture, through ASCII85 and Flate
    [InlineData("image-poppler.eps", "image.png")]       // through ASCII85 and LZW
    [InlineData("jpeg-cairo.eps", "jpeg.png")]
    [InlineData("jpeg-poppler.eps", "jpeg.png")]
    [InlineData("photo-magick.eps", "photo.png")]        // ImageMagick: hex data read by procedures
    [InlineData("text-cairo.eps", "text.png")]           // text: Cairo turns it into outlines
    [InlineData("text-pdftocairo.eps", "text.png")]      // TrueType fonts as Type 42
    [InlineData("text-poppler.eps", "text.png")]
    [InlineData("printer-fonts.eps", "printer-fonts.png")] // Times, Helvetica and Courier named, drawn in Liberation
    public void Eps_files_are_drawn_like_their_producers_draw_them(string eps, string reference)
    {
        using var expected = SKBitmap.Decode(EpsSample(reference));
        using var drawn = DrawEps(File.ReadAllBytes(EpsSample(eps)), expected.Width, expected.Height);
        if (Shots is { } dir)
        {
            Directory.CreateDirectory(dir);
            using var data = drawn.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, "eps-" + Path.GetFileNameWithoutExtension(eps) + ".png"), data.ToArray());
        }
        // Edges: renderers smooth them differently, and a bounding box in whole points (113 round a picture
        // 112.5 high) moves the drawing up to half a pixel. Anything wrong shows as far more.
        var difference = Difference(drawn, expected);
        Assert.True(difference < 0.05, $"{difference:P1} of the pixels differ from {reference}.");
    }

    [AvaloniaFact]
    public void A_placed_eps_is_drawn_by_AVAPageStream_and_stays_vector_in_the_pdf()
    {
        var w = Open();
        var pic = w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 72, Y = 72, W = 240, H = 150 });
        w.PickOpen = _ => Task.FromResult<string?>(EpsSample("chart-poppler.eps"));
        Wait(w.View.ReplaceImage(pic.Id));
        Assert.Contains("an EPS drawing, kept as vector", w.Message.Text);
        var pdf = Path.Combine(Folder(), "chart.pdf");
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 150));
        Wait(w.ExportPdf());
        Assert.DoesNotContain("/Subtype /Image", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)));
        Done(w);
    }
}
