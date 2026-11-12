using System.Diagnostics;
using AVAPageStream.Core;
using SkiaSharp;

namespace AVAPageStream.Tests;

/// <summary>
/// For working on the PDF reader: draws the pages AVAPAGESTREAM_PDFS lists ("file|page" a line) as AVAPageStream
/// does and as Poppler does, into AVAPAGESTREAM_PDF_OUT, side by side, with how much they differ. Does nothing
/// unless both are set (and Poppler's pdftoppm is installed).
/// </summary>
public class PdfCompare
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Compare_with_poppler()
    {
        if (Environment.GetEnvironmentVariable("AVAPAGESTREAM_PDFS") is not { } list || Environment.GetEnvironmentVariable("AVAPAGESTREAM_PDF_OUT") is not { } dir) return;
        EpsRender.Ready();
        Directory.CreateDirectory(dir);
        var report = new List<string>();
        var n = 0;
        foreach (var line in File.ReadAllLines(list).Where(l => l.Trim().Length > 0 && !l.StartsWith('#')))
        {
            var parts = line.Split('|');
            var (file, page) = (parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 1);
            var name = $"{n++:00}";
            var bytes = File.ReadAllBytes(file);
            var watch = Stopwatch.StartNew();
            var drawing = PdfPages.Draw(bytes, page, out var why, out var notes);
            var ms = watch.ElapsedMilliseconds;
            if (drawing is null) { report.Add($"{name} {file}#{page}: FAILED {why}"); continue; }
            const float scale = 1.5f; // 108 dpi
            var (w, h) = ((int)Math.Ceiling(drawing.BoundingBox.W * scale), (int)Math.Ceiling(drawing.BoundingBox.H * scale));
            using var ours = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(ours))
            {
                canvas.Clear(SKColors.White);
                canvas.Scale(scale);
                using var picture = EpsRender.Record(drawing);
                canvas.DrawPicture(picture);
            }
            var poppler = Path.Combine(dir, name + "-poppler");
            var p = Process.Start(new ProcessStartInfo("pdftoppm", ["-r", "108", "-f", page.ToString(), "-l", page.ToString(), "-png", "-singlefile", file, poppler]) { RedirectStandardError = true })!;
            p.WaitForExit(60000);
            using var theirs = SKBitmap.Decode(poppler + ".png");
            var diff = 0.0;
            using var both = new SKBitmap(w * 2 + 10, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(both))
            {
                c.Clear(SKColors.Red);
                c.DrawBitmap(ours, 0, 0);
                if (theirs is not null) c.DrawBitmap(theirs, w + 10, 0);
            }
            if (theirs is not null)
            {
                long bad = 0, total = 0;
                for (var y = 0; y < Math.Min(h, theirs.Height); y += 2)
                    for (var x = 0; x < Math.Min(w, theirs.Width); x += 2)
                    {
                        var a = ours.GetPixel(x, y);
                        var b = theirs.GetPixel(x, y);
                        total++;
                        if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 120) bad++;
                    }
                diff = total == 0 ? 0 : 100.0 * bad / total;
            }
            using (var f = File.Create(Path.Combine(dir, name + ".png"))) both.Encode(f, SKEncodedImageFormat.Png, 90);
            File.Delete(poppler + ".png");
            report.Add($"{name} {Path.GetFileName(file)}#{page}: {diff:0.00}% differ, {drawing.Ops.Count} ops, {ms} ms{(notes.Count > 0 ? " — " + string.Join("; ", notes) : "")}");
        }
        File.WriteAllLines(Path.Combine(dir, "report.txt"), report);
    }
}
