using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>Milestone 8, stage 4: PDF/X-4's output profile. (The export itself: WindowTests' A_pdf_x4_….)</summary>
public class PdfXTests
{
    /// <summary>Set AVAPS_ICC_OUT to a path to have the profile written there, to check with LittleCMS or another CMM.</summary>
    [Fact]
    public void Write_profile_for_checking()
    {
        if (Environment.GetEnvironmentVariable("AVAPS_ICC_OUT") is { } path) File.WriteAllBytes(path, IccCmyk.Profile);
    }

    [Fact]
    public void The_profile_is_a_cmyk_output_profile_with_the_tags_one_needs()
    {
        var p = IccCmyk.Profile;
        int U32(int at) => p[at] << 24 | p[at + 1] << 16 | p[at + 2] << 8 | p[at + 3];
        string Sig(int at) => System.Text.Encoding.ASCII.GetString(p, at, 4);
        Assert.Equal(p.Length, U32(0));
        Assert.Equal(("prtr", "CMYK", "Lab ", "acsp"), (Sig(12), Sig(16), Sig(20), Sig(36)));
        Assert.Equal(0x02400000, U32(8));
        var tags = Enumerable.Range(0, U32(128)).Select(i => Sig(132 + i * 12)).ToHashSet();
        Assert.Superset(new HashSet<string> { "desc", "cprt", "wtpt", "A2B0", "A2B1", "A2B2", "B2A0", "B2A1", "B2A2", "gamt" }, tags);
        Assert.True(p.Length < 120_000, $"{p.Length} bytes");
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 0.6, 0.9, 0)]
    [InlineData(0, 0, 0, 0.5)]
    public void Its_conversions_go_through_lab_and_back(double c, double m, double y, double k)
    {
        var (l, a, b) = IccCmyk.ToLab(c, m, y, k);
        var back = IccCmyk.FromLab(l, a, b);
        // Back as the plain conversion makes it from the same colour (black taken out of the three inks).
        var expected = PdfColour.Plain((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
        Assert.Equal(expected.C, back.C, 3);
        Assert.Equal(expected.M, back.M, 3);
        Assert.Equal(expected.Y, back.Y, 3);
        Assert.Equal(expected.K, back.K, 3);
    }
}
