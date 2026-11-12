using AVAPageStream.Core;
using AVAPageStream.Core.PostScript;
using SkiaSharp;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>Milestone 8, stage 2: the pictures scanners make.</summary>
public class ScanTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Core", "Samples", "scans", name);

    /// <summary>The picture the samples were made from, a bit a pixel (1 white, 0 black), rows to a byte.</summary>
    private static (int W, int H, byte[] Bits) Original()
    {
        using var png = SKBitmap.Decode(Sample("fax.png"));
        var row = (png.Width + 7) / 8;
        var bits = new byte[row * png.Height];
        for (var y = 0; y < png.Height; y++)
            for (var x = 0; x < png.Width; x++)
                if (png.GetPixel(x, y).Red > 127) bits[y * row + x / 8] |= (byte)(0x80 >> (x % 8));
        // Spare bits at the end of a row: white, as decoders give them.
        for (var y = 0; y < png.Height; y++)
            for (var x = png.Width; x < row * 8; x++) bits[y * row + x / 8] |= (byte)(0x80 >> (x % 8));
        return (png.Width, png.Height, bits);
    }

    private static int Wrong(byte[] a, byte[] b) => a.Zip(b).Sum(p => System.Numerics.BitOperations.PopCount((uint)(p.First ^ p.Second))) + Math.Abs(a.Length - b.Length) * 8;

    [Fact]
    public void Group_4_fax_in_a_pdf_is_the_picture_it_was_made_from()
    {
        var (w, h, bits) = Original();
        var pdf = File.ReadAllBytes(Sample("fax-g4.pdf"));
        var reader = new PdfReader(pdf);
        var image = reader.Pages[0].Get("Resources") is var _ ? FindImage(reader) : null;
        var data = reader.Decode(image!, out var left);
        Assert.Null(left);
        Assert.Equal(0, Wrong(bits, data));
        var d = PdfPages.Draw(pdf, 1, out var why, out var notes)!;
        Assert.True(d is not null, why);
        Assert.Empty(notes);
        var img = Assert.IsType<PsImage>(Assert.Single(d.Ops));
        Assert.Equal((w, h), (img.Width, img.Height));
    }

    private static PsDict FindImage(PdfReader r)
    {
        var res = (PsDict)r.Deep(r.Pages[0].Get("Resources"));
        return ((PsDict)res.Get("XObject")!).Entries.Select(e => e.Value).OfType<PsDict>().First(x => (x.Get("Subtype") as PsName)?.Value == "Image");
    }

    [Fact]
    public void Group_3_fax_with_its_lines_ending_on_bytes_is_the_picture()
    {
        var (w, h, bits) = Original();
        var tif = File.ReadAllBytes(Sample("fax-g3.tif"));
        // The TIFF's one strip (StripOffsets 8, StripByteCounts in its tags), coded with T4Options 4: lines end on bytes.
        var count = BitConverter.ToInt32(tif, 4);
        var tags = BitConverter.ToUInt16(tif, count);
        int Tag(int id)
        {
            for (var i = 0; i < tags; i++)
                if (BitConverter.ToUInt16(tif, count + 2 + i * 12) == id) return BitConverter.ToInt32(tif, count + 2 + i * 12 + 8);
            return -1;
        }
        var strip = tif[Tag(273)..(Tag(273) + Tag(279))];
        // ImageMagick wrote it "min is black" (Photometric 1): its black runs are the picture's ones, white pixels.
        Assert.Equal(1, Tag(262) & 0xFFFF);
        var data = Ccitt.Decode(strip, k: 0, columns: w, rows: h, byteAlign: true, blackIs1: true);
        Assert.Equal(0, Wrong(bits, data));
        // Without BlackIs1, every bit the other way.
        var inverted = Ccitt.Decode(strip, k: 0, columns: w, rows: h, byteAlign: true);
        Assert.Equal(bits.Length * 8, Wrong(bits, inverted));
    }
}
