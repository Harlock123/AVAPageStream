namespace AVAPageStream.Core.PostScript;

public sealed partial class Interpreter
{
    private void RegisterShading()
    {
        Op("shfill", i => i.Shade(i.Pop<PsDict>("shfill"), PsMatrix.Identity.Then(i.G.Ctm), null, false));
        Op("makepattern", i =>
        {
            var m = ToMatrix(i.Pop<PsArray>("makepattern"));
            var d = i.Pop<PsDict>("makepattern");
            var type = d.Get("PatternType") is { } t ? (int)ToNum(t) : 0;
            if (type is not (1 or 2)) throw new PsError("unsupported", "patterns of type " + type);
            var made = new PsDict();
            foreach (var (k, v) in d.Entries) made.Put(k, v);
            if (type == 1) made.Put(TileKey, i.PaintCell(d));
            // The pattern's space, fixed now: its matrix, then the current transformation.
            made.Put("Implementation", NewMatrix(m.Then(i.G.Ctm)));
            i.Push(made);
        });
        Op("setpattern", i =>
        {
            var p = i.Pop<PsDict>("setpattern");
            i.SetSpace(new PsName("Pattern"));
            i.G.Pattern = p;
        });
    }

    private void FillWithPattern(PsPath area, bool evenOdd)
    {
        var p = G.Pattern ?? throw new PsError("undefined", "no pattern set");
        var m = p.Get("Implementation") is PsArray a ? ToMatrix(a) : G.Ctm;
        if (p.Get(TileKey) is List<PsOp> cell)
        {
            double N(string key) => p.Get(key) is { } v ? ToNum(v) : 0;
            var bbox = (p.Get("BBox") as PsArray)?.All().Select(ToNum).ToArray() ?? [0, 0, 1, 1];
            var uncoloured = p.Get("PaintType") is { } pt && (int)ToNum(pt) == 2;
            Ops.Add(new PsTiling(cell, new Box(bbox[0], bbox[1], bbox[2] - bbox[0], bbox[3] - bbox[1]), N("XStep"), N("YStep"), m,
                uncoloured ? G.PatternColour : null, area, evenOdd, [.. G.Clips]));
            return;
        }
        if (p.Get("Shading") is not PsDict shading) throw new PsError("undefined", "the pattern's shading");
        Shade(shading, m, area, evenOdd);
    }

    /// <summary>An axial or radial shading, its colours sampled along it, filling the clip (or a pattern's area).</summary>
    private void Shade(PsDict d, PsMatrix matrix, PsPath? area, bool evenOdd)
    {
        var type = d.Get("ShadingType") is { } t ? (int)ToNum(t) : 0;
        if (type is 1 or 4 or 5 or 6 or 7) { Mesh(d, type, matrix, area, evenOdd); return; }
        if (type is not (2 or 3)) throw new PsError("unsupported", "shading of type " + type);
        var coords = (d.Get("Coords") as PsArray ?? throw new PsError("undefined", "Coords")).All().Select(ToNum).ToArray();
        if (coords.Length != (type == 2 ? 4 : 6)) throw new PsError("rangecheck", "Coords");
        var domain = (d.Get("Domain") as PsArray)?.All().Select(ToNum).ToArray() ?? [0, 1];
        var extend = (d.Get("Extend") as PsArray)?.All().Select(o => o is true).ToArray() ?? [false, false];
        var spaceObj = d.Get("ColorSpace") ?? throw new PsError("undefined", "ColorSpace");
        var keep = (G.Space, G.SpaceArray, G.Components, G.Colour);
        SetSpace(spaceObj);
        var (space, spaceArray) = (G.Space, G.SpaceArray);
        (G.Space, G.SpaceArray, G.Components, G.Colour) = keep;
        var function = d.Get("Function") ?? throw new PsError("undefined", "Function");
        const int steps = 64;
        var colours = new PsColour[steps + 1];
        var positions = new double[steps + 1];
        for (var k = 0; k <= steps; k++)
        {
            var s = k / (double)steps;
            var input = domain[0] + s * (domain[1] - domain[0]);
            positions[k] = s;
            colours[k] = ToRgb(space, spaceArray, Evaluate(function, input));
        }
        Ops.Add(new PsShading(type == 3, coords, colours, positions, extend.Length > 0 && extend[0], extend.Length > 1 && extend[1], matrix, area, evenOdd, [.. G.Clips]));
    }

    /// <summary>A function of one input, as shadings use: sampled (0), exponential (2), stitching (3) or PostScript (4); or an array of them, one per output.</summary>
    private double[] Evaluate(object function, double x)
    {
        if (function is PsArray many && !many.Exec) return many.All().SelectMany(f => Evaluate(f, x)).ToArray();
        if (function is not PsDict f) throw new PsError("typecheck", "function");
        double[] Get(string key, double[] otherwise) => (f.Get(key) as PsArray)?.All().Select(ToNum).ToArray() ?? otherwise;
        var domain = Get("Domain", [0, 1]);
        x = Math.Clamp(x, domain[0], domain.Length > 1 ? domain[1] : domain[0]);
        var type = f.Get("FunctionType") is { } t ? (int)ToNum(t) : -1;
        double[] result;
        switch (type)
        {
            case 2:
            {
                var c0 = Get("C0", [0]);
                var c1 = Get("C1", [1]);
                var n = f.Get("N") is { } nn ? ToNum(nn) : 1;
                var xn = Math.Pow(x, n);
                result = c0.Select((c, k) => c + xn * ((k < c1.Length ? c1[k] : 1) - c)).ToArray();
                break;
            }
            case 3:
            {
                var funcs = f.Get("Functions") as PsArray ?? throw new PsError("undefined", "Functions");
                var bounds = Get("Bounds", []);
                var encode = Get("Encode", []);
                var k = 0;
                while (k < bounds.Length && x >= bounds[k]) k++;
                var lo = k == 0 ? domain[0] : bounds[k - 1];
                var hi = k == bounds.Length ? domain[1] : bounds[k];
                var (e0, e1) = encode.Length >= 2 * k + 2 ? (encode[2 * k], encode[2 * k + 1]) : (0.0, 1.0);
                var y = hi == lo ? e0 : e0 + (x - lo) * (e1 - e0) / (hi - lo);
                result = Evaluate(funcs[Math.Min(k, funcs.Length - 1)], y);
                break;
            }
            case 0:
            {
                var size = Get("Size", [2]);
                var bps = f.Get("BitsPerSample") is { } b ? (int)ToNum(b) : 8;
                var range = Get("Range", [0, 1]);
                var outputs = range.Length / 2;
                var encode = Get("Encode", [0, size[0] - 1]);
                var decode = Get("Decode", range);
                var data = f.Get("DataSource") switch
                {
                    PsString s => s.Span.ToArray(),
                    PsFile file => ReadData(file, (int)size[0] * outputs * bps / 8),
                    _ => throw new PsError("typecheck", "DataSource"),
                };
                var e = encode[0] + (x - domain[0]) * (encode[1] - encode[0]) / (domain[1] - domain[0]);
                e = Math.Clamp(e, 0, size[0] - 1);
                var (i0, frac) = ((int)Math.Floor(e), e - Math.Floor(e));
                var i1 = Math.Min(i0 + 1, (int)size[0] - 1);
                var max = (1 << bps) - 1;
                double S(int index, int o)
                {
                    var bit = (index * outputs + o) * bps;
                    if (bps == 8) return bit / 8 < data.Length ? data[bit / 8] : 0;
                    if (bps == 16) return bit / 8 + 1 < data.Length ? data[bit / 8] << 8 | data[bit / 8 + 1] : 0;
                    var by = bit / 8 < data.Length ? data[bit / 8] : 0;
                    return (by >> (8 - bps - bit % 8)) & max;
                }
                result = new double[outputs];
                for (var o = 0; o < outputs; o++)
                {
                    var s = S(i0, o) + frac * (S(i1, o) - S(i0, o));
                    result[o] = decode[2 * o] + s * (decode[2 * o + 1] - decode[2 * o]) / max;
                }
                break;
            }
            case 4:
            {
                var proc = f.Get("Function") as PsArray ?? throw new PsError("unsupported", "a PostScript function from a stream");
                var range = Get("Range", []);
                var before = _stack.Count;
                Push(x);
                RunProc(proc);
                result = _stack.Skip(before).Select(ToNum).ToArray();
                _stack.RemoveRange(before, _stack.Count - before);
                if (range.Length >= 2 * result.Length)
                    for (var k = 0; k < result.Length; k++) result[k] = Math.Clamp(result[k], range[2 * k], range[2 * k + 1]);
                break;
            }
            default:
                throw new PsError("unsupported", "functions of type " + type);
        }
        if (f.Get("Range") is PsArray r && type != 4)
            for (var k = 0; k < result.Length && 2 * k + 1 < r.Length; k++) result[k] = Math.Clamp(result[k], ToNum(r[2 * k]), ToNum(r[2 * k + 1]));
        return result;
    }
}
