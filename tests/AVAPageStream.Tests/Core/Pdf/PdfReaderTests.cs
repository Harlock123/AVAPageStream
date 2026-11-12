using AVAPageStream.Core;
using AVAPageStream.Core.PostScript;
using Avalonia.Headless.XUnit;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>Milestone 7: reading PDF files, and drawing their pages.</summary>
public class PdfReaderTests
{
    private static IReadOnlyList<string> Notes = [];

    private static PsDrawing Draw(byte[] pdf, int page = 1)
    {
        EpsRender.Ready();
        var d = PdfPages.Draw(pdf, page, out var why, out var notes);
        Notes = notes;
        Assert.True(d is not null, why);
        return d!;
    }

    [AvaloniaFact]
    public void A_printer_font_s_bullet_is_drawn()
    {
        var pdf = PdfMaker.Page("BT /F1 12 Tf 10 50 Td (\\177 a) Tj ET",
            "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >>").Build();
        var ops = Draw(pdf).Ops;
        Assert.True(ops.Count == 1, string.Join("; ", Notes));
        var fill = Assert.IsType<PsFill>(ops[0]);
        var xs = fill.Path.Segs.Select(s => s.X3).ToList();
        Assert.True(xs.Min() < 14, $"the bullet starts the line: {xs.Min()}");
    }

    [AvaloniaFact]
    public void A_tiling_pattern_drawing_a_form_fills_an_area()
    {
        var m = PdfMaker.Page("q 10 10 80 80 re W n /Pattern cs /P1 scn 0 0 200 100 re f Q",
            "<< /Pattern << /P1 5 0 R >> >>");
        m.Add(5, "<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 9 9] /XStep 9 /YStep 9 /Matrix [1 0 0 -1 0 100] /Resources << /XObject << /X 6 0 R >> >> >>", "/X Do");
        m.Add(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] >>", "0 .6 .3 rg 0 0 4.5 4.5 re f");
        var ops = Draw(m.Build()).Ops;
        Assert.True(ops.Count == 1, $"{ops.Count} ops; {string.Join("; ", Notes)}");
        var tiling = Assert.IsType<PsTiling>(ops[0]);
        Assert.Single(tiling.Clips);
        var cell = Assert.IsType<PsFill>(Assert.Single(tiling.Cell));
        Assert.Equal(new PsColour(0, .6, .3), cell.Colour);
    }

    private const string Square = "0 0 1 rg 10 10 30 30 re f";

    private static Box Bounds(PsPath p)
    {
        var xs = p.Segs.Where(s => s.Kind != SegKind.Close).Select(s => s.X3).ToList();
        var ys = p.Segs.Where(s => s.Kind != SegKind.Close).Select(s => s.Y3).ToList();
        return Box.FromCorners(new Pt(xs.Min(), ys.Min()), new Pt(xs.Max(), ys.Max()));
    }

    [Fact]
    public void Cross_reference_streams_and_object_streams_are_read()
    {
        var pdf = PdfMaker.Page(Square).Build(xrefStream: true);
        Assert.Contains("/ObjStm", System.Text.Encoding.Latin1.GetString(pdf));
        var fill = Assert.IsType<PsFill>(Assert.Single(Draw(pdf).Ops));
        Assert.Equal(new PsColour(0, 0, 1), fill.Colour);
        Assert.Equal(1, PdfPages.Count(pdf));
    }

    [Fact]
    public void A_damaged_cross_reference_is_rebuilt_from_the_objects()
    {
        var pdf = PdfMaker.Page(Square).Build();
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        // Every offset wrong, and then no cross-reference at all.
        var wrong = System.Text.RegularExpressions.Regex.Replace(text, @"(\d{10}) 00000 n", m => $"{long.Parse(m.Groups[1].Value) + 7:D10} 00000 n");
        Assert.IsType<PsFill>(Assert.Single(Draw(System.Text.Encoding.Latin1.GetBytes(wrong)).Ops));
        var none = text[..text.IndexOf("xref", StringComparison.Ordinal)];
        Assert.IsType<PsFill>(Assert.Single(Draw(System.Text.Encoding.Latin1.GetBytes(none)).Ops));
        Assert.Null(PdfPages.Draw("not a pdf at all"u8.ToArray(), 1, out var why, out _));
        Assert.Equal("it isn't a PDF", why);
    }

    [Fact]
    public void A_pdf_protected_by_another_security_handler_is_refused_saying_so()
    {
        var pdf = System.Text.Encoding.Latin1.GetString(PdfMaker.Page(Square).Build()).Replace("/Root 1 0 R", "/Root 1 0 R /Encrypt << /Filter /Adobe.PubSec >>");
        Assert.Null(PdfPages.Draw(System.Text.Encoding.Latin1.GetBytes(pdf), 1, out var why, out _));
        Assert.Equal("it is protected by the Adobe.PubSec security handler, which AVAPageStream can't open", why);
    }

    [Fact]
    public void A_page_is_shown_in_its_crop_box_turned_as_it_says()
    {
        var turned = Draw(PdfMaker.Page(Square, box: "[0 0 200 100]", extra: "/Rotate 90").Build());
        Assert.Equal(new Box(0, 0, 100, 200), turned.BoundingBox);
        // Turned a quarter clockwise: the square near the lower left is now near the upper left (y up).
        var b = Bounds(((PsFill)turned.Ops[0]).Path);
        Assert.Equal((10.0, 160.0, 30.0, 30.0), (Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.W, 6), Math.Round(b.H, 6)));
        var cropped = Draw(PdfMaker.Page(Square, extra: "/CropBox [5 5 105 55]").Build());
        Assert.Equal(new Box(0, 0, 100, 50), cropped.BoundingBox);
        Assert.Equal(5, Math.Round(Bounds(((PsFill)cropped.Ops[0]).Path).X, 6));
        // Inherited from the page tree.
        var m = PdfMaker.Page(Square);
        m.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 150] /Rotate 180 >>");
        m.Add(3, "<< /Type /Page /Parent 2 0 R /Resources << >> /Contents 4 0 R >>");
        var inherited = Draw(m.Build());
        Assert.Equal(new Box(0, 0, 300, 150), inherited.BoundingBox);
        Assert.Equal(260, Math.Round(Bounds(((PsFill)inherited.Ops[0]).Path).X, 6));
    }

    [Fact]
    public void Inline_pictures_and_masked_pictures_are_drawn()
    {
        // A 2 × 1 inline picture, red then green; and a picture of blue with a soft mask half see-through.
        var inline = Draw(PdfMaker.Page("q 20 0 0 10 0 0 cm BI /W 2 /H 1 /CS /RGB /BPC 8 ID \xff\x00\x00\x00\xff\x00 EI Q").Build());
        var img = Assert.IsType<PsImage>(Assert.Single(inline.Ops));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, img.Rgba);
        var m = PdfMaker.Page("q 20 0 0 20 0 0 cm /Im Do Q", "<< /XObject << /Im 5 0 R >> >>");
        m.Add(5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R >>", "\x00\x00\xff");
        m.Add(6, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 >>", "\x80");
        var masked = Assert.IsType<PsImage>(Assert.Single(Draw(m.Build()).Ops));
        Assert.Equal(new byte[] { 0, 0, 255, 128 }, masked.Rgba);
        // A stencil (image mask) paints the fill colour where its bits are 0.
        var stencil = Draw(PdfMaker.Page("1 0 0 rg q 8 0 0 1 0 0 cm BI /W 8 /H 1 /IM true ID \x0f EI Q").Build());
        var s = Assert.IsType<PsImage>(Assert.Single(stencil.Ops));
        Assert.Equal(255, s.Rgba[3]);       // painted
        Assert.Equal(0, s.Rgba[4 * 7 + 3]); // not
    }

    [Fact]
    public void Opacity_blending_and_soft_masks_make_groups()
    {
        var res = "<< /ExtGState << /Half << /ca 0.5 /BM /Multiply >> /Masked << /SMask << /S /Luminosity /G 5 0 R >> >> >> >>";
        var m = PdfMaker.Page("/Half gs " + Square + " q /Masked gs 0 1 0 rg 0 0 200 100 re f Q", res);
        m.Add(5, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 100] /Group << /S /Transparency /CS /DeviceGray >> >>", "1 g 0 0 100 100 re f");
        var ops = Draw(m.Build()).Ops;
        Assert.Equal(2, ops.Count);
        var half = Assert.IsType<PsGroup>(ops[0]);
        Assert.Equal((0.5, "Multiply", (PsMask?)null), (half.Alpha, half.Blend, half.Mask));
        var masked = Assert.IsType<PsGroup>(ops[1]);
        Assert.Equal(0.5, masked.Alpha);  // the opacity set outside its q carries in
        Assert.True(masked.Mask is { Luminosity: true, Ops.Count: 1 });
    }

    [Fact]
    public void Optional_content_turned_off_is_left_out()
    {
        var m = PdfMaker.Page("/OC /Off BDC " + Square + " EMC /OC /On BDC 1 0 0 rg 50 10 10 10 re f EMC", "<< /Properties << /Off 5 0 R /On 6 0 R >> >>");
        m.Add(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /OFF [5 0 R] >> >> >>");
        m.Add(5, "<< /Type /OCG /Name (Notes) >>");
        m.Add(6, "<< /Type /OCG /Name (Art) >>");
        var fill = Assert.IsType<PsFill>(Assert.Single(Draw(m.Build()).Ops));
        Assert.Equal(new PsColour(1, 0, 0), fill.Colour);
    }

    [Fact]
    public void Separation_and_indexed_colours_are_turned_into_rgb()
    {
        var res = "<< /ColorSpace << /Gold [/Separation /Gold /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0.2 1 0] /N 1 >>] /Pal [/Indexed /DeviceRGB 1 5 0 R] >> >>";
        var m = PdfMaker.Page("/Gold cs 1 scn 0 0 10 10 re f /Pal cs 1 sc 20 0 10 10 re f", res);
        m.Add(5, "<< >>", "\x00\x00\x00\x10\x80\xf0");
        var ops = Draw(m.Build()).Ops;
        Assert.Equal(new PsColour(1, 0.8, 0), ((PsFill)ops[0]).Colour);
        var c = ((PsFill)ops[1]).Colour;
        Assert.Equal((16, 128, 240), ((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)));
    }

    [AvaloniaFact]
    public void Embedded_truetype_text_is_set_by_the_pdf_s_widths()
    {
        var ttf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AVAPageStream", "Assets", "Fonts", "LiberationSans-Regular.ttf"));
        // "AA" with A given a width of 1000 (wider than its own 667): the second A starts 10 points on at size 10.
        var m = PdfMaker.Page("BT /F 10 Tf 10 50 Td (AA) Tj ET", "<< /Font << /F 5 0 R >> >>");
        m.Add(5, "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+LiberationSans /FirstChar 65 /LastChar 65 /Widths [1000] /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>");
        m.Add(6, "<< /Type /FontDescriptor /FontName /ABCDEF+LiberationSans /Flags 32 /FontFile2 7 0 R >>");
        m.Add(7, "<< >>", ttf);
        var fill = Assert.IsType<PsFill>(Assert.Single(Draw(m.Build()).Ops));
        var b = Bounds(fill.Path);
        Assert.InRange(b.X, 10, 10.1);              // A's foot is at its side bearing (about 0)
        Assert.InRange(b.Right, 26.6, 26.8);        // 10 for the first A, then the second A's 6.67
        Assert.InRange(b.H, 6.8, 7);                // a capital's height at 10 points
    }

    [Fact]
    public void Type3_glyphs_are_drawn_in_the_text_s_colour_and_moved_on_by_their_widths()
    {
        var res = "<< /Font << /T 5 0 R >> >>";
        var m = PdfMaker.Page("0 1 0 rg BT /T 10 Tf 0 0 Td (aa) Tj ET", res);
        m.Add(5, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /FirstChar 97 /LastChar 97 /Widths [2] /Encoding << /Differences [97 /box] >> /CharProcs << /box 6 0 R >> >>");
        m.Add(6, "<< >>", "1 0 0 0 1 1 d1 1 0 0 rg 0 0 1 1 re f");
        var ops = Draw(m.Build()).Ops;
        Assert.Equal(2, ops.Count);
        var (a, b2) = ((PsFill)ops[0], (PsFill)ops[1]);
        Assert.Equal(new PsColour(0, 1, 0), a.Colour);                        // uncoloured (d1): its own red is ignored
        Assert.Equal(20, Math.Round(Bounds(b2.Path).X - Bounds(a.Path).X, 6)); // width 2 in a matrix of 1, at size 10
    }

    [AvaloniaFact]
    public void Text_in_a_clipping_mode_clips_what_follows()
    {
        var res = "<< /Font << /F << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        var ops = Draw(PdfMaker.Page("BT 7 Tr /F 40 Tf 10 10 Td (I) Tj ET 1 0 0 rg 0 0 200 100 re f", res).Build()).Ops;
        var red = Assert.IsType<PsFill>(Assert.Single(ops));                   // the invisible text paints nothing
        var clip = Bounds(red.Clips[^1].Path);
        Assert.InRange(clip.W, 3, 6);                                          // an I's stem
        Assert.InRange(clip.H, 27, 30);
    }

    [Fact]
    public void Annotations_that_print_are_drawn_from_their_appearances()
    {
        var m = PdfMaker.Page("", extra: "/Annots [5 0 R 7 0 R]");
        m.Add(5, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect [100 50 140 70] /AP << /N 6 0 R >> >>");
        m.Add(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 10] >>", "1 0 0 rg 0 0 20 10 re f");
        m.Add(7, "<< /Type /Annot /Subtype /Stamp /F 6 /Rect [0 0 10 10] /AP << /N 6 0 R >> >>"); // hidden
        var fill = Assert.IsType<PsFill>(Assert.Single(Draw(m.Build()).Ops));
        Assert.Equal(new Box(100, 50, 40, 20), Bounds(fill.Path));
    }

    private static IEnumerable<PsOp> All(IEnumerable<PsOp> ops) =>
        ops.SelectMany(o => o is PsGroup g ? All(g.Ops).Prepend(o) : [o]);

    [AvaloniaFact]
    public void Cairo_s_pdf_of_an_svg_is_drawn_whole()
    {
        // rsvg-convert's PDF of cairo-art.svg: gradients, a radial one fading out (a soft mask), a pattern in a clip,
        // a blur (a masked picture), and embedded TrueType text.
        var pdf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Core", "Samples", "pdf", "cairo-art.pdf"));
        var d = Draw(pdf);
        Assert.Empty(Notes);
        Assert.Equal(new Box(0, 0, 300, 225), d.BoundingBox);
        var all = All(d.Ops).ToList();
        Assert.Contains(all, o => o is PsShading { Radial: false });
        Assert.Contains(all, o => o is PsShading { Radial: true });
        Assert.Contains(all, o => o is PsGroup { Mask: not null });
        Assert.Contains(all, o => o is PsTiling);
        Assert.Contains(all, o => o is PsImage);
        Assert.Contains(all, o => o is PsFill { Colour: { R: 1, G: 1, B: 1 } } f && Bounds(f.Path).W > 100); // "Harbour Lights", in white
    }
}
