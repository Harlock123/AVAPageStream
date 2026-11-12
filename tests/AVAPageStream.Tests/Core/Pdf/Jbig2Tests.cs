using System.Diagnostics;
using AVAPageStream.Core;
using AVAPageStream.Core.PostScript;
using SkiaSharp;
using static AVAPageStream.Tests.Core.Pdf.Jbig2Stream;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>Milestone 8, stage 2: JBIG2 pictures, made by the tests' encoder and checked against Poppler too.</summary>
public class Jbig2Tests
{
    private static string Font => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AVAPageStream", "Assets", "Fonts", "LiberationSans-Bold.ttf");

    /// <summary>Text drawn in black on white, as a bitmap (1 black).</summary>
    private static Jbig2.Bitmap Drawn(string text, int w, int h, float size)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var tf = SKTypeface.FromFile(Font);
            using var font = new SKFont(tf, size);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
            canvas.DrawText(text, 2, h - size * 0.25f, font, paint);
            canvas.DrawCircle(w - h / 2f, h / 2f, h / 3f, paint);
        }
        var b = new Jbig2.Bitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) b.P[y * w + x] = (byte)(bmp.GetPixel(x, y).Red < 128 ? 1 : 0);
        return b;
    }

    /// <summary>A bitmap as PDF's rows of bits: 1 white.</summary>
    private static byte[] Rows(Jbig2.Bitmap b)
    {
        var row = (b.W + 7) / 8;
        var o = new byte[row * b.H];
        Array.Fill(o, (byte)0xFF);
        for (var y = 0; y < b.H; y++)
            for (var x = 0; x < b.W; x++)
                if (b.P[y * b.W + x] == 1) o[y * row + x / 8] &= (byte)~(0x80 >> (x % 8));
        return o;
    }

    /// <summary>A one-page PDF of a JBIG2 picture at a point a pixel, and how many pixels of it Poppler draws other than expected (null without Poppler).</summary>
    private static int? PopplerWrong(byte[] stream, byte[]? globals, Jbig2.Bitmap expected)
    {
        var m = PdfMaker.Page($"q {expected.W} 0 0 {expected.H} 0 0 cm /Im Do Q", "<< /XObject << /Im 5 0 R >> >>", $"[0 0 {expected.W} {expected.H}]");
        m.Add(5, $"<< /Type /XObject /Subtype /Image /Width {expected.W} /Height {expected.H} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode{(globals is null ? "" : " /DecodeParms << /JBIG2Globals 6 0 R >>")} >>", stream);
        if (globals is not null) m.Add(6, "<< >>", globals);
        var pdf = m.Build();
        // And ours, drawn from the PDF.
        var d = PdfPages.Draw(pdf, 1, out var why, out var notes);
        Assert.True(d is not null, why);
        Assert.Empty(notes);
        var dir = Path.Combine(Path.GetTempPath(), "avaps-jbig2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "in.pdf"), pdf);
            File.WriteAllBytes(Path.Combine(dir, "expected.pgm"), [.. System.Text.Encoding.ASCII.GetBytes($"P5 {expected.W} {expected.H} 255\n"), .. expected.P.Select(v => (byte)(v == 1 ? 0 : 255))]);
            Process? p;
            try { p = Process.Start(new ProcessStartInfo("pdftoppm", ["-r", "288", "-aa", "no", "-aaVector", "no", "-png", "-singlefile", Path.Combine(dir, "in.pdf"), Path.Combine(dir, "out")]) { RedirectStandardError = true }); }
            catch (System.ComponentModel.Win32Exception) { return null; }
            p!.WaitForExit(20000);
            using var bmp = SKBitmap.Decode(Path.Combine(dir, "out.png"));
            if (bmp is null) return -1;
            var wrong = 0;
            for (var y = 0; y < expected.H; y++)
                for (var x = 0; x < expected.W; x++)
                    if ((bmp.GetPixel(x * 4 + 2, y * 4 + 2).Red < 128 ? 1 : 0) != expected.P[y * expected.W + x]) wrong++; // each pixel's middle, at four times the size
            return wrong;
        }
        finally { if (Environment.GetEnvironmentVariable("AVAPS_KEEP") is { } keep) { Directory.CreateDirectory(keep); foreach (var f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), true); } Directory.Delete(dir, true); }
    }

    private static byte[] GenericSegment(Jbig2.Bitmap b, int template, bool tpgdon)
    {
        var at = Jbig2.DefaultAt(template);
        var c = new Jbig2Coder();
        c.Generic(b, template, tpgdon, at);
        var flags = template << 1 | (tpgdon ? 8 : 0);
        return [.. RegionInfo(b.W, b.H, 0, 0), (byte)flags, .. at.SelectMany(a => new[] { (byte)(sbyte)a.X, (byte)(sbyte)a.Y }), .. c.Mq.Finish()];
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void A_generic_region_is_decoded_to_the_bitmap_it_was_made_from(int template, bool tpgdon)
    {
        var picture = Drawn("Harbour", 150, 40, 30);
        var s = new Jbig2Stream();
        s.Add(48, PageInfo(picture.W, picture.H));
        s.Add(38, GenericSegment(picture, template, tpgdon));
        s.Add(49, []);
        Assert.Equal(Rows(picture), Jbig2.Decode(s.Bytes, null, picture.W, picture.H));
        if (PopplerWrong(s.Bytes, null, picture) is { } wrong) Assert.Equal(0, wrong);
    }

    [Fact]
    public void Symbols_from_the_globals_are_set_by_a_text_region()
    {
        // Three symbols of two heights, kept in the globals; the page spells with them on two strips.
        Jbig2.Bitmap Glyph(string ch, int w, int h) => Drawn(ch, w, h, h * 0.9f) is var b ? Crop(b, w - h, h) : b;
        var symbols = new[] { Glyph("H", 40, 20), Glyph("a", 36, 20), Glyph("W", 44, 24) }; // widths w - h: 20, 16, 20
        var c = new Jbig2Coder();
        // Height classes: 20 (H, a), then 24 (W).
        foreach (var group in symbols.GroupBy(b => b.H).OrderBy(g => g.Key))
        {
            c.Integer("IADH", group.Key - (symbols.Where(b => b.H < group.Key).Select(b => b.H).DefaultIfEmpty(0).Max()));
            var w = 0;
            foreach (var b in group)
            {
                c.Integer("IADW", b.W - w);
                w = b.W;
                c.Generic(b, 0, false, Jbig2.DefaultAt(0));
            }
            c.Integer("IADW", null);
        }
        c.Integer("IAEX", 0); // none left out
        c.Integer("IAEX", 3); // all three exported
        var globals = new Jbig2Stream();
        byte[] dictFlags = [0, 0]; // arithmetic, template 0
        globals.Add(0, [.. dictFlags, .. Jbig2.DefaultAt(0).SelectMany(a => new[] { (byte)(sbyte)a.X, (byte)(sbyte)a.Y }), .. Be32(3), .. Be32(3), .. c.Mq.Finish()]);

        // Instances (symbol, x, y) top-left placed, strip by strip.
        var page = new Jbig2.Bitmap(120, 60);
        (int Id, int X, int Y)[][] strips = [[(0, 4, 4), (1, 26, 4), (2, 46, 0)], [(2, 10, 30), (0, 34, 34)]];
        var t = new Jbig2Coder();
        t.Integer("IADT", 0);
        var stripT = 0;
        var firstS = 0;
        foreach (var strip in strips)
        {
            var top = strip[0].Y;
            t.Integer("IADT", top - stripT);
            stripT = top;
            t.Integer("IAFS", strip[0].X - firstS);
            firstS = strip[0].X;
            var s = 0;
            for (var i = 0; i < strip.Length; i++)
            {
                var (id, x, y) = strip[i];
                Assert.Equal(top, y < top ? top : top); // one strip's symbols share its T (SBSTRIPS 1)
                if (i > 0) t.Integer("IADS", x - s);
                t.Id(id, 2);
                page.Combine(symbols[id], x, top, 0);
                s = x + symbols[id].W - 1;
            }
            t.Integer("IADS", null);
        }
        var instances = strips.Sum(st => st.Length);
        var textFlags = 1 << 4; // top-left corner, one strip, OR
        var stream = new Jbig2Stream().From(globals);
        stream.Add(48, PageInfo(page.W, page.H));
        stream.Add(6, [.. RegionInfo(page.W, page.H, 0, 0), .. Be16(textFlags), .. Be32(instances), .. t.Mq.Finish()], 0);
        stream.Add(49, []);
        Assert.Equal(Rows(page), Jbig2.Decode(stream.Bytes, globals.Bytes, page.W, page.H));
        if (PopplerWrong(stream.Bytes, globals.Bytes, page) is { } wrong) Assert.Equal(0, wrong);
    }

    private static Jbig2.Bitmap Crop(Jbig2.Bitmap b, int w, int h)
    {
        var o = new Jbig2.Bitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) o.P[y * w + x] = (byte)b.Get(x, y);
        return o;
    }

    private static byte[] Be32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    [Fact]
    public void A_halftone_region_draws_its_patterns_by_gray_value()
    {
        // Four 4 × 4 patterns, from white to black, and a grid of 12 × 6 cells shaded left to right.
        var patterns = Enumerable.Range(0, 4).Select(g =>
        {
            var p = new Jbig2.Bitmap(4, 4);
            for (var i = 0; i < 16; i++) p.P[i] = (byte)((i * 7 % 16) < g * 16 / 3 ? 1 : 0);
            return p;
        }).ToArray();
        var all = new Jbig2.Bitmap(16, 4);
        for (var g = 0; g < 4; g++) all.Combine(patterns[g], g * 4, 0, 4);
        var pc = new Jbig2Coder();
        (int, int)[] pat = [(-4, 0), (-3, -1), (2, -2), (-2, -2)];
        pc.Generic(all, 0, false, pat);
        var (gw, gh) = (12, 6);
        int Gray(int m, int n) => (n / 3 + m) % 4;
        var hc = new Jbig2Coder();
        // Gray-coded bit planes, the most significant first.
        for (var j = 1; j >= 0; j--)
        {
            var plane = new Jbig2.Bitmap(gw, gh);
            for (var m = 0; m < gh; m++)
                for (var n = 0; n < gw; n++)
                {
                    var v = Gray(m, n);
                    plane.P[m * gw + n] = (byte)((v >> j & 1) ^ (j < 1 ? v >> (j + 1) & 1 : 0));
                }
            hc.Generic(plane, 0, false, Jbig2.DefaultAt(0));
        }
        var page = new Jbig2.Bitmap(gw * 4, gh * 4);
        for (var m = 0; m < gh; m++)
            for (var n = 0; n < gw; n++) page.Combine(patterns[Gray(m, n)], n * 4, m * 4, 0);
        var s = new Jbig2Stream();
        s.Add(48, PageInfo(page.W, page.H));
        var dict = s.Add(16, [0, 4, 4, .. Be32(3), .. pc.Mq.Finish()]);
        s.Add(22, [.. RegionInfo(page.W, page.H, 0, 0), 0, .. Be32(gw), .. Be32(gh), .. Be32(0), .. Be32(0), .. Be16(4 * 256), .. Be16(0), .. hc.Mq.Finish()], dict);
        s.Add(49, []);
        Assert.Equal(Rows(page), Jbig2.Decode(s.Bytes, null, page.W, page.H));
        if (PopplerWrong(s.Bytes, null, page) is { } wrong) Assert.Equal(0, wrong);
    }

    [Fact]
    public void An_mmr_region_is_group_4_fax()
    {
        // The fax sample's Group 4 data, as a JBIG2 generic region coded with MMR.
        var pdf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Core", "Samples", "scans", "fax-g4.pdf"));
        var r = new PdfReader(pdf);
        var res = (PsDict)r.Deep(r.Pages[0].Get("Resources"));
        var image = ((PsDict)res.Get("XObject")!).Entries.Select(e => e.Value).OfType<PsDict>().First(x => (x.Get("Subtype") as PsName)?.Value == "Image");
        var g4 = ((PdfStreamBytes)image.Get(PdfReader.StreamKey)!).ToArray();
        var (w, h) = ((int)image.Get("Width")!, (int)image.Get("Height")!);
        var expected = r.Decode(image); // the same picture, through the CCITT filter
        var s = new Jbig2Stream();
        s.Add(48, PageInfo(w, h));
        s.Add(38, [.. RegionInfo(w, h, 0, 0), 1, .. g4]);
        s.Add(49, []);
        Assert.Equal(expected, Jbig2.Decode(s.Bytes, null, w, h));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void A_refinement_region_refines_what_the_page_has_there(int template)
    {
        // A coarse picture first, then refined to the real one against it (template 1, no prediction).
        var real = Drawn("Quay", 96, 32, 26);
        var coarse = new Jbig2.Bitmap(real.W, real.H);
        for (var y = 0; y < real.H; y++)
            for (var x = 0; x < real.W; x++) coarse.P[y * real.W + x] = real.P[(y & ~1) * real.W + (x & ~1)];
        var c = new Jbig2Coder();
        var cx = c.Contexts("GR", 1 << 14);
        (int X, int Y)[] coding = template == 1 ? [(-1, -1), (0, -1), (1, -1), (-1, 0)] : [(0, -1), (1, -1), (-1, 0), (-1, -1)];
        (int X, int Y)[] refs = template == 1 ? [(0, -1), (-1, 0), (0, 0), (1, 0), (0, 1), (1, 1)] : [(0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (-1, 1), (0, 1), (1, 1), (-1, -1)];
        for (var y = 0; y < real.H; y++)
            for (var x = 0; x < real.W; x++)
            {
                var ctx = 0;
                foreach (var (px, py) in coding) ctx = (ctx << 1) | real.Get(x + px, y + py);
                foreach (var (px, py) in refs) ctx = (ctx << 1) | coarse.Get(x + px, y + py);
                c.Mq.Encode(cx, ctx, real.P[y * real.W + x]);
            }
        var s = new Jbig2Stream();
        s.Add(48, PageInfo(real.W, real.H));
        s.Add(38, GenericSegment(coarse, 0, true));
        byte[] flags = template == 1 ? [1] : [0, 0xFF, 0xFF, 0xFF, 0xFF]; // template 0: its adaptive pixels at (-1, -1) in both
        s.Add(42, [.. RegionInfo(real.W, real.H, 0, 0, 4), .. flags, .. c.Mq.Finish()]);
        s.Add(49, []);
        Assert.Equal(Rows(real), Jbig2.Decode(s.Bytes, null, real.W, real.H));
        // Poppler (as of 25.x) ORs the last byte of each row when it replaces a region, rather than replacing it: so
        // there it keeps the coarse picture's black too. (The same refinement in a text region, which pdf.js also
        // reads, agrees with both exactly: A_text_region_refines_a_symbol_as_it_places_it.)
        var popplers = new Jbig2.Bitmap(real.W, real.H);
        for (var y = 0; y < real.H; y++)
            for (var x = 0; x < real.W; x++)
                popplers.P[y * real.W + x] = (byte)(x >= (real.W - 1) / 8 * 8 ? real.Get(x, y) | coarse.Get(x, y) : real.Get(x, y));
        if (PopplerWrong(s.Bytes, null, popplers) is { } wrong) Assert.Equal(0, wrong);
    }

    /// <summary>Codes a bitmap by refinement against a reference offset by (dx, dy), as the decoder reads it.</summary>
    private static void EncodeRefinement(Jbig2Coder c, Jbig2.Bitmap real, Jbig2.Bitmap reference, int dx, int dy, int template)
    {
        var cx = c.Contexts("GR", 1 << 14);
        (int X, int Y)[] coding = template == 1 ? [(-1, -1), (0, -1), (1, -1), (-1, 0)] : [(0, -1), (1, -1), (-1, 0), (-1, -1)];
        (int X, int Y)[] refs = template == 1 ? [(0, -1), (-1, 0), (0, 0), (1, 0), (0, 1), (1, 1)] : [(0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (-1, 1), (0, 1), (1, 1), (-1, -1)];
        for (var y = 0; y < real.H; y++)
            for (var x = 0; x < real.W; x++)
            {
                var ctx = 0;
                foreach (var (px, py) in coding) ctx = (ctx << 1) | real.Get(x + px, y + py);
                foreach (var (px, py) in refs) ctx = (ctx << 1) | reference.Get(x - dx + px, y - dy + py);
                c.Mq.Encode(cx, ctx, real.P[y * real.W + x]);
            }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void A_text_region_refines_a_symbol_as_it_places_it(int template)
    {
        // One coarse symbol in the dictionary; the page places it refined, two pixels wider and one taller, into the real picture.
        var real = Drawn("Ok", 40, 30, 26);
        var coarse = new Jbig2.Bitmap(38, 29);
        for (var y = 0; y < coarse.H; y++)
            for (var x = 0; x < coarse.W; x++) coarse.P[y * coarse.W + x] = (byte)real.Get(x & ~1, y & ~1);
        var d = new Jbig2Coder();
        d.Integer("IADH", coarse.H);
        d.Integer("IADW", coarse.W);
        d.Generic(coarse, 0, false, Jbig2.DefaultAt(0));
        d.Integer("IADW", null);
        d.Integer("IAEX", 0);
        d.Integer("IAEX", 1);
        var globals = new Jbig2Stream();
        globals.Add(0, [0, 0, .. Jbig2.DefaultAt(0).SelectMany(a => new[] { (byte)(sbyte)a.X, (byte)(sbyte)a.Y }), .. Be32(1), .. Be32(1), .. d.Mq.Finish()]);
        var t = new Jbig2Coder();
        t.Integer("IADT", 0);
        t.Integer("IADT", 5);   // the strip's T: the symbol's top at y 5
        t.Integer("IAFS", 7);   // and its left at x 7
        t.Id(0, 0);
        t.Integer("IARI", 1);
        var (rdw, rdh, rdx, rdy) = (2, 1, 0, 0);
        t.Integer("IARDW", rdw);
        t.Integer("IARDH", rdh);
        t.Integer("IARDX", rdx);
        t.Integer("IARDY", rdy);
        EncodeRefinement(t, real, coarse, (rdw >> 1) + rdx, (rdh >> 1) + rdy, template);
        t.Integer("IADS", null);
        var page = new Jbig2.Bitmap(60, 40);
        page.Combine(real, 7, 5, 0);
        var flags = 2 | 1 << 4 | template << 15; // refinement, top-left corner
        byte[] rat = template == 0 ? [0xFF, 0xFF, 0xFF, 0xFF] : [];
        var stream = new Jbig2Stream().From(globals);
        stream.Add(48, PageInfo(page.W, page.H));
        stream.Add(6, [.. RegionInfo(page.W, page.H, 0, 0), .. Be16(flags), .. rat, .. Be32(1), .. t.Mq.Finish()], 0);
        stream.Add(49, []);
        Assert.Equal(Rows(page), Jbig2.Decode(stream.Bytes, globals.Bytes, page.W, page.H));
        if (PopplerWrong(stream.Bytes, globals.Bytes, page) is { } wrong) Assert.Equal(0, wrong);
    }
}
