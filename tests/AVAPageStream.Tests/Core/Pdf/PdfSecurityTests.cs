using System.Diagnostics;
using AVAPageStream.Core;
using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>Milestone 8, stage 1: protected PDFs.</summary>
public class PdfSecurityTests
{
    /// <summary>
    /// A page painting a square in the second colour of an indexed colour space, whose table is a string in an object:
    /// so both a stream (the page's content) and a string must be decrypted for it to come out right.
    /// </summary>
    private static PdfMaker Page() => PdfMaker.Page("/Pal cs 1 sc 10 10 30 30 re f",
        "<< /ColorSpace << /Pal [/Indexed /DeviceRGB 1 (ABC\x10\x80\xf0)] >> >>");

    private static readonly PsColour Expected = new(0x10 / 255.0, 0x80 / 255.0, 0xF0 / 255.0);

    /// <summary>What Poppler draws in the middle of the square, if it is installed (null if not): an independent check.</summary>
    private static (int R, int G, int B)? Poppler(byte[] pdf, string? password = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "avaps-crypt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "in.pdf");
            File.WriteAllBytes(file, pdf);
            var args = new List<string> { "-r", "72", "-png", "-singlefile" };
            if (password is not null) args.AddRange(["-upw", password]);
            args.AddRange([file, Path.Combine(dir, "out")]);
            Process? p;
            try { p = Process.Start(new ProcessStartInfo("pdftoppm", args) { RedirectStandardError = true }); }
            catch (System.ComponentModel.Win32Exception) { return null; }
            p!.WaitForExit(20000);
            using var bmp = SkiaSharp.SKBitmap.Decode(Path.Combine(dir, "out.png"));
            if (bmp is null) return (-1, -1, -1);
            var c = bmp.GetPixel(25, 100 - 25);
            return (c.Red, c.Green, c.Blue);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void A_pdf_protected_without_a_user_password_opens_as_any_other(int revision)
    {
        var pdf = Page().Build(crypt: new PdfCrypt(revision));
        Assert.Contains("/Encrypt", System.Text.Encoding.Latin1.GetString(pdf));
        if (Poppler(pdf) is { } seen) Assert.Equal((16, 128, 240), seen);
        Assert.True(new PdfReader(pdf).Protected);
        var d = PdfPages.Draw(pdf, 1, out var why, out _);
        Assert.True(d is not null, why);
        Assert.Equal(Expected, Assert.IsType<PsFill>(Assert.Single(d!.Ops)).Colour);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void A_pdf_with_a_password_needs_it_or_its_owner_s(int revision)
    {
        var pdf = Page().Build(crypt: new PdfCrypt(revision, user: "harbour", owner: "keeper"));
        if (Poppler(pdf, "harbour") is { } seen) Assert.Equal((16, 128, 240), seen);
        Assert.False(Assert.Throws<PdfPasswordException>(() => new PdfReader(pdf)).Wrong);
        Assert.True(Assert.Throws<PdfPasswordException>(() => new PdfReader(pdf, "lighthouse")).Wrong);
        foreach (var password in new[] { "harbour", "keeper" })
        {
            var d = PdfPages.Draw(pdf, 1, out var why, out _, password);
            Assert.True(d is not null, why);
            Assert.Equal(Expected, ((PsFill)d!.Ops[0]).Colour);
        }
        Assert.Null(PdfPages.Draw(pdf, 1, out var none, out _));
        Assert.Equal("it is protected by a password", none);
    }
}
