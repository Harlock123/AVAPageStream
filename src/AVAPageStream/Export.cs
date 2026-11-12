using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Skia.Helpers;
using AVAPageStream.Core;
using SkiaSharp;

namespace AVAPageStream;

/// <summary>
/// Writing pages out as PDF and PNG, drawn by the same renderer as the screen (without the on-screen extras), so
/// the files match what was seen.
/// </summary>
internal static class Export
{
    /// <summary>Writes the pages (all, or the ones given, counting from 0) to a PDF, with their fonts embedded.</summary>
    public static async Task Pdf(Document doc, string path, IEnumerable<int>? pages = null, string? title = null)
    {
        var renderer = new PageRenderer(new Layouts(FontMeasurer.Shared)) { Screen = false };
        var temp = path + ".part";
        try
        {
            await using (var stream = File.Create(temp))
            {
                // From the defaults: a bare initialiser zeroes the rest, and an encoding quality of 0 means the
                // worst JPEG for every picture (the default, 101, keeps them lossless).
                var metadata = SKDocumentPdfMetadata.Default;
                metadata.Title = title ?? Path.GetFileNameWithoutExtension(path);
                metadata.Creator = "AVAPageStream";
                metadata.Producer = "AVAPageStream";
                using var pdf = SKDocument.CreatePdf(stream, metadata);
                foreach (var index in pages ?? Enumerable.Range(0, doc.Pages.Count))
                {
                    var canvas = pdf.BeginPage((float)doc.Setup.PageWidth, (float)doc.Setup.PageHeight);
                    // At 96 dpi Avalonia draws one of its units as one of the canvas's, and a PDF page's unit is a point.
                    var visual = new PageVisual(doc, doc.Pages[index], renderer);
                    await DrawingContextHelper.RenderAsync(canvas, visual, new Rect(0, 0, doc.Setup.PageWidth, doc.Setup.PageHeight), new Vector(96, 96));
                    pdf.EndPage();
                }
                pdf.Close();
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Writes the pages as a booklet: each PDF page a sheet side twice a page wide, with two pages on it in the order
    /// that folds into a saddle-stitched booklet (made up to a multiple of four with blank pages).
    /// </summary>
    public static async Task Booklet(Document doc, string path, IReadOnlyList<int>? pages = null, string? title = null)
    {
        var chosen = pages ?? Enumerable.Range(0, doc.Pages.Count).ToList();
        var renderer = new PageRenderer(new Layouts(FontMeasurer.Shared)) { Screen = false };
        var (w, h) = (doc.Setup.PageWidth, doc.Setup.PageHeight);
        var temp = path + ".part";
        try
        {
            await using (var stream = File.Create(temp))
            {
                var metadata = SKDocumentPdfMetadata.Default;
                metadata.Title = title ?? Path.GetFileNameWithoutExtension(path);
                metadata.Creator = metadata.Producer = "AVAPageStream";
                using var pdf = SKDocument.CreatePdf(stream, metadata);
                foreach (var side in Imposition.Booklet(chosen.Count))
                {
                    var canvas = pdf.BeginPage((float)(2 * w), (float)h);
                    Sheet? Page(int? slot) => slot is { } i ? doc.Pages[chosen[i]] : null;
                    var sheet = new SheetVisual(doc, [(Page(side.Left), 0), (Page(side.Right), w)], 2 * w, renderer);
                    await DrawingContextHelper.RenderAsync(canvas, sheet, new Rect(0, 0, 2 * w, h), new Vector(96, 96));
                    pdf.EndPage();
                }
                pdf.Close();
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Writes one page to a PNG at a resolution in dots per inch.</summary>
    public static void Png(Document doc, Sheet sheet, string path, int dpi)
    {
        var renderer = new PageRenderer(new Layouts(FontMeasurer.Shared)) { Screen = false };
        var pixels = new PixelSize((int)Math.Ceiling(doc.Setup.PageWidth * dpi / 72), (int)Math.Ceiling(doc.Setup.PageHeight * dpi / 72));
        // At this resolution one of the bitmap's device-independent units is one point.
        var scale = dpi * 96.0 / 72;
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(scale, scale));
        bitmap.Render(new PageVisual(doc, sheet, renderer));
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    /// <summary>The file a page goes to when several are exported: "name-3.png" beside "name.png".</summary>
    public static string Numbered(string path, int page, int count) =>
        count == 1 ? path : Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}-{page + 1}{Path.GetExtension(path)}");
}
