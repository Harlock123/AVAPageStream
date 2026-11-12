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
    /// <param name="marks">Each page on a larger sheet with its bleed and crop marks, for printing to the edge.</param>
    /// <remarks>
    /// The PDF has the document's title and author, a bookmark for each heading, links from a table of contents to
    /// its headings and from web addresses to the web, and (with marks) trim and bleed boxes for the printer.
    /// </remarks>
    public static async Task<CmykResult?> Pdf(Document doc, string path, IEnumerable<int>? pages = null, string? title = null, bool marks = false, bool cmyk = false)
    {
        var renderer = new PageRenderer(new Layouts(FontMeasurer.Shared)) { Screen = false };
        var chosen = (pages ?? Enumerable.Range(0, doc.Pages.Count)).ToList();
        var bookmarks = PdfExtras.Bookmarks(doc, FontMeasurer.Shared);
        var slug = marks ? PrintMarks.Slug(doc.Setup) : 0;
        var outline = new List<OutlineEntry>();
        var boxes = new List<(Box, Box)>();
        var temp = path + ".part";
        try
        {
            await using (var stream = File.Create(temp))
            {
                // From the defaults: a bare initialiser zeroes the rest, and an encoding quality of 0 means the
                // worst JPEG for every picture (the default, 101, keeps them lossless).
                var metadata = SKDocumentPdfMetadata.Default;
                metadata.Title = doc.Title.Length > 0 ? doc.Title : title ?? Path.GetFileNameWithoutExtension(path);
                if (doc.Author.Length > 0) metadata.Author = doc.Author;
                metadata.Creator = "AVAPageStream";
                metadata.Producer = "AVAPageStream";
                using var pdf = SKDocument.CreatePdf(stream, metadata);
                foreach (var index in chosen)
                {
                    var (w, h) = marks ? PrintMarks.Sheet(doc.Setup) : (doc.Setup.PageWidth, doc.Setup.PageHeight);
                    var canvas = pdf.BeginPage((float)w, (float)h);
                    // At 96 dpi Avalonia draws one of its units as one of the canvas's, and a PDF page's unit is a point.
                    Avalonia.Visual visual = marks ? new MarkedPageVisual(doc, doc.Pages[index], renderer) : new PageVisual(doc, doc.Pages[index], renderer);
                    await DrawingContextHelper.RenderAsync(canvas, visual, new Rect(0, 0, w, h), new Vector(96, 96));

                    // Where the headings on it are, and links from its contents and web addresses.
                    for (var b = 0; b < bookmarks.Count; b++)
                        if (bookmarks[b].Page == index)
                        {
                            canvas.DrawNamedDestinationAnnotation(new SKPoint((float)slug, (float)(slug + bookmarks[b].Y)), "h" + b);
                            outline.Add(new OutlineEntry(bookmarks[b].Title, bookmarks[b].Level, chosen.IndexOf(index), h - slug - bookmarks[b].Y));
                        }
                    foreach (var link in PdfExtras.Links(doc, index, bookmarks, id => renderer.Layouts.Of(doc, id)))
                    {
                        var rect = new SKRect((float)(slug + link.Box.X), (float)(slug + link.Box.Y), (float)(slug + link.Box.Right), (float)(slug + link.Box.Bottom));
                        if (link.Url is { } url) canvas.DrawUrlAnnotation(rect, url);
                        else if (link.Bookmark is { } to && chosen.Contains(bookmarks[to].Page)) canvas.DrawLinkDestinationAnnotation(rect, "h" + to);
                    }
                    if (marks)
                    {
                        // In PDF units, up from the sheet's foot.
                        var trim = new Box(slug, slug, doc.Setup.PageWidth, doc.Setup.PageHeight);
                        var bl = doc.Setup.Bleed;
                        boxes.Add((trim, new Box(slug - bl, slug - bl, doc.Setup.PageWidth + 2 * bl, doc.Setup.PageHeight + 2 * bl)));
                    }
                    pdf.EndPage();
                }
                pdf.Close();
            }
            // CMYK for a press, then the outline and the printer's boxes, none of which SkiaSharp writes, added to the
            // finished file.
            CmykResult? converted = null;
            if (cmyk)
            {
                converted = PdfColour.Convert(await File.ReadAllBytesAsync(temp), PdfColour.InksOf(doc));
                if (converted is not null) await File.WriteAllBytesAsync(temp, converted.Pdf);
            }
            if (outline.Count > 0 || boxes.Count > 0)
                await File.WriteAllBytesAsync(temp, PdfExtras.Finish(await File.ReadAllBytesAsync(temp), outline, boxes.Count > 0 ? boxes : null));
            File.Move(temp, path, overwrite: true);
            return converted;
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
