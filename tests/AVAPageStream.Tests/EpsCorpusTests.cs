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

    [Theory]
    [InlineData("gradient-cairo.eps", "shading")]   // Cairo's gradients are smooth shading
    [InlineData("photo-magick.eps", "images")]      // ImageMagick's EPS is a picture
    public void Eps_files_using_what_isnt_drawn_yet_say_so(string eps, string what)
    {
        Assert.Null(Eps.Draw(File.ReadAllBytes(EpsSample(eps)), out var why));
        Assert.Contains(what, why);
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
