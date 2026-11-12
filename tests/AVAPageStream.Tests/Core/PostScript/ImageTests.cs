using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.PostScript;

/// <summary>Stage 2: pictures, masks, the filters their data comes through, and smooth shading.</summary>
public class ImageTests
{
    private static Interpreter Run(string program)
    {
        var i = new Interpreter();
        Assert.Null(i.Run(program));
        return i;
    }

    private static (byte, byte, byte, byte) Pixel(PsImage img, int x, int y)
    {
        var o = (y * img.Width + x) * 4;
        return (img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2], img.Rgba[o + 3]);
    }

    [Fact]
    public void A_grey_image_is_read_from_the_program_and_placed_by_its_matrix()
    {
        // Two by two, 8 bits: black, white / grey, black; scaled to 20 by 10.
        var i = Run("/s 4 string def 20 10 scale 2 2 8 [2 0 0 -2 0 2] {currentfile s readhexstring pop} image\n00FF8000\n");
        var img = Assert.IsType<PsImage>(Assert.Single(i.Ops));
        Assert.Equal((2, 2), (img.Width, img.Height));
        Assert.Equal((0, 0, 0, 255), Pixel(img, 0, 0));
        Assert.Equal((255, 255, 255, 255), Pixel(img, 1, 0));
        Assert.Equal((128, 128, 128, 255), Pixel(img, 0, 1));
        // The unit square to the device: first row at the top of the 20 by 10 box.
        Assert.Equal((20.0, 0.0, 0.0, -10.0, 0.0, 10.0), (img.Matrix.A, img.Matrix.B, img.Matrix.C, img.Matrix.D, img.Matrix.E, img.Matrix.F));
    }

    [Fact]
    public void A_dictionary_image_takes_the_colour_space_its_data_and_its_decode()
    {
        var i = Run("""
            /DeviceRGB setcolorspace
            << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 1 0 0]
               /Decode [1 0 1 0 1 0] /DataSource currentfile /ASCIIHexDecode filter >>
            image
            FF000000FF00>
            """);
        var img = (PsImage)Assert.Single(i.Ops);
        Assert.Equal((0, 255, 255, 255), Pixel(img, 0, 0)); // decoded inverted: red becomes cyan
        Assert.Equal((255, 0, 255, 255), Pixel(img, 1, 0));
    }

    [Fact]
    public void Colour_images_from_one_source_or_one_per_colour()
    {
        var one = Run("1 1 8 [1 0 0 1 0 0] <FF8000> false 3 colorimage");
        Assert.Equal((255, 128, 0, 255), Pixel((PsImage)one.Ops[0], 0, 0));
        var three = Run("1 1 8 [1 0 0 1 0 0] <00> <FF> <80> true 3 colorimage");
        Assert.Equal((0, 255, 128, 255), Pixel((PsImage)three.Ops[0], 0, 0));
        var cmyk = Run("1 1 8 [1 0 0 1 0 0] {<FF000000>} false 4 colorimage");
        Assert.Equal((0, 255, 255, 255), Pixel((PsImage)cmyk.Ops[0], 0, 0));
        var indexed = Run("[/Indexed /DeviceRGB 1 <000000 00FF00>] setcolorspace << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 1 /ImageMatrix [2 0 0 1 0 0] /DataSource <40> >> image");
        Assert.Equal((0, 0, 0, 255), Pixel((PsImage)indexed.Ops[0], 0, 0));
        Assert.Equal((0, 255, 0, 255), Pixel((PsImage)indexed.Ops[0], 1, 0));
    }

    [Fact]
    public void A_mask_paints_the_current_colour_where_its_bits_say()
    {
        var i = Run("1 0 0 setrgbcolor 4 1 true [4 0 0 1 0 0] <A0> imagemask");
        var img = (PsImage)i.Ops[0];
        Assert.Equal((255, 0, 0, 255), Pixel(img, 0, 0)); // 1010: painted, clear, painted, clear
        Assert.Equal(0, Pixel(img, 1, 0).Item4);
        Assert.Equal((255, 0, 0, 255), Pixel(img, 2, 0));
        var other = Run("4 1 false [4 0 0 1 0 0] <A0> imagemask");
        Assert.Equal(0, Pixel((PsImage)other.Ops[0], 0, 0).Item4);
    }

    [Fact]
    public void A_filter_reads_its_data_when_it_is_read_from_not_when_it_is_made()
    {
        // The filter is made before the dictionary's end and image: the data comes after them.
        var i = Run("""
            /src currentfile /ASCII85Decode filter def
            << /ImageType 1 /Width 3 /Height 1 /BitsPerComponent 8 /ImageMatrix [3 0 0 1 0 0] /DataSource src >>
            image
            !!*-'~>
            3 4 add
            """);
        var img = (PsImage)Assert.Single(i.Ops);
        Assert.Equal((0, 0, 0, 255), Pixel(img, 0, 0));
        Assert.Equal(7, Assert.Single(i.Stack));         // the program carries on after the data
        // A filter on a filter, and SubFileDecode's end string.
        var j = Run("currentfile 0 (%EOD) /SubFileDecode filter /ASCIIHexDecode filter 8 string readstring\n4142 4344\n%EOD\npop");
        Assert.Equal("(ABCD)", Interpreter.Write(j.Stack[0]));
    }

    [Fact]
    public void Run_length_lzw_and_flate_data_decode()
    {
        Assert.Equal("aaab"u8.ToArray(), Filters.RunLength([254, (byte)'a', 0, (byte)'b', 128]));
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write("harbour harbour"u8);
        Assert.Equal("harbour harbour"u8.ToArray(), Filters.Flate(ms.ToArray()));
        Assert.Equal("ABC"u8.ToArray(), Filters.AsciiHex("41 42 43>"u8));
        // LZW: the clear code, A, B, A B as one code, the end.
        byte[] lzw = [0x80, 0x10, 0x48, 0x50, 0x28, 0x08]; // 256 65 66 258 257 in 9-bit codes, most significant bit first
        Assert.Equal("ABAB"u8.ToArray(), Filters.Lzw(lzw));
    }

    [Fact]
    public void Axial_and_radial_shading_sample_their_functions()
    {
        var i = Run("""
            << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Extend [true true]
               /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> shfill
            """);
        var s = Assert.IsType<PsShading>(Assert.Single(i.Ops));
        Assert.False(s.Radial);
        Assert.Equal(new PsColour(1, 0, 0), s.Colours[0]);
        Assert.Equal(new PsColour(0, 0, 1), s.Colours[^1]);
        Assert.Equal(0.5, s.Colours[32].R, 6);
        Assert.True(s.ExtendStart && s.ExtendEnd);

        // Stitching two functions, and a PostScript function, in a radial shading used as a pattern.
        var j = Run("""
            << /PatternType 2 /Shading << /ShadingType 3 /ColorSpace /DeviceGray /Coords [0 0 0 0 0 10]
               /Function << /FunctionType 3 /Domain [0 1] /Bounds [0.5] /Encode [0 1 0 1]
                  /Functions [ << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>
                               << /FunctionType 4 /Domain [0 1] /Range [0 1] /Function {pop 0.25} >> ] >> >> >>
            matrix makepattern setpattern 0 0 10 10 rectfill
            """);
        var r = Assert.IsType<PsShading>(Assert.Single(j.Ops));
        Assert.True(r.Radial);
        Assert.NotNull(r.Area);                            // the rectangle filled with it
        Assert.Equal(0.5, r.Colours[16].R, 6);              // a quarter of the way: half of the first function
        Assert.Equal(0.25, r.Colours[48].R, 6);             // past the bound: the PostScript function
        Assert.Equal("unsupported", new Interpreter().Run("<< /PatternType 1 >> matrix makepattern")!.Name); // tiling: not yet
    }
}
