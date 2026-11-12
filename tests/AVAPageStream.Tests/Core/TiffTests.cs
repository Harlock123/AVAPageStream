using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Reading the TIFF previews EPS files carry, from samples ImageMagick wrote (red left, blue right, a white last row).</summary>
public class TiffTests
{
    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Core", "Samples", name));

    private static (byte R, byte G, byte B, byte A) At(ColourImage i, int x, int y)
    {
        var o = (y * i.Width + x) * 4;
        return (i.Rgba[o], i.Rgba[o + 1], i.Rgba[o + 2], i.Rgba[o + 3]);
    }

    [Theory]
    [InlineData("rgb-none.tif")]
    [InlineData("rgb-lzw.tif")]
    [InlineData("rgb-lzw-predictor.tif")]
    [InlineData("rgb-deflate.tif")]
    [InlineData("rgb-packbits-mm.tif")] // big-endian
    [InlineData("palette-lzw.tif")]
    [InlineData("cmyk.tif")]
    public void Colour_tiffs_are_read_in_every_encoding(string file)
    {
        var i = Tiff.Read(Sample(file))!;
        Assert.Equal((8, 4), (i.Width, i.Height));
        Assert.Equal((255, 0, 0, 255), At(i, 0, 0));
        Assert.Equal((0, 0, 255, 255), At(i, 7, 1));
        Assert.Equal((255, 255, 255, 255), At(i, 3, 3));
    }

    [Fact]
    public void Grey_and_black_and_white_tiffs_are_read()
    {
        var grey = Tiff.Read(Sample("grey-packbits.tif"))!;
        var (r, g, b, _) = At(grey, 0, 0);
        Assert.True(r == g && g == b && r is > 20 and < 120); // red, as grey
        Assert.True(At(grey, 7, 0).R < r);                    // blue is darker
        Assert.Equal(255, At(grey, 0, 3).R);
        foreach (var file in new[] { "bilevel.tif", "bilevel-white.tif" })
        {
            var bw = Tiff.Read(Sample(file))!;
            Assert.Equal((0, 0, 0, 255), At(bw, 0, 0));
            Assert.Equal((255, 255, 255, 255), At(bw, 5, 3));
        }
    }

    [Fact]
    public void Damaged_or_unusual_tiffs_give_nothing()
    {
        var good = Sample("rgb-lzw.tif");
        Assert.Null(Tiff.Read(good[..40]));
        Assert.Null(Tiff.Read("not a tiff at all"u8.ToArray()));
        Assert.Null(Tiff.Read([]));
    }

    [Fact]
    public void A_dos_binary_eps_shows_its_tiff_preview()
    {
        var ps = System.Text.Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 80 40\nnewpath 0 0 moveto 80 40 lineto stroke\n");
        var tiff = Sample("rgb-deflate.tif");
        var header = new byte[30];
        header[0] = 0xC5; header[1] = 0xD0; header[2] = 0xD3; header[3] = 0xC6;
        BitConverter.GetBytes(30).CopyTo(header, 4);
        BitConverter.GetBytes(ps.Length).CopyTo(header, 8);
        BitConverter.GetBytes(30 + ps.Length).CopyTo(header, 20);
        BitConverter.GetBytes(tiff.Length).CopyTo(header, 24);
        header[28] = header[29] = 0xFF;
        byte[] eps = [.. header, .. ps, .. tiff];
        var preview = Eps.TiffPreview(eps)!;
        Assert.Equal((255, 0, 0, 255), At(preview, 0, 0));
        Assert.Equal(new Box(0, 0, 80, 40), Eps.BoundingBox(eps));
        Assert.Null(Eps.TiffPreview(ps)); // plain EPS: no header, no TIFF
    }
}
