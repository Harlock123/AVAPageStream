using AVAPageStream.Core;
using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>Milestone 8, stage 3: Asian text in fonts a PDF only names, through Adobe's predefined CMaps.</summary>
public class CjkTests
{
    private static int Cid(string cmap, params byte[] code) => PredefinedCMaps.Get(cmap)!.Lookup(PsString.Of(code), 0).Cid;

    [Theory]
    [InlineData("UniJIS-UCS2-H", "Japan1", 0x3042)]   // あ
    [InlineData("UniGB-UCS2-H", "GB1", 0x4E2D)]       // 中
    [InlineData("UniCNS-UCS2-H", "CNS1", 0x6F22)]     // 漢
    [InlineData("UniKS-UCS2-H", "Korea1", 0xD55C)]    // 한
    public void A_character_goes_to_its_cid_and_back(string cmap, string ordering, int ch)
    {
        var cid = Cid(cmap, (byte)(ch >> 8), (byte)ch);
        Assert.True(cid > 0);
        Assert.Equal(ch, PredefinedCMaps.Unicode(ordering, cid));
    }

    [Fact]
    public void The_bundle_has_the_cmaps_pdfs_name()
    {
        Assert.True(PredefinedCMaps.Count >= 170);
        // Shift-JIS あ (82 A0) is the same CID as Unicode's, by a CMap that uses another (90ms-RKSJ-H uses nothing; 90ms-RKSJ-V uses it).
        Assert.Equal(Cid("UniJIS-UCS2-H", 0x30, 0x42), Cid("90ms-RKSJ-H", 0x82, 0xA0));
        Assert.Equal(Cid("90ms-RKSJ-H", 0x82, 0xA0), Cid("90ms-RKSJ-V", 0x82, 0xA0));
        Assert.Equal(843, Cid("90ms-RKSJ-H", 0x82, 0xA0)); // Adobe-Japan1's CID for あ
        Assert.True(PredefinedCMaps.Vertical("UniJIS-UCS2-V"));
        Assert.Null(PredefinedCMaps.Get("No-Such-CMap"));
    }

    /// <summary>Draws a page with an interpreter whose printer-font outlines are squares, noting the characters asked for.</summary>
    private static (PsDrawing Drawing, List<int> Asked) Draw(byte[] pdf)
    {
        var asked = new List<int>();
        var interpreter = new Interpreter
        {
            Glyphs = (family, bold, italic, ch) =>
            {
                asked.Add(ch);
                var p = new PsPath();
                p.Segs.AddRange([new PsSeg(SegKind.Move, X3: 100, Y3: 0), new PsSeg(SegKind.Line, X3: 900, Y3: 0), new PsSeg(SegKind.Line, X3: 900, Y3: 800), new PsSeg(SegKind.Close)]);
                return new SystemGlyph(p, 1000);
            },
        };
        var box = interpreter.RunPdfPage(new PdfReader(pdf), 0);
        Assert.Empty(interpreter.PdfNotes);
        return (new PsDrawing(box, interpreter.Ops), asked);
    }

    private static byte[] Page(string cmap, string text) => PdfMaker.Page($"BT /F 20 Tf 10 50 Td <{text}> Tj ET",
        $"<< /Font << /F << /Type /Font /Subtype /Type0 /BaseFont /MS-Mincho /Encoding /{cmap} /DescendantFonts [<< /Type /Font /Subtype /CIDFontType0 /BaseFont /MS-Mincho /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> /DW 1000 >>] >> >> >>").Build();

    [Fact]
    public void Japanese_text_in_a_named_font_is_drawn_from_its_characters()
    {
        // あい in Shift-JIS, set across.
        var (d, asked) = Draw(Page("90ms-RKSJ-H", "82A082A2"));
        Assert.Equal([0x3042, 0x3044], asked);
        var xs = ((PsFill)d.Ops.Single()).Path.Segs.Where(s => s.Kind == SegKind.Move).Select(s => s.X3).ToList();
        Assert.Equal([12, 32], xs.Select(x => Math.Round(x, 6)));  // the second a full em (20) on
    }

    [Fact]
    public void Vertical_writing_sets_down_the_page()
    {
        var (d, asked) = Draw(Page("90ms-RKSJ-V", "82A082A2"));
        Assert.Equal([0x3042, 0x3044], asked);
        var moves = ((PsFill)d.Ops.Single()).Path.Segs.Where(s => s.Kind == SegKind.Move).ToList();
        // Each glyph's origin moved half its width left and 880 thousandths up; the second 20 points below the first.
        Assert.Equal(12 - 10, Math.Round(moves[0].X3, 6));
        Assert.Equal(50 - 17.6, Math.Round(moves[0].Y3, 6));
        Assert.Equal(moves[0].X3, moves[1].X3, 6);
        Assert.Equal(-20, Math.Round(moves[1].Y3 - moves[0].Y3, 6));
    }
}
