namespace AVAPageStream.Core.PostScript;

public sealed partial class Interpreter
{
    private const string TileKey = "AVAPageStream.Tile";

    /// <summary>
    /// A tiling pattern's cell: its PaintProc run once, in pattern space with nothing clipped, what it paints kept
    /// (not drawn) to be repeated wherever the pattern fills.
    /// </summary>
    private List<PsOp> PaintCell(PsDict pattern)
    {
        var proc = pattern.Get("PaintProc") as PsArray ?? throw new PsError("undefined", "PaintProc");
        var start = Ops.Count;
        Gsave();
        try
        {
            G.Ctm = PsMatrix.Identity;
            G.Clips = [];
            NewPath();
            Push(pattern);
            RunProc(proc);
        }
        finally
        {
            GrestoreOne();
        }
        var cell = Ops.GetRange(start, Ops.Count - start);
        Ops.RemoveRange(start, Ops.Count - start);
        return cell;
    }

    /// <summary>
    /// The shadings made of triangles and patches: function-based (1), free-form (4) and lattice (5) triangle meshes,
    /// and Coons (6) and tensor-product (7) patches, each turned into triangles with their corners' colours.
    /// </summary>
    private void Mesh(PsDict d, int type, PsMatrix matrix, PsPath? area, bool evenOdd)
    {
        var spaceObj = d.Get("ColorSpace") ?? throw new PsError("undefined", "ColorSpace");
        var keep = (G.Space, G.SpaceArray, G.Components, G.Colour);
        SetSpace(spaceObj);
        var (space, spaceArray, comps) = (G.Space, G.SpaceArray, G.Components.Length);
        (G.Space, G.SpaceArray, G.Components, G.Colour) = keep;
        var function = d.Get("Function");
        PsColour Colour(double[] c) => ToRgb(space, spaceArray, function is null ? c : EvaluateN(function, c));
        var points = new List<double>();
        var colours = new List<PsColour>();
        void Tri((double X, double Y, PsColour C) a, (double X, double Y, PsColour C) b, (double X, double Y, PsColour C) c)
        {
            foreach (var v in new[] { a, b, c })
            {
                var (x, y) = matrix.Apply(v.X, v.Y);
                points.Add(x);
                points.Add(y);
                colours.Add(v.C);
            }
        }

        if (type == 1)
        {
            // A function of x and y over its domain, sampled on a grid through the shading's own matrix.
            var domain = (d.Get("Domain") as PsArray)?.All().Select(ToNum).ToArray() ?? [0, 1, 0, 1];
            var m = d.Get("Matrix") is PsArray ma ? ToMatrix(ma) : PsMatrix.Identity;
            const int n = 32;
            (double, double, PsColour) At(int i, int j)
            {
                var x = domain[0] + (domain[1] - domain[0]) * i / n;
                var y = domain[2] + (domain[3] - domain[2]) * j / n;
                var (px, py) = m.Apply(x, y);
                return (px, py, ToRgb(space, spaceArray, EvaluateN(function ?? throw new PsError("undefined", "Function"), [x, y])));
            }
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    var (a, b, c, e) = (At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j + 1));
                    Tri(a, b, c);
                    Tri(a, c, e);
                }
        }
        else
        {
            var reader = new MeshReader(this, d, function is null ? comps : 1, type);
            if (type == 4)
            {
                (double, double, PsColour)? a = null, b = null, c = null;
                while (reader.Vertex(out var flag, out var x, out var y, out var col))
                {
                    var v = (x, y, Colour(col));
                    switch (flag)
                    {
                        case 0:
                            // A new triangle: this vertex and the next two.
                            if (!reader.Vertex(out _, out var x2, out var y2, out var c2) || !reader.Vertex(out _, out var x3, out var y3, out var c3)) break;
                            (a, b, c) = (v, (x2, y2, Colour(c2)), (x3, y3, Colour(c3)));
                            break;
                        case 1: (a, b, c) = (b, c, v); break;
                        default: (b, c) = (c, v); break; // the first vertex kept, the last two moved on
                    }
                    if (a is { } p1 && b is { } p2 && c is { } p3) Tri(p1, p2, p3);
                }
            }
            else if (type == 5)
            {
                var perRow = d.Get("VerticesPerRow") is { } vr ? (int)ToNum(vr) : throw new PsError("undefined", "VerticesPerRow");
                var all = new List<(double, double, PsColour)>();
                while (reader.Vertex(out _, out var x, out var y, out var col)) all.Add((x, y, Colour(col)));
                for (var r = 0; r + 1 < all.Count / perRow; r++)
                    for (var k = 0; k + 1 < perRow; k++)
                    {
                        var (p, q, s, t) = (all[r * perRow + k], all[r * perRow + k + 1], all[(r + 1) * perRow + k + 1], all[(r + 1) * perRow + k]);
                        Tri(p, q, s);
                        Tri(p, s, t);
                    }
            }
            else
            {
                // Patches: twelve points round the edge (sixteen for type 7; its inner four are left to the edges),
                // and a colour at each corner. A patch after the first may share an edge of the one before.
                var pts = new (double X, double Y)[12];
                var cols = new double[4][];
                var first = true;
                while (reader.Patch(out var flag, type == 7 ? 16 : 12, out var read, out var readColours))
                {
                    var (np, nc) = (new (double, double)[12], new double[4][]);
                    if (flag == 0 || first)
                    {
                        for (var k = 0; k < 12 && k < read.Count; k++) np[k] = read[k];
                        for (var k = 0; k < 4 && k < readColours.Count; k++) nc[k] = readColours[k];
                    }
                    else
                    {
                        var (e0, c0) = flag switch { 1 => (3, 1), 2 => (6, 2), _ => (9, 3) };
                        for (var k = 0; k < 4; k++) np[k] = pts[(e0 + k) % 12];
                        for (var k = 0; k < 8 && k < read.Count; k++) np[4 + k] = read[k];
                        (nc[0], nc[1]) = (cols[c0], cols[(c0 + 1) % 4]);
                        for (var k = 0; k < 2 && k < readColours.Count; k++) nc[2 + k] = readColours[k];
                    }
                    (pts, cols, first) = (np, nc, false);
                    if (cols.Any(c => c is null)) continue;
                    Coons(pts, cols.Select(Colour).ToArray(), Tri);
                }
            }
        }
        if (points.Count > 0) Ops.Add(new PsMesh([.. points], [.. colours], area, evenOdd, [.. G.Clips]));
    }

    /// <summary>A Coons patch from its twelve edge points and four corner colours, as a grid of triangles.</summary>
    private static void Coons((double X, double Y)[] p, PsColour[] c, Action<(double, double, PsColour), (double, double, PsColour), (double, double, PsColour)> tri)
    {
        static (double, double) Bez((double X, double Y) a, (double X, double Y) b, (double X, double Y) cc, (double X, double Y) d, double t)
        {
            var u = 1 - t;
            return (u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * cc.X + t * t * t * d.X, u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * cc.Y + t * t * t * d.Y);
        }
        // Corners: (0,0) p0, (0,1) p3, (1,1) p6, (1,0) p9; edges p0-p3 (u = 0), p3-p6 (v = 1), p6-p9 (u = 1, backward), p9-p0 (v = 0, backward).
        (double, double, PsColour) S(double u, double v)
        {
            var l = Bez(p[0], p[1], p[2], p[3], v);
            var top = Bez(p[3], p[4], p[5], p[6], u);
            var r = Bez(p[9], p[8], p[7], p[6], v);
            var bottom = Bez(p[0], p[11], p[10], p[9], u);
            double Blend(Func<(double X, double Y), double> f) =>
                (1 - u) * f(l) + u * f(r) + (1 - v) * f(bottom) + v * f(top)
                - ((1 - u) * (1 - v) * f(p[0]) + (1 - u) * v * f(p[3]) + u * v * f(p[6]) + u * (1 - v) * f(p[9]));
            double Mix(Func<PsColour, double> f) => (1 - u) * (1 - v) * f(c[0]) + (1 - u) * v * f(c[1]) + u * v * f(c[2]) + u * (1 - v) * f(c[3]);
            return (Blend(q => q.X), Blend(q => q.Y), new PsColour(Mix(k => k.R), Mix(k => k.G), Mix(k => k.B)));
        }
        const int n = 12;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                var (a, b, cc, d) = (S(i / (double)n, j / (double)n), S((i + 1) / (double)n, j / (double)n), S((i + 1) / (double)n, (j + 1) / (double)n), S(i / (double)n, (j + 1) / (double)n));
                tri(a, b, cc);
                tri(a, cc, d);
            }
    }

    /// <summary>A function of several inputs: PostScript calculator (4) or sampled (0, by its nearest sample), or one input otherwise.</summary>
    private double[] EvaluateN(object function, double[] inputs)
    {
        if (inputs.Length == 1) return Evaluate(function, inputs[0]);
        if (function is PsArray many && !many.Exec) return many.All().SelectMany(f => EvaluateN(f, inputs)).ToArray();
        if (function is not PsDict f) throw new PsError("typecheck", "function");
        var type = f.Get("FunctionType") is { } t ? (int)ToNum(t) : -1;
        if (type == 4)
        {
            var proc = f.Get("Function") as PsArray ?? throw new PsError("unsupported", "a PostScript function from a stream");
            var before = _stack.Count;
            foreach (var x in inputs) Push(x);
            RunProc(proc);
            var result = _stack.Skip(before).Select(ToNum).ToArray();
            _stack.RemoveRange(before, _stack.Count - before);
            return result;
        }
        if (type == 0)
        {
            double[] Get(string key, double[] otherwise) => (f.Get(key) as PsArray)?.All().Select(ToNum).ToArray() ?? otherwise;
            var size = Get("Size", [2, 2]).Select(v => (int)v).ToArray();
            var domain = Get("Domain", [0, 1, 0, 1]);
            var range = Get("Range", [0, 1]);
            var outputs = range.Length / 2;
            var bps = f.Get("BitsPerSample") is { } b ? (int)ToNum(b) : 8;
            var data = f.Get("DataSource") is PsString s ? s.Span.ToArray() : throw new PsError("unsupported", "sampled functions from a file");
            var index = 0;
            var stride = 1;
            for (var k = 0; k < inputs.Length && k < size.Length; k++)
            {
                var e = (inputs[k] - domain[2 * k]) / Math.Max(1e-12, domain[2 * k + 1] - domain[2 * k]) * (size[k] - 1);
                index += Math.Clamp((int)Math.Round(e), 0, size[k] - 1) * stride;
                stride *= size[k];
            }
            var max = (1 << bps) - 1;
            var r = new double[outputs];
            for (var o = 0; o < outputs; o++)
            {
                var bit = (index * outputs + o) * bps;
                var v = bps == 8 ? (bit / 8 < data.Length ? data[bit / 8] : 0) : bit / 8 < data.Length ? (data[bit / 8] >> (8 - bps - bit % 8)) & max : 0;
                r[o] = range[2 * o] + v * (range[2 * o + 1] - range[2 * o]) / max;
            }
            return r;
        }
        throw new PsError("unsupported", $"functions of type {type} with {inputs.Length} inputs");
    }

    /// <summary>Reading a mesh shading's packed vertices: each a flag, x and y, and its colour (or a value for its function).</summary>
    private sealed class MeshReader
    {
        private readonly byte[] _data;
        private readonly double[]? _numbers;
        private int _bit, _number;
        private readonly int _flagBits, _coordBits, _compBits, _comps;
        private readonly double[] _decode;

        public MeshReader(Interpreter i, PsDict d, int comps, int type)
        {
            _comps = comps;
            int N(string key, int otherwise) => d.Get(key) is { } v ? (int)ToNum(v) : otherwise;
            _flagBits = type == 5 ? 0 : N("BitsPerFlag", 8);
            _coordBits = N("BitsPerCoordinate", 16);
            _compBits = N("BitsPerComponent", 8);
            _decode = (d.Get("Decode") as PsArray)?.All().Select(ToNum).ToArray() ?? [];
            switch (d.Get("DataSource"))
            {
                case PsArray a: _numbers = a.All().Select(ToNum).ToArray(); _data = []; break;
                case PsString s: _data = s.Span.ToArray(); break;
                case PsFile f: _data = i.ReadData(f, 50_000_000 / 10); break;
                default: throw new PsError("undefined", "DataSource");
            }
        }

        private bool More(int bits) => _numbers is not null ? _number < _numbers.Length : _bit + bits <= _data.Length * 8;

        private long Bits(int n)
        {
            long v = 0;
            for (var k = 0; k < n; k++, _bit++) v = v << 1 | (uint)(_data[_bit >> 3] >> (7 - (_bit & 7)) & 1);
            return v;
        }

        private double Value(int bits, int decodeIndex)
        {
            if (_numbers is not null) return _number < _numbers.Length ? _numbers[_number++] : 0;
            var raw = Bits(bits);
            if (_decode.Length < 2 * decodeIndex + 2) return raw;
            var max = Math.Pow(2, bits) - 1;
            return _decode[2 * decodeIndex] + raw * (_decode[2 * decodeIndex + 1] - _decode[2 * decodeIndex]) / max;
        }

        private void Align() { if (_numbers is null && _bit % 8 != 0) _bit += 8 - _bit % 8; }

        public bool Vertex(out int flag, out double x, out double y, out double[] colour)
        {
            (flag, x, y, colour) = (0, 0, 0, []);
            if (!More(_flagBits + 2 * _coordBits)) return false;
            flag = _flagBits == 0 ? 0 : (int)(_numbers is not null ? Value(0, -1) : Bits(_flagBits));
            x = Value(_coordBits, 0);
            y = Value(_coordBits, 1);
            colour = new double[_comps];
            for (var k = 0; k < _comps; k++) colour[k] = Value(_compBits, 2 + k);
            Align();
            return true;
        }

        public bool Patch(out int flag, int count, out List<(double, double)> points, out List<double[]> colours)
        {
            (flag, points, colours) = (0, [], []);
            if (!More(_flagBits + 2 * _coordBits)) return false;
            flag = (int)(_numbers is not null ? Value(0, -1) : Bits(_flagBits));
            var n = flag == 0 ? count : count - 4;
            for (var k = 0; k < n; k++)
            {
                if (!More(2 * _coordBits)) return false;
                points.Add((Value(_coordBits, 0), Value(_coordBits, 1)));
            }
            for (var k = 0; k < (flag == 0 ? 4 : 2); k++)
            {
                var c = new double[_comps];
                for (var j = 0; j < _comps; j++) c[j] = Value(_compBits, 2 + j);
                colours.Add(c);
            }
            Align();
            // A tensor patch's sixteen points: the twelve round its edge come first in this order too.
            if (count == 16 && flag == 0) points = points.Take(12).ToList();
            else if (count == 16) points = points.Take(8).ToList();
            return true;
        }
    }
}
