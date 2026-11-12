using AVAPageStream.Core.PostScript;
using SkiaSharp;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>Milestone 8, stage 2: JPEG 2000, against OpenJPEG's own decoding of each sample.</summary>
public class JpxTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Core", "Samples", "jpx", name);

    /// <summary>The largest difference, over every sample of every component, from OpenJPEG's decoding.</summary>
    private static int Worst(string file)
    {
        var pic = Jpx.Decode(File.ReadAllBytes(Sample(file)));
        // Read without premultiplying, so the colour of see-through pixels is kept.
        using var codec = SKCodec.Create(Sample(Path.GetFileNameWithoutExtension(file) + ".ref.png"));
        using var reference = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.Equal((reference.Width, reference.Height), (pic.Width, pic.Height));
        var grey = pic.Components.Length - (pic.AlphaChannel >= 0 ? 1 : 0) == 1;
        var worst = 0;
        for (var y = 0; y < pic.Height; y++)
            for (var x = 0; x < pic.Width; x++)
            {
                var c = reference.GetPixel(x, y);
                byte[] want = grey ? [c.Red] : [c.Red, c.Green, c.Blue];
                if (pic.AlphaChannel >= 0) want = [.. want, c.Alpha];
                for (var k = 0; k < want.Length; k++)
                    worst = Math.Max(worst, Math.Abs(pic.Components[k][y * pic.Width + x] - want[k]));
            }
        return worst;
    }

    [Theory]
    [InlineData("lossless.jp2")]            // 5/3, five levels, the colour transform, a JP2 file
    [InlineData("tiles-rpcl.j2k")]          // tiles, RPCL, precincts, 16 × 16 code-blocks
    [InlineData("styles.j2k")]              // every code-block style: bypass, reset, termination, causal, segmentation
    [InlineData("pcrl-sop-eph.j2k")]        // PCRL, SOP and EPH markers, three levels
    [InlineData("rlcp.j2k")]                // RLCP, one level
    [InlineData("roi.j2k")]                 // a region of interest, by maximum shift
    [InlineData("grey.jp2")]                // one component
    [InlineData("alpha.jp2")]               // four, the last its opacity
    public void A_lossless_picture_is_decoded_exactly(string file) => Assert.Equal(0, Worst(file));

    /// <remarks>
    /// OpenJPEG computes the 9/7 wavelet in single-precision floats, AVAPageStream in doubles: a level or two apart,
    /// and in the three-layer sample, at a few dozen edge pixels of 57,600, up to four in blue (which is Y plus
    /// 1.772 times Cb, so it magnifies the difference most).
    /// </remarks>
    [Theory]
    [InlineData("lossy.j2k", 2)]            // 9/7, the irreversible colour transform, a fifth of the bits
    [InlineData("cprl-layers.j2k", 4)]      // CPRL, three quality layers
    public void A_lossy_picture_is_decoded_as_openjpeg_does(string file, int within) => Assert.InRange(Worst(file), 0, within);
}
