namespace AVAPageStream.Core.PostScript;

public sealed partial class Interpreter
{
    /// <summary>
    /// The CIE-based colour spaces: components through their decode procedures and matrices to CIE XYZ, then (adapted
    /// from the space's white point to D65) to sRGB. DEF and DEFG look their ABC up in their table first.
    /// </summary>
    private PsColour Cie(string space, PsArray array, double[] c)
    {
        var d = array.Length > 1 && array[1] is PsDict dict ? dict : throw new PsError("typecheck", space);
        double[] Nums(string key, double[] otherwise) => (d.Get(key) as PsArray)?.All().Select(ToNum).ToArray() ?? otherwise;
        double Run(object? proc, double v)
        {
            if (proc is not PsArray { Exec: true } p) return v;
            Push(v);
            RunProc(p);
            return Num();
        }
        double[] Decode(string key, double[] values)
        {
            var procs = d.Get(key);
            var r = (double[])values.Clone();
            for (var k = 0; k < r.Length; k++) r[k] = Run(procs is PsArray { Exec: false } a && k < a.Length ? a[k] : procs is PsArray { Exec: true } ? procs : null, r[k]);
            return r;
        }
        static double[] Times(double[] m, double[] v) => [m[0] * v[0] + m[3] * v[1] + m[6] * v[2], m[1] * v[0] + m[4] * v[1] + m[7] * v[2], m[2] * v[0] + m[5] * v[1] + m[8] * v[2]];
        static double Clamp(double v, double[] range, int k) => range.Length >= 2 * k + 2 ? Math.Clamp(v, range[2 * k], range[2 * k + 1]) : v;

        double[] abc;
        if (space is "CIEBasedDEF" or "CIEBasedDEFG")
        {
            var n = space == "CIEBasedDEF" ? 3 : 4;
            var def = c.Take(n).Select((v, k) => Clamp(v, Nums(n == 3 ? "RangeDEF" : "RangeDEFG", []), k)).ToArray();
            var hij = Decode(n == 3 ? "DecodeDEF" : "DecodeDEFG", def);
            var rangeHij = Nums(n == 3 ? "RangeHIJ" : "RangeHIJK", Enumerable.Range(0, n).SelectMany(_ => new double[] { 0, 1 }).ToArray());
            var table = d.Get("Table") as PsArray ?? throw new PsError("undefined", "Table");
            var sizes = Enumerable.Range(0, n).Select(k => (int)ToNum(table[k])).ToArray();
            int Index(int k) => (int)Math.Round(Math.Clamp((hij[k] - rangeHij[2 * k]) / Math.Max(1e-9, rangeHij[2 * k + 1] - rangeHij[2 * k]), 0, 1) * (sizes[k] - 1));
            PsString entry;
            int offset;
            if (n == 3)
            {
                entry = (PsString)((PsArray)table[3])[Index(0)];
                offset = 3 * (Index(1) * sizes[2] + Index(2));
            }
            else
            {
                entry = (PsString)((PsArray)((PsArray)table[4])[Index(0)])[Index(1)];
                offset = 3 * (Index(2) * sizes[3] + Index(3));
            }
            var rangeAbc = Nums("RangeABC", [0, 1, 0, 1, 0, 1]);
            abc = Enumerable.Range(0, 3).Select(k => rangeAbc[2 * k] + entry[offset + k] / 255.0 * (rangeAbc[2 * k + 1] - rangeAbc[2 * k])).ToArray();
            space = "CIEBasedABC";
        }
        else abc = c;

        double[] lmn;
        if (space == "CIEBasedA")
        {
            var a = Clamp(c.Length > 0 ? c[0] : 0, Nums("RangeA", [0, 1]), 0);
            a = Run(d.Get("DecodeA"), a);
            var m = Nums("MatrixA", [1, 1, 1]);
            lmn = [a * m[0], a * m[1], a * m[2]];
        }
        else
        {
            var v = abc.Take(3).Select((x, k) => Clamp(x, Nums("RangeABC", []), k)).ToArray();
            v = Decode("DecodeABC", v);
            lmn = Times(Nums("MatrixABC", [1, 0, 0, 0, 1, 0, 0, 0, 1]), v);
        }
        lmn = lmn.Select((x, k) => Clamp(x, Nums("RangeLMN", []), k)).ToArray();
        lmn = Decode("DecodeLMN", lmn);
        var xyz = Times(Nums("MatrixLMN", [1, 0, 0, 0, 1, 0, 0, 0, 1]), lmn);
        var white = Nums("WhitePoint", [0.9505, 1, 1.089]);
        var (x0, y0, z0) = (xyz[0] * 0.9505 / white[0], xyz[1] / white[1], xyz[2] * 1.089 / white[2]);
        double Gamma(double l) => Math.Clamp(l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055, 0, 1);
        return new PsColour(
            Gamma(3.2406 * x0 - 1.5372 * y0 - 0.4986 * z0),
            Gamma(-0.9689 * x0 + 1.8758 * y0 + 0.0415 * z0),
            Gamma(0.0557 * x0 - 0.2040 * y0 + 1.0570 * z0));
    }
}
