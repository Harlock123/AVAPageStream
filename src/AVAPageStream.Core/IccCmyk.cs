using System.Text;

namespace AVAPageStream.Core;

/// <summary>
/// An ICC (version 2) output profile for AVAPageStream's own CMYK: the plain conversion it makes colours by (each of
/// cyan, magenta and yellow what its opposite and the black leave of white, as sRGB), described both ways through
/// CIELAB at D50, so a PDF/X's output intent says exactly how its colours were made.
/// </summary>
public static class IccCmyk
{
    public const string Description = "AVAPageStream plain CMYK";

    private static readonly Lazy<byte[]> Made = new(Make);

    public static byte[] Profile => Made.Value;

    /// <summary>CMYK (0 to 1) to CIELAB, through sRGB.</summary>
    public static (double L, double A, double B) ToLab(double c, double m, double y, double k)
    {
        var black = 1 - k;
        return RgbToLab((1 - c) * black, (1 - m) * black, (1 - y) * black);
    }

    /// <summary>CIELAB to CMYK, through sRGB (clamped to it) and the plain conversion.</summary>
    public static (double C, double M, double Y, double K) FromLab(double l, double a, double b)
    {
        var (r, g, bl) = LabToRgb(l, a, b);
        return PdfColour.Plain(r, g, bl);
    }

    private static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    private static double Gamma(double v) => Math.Clamp(v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055, 0, 1);

    // sRGB to XYZ at D50 (Bradford-adapted, as ICC's PCS is), and back.
    private static readonly double[,] ToXyz = { { 0.4360747, 0.3850649, 0.1430804 }, { 0.2225045, 0.7168786, 0.0606169 }, { 0.0139322, 0.0971045, 0.7141733 } };
    private static readonly double[,] FromXyz = { { 3.1338561, -1.6168667, -0.4906146 }, { -0.9787684, 1.9161415, 0.0334540 }, { 0.0719453, -0.2289914, 1.4052427 } };
    private const double Xn = 0.9642, Yn = 1.0, Zn = 0.8249;

    private static (double, double, double) RgbToLab(double r, double g, double b)
    {
        var (lr, lg, lb) = (Linear(r), Linear(g), Linear(b));
        var x = ToXyz[0, 0] * lr + ToXyz[0, 1] * lg + ToXyz[0, 2] * lb;
        var y = ToXyz[1, 0] * lr + ToXyz[1, 1] * lg + ToXyz[1, 2] * lb;
        var z = ToXyz[2, 0] * lr + ToXyz[2, 1] * lg + ToXyz[2, 2] * lb;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        var (fx, fy, fz) = (F(x / Xn), F(y / Yn), F(z / Zn));
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static (double, double, double) LabToRgb(double l, double a, double b)
    {
        var fy = (l + 16) / 116;
        var (fx, fz) = (fy + a / 500, fy - b / 200);
        static double G(double f) => f * f * f > 216.0 / 24389 ? f * f * f : (116 * f - 16) * 27 / 24389;
        var (x, y, z) = (G(fx) * Xn, G(fy) * Yn, G(fz) * Zn);
        var r = FromXyz[0, 0] * x + FromXyz[0, 1] * y + FromXyz[0, 2] * z;
        var g = FromXyz[1, 0] * x + FromXyz[1, 1] * y + FromXyz[1, 2] * z;
        var bl = FromXyz[2, 0] * x + FromXyz[2, 1] * y + FromXyz[2, 2] * z;
        return (Gamma(Math.Clamp(r, 0, 1)), Gamma(Math.Clamp(g, 0, 1)), Gamma(Math.Clamp(bl, 0, 1)));
    }

    // Version 2's 16-bit Lab: L from 0 to 0xFF00 for 0 to 100; a and b with 0x8000 for 0, 256 a unit.
    private static ushort EncL(double l) => (ushort)Math.Clamp(Math.Round(l * 652.8), 0, 0xFFFF);
    private static ushort EncAb(double v) => (ushort)Math.Clamp(Math.Round((v + 128) * 256), 0, 0xFFFF);
    private static double DecL(int v) => v / 652.8;
    private static double DecAb(int v) => v / 256.0 - 128;

    private static byte[] Make()
    {
        // The tags' data; the A-to-B and B-to-A tables shared by the three intents.
        var a2b = Lut16(4, 3, 9, input =>
        {
            var (l, a, b) = ToLab(input[0], input[1], input[2], input[3]);
            return [EncL(l), EncAb(a), EncAb(b)];
        });
        var b2a = Lut16(3, 4, 17, input =>
        {
            var (c, m, y, k) = FromLab(DecL((int)Math.Round(input[0] * 65535)), DecAb((int)Math.Round(input[1] * 65535)), DecAb((int)Math.Round(input[2] * 65535)));
            return [(ushort)Math.Round(c * 65535), (ushort)Math.Round(m * 65535), (ushort)Math.Round(y * 65535), (ushort)Math.Round(k * 65535)];
        });
        var gamut = Lut16(3, 1, 2, _ => [0]); // everything in gamut
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Desc(Description)),
            ("cprt", Text("No copyright: made by AVAPageStream, free to use")),
            ("wtpt", Xyz(Xn, Yn, Zn)),
            ("A2B0", a2b), ("A2B1", a2b), ("A2B2", a2b),
            ("B2A0", b2a), ("B2A1", b2a), ("B2A2", b2a),
            ("gamt", gamut),
        };
        var table = 4 + tags.Count * 12;
        var o = new List<byte>(new byte[128 + table]);
        var placed = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        var entries = new List<(string, int, int)>();
        foreach (var (sig, data) in tags)
        {
            if (!placed.TryGetValue(data, out var at))
            {
                while (o.Count % 4 != 0) o.Add(0);
                at = o.Count;
                o.AddRange(data);
                placed[data] = at;
            }
            entries.Add((sig, at, data.Length));
        }
        while (o.Count % 4 != 0) o.Add(0);
        var b = o.ToArray();
        void U32(int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        void Sig(int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b, at);
        U32(0, (uint)b.Length);
        U32(8, 0x02400000);       // version 2.4
        Sig(12, "prtr");          // an output profile
        Sig(16, "CMYK");
        Sig(20, "Lab ");
        // Its date: fixed, so the profile (and the PDFs using it) are the same each time.
        int[] date = [2026, 11, 12, 0, 0, 0];
        for (var i = 0; i < 6; i++) { b[24 + i * 2] = (byte)(date[i] >> 8); b[25 + i * 2] = (byte)date[i]; }
        Sig(36, "acsp");
        U32(64, 0);               // perceptual
        U32(68, S15(Xn)); U32(72, S15(Yn)); U32(76, S15(Zn));
        U32(128, (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            Sig(132 + i * 12, entries[i].Item1);
            U32(136 + i * 12, (uint)entries[i].Item2);
            U32(140 + i * 12, (uint)entries[i].Item3);
        }
        return b;
    }

    private static uint S15(double v) => (uint)(int)Math.Round(v * 65536);

    private static byte[] Be(params uint[] v) => v.SelectMany(x => new[] { (byte)(x >> 24), (byte)(x >> 16), (byte)(x >> 8), (byte)x }).ToArray();

    private static byte[] Xyz(double x, double y, double z) => [.. "XYZ "u8.ToArray(), 0, 0, 0, 0, .. Be(S15(x), S15(y), S15(z))];

    private static byte[] Text(string s) => [.. "text"u8.ToArray(), 0, 0, 0, 0, .. Encoding.ASCII.GetBytes(s), 0];

    /// <summary>textDescriptionType: the ASCII description, and empty Unicode and Macintosh ones.</summary>
    private static byte[] Desc(string s)
    {
        var ascii = Encoding.ASCII.GetBytes(s + "\0");
        return [.. "desc"u8.ToArray(), 0, 0, 0, 0, .. Be((uint)ascii.Length), .. ascii, .. Be(0, 0), 0, 0, 0, .. new byte[67]];
    }

    /// <summary>
    /// lut16Type: an identity matrix, linear input and output tables of two entries, and a grid of so many points a
    /// side, each from <paramref name="at"/> (its inputs from 0 to 1, first the slowest varying).
    /// </summary>
    private static byte[] Lut16(int inputs, int outputs, int grid, Func<double[], ushort[]> at)
    {
        var o = new List<byte>("mft2"u8.ToArray()) { 0, 0, 0, 0, (byte)inputs, (byte)outputs, (byte)grid, 0 };
        foreach (var v in new[] { 1.0, 0, 0, 0, 1, 0, 0, 0, 1 }) o.AddRange(Be(S15(v)));
        o.AddRange([0, 2, 0, 2]); // two entries in each input and output table
        for (var i = 0; i < inputs; i++) o.AddRange([0, 0, 0xFF, 0xFF]);
        var points = (int)Math.Pow(grid, inputs);
        var input = new double[inputs];
        for (var p = 0; p < points; p++)
        {
            var rest = p;
            for (var i = inputs - 1; i >= 0; i--)
            {
                input[i] = (rest % grid) / (double)(grid - 1);
                rest /= grid;
            }
            foreach (var v in at(input)) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
        }
        for (var i = 0; i < outputs; i++) o.AddRange([0, 0, 0xFF, 0xFF]);
        return [.. o];
    }
}
