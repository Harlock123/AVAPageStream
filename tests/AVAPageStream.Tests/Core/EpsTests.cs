using System.Text;
using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class EpsTests
{
    /// <summary>A small EPS: a 4×2 preview (1 bit: a black left half) and a bounding box of 0 0 40 20.</summary>
    /// <param name="text">With Lab colour in it, which AVAPageStream doesn't draw itself: so the preview is what shows.</param>
    internal static byte[] Sample(bool binary = false, bool text = false)
    {
        var ps = """
            %!PS-Adobe-3.0 EPSF-3.0
            %%BoundingBox: 0 0 40 20
            %%HiResBoundingBox: 0 0 40.5 20.25
            %%BeginPreview: 4 2 1 2
            %C0
            %C0
            %%EndPreview
            0 0 20 20 rectfill
            %%EOF
            """.Replace("\r\n", "\n");
        if (text) ps = ps.Replace("%%EOF", "[/DeviceFoo] setcolorspace\n%%EOF");
        var bytes = Encoding.ASCII.GetBytes(ps);
        if (!binary) return bytes;
        // The DOS binary form: a 30-byte header pointing at the PostScript.
        var header = new byte[30];
        new byte[] { 0xC5, 0xD0, 0xD3, 0xC6 }.CopyTo(header, 0);
        BitConverter.GetBytes(30).CopyTo(header, 4);
        BitConverter.GetBytes(bytes.Length).CopyTo(header, 8);
        return [.. header, .. bytes];
    }

    [Fact]
    public void Eps_files_are_known_by_their_first_line_or_binary_header()
    {
        Assert.True(ImageStore.IsEps(Sample()));
        Assert.True(ImageStore.IsEps(Sample(binary: true)));
        Assert.False(ImageStore.IsEps(Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\nshowpage")));
        Assert.Equal("eps", ImageStore.Extension(Sample()));
        Assert.True(ImageStore.IsImage(Sample()));
    }

    [Fact]
    public void Svg_files_are_known_by_their_svg_element()
    {
        Assert.Equal("svg", ImageStore.Extension(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
        Assert.Equal("svg", ImageStore.Extension(Encoding.UTF8.GetBytes("﻿<svg width=\"10\" height=\"10\"></svg>")));
        Assert.Equal("bin", ImageStore.Extension(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><html/>")));
    }

    [Fact]
    public void The_bounding_box_prefers_the_high_resolution_one()
    {
        Assert.Equal(new Box(0, 0, 40.5, 20.25), Eps.BoundingBox(Sample()));
        Assert.Equal(new Box(0, 0, 40.5, 20.25), Eps.BoundingBox(Sample(binary: true)));
        Assert.Null(Eps.BoundingBox(Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: (atend)\n")));
    }

    [Fact]
    public void The_preview_is_read_with_ones_as_black()
    {
        var p = Eps.Preview(Sample())!;
        Assert.Equal((4, 2), (p.Width, p.Height));
        Assert.Equal([0, 0, 255, 255, 0, 0, 255, 255], p.Pixels); // C0 = 1100: black, black, white, white
        Assert.NotNull(Eps.Preview(Sample(binary: true)));
        Assert.Null(Eps.Preview(Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 1 1\n")));
    }

    [Fact]
    public void Without_ghostscript_nothing_is_rendered()
    {
        if (Ghostscript.Find() is not null) return; // this computer has it: nothing to show here
        Assert.Null(Ghostscript.Render(Sample()));
    }
}
