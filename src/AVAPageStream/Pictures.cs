using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using AVAPageStream.Core;
using SkiaSharp;

namespace AVAPageStream;

/// <summary>A picture ready to draw, with its own size in points.</summary>
internal abstract record PictureSource(Size Size);

/// <summary>A bitmap: PNG, JPEG, GIF, BMP or WebP.</summary>
internal sealed record RasterPicture(Bitmap Bitmap, Size Size) : PictureSource(Size);

/// <summary>An SVG drawing, kept as a Skia picture so it is drawn as vector on screen and in PDFs.</summary>
/// <param name="PixelsPerPoint">For an EPS with pictures in it: the fewest pixels its coarsest picture has a point, at its own size.</param>
internal sealed record VectorPicture(SKPicture Picture, SKRect Cull, Size Size, string What = "an SVG drawing", double? PixelsPerPoint = null) : PictureSource(Size);

/// <summary>An EPS: shown by its Ghostscript rendering or its own preview, or (with neither) as a labelled box.</summary>
internal sealed record PostScriptPicture(Bitmap? Shown, Size Size, string How, string Kind = "EPS") : PictureSource(Size);

internal static class Pictures
{
    /// <summary>Renders EPS with Ghostscript when it is installed; tests and computers without it get null.</summary>
    public static Func<byte[], byte[]?> Ghostscript { get; set; } = eps => Core.Ghostscript.Render(eps);

    /// <param name="page">For a PDF, the page (from 1).</param>
    /// <param name="password">For a protected PDF that needs one, its password.</param>
    public static PictureSource? Load(byte[] bytes, int page = 1, string? password = null)
    {
        try
        {
            if (ImageStore.IsSvg(bytes)) return Svg(bytes);
            if (ImageStore.IsEps(bytes)) return PostScript(bytes);
            if (PdfPages.IsPdf(bytes)) return Pdf(bytes, page, password);
            var bitmap = new Bitmap(new MemoryStream(bytes));
            // Its size in points: its pixels at the resolution it says it was made for.
            return new RasterPicture(bitmap, new Size(bitmap.PixelSize.Width * 72 / bitmap.Dpi.X, bitmap.PixelSize.Height * 72 / bitmap.Dpi.Y));
        }
        catch (Exception)
        {
            return null; // a damaged picture shows as a missing one
        }
    }

    private static VectorPicture? Svg(byte[] bytes)
    {
        var svg = new Svg.Skia.SKSvg();
        using (var stream = new MemoryStream(bytes)) svg.Load(stream);
        if (svg.Picture is not { } picture) return null;
        var cull = picture.CullRect;
        // SVG lengths are CSS pixels, 96 to the inch.
        return new VectorPicture(picture, cull, new Size(cull.Width * 72 / 96, cull.Height * 72 / 96));
    }

    private static PictureSource PostScript(byte[] bytes)
    {
        var box = Eps.BoundingBox(bytes) ?? new Box(0, 0, 144, 144);
        var size = new Size(Math.Max(1, box.W), Math.Max(1, box.H));
        // Drawn by AVAPageStream itself when it can, so it stays vector; otherwise by Ghostscript or a preview.
        EpsRender.Ready();
        if (Eps.Draw(bytes, out _) is { } drawing)
            return new VectorPicture(EpsRender.Record(drawing), new SKRect(0, 0, (float)box.W, (float)box.H), size, "an EPS drawing", EpsRender.PixelsPerPoint(drawing));
        if (Ghostscript(bytes) is { } png)
            return new PostScriptPicture(new Bitmap(new MemoryStream(png)), size, "rendered by Ghostscript at 300 dpi");
        if (Eps.TiffPreview(bytes) is { } colour) return new PostScriptPicture(FromColour(colour), size, "its colour preview (install Ghostscript to see it in full)");
        if (Eps.Preview(bytes) is { } grey) return new PostScriptPicture(FromGrey(grey), size, "its preview (install Ghostscript to see it in full)");
        return new PostScriptPicture(null, size, "no preview (install Ghostscript to see it)");
    }

    /// <summary>A PDF's page, drawn by AVAPageStream's own reader, so it stays vector; or a labelled box if it can't be read.</summary>
    private static PictureSource Pdf(byte[] bytes, int page, string? password)
    {
        EpsRender.Ready();
        if (PdfPages.Draw(bytes, page, out var why, out var notes, password) is not { } drawing)
            return new PostScriptPicture(null, new Size(144, 144), why ?? "it couldn't be read", "PDF");
        var box = drawing.BoundingBox;
        var count = PdfPages.Count(bytes, password);
        var what = count > 1 ? $"page {Math.Clamp(page, 1, count)} of {count} of a PDF" : "a PDF page";
        if (notes.Count > 0) what += " (" + string.Join("; ", notes) + ")";
        return new VectorPicture(EpsRender.Record(drawing), new SKRect(0, 0, (float)box.W, (float)box.H), new Size(Math.Max(1, box.W), Math.Max(1, box.H)), what, EpsRender.PixelsPerPoint(drawing));
    }

    private static WriteableBitmap FromColour(ColourImage c)
    {
        var bitmap = new WriteableBitmap(new PixelSize(c.Width, c.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bitmap.Lock();
        for (var y = 0; y < c.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(c.Rgba, y * c.Width * 4, fb.Address + y * fb.RowBytes, c.Width * 4);
        return bitmap;
    }

    private static WriteableBitmap FromGrey(GreyImage g)
    {
        var bitmap = new WriteableBitmap(new PixelSize(g.Width, g.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        var row = new byte[g.Width * 4];
        for (var y = 0; y < g.Height; y++)
        {
            for (var x = 0; x < g.Width; x++)
            {
                var v = g.Pixels[y * g.Width + x];
                (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]) = (v, v, v, 255);
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bitmap;
    }
}

/// <summary>
/// Draws an SVG's Skia picture straight onto the canvas the page is being drawn on (the screen's, a bitmap's or a
/// PDF page's), so it keeps its lines and curves; the canvas comes with the page's transform already set.
/// </summary>
internal sealed class SvgDraw(SKPicture picture, SKRect cull, Rect dest, Rect clip) : ICustomDrawOperation
{
    public Rect Bounds => clip;

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Save();
        canvas.ClipRect(new SKRect((float)clip.X, (float)clip.Y, (float)clip.Right, (float)clip.Bottom));
        canvas.Translate((float)dest.X, (float)dest.Y);
        if (cull.Width > 0 && cull.Height > 0) canvas.Scale((float)(dest.Width / cull.Width), (float)(dest.Height / cull.Height));
        canvas.Translate(-cull.Left, -cull.Top);
        canvas.DrawPicture(picture);
        canvas.Restore();
    }
}
