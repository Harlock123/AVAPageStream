using AVAPageStream.Core;
using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core;

/// <summary>The small fixes and the earlier milestones' limits: Symbol and Dingbats, and CMYK PDFs.</summary>
public class FixesTests
{
    [Fact]
    public void The_symbol_and_dingbats_fonts_give_their_own_characters()
    {
        var asked = new List<int>();
        SystemGlyph? Record(string family, bool bold, bool italic, int c) { asked.Add(c); return new SystemGlyph(new PsPath(), 500); }
        var i = new Interpreter { Glyphs = Record };
        Assert.Null(i.Run("/Symbol findfont 10 scalefont setfont 0 0 moveto (abp\\245) show /ZapfDingbats findfont 10 scalefont setfont (H\\254) show"));
        Assert.Equal([0x3B1, 0x3B2, 0x3C0, 0x221E, 0x2605, 0x2460], asked); // α β π ∞ ★ ①
        Assert.Equal(0x2212, Encodings.Symbol(45)); // the Symbol font's minus
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0.2, 0.5, 0.9)]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    public void Rgb_turns_into_inks_that_turn_back_into_it(double r, double g, double b)
    {
        var (c, m, y, k) = PdfColour.Plain(r, g, b);
        // As a PDF reader shows CMYK: each of red, green and blue is one less its ink and the black.
        Assert.Equal(r, 1 - Math.Min(1, c + k), 9);
        Assert.Equal(g, 1 - Math.Min(1, m + k), 9);
        Assert.Equal(b, 1 - Math.Min(1, y + k), 9);
        // The same, as the PostScript a gradient's function is given.
        var i = new Interpreter();
        Assert.Null(i.Run(FormattableString.Invariant($"{r} {g} {b}") + PdfColour.CalculatorToCmyk));
        Assert.Equal(new[] { c, m, y, k }.Select(v => Math.Round(v, 6)), i.Stack.Select(o => Math.Round(Interpreter.ToNum(o), 6)));
    }

    [Fact]
    public void A_documents_swatches_give_their_colours_inks()
    {
        var e = new Editor(Document.New());
        Assert.Null(e.AddSwatch("Navy", "100 80 0 20", spot: false));
        Assert.Null(e.AddSwatch("Gold", "0 20 100 0", spot: true));
        e.Add(SheetRef.PageAt(0), new RectangleObject { Fill = "@Navy", Stroke = "@Gold 50%" });
        var inks = PdfColour.InksOf(e.Doc);
        var navy = Colours.Resolve("@Navy", e.Doc.Swatches)!;
        Colours.TryParse(navy, out var n);
        Assert.Equal(new Ink(1, 0.8, 0, 0.2), inks[(n.R, n.G, n.B)]);
        Colours.TryParse(Colours.Resolve("@Gold 50%", e.Doc.Swatches)!, out var g);
        var gold = inks[(g.R, g.G, g.B)];
        Assert.Equal(("Gold", 0.5), (gold.Spot, gold.Tint));
    }
}
