using System.Text;
using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.PostScript;

/// <summary>Stage 3: text, in printer fonts, Type 3 fonts, Type 1 fonts (Type 42 is tested on real files).</summary>
public class FontTests
{
    /// <summary>A printer font's glyphs: every character a box 500 wide and 700 high (600 for Mono), so sums are easy.</summary>
    private static SystemGlyph? Boxes(string family, bool bold, bool italic, int c)
    {
        var w = family.Contains("Mono") ? 600 : 500;
        var p = new PsPath();
        p.Segs.Add(new PsSeg(SegKind.Move, X3: 0, Y3: 0));
        p.Segs.Add(new PsSeg(SegKind.Line, X3: w, Y3: 0));
        p.Segs.Add(new PsSeg(SegKind.Line, X3: w, Y3: 700));
        p.Segs.Add(new PsSeg(SegKind.Close));
        return c == ' ' ? new SystemGlyph(new PsPath(), 250) : new SystemGlyph(p, w);
    }

    private static Interpreter Run(string program)
    {
        var i = new Interpreter { Glyphs = Boxes };
        Assert.Null(i.Run(program));
        return i;
    }

    private static double[] Numbers(Interpreter i) => i.Stack.Select(o => Math.Round(Interpreter.ToNum(o), 4)).ToArray();

    [Fact]
    public void Printer_fonts_are_found_by_name_and_shown_from_the_current_point()
    {
        var i = Run("/Times-Roman findfont 20 scalefont setfont 10 10 moveto (Ab c) show currentpoint");
        // Three boxes of 10 and a space of 5.
        Assert.Equal([45.0, 10], Numbers(i));
        var f = Assert.IsType<PsFill>(Assert.Single(i.Ops));
        Assert.Equal((10.0, 10.0, 45.0, 24.0), f.Path.Bounds()!.Value);
        Assert.Equal("Liberation Mono|False|False", ((PsString)i.FindFont(new PsName("Courier"))!.Get("AVAPageStream.Font")!).Text);
        Assert.Equal("Liberation Sans|True|True", ((PsString)i.FindFont(new PsName("Helvetica-BoldOblique"))!.Get("AVAPageStream.Font")!).Text);
        Assert.Equal("Liberation Serif|True|False", ((PsString)i.FindFont(new PsName("Times-Bold"))!.Get("AVAPageStream.Font")!).Text);
    }

    [Fact]
    public void The_show_relatives_space_and_place_their_glyphs()
    {
        Assert.Equal([24.0, 0, 28, 0], Numbers(Run("/Courier 10 selectfont (abcd) stringwidth 0 0 moveto 1 0 (abcd) ashow currentpoint")));
        Assert.Equal([20.0, 0], Numbers(Run("/Courier 10 selectfont 0 0 moveto 2 0 98 (abc) widthshow currentpoint")));       // 2 more after the b
        Assert.Equal([15.0, 3], Numbers(Run("/Courier 10 selectfont 0 0 moveto (abc) [5 1 5 1 5 1] xyshow currentpoint")));
        Assert.Equal([21.0, 0], Numbers(Run("/Courier 10 selectfont 0 0 moveto {pop pop 1.5 0 rmoveto} (abc) kshow currentpoint"))); // between each pair
        var c = Run("/Courier 10 selectfont newpath 0 0 moveto (ab) false charpath pathbbox");
        Assert.Equal([0.0, 0, 12, 7], Numbers(c));
        Assert.Empty(c.Ops); // charpath paints nothing
        Assert.Equal([6.0, 0], Numbers(Run("/Courier 10 selectfont 0 0 moveto /a glyphshow currentpoint")));
    }

    [Fact]
    public void A_font_re_encoded_the_usual_way_keeps_its_glyphs()
    {
        var i = Run("""
            /Helvetica findfont dup length dict begin
              { 1 index /FID ne { def } { pop pop } ifelse } forall
              /Encoding ISOLatin1Encoding def
              currentdict
            end
            /Helvetica-ISO exch definefont pop
            /Helvetica-ISO 10 selectfont 0 0 moveto (caf\351) show currentpoint
            """);
        Assert.Equal([20.0, 0], Numbers(i)); // é found through ISOLatin1Encoding
        Assert.Equal("eacute", ((PsName)((PsArray)i.FindFont(new PsName("Helvetica-ISO"))!.Get("Encoding")!)[0xE9]).Value);
    }

    [Fact]
    public void A_type_3_font_draws_each_glyph_with_its_procedure()
    {
        var i = Run("""
            8 dict begin
              /FontType 3 def
              /FontMatrix [0.01 0 0 0.01 0 0] def
              /FontBBox [0 0 100 100] def
              /Encoding 256 array def
              0 1 255 { Encoding exch /.notdef put } for
              Encoding 66 /square put
              /CharProcs 2 dict def
              CharProcs begin
                /square { 0 0 moveto 80 0 lineto 80 80 lineto 0 80 lineto closepath fill } def
                /.notdef { } def
              end
              /BuildGlyph { exch begin 100 0 0 0 100 100 setcachedevice CharProcs exch get exec end } def
              /BuildChar { 1 index /Encoding get exch get 1 index /BuildGlyph get exec } bind def
              currentdict
            end
            /Squares exch definefont pop
            /Squares 20 selectfont 1 0 0 setrgbcolor 10 10 moveto (BB) show currentpoint
            /Squares 20 selectfont (BBB) stringwidth
            """);
        Assert.Equal([50.0, 10, 60, 0], Numbers(i));
        Assert.Equal(2, i.Ops.Count); // a square each, and nothing from stringwidth
        var f = (PsFill)i.Ops[1];
        Assert.Equal((30.0, 10.0, 46.0, 26.0), f.Path.Bounds()!.Value);
        Assert.Equal(new PsColour(1, 0, 0), f.Colour);
    }

    // A Type 1 font made here: charstrings encrypted with 4330, the font program with eexec.
    private static string Cs(params int[] items)
    {
        // Numbers and operators as a charstring, with the four bytes of encryption padding before it.
        var b = new List<byte> { 0, 0, 0, 0 };
        for (var k = 0; k < items.Length; k++)
        {
            var v = items[k];
            if (v <= -10_000) { var op = -v - 10_000; if (op >= 100) { b.Add(12); b.Add((byte)(op - 100)); } else b.Add((byte)op); continue; }
            if (v is >= -107 and <= 107) b.Add((byte)(v + 139));
            else if (v is >= 108 and <= 1131) { v -= 108; b.Add((byte)(v / 256 + 247)); b.Add((byte)(v % 256)); }
            else if (v is >= -1131 and <= -108) { v = -v - 108; b.Add((byte)(v / 256 + 251)); b.Add((byte)(v % 256)); }
            else { b.Add(255); b.AddRange(BitConverter.GetBytes(v).Reverse()); }
        }
        var enc = Type1.Encrypt([.. b], 4330);
        return $"{enc.Length} RD " + Encoding.Latin1.GetString(enc);
    }

    private static int O(int op) => -10_000 - op;
    private const int Hsbw = 13, Rmoveto = 21, Rlineto = 5, Closepath = 9, Endchar = 14, Callsubr = 10, Return = 11, Seac = 106, Rrcurveto = 8, Callothersubr = 116, Pop = 117, Setcurrentpoint = 133;

    private static byte[] Type1Font()
    {
        // A: a triangle 600 wide, drawn partly by a subroutine. acute: a little triangle. Aacute: A with acute over it.
        var a = Cs(50, 600, O(Hsbw), 0, 0, O(Rmoveto), 500, 0, O(Rlineto), 0, O(Callsubr), O(Closepath), O(Endchar));
        var acute = Cs(0, 300, O(Hsbw), 100, 800, O(Rmoveto), 100, 0, O(Rlineto), -50, 100, O(Rlineto), O(Closepath), O(Endchar));
        var aacute = Cs(50, 600, O(Hsbw), 0, 100, 0, 65, 194, O(Seac));
        // A flexed curve: the reference point, then six more, joined as two curves.
        var flex = Cs(0, 500, O(Hsbw), 0, 0, O(Rmoveto),
            0, 1, O(Callothersubr),
            100, 0, O(Rmoveto), 0, 2, O(Callothersubr),
            0, 10, O(Rmoveto), 0, 2, O(Callothersubr), 100, 0, O(Rmoveto), 0, 2, O(Callothersubr), 100, -10, O(Rmoveto), 0, 2, O(Callothersubr),
            0, 10, O(Rmoveto), 0, 2, O(Callothersubr), 100, 0, O(Rmoveto), 0, 2, O(Callothersubr), 100, -10, O(Rmoveto), 0, 2, O(Callothersubr),
            50, 400, 0, 3, 0, O(Callothersubr), O(Pop), O(Pop), O(Setcurrentpoint), 0, 300, O(Rlineto), O(Closepath), O(Endchar));
        var subr0 = Cs(-250, 700, O(Rlineto), O(Return));
        var program = $$"""
            dup /Private 8 dict dup begin
            /RD {string currentfile exch readstring pop} executeonly def
            /ND {noaccess def} executeonly def
            /NP {noaccess put} executeonly def
            /Subrs 1 array
            dup 0 {{subr0}} NP
            ND
            /lenIV 4 def
            2 index /CharStrings 5 dict dup begin
            /.notdef {{Cs(0, 500, O(Hsbw), O(Endchar))}} ND
            /A {{a}} ND
            /acute {{acute}} ND
            /Aacute {{aacute}} ND
            /flexed {{flex}} ND
            end
            end
            readonly put
            noaccess put
            dup /FontName get exch definefont pop
            mark currentfile closefile

            """;
        var cipher = Type1.Encrypt(Encoding.Latin1.GetBytes("abcd" + program), 55665);
        var hex = Convert.ToHexString(cipher);
        var lines = string.Join("\n", Enumerable.Range(0, (hex.Length + 63) / 64).Select(k => hex.Substring(k * 64, Math.Min(64, hex.Length - k * 64))));
        var zeros = string.Join("\n", Enumerable.Repeat(new string('0', 64), 8));
        var clear = """
            %!PS-AdobeFont-1.0: Test 1.0
            12 dict begin
            /FontType 1 def
            /FontName /Test def
            /FontMatrix [0.001 0 0 0.001 0 0] readonly def
            /FontBBox {0 0 1000 1000} readonly def
            /PaintType 0 def
            /Encoding StandardEncoding def
            currentdict end
            currentfile eexec

            """;
        return Encoding.Latin1.GetBytes(clear + lines + "\n" + zeros + "\ncleartomark\n");
    }

    [Fact]
    public void A_type_1_font_is_decrypted_and_its_charstrings_drawn()
    {
        var font = Type1Font();
        var i = new Interpreter();
        var show = Encoding.Latin1.GetBytes("/Test findfont 100 scalefont setfont 0 0 moveto (A) show currentpoint 200 0 moveto /Aacute glyphshow 400 0 moveto /flexed glyphshow");
        Assert.Null(i.Run([.. font, .. show]));
        Assert.Equal([60.0, 0], i.Stack.Select(o => Math.Round(Interpreter.ToNum(o), 4)));
        Assert.Equal(3, i.Ops.Count);
        // A: from its side bearing at 5 along to 55, and up to 70 at 30 (the subroutine's line).
        Assert.Equal((5.0, 0.0, 55.0, 70.0), ((PsFill)i.Ops[0]).Path.Bounds()!.Value);
        // Aacute: the A, with the accent placed 100 across from the side bearing (less the accent's own, 0), not raised:
        // its top at 900, so 90 at this size, and across from 225 to 235.
        var accented = (PsFill)i.Ops[1];
        Assert.Equal((205.0, 0.0, 255.0, 90.0), accented.Path.Bounds()!.Value);
        var accent = accented.Path.Segs.Where(s => s.Kind != SegKind.Close).TakeLast(3).ToList();
        Assert.Equal([225.0, 235, 230], accent.Select(s => Math.Round(s.X3, 3)));
        // The flex: two curves (out to 300 across and back), then a line up.
        var flexed = (PsFill)i.Ops[2];
        Assert.Equal(2, flexed.Path.Segs.Count(s => s.Kind == SegKind.Curve));
        Assert.Equal(450.0, flexed.Path.Bounds()!.Value.X1, 3); // the seventh point, 500 units on, at a tenth
    }

    [Fact]
    public void Composite_fonts_and_the_symbol_font_fall_back()
    {
        Assert.Equal("unsupported", new Interpreter().Run("/Symbol findfont")!.Name);
        Assert.Equal("unsupported", new Interpreter().Run("/X << /FontType 0 >> definefont")!.Name);
    }
}
