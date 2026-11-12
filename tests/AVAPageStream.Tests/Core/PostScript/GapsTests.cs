using System.IO.Compression;
using System.Text;
using AVAPageStream.Core;
using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.PostScript;

/// <summary>The gaps filled: binary data, strokepath, cshow, packed numbers, CIE colour, masked images, tiling patterns, meshes, composite and CFF fonts.</summary>
public class GapsTests
{
    private static Interpreter Run(byte[] program, Interpreter? i = null)
    {
        i ??= new Interpreter();
        var error = i.Run(program);
        Assert.Null(error);
        return i;
    }

    private static Interpreter Run(string program, Interpreter? i = null) => Run(Encoding.Latin1.GetBytes(program), i);

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static (byte, byte, byte, byte) Pixel(PsImage img, int x, int y)
    {
        var o = (y * img.Width + x) * 4;
        return (img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2], img.Rgba[o + 3]);
    }

    private const string Image2x1 = "<< /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /Decode [0 1] /ImageMatrix [2 0 0 1 0 0] /DataSource currentfile /";

    [Fact]
    public void Binary_data_in_the_program_is_read_to_its_own_end()
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write([0, 255]);
        foreach (var (filter, data) in new[] { ("FlateDecode", ms.ToArray()), ("RunLengthDecode", new byte[] { 1, 0, 255, 128 }), ("LZWDecode", new byte[] { 0x80, 0x00, 0x1F, 0xF0, 0x10 }) })
        {
            var i = Run([.. B(Image2x1 + filter + " filter >> image\n"), .. data, .. B("\n3 4 add")]);
            var img = (PsImage)Assert.Single(i.Ops);
            Assert.Equal((0, 0, 0, 255), Pixel(img, 0, 0));
            Assert.Equal((255, 255, 255, 255), Pixel(img, 1, 0));
            Assert.Equal(7, Assert.Single(i.Stack)); // the program goes on after the data
        }
        // A JPEG: its segments and scan read up to its end marker, then decoded by the program's decoder.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x04, 1, 2, 0xFF, 0xDA, 0x00, 0x03, 9, 0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD9];
        byte[]? seen = null;
        var j = new Interpreter { JpegDecoder = b => { seen = b; return new ColourImage(2, 1, [10, 20, 30, 255, 40, 50, 60, 255]); } };
        Run([.. B("<< /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 1 0 0] /DataSource currentfile /DCTDecode filter >> /DeviceRGB setcolorspace image "), .. jpeg, .. B(" 3 4 add")], j);
        Assert.Equal(jpeg, seen);
        Assert.Equal(7, Assert.Single(j.Stack));
    }

    [Fact]
    public void Strokepath_cshow_and_packed_numbers()
    {
        var outlined = new PsPath();
        outlined.Segs.Add(new PsSeg(SegKind.Move, X3: 1, Y3: 2));
        var i = Run("2 2 scale newpath 0 0 moveto 10 0 lineto strokepath", new Interpreter { StrokeOutliner = (p, pen) => outlined });
        Assert.Equal((2.0, 4.0), (i.G.Path.Segs[0].X3, i.G.Path.Segs[0].Y3)); // back through the matrix
        var widths = new List<string>();
        SystemGlyph? Box(string f, bool b, bool it, int c) => new(new PsPath(), 600);
        var k = Run("/Courier 10 selectfont 0 0 moveto {exch 2 index 3 string cvs print (:) print = pop} (ab) cshow", new Interpreter { Glyphs = Box });
        Assert.Equal("97:6\n98:6\n", k.Output.ToString().Replace(".0", ""));
        // Two 16-bit numbers with 8 bits of fraction, big-endian: 1.5 and -2.
        Assert.Equal([1.5, -2], Interpreter.EncodedNumbers(PsString.Of([149, 40, 0, 2, 0x01, 0x80, 0xFE, 0x00])));
    }

    [Fact]
    public void Cie_colour_spaces_turn_into_srgb()
    {
        // XYZ straight through: D65 white is white, and Y alone a grey.
        var white = Run("[/CIEBasedABC << /WhitePoint [0.9505 1 1.089] /RangeABC [0 2 0 2 0 2] >>] setcolorspace 0.9505 1 1.089 setcolor").G.Colour;
        Assert.Equal((1.0, 1.0, 1.0), (Math.Round(white.R, 2), Math.Round(white.G, 2), Math.Round(white.B, 2)));
        var grey = Run("[/CIEBasedA << /WhitePoint [0.9505 1 1.089] /MatrixA [0.9505 1 1.089] >>] setcolorspace 0.2 setcolor").G.Colour;
        Assert.Equal(grey.R, grey.B, 2);
        Assert.InRange(grey.R, 0.45, 0.5); // 0.2 of the light: about half way in sRGB
    }

    [Fact]
    public void Masked_images_leave_their_masked_pixels_clear()
    {
        // ImageType 4: the grey 255 left clear.
        var four = (PsImage)Assert.Single(Run("<< /ImageType 4 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 1 0 0] /MaskColor [255] /DataSource <00FF> >> image").Ops);
        Assert.Equal(255, Pixel(four, 0, 0).Item4);
        Assert.Equal(0, Pixel(four, 1, 0).Item4);
        // ImageType 3, its mask from a source of its own: 0 paints.
        var three = (PsImage)Assert.Single(Run("""
            << /ImageType 3 /InterleaveType 3
               /DataDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /ImageMatrix [2 0 0 1 0 0] /DataSource <8080> >>
               /MaskDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 1 /ImageMatrix [2 0 0 1 0 0] /DataSource <40> >> >> image
            """).Ops);
        Assert.Equal(255, Pixel(three, 0, 0).Item4);
        Assert.Equal(0, Pixel(three, 1, 0).Item4);
    }

    [Fact]
    public void A_tiling_pattern_keeps_its_cell_and_an_uncoloured_one_its_colour()
    {
        var i = Run("""
            << /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10
               /PaintProc { pop 0 0 5 5 rectfill } >> matrix makepattern
            [/Pattern /DeviceRGB] setcolorspace 1 0 0 4 -1 roll setcolor
            0 0 100 100 rectfill
            """);
        var t = Assert.IsType<PsTiling>(Assert.Single(i.Ops));
        Assert.IsType<PsFill>(Assert.Single(t.Cell));
        Assert.Equal(new PsColour(1, 0, 0), t.Colour);
        Assert.Equal((10.0, 10.0), (t.XStep, t.YStep));
        Assert.NotNull(t.Area);
    }

    [Fact]
    public void Mesh_shadings_become_coloured_triangles()
    {
        // Free-form, from an array: one triangle, then one more sharing its last two corners.
        var four = Run("<< /ShadingType 4 /ColorSpace /DeviceGray /DataSource [0 0 0 0  0 10 0 0.5  0 0 10 1  1 10 10 0] >> shfill");
        var m = Assert.IsType<PsMesh>(Assert.Single(four.Ops));
        Assert.Equal(6, m.Colours.Length);
        Assert.Equal(new PsColour(0.5, 0.5, 0.5), m.Colours[1]);
        // A lattice of two rows of two: one square, two triangles.
        var five = (PsMesh)Assert.Single(Run("<< /ShadingType 5 /ColorSpace /DeviceGray /VerticesPerRow 2 /DataSource [0 0 0 10 0 1 0 10 1 10 10 0] >> shfill").Ops);
        Assert.Equal(6, five.Colours.Length);
        // A Coons patch: its corners' colours at its corners.
        var patch = string.Join(" ", new[] { "0", "0 0", "0 3", "0 7", "0 10", "3 10", "7 10", "10 10", "10 7", "10 3", "10 0", "7 0", "3 0", "0", "1", "0", "1" });
        var six = (PsMesh)Assert.Single(Run($"<< /ShadingType 6 /ColorSpace /DeviceGray /DataSource [{patch}] >> shfill").Ops);
        Assert.Equal(12 * 12 * 6, six.Colours.Length);
        Assert.Equal(PsColour.Black, six.Colours[0]);
        // Function-based: a function of x and y over its domain.
        var one = (PsMesh)Assert.Single(Run("<< /ShadingType 1 /ColorSpace /DeviceGray /Domain [0 1 0 1] /Function << /FunctionType 4 /Domain [0 1 0 1] /Range [0 1] /Function {add 2 div} >> >> shfill").Ops);
        var middle = Enumerable.Range(0, one.Colours.Length).First(k => one.Points[2 * k] == 0.5 && one.Points[2 * k + 1] == 0.5);
        Assert.Equal(new PsColour(0.5, 0.5, 0.5), one.Colours[middle]); // (0.5 + 0.5) / 2
    }

    [Fact]
    public void A_composite_font_shows_each_code_in_its_own_font()
    {
        SystemGlyph? Box(string family, bool b, bool it, int c) => new(new PsPath(), family.Contains("Mono") ? 600 : 500);
        var i = Run("""
            /Mixed << /FontType 0 /FMapType 2 /FontMatrix [1 0 0 1 0 0] /Encoding [0 1]
                      /FDepVector [/Helvetica findfont /Courier findfont] >> definefont pop
            /Mixed 10 selectfont 0 0 moveto <0041 0142 0041> show currentpoint
            """, new Interpreter { Glyphs = Box });
        Assert.Equal([16.0, 0], i.Stack.Select(o => Math.Round(Interpreter.ToNum(o), 4))); // 5 + 6 + 5
    }

    /// <summary>A Type 1 charstring, not encrypted (lenIV -1), from numbers and operators (negative: -100 - op).</summary>
    private static byte[] Plain(params int[] items)
    {
        var b = new List<byte>();
        foreach (var v in items)
        {
            if (v <= -10_000) { var op = -v - 10_000; if (op >= 100) { b.Add(12); b.Add((byte)(op - 100)); } else b.Add((byte)op); continue; }
            if (v is >= -107 and <= 107) b.Add((byte)(v + 139));
            else if (v is >= 108 and <= 1131) { var w = v - 108; b.Add((byte)(w / 256 + 247)); b.Add((byte)(w % 256)); }
            else { var w = -v - 108; b.Add((byte)(w / 256 + 251)); b.Add((byte)(w % 256)); }
        }
        return [.. b];
    }

    private static int O(int op) => -10_000 - op;

    [Fact]
    public void A_cmap_and_cidfont_made_with_cidinit_show_their_glyphs()
    {
        // CID 1 a square 400 wide, advance 500; CID 2 nothing, advance 300.
        var square = Plain(0, 500, O(13), 0, 0, O(21), 400, 0, O(5), 0, 400, O(5), -400, 0, O(5), O(9), O(14));
        var blank = Plain(0, 300, O(13), O(14));
        static string Hex(byte[] b) => Convert.ToHexString(b);
        var program = $$"""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CMapName /Test-H def
            1 begincodespacerange <00> <FF> endcodespacerange
            1 begincidrange <41> <42> 1 endcidrange
            endcmap
            CMapName currentdict /CMap defineresource pop
            end end
            /T-CID << /CIDFontType 0 /CIDFontName /T-CID /FontMatrix [1 0 0 1 0 0] /FDBytes 1 /GDBytes 2
              /FDArray [ << /FontType 1 /FontMatrix [0.001 0 0 0.001 0 0] /Private << /lenIV -1 >> >> ]
              /GlyphDirectory << 1 <00{{Hex(square)}}> 2 <00{{Hex(blank)}}> >> >> /CIDFont defineresource pop
            /T /Test-H [/T-CID] composefont pop
            /T 100 selectfont 0 0 moveto (AB) show currentpoint
            """;
        var i = Run(program);
        Assert.Equal([80.0, 0], i.Stack.Select(o => Math.Round(Interpreter.ToNum(o), 4)));
        Assert.Equal((0.0, 0.0, 40.0, 40.0), ((PsFill)Assert.Single(i.Ops)).Path.Bounds()!.Value);
    }

    /// <summary>A one-glyph CFF font set: "A", a triangle, its width 600 (nominal 0).</summary>
    private static byte[] Cff()
    {
        static byte[] Index(params byte[][] items)
        {
            if (items.Length == 0) return [0, 0];
            var o = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 2 };
            var at = 1;
            o.Add((byte)(at >> 8)); o.Add((byte)at);
            foreach (var it in items) { at += it.Length; o.Add((byte)(at >> 8)); o.Add((byte)at); }
            foreach (var it in items) o.AddRange(it);
            return [.. o];
        }
        static byte[] Int(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        byte[] notdef = [139 + 0 - 0, 14];                                             // width 0 (nominal + 0), endchar
        // The width (600) first, then 100 0 rmoveto, 600 0 rlineto, -100 500 rlineto, endchar. 600 is 248 236 and 500 is 248 136.
        byte[] a = [248, 236, 139 + 100, 139, 21, 248, 236, 139, 5, 139 - 100, 248, 136, 5, 14];
        byte[] priv = [.. Int(0), 20, .. Int(0), 21];                                    // defaultWidthX 0, nominalWidthX 0
        var header = new byte[] { 1, 0, 4, 1 };
        var names = Index("T"u8.ToArray());
        var strings = Index();
        var gsubrs = Index();
        var charstrings = Index(notdef, a);
        byte[] charset = [0, 0, 34];                                                      // format 0: glyph 1 is SID 34, "A"
        // The top DICT's offsets, filled in once its size (fixed, by Int) is known.
        byte[] Top(int cs, int cset, int pv) => [.. Int(cset), 15, .. Int(cs), 17, .. Int(priv.Length), .. Int(pv), 18];
        var topSize = Index(Top(0, 0, 0)).Length;
        var start = header.Length + names.Length + topSize + strings.Length + gsubrs.Length;
        var csAt = start;
        var csetAt = csAt + charstrings.Length;
        var privAt = csetAt + charset.Length;
        return [.. header, .. names, .. Index(Top(csAt, csetAt, privAt)), .. strings, .. gsubrs, .. charstrings, .. charset, .. priv];
    }

    [Fact]
    public void A_cff_font_set_is_read_and_its_type_2_charstrings_drawn()
    {
        var cff = Cff();
        var i = Run([.. B($"/FontSetInit /ProcSet findresource begin\n/TSet {cff.Length} StartData "), .. cff, .. B("\nend /T 100 selectfont 0 0 moveto (A) show currentpoint")]);
        Assert.Equal([60.0, 0], i.Stack.Select(o => Math.Round(Interpreter.ToNum(o), 4)));
        Assert.Equal((10.0, 0.0, 70.0, 50.0), ((PsFill)Assert.Single(i.Ops)).Path.Bounds()!.Value);
    }
}
