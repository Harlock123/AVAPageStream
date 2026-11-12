namespace AVAPageStream.Core.PostScript;

/// <summary>
/// Reading a TrueType font, as a Type 42 font carries it in its sfnts strings: each glyph's outline (simple or made of
/// others), its advance width, and the units per em, which glyphs are scaled by to the em of 1 Type 42 fonts use.
/// </summary>
public sealed class TrueType
{
    private readonly byte[] _b;
    private readonly Dictionary<string, int> _tables = [];
    private readonly int _units, _locaFormat, _glyphs, _metrics;
    private readonly Dictionary<int, PsPath> _paths = [];

    public TrueType(byte[] font)
    {
        _b = font;
        var count = U16(4);
        for (var i = 0; i < count; i++)
        {
            var e = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(font, e, 4);
            _tables[tag] = (int)U32(e + 8);
        }
        if (!_tables.TryGetValue("head", out var head) || !_tables.ContainsKey("glyf") || !_tables.ContainsKey("loca")) throw new PsError("invalidfont", "TrueType tables");
        _units = Math.Max(1, (int)U16(head + 18));
        _locaFormat = (short)U16(head + 50);
        _glyphs = _tables.TryGetValue("maxp", out var maxp) ? U16(maxp + 4) : 65535;
        _metrics = _tables.TryGetValue("hhea", out var hhea) ? U16(hhea + 34) : 0;
    }

    /// <summary>The font's strings put together: each string of odd length has a byte of padding at its end.</summary>
    public static byte[] FromStrings(IEnumerable<PsString> parts)
    {
        var all = new List<byte>();
        foreach (var p in parts) all.AddRange(p.Length % 2 == 1 ? p.Span[..^1].ToArray() : p.Span.ToArray());
        return [.. all];
    }

    public double UnitsPerEm => _units;

    private int U16(int at) => at + 1 < _b.Length ? _b[at] << 8 | _b[at + 1] : 0;
    private short S16(int at) => (short)U16(at);
    private long U32(int at) => at + 3 < _b.Length ? (uint)(_b[at] << 24 | _b[at + 1] << 16 | _b[at + 2] << 8 | _b[at + 3]) : 0;

    /// <summary>A glyph's advance, in ems.</summary>
    public double Advance(int glyph)
    {
        if (!_tables.TryGetValue("hmtx", out var hmtx) || _metrics == 0) return 0;
        var i = Math.Min(glyph, _metrics - 1);
        return U16(hmtx + i * 4) / (double)_units;
    }

    /// <summary>A glyph's outline, in ems, y up.</summary>
    public PsPath Outline(int glyph)
    {
        if (_paths.TryGetValue(glyph, out var cached)) return cached;
        var path = new PsPath();
        Add(path, glyph, PsMatrix.Identity, 0);
        var scaled = path.Transformed(new PsMatrix(1.0 / _units, 0, 0, 1.0 / _units, 0, 0));
        _paths[glyph] = scaled;
        return scaled;
    }

    private (int Start, int End) Location(int glyph)
    {
        var loca = _tables["loca"];
        var glyf = _tables["glyf"];
        return _locaFormat == 0
            ? (glyf + U16(loca + glyph * 2) * 2, glyf + U16(loca + glyph * 2 + 2) * 2)
            : (glyf + (int)U32(loca + glyph * 4), glyf + (int)U32(loca + glyph * 4 + 4));
    }

    private void Add(PsPath path, int glyph, PsMatrix m, int depth)
    {
        if (glyph < 0 || glyph >= _glyphs || depth > 8) return;
        var (start, end) = Location(glyph);
        if (end <= start) return; // an empty glyph: a space
        var contours = S16(start);
        if (contours >= 0) Simple(path, start, contours, m);
        else Composite(path, start, m, depth);
    }

    private void Simple(PsPath path, int at, int contours, PsMatrix m)
    {
        var ends = new int[contours];
        for (var k = 0; k < contours; k++) ends[k] = U16(at + 10 + k * 2);
        var points = contours == 0 ? 0 : ends[^1] + 1;
        var p = at + 10 + contours * 2;
        var instructions = U16(p);
        p += 2 + instructions;
        var flags = new byte[points];
        for (var k = 0; k < points;)
        {
            var f = _b[p++];
            flags[k++] = f;
            if ((f & 8) != 0)
                for (var r = _b[p++]; r > 0 && k < points; r--) flags[k++] = f;
        }
        var xs = new int[points];
        var ys = new int[points];
        int v = 0;
        for (var k = 0; k < points; k++)
        {
            var f = flags[k];
            if ((f & 2) != 0) { var d = _b[p++]; v += (f & 16) != 0 ? d : -d; }
            else if ((f & 16) == 0) { v += S16(p); p += 2; }
            xs[k] = v;
        }
        v = 0;
        for (var k = 0; k < points; k++)
        {
            var f = flags[k];
            if ((f & 4) != 0) { var d = _b[p++]; v += (f & 32) != 0 ? d : -d; }
            else if ((f & 32) == 0) { v += S16(p); p += 2; }
            ys[k] = v;
        }
        var first = 0;
        foreach (var last in ends)
        {
            Contour(path, xs, ys, flags, first, last, m);
            first = last + 1;
        }
    }

    /// <summary>One contour: quadratic curves between points on the curve, an off-curve pair meeting at their middle, as cubics.</summary>
    private static void Contour(PsPath path, int[] xs, int[] ys, byte[] flags, int first, int last, PsMatrix m)
    {
        if (last < first) return;
        var n = last - first + 1;
        bool On(int k) => (flags[first + (k % n + n) % n] & 1) != 0;
        (double, double) P(int k) { var i = first + (k % n + n) % n; return (xs[i], ys[i]); }
        // Start at a point on the curve (or midway between two off it).
        var s = Enumerable.Range(0, n).FirstOrDefault(k => On(k), -1);
        (double X, double Y) start;
        if (s >= 0) start = P(s);
        else { var (ax, ay) = P(0); var (bx, by) = P(1); start = ((ax + bx) / 2, (ay + by) / 2); s = 0; }
        void Seg(SegKind kind, double x1 = 0, double y1 = 0, double x2 = 0, double y2 = 0, double x3 = 0, double y3 = 0)
        {
            var (a, b) = m.Apply(x1, y1);
            var (c, d) = m.Apply(x2, y2);
            var (e, f) = m.Apply(x3, y3);
            path.Segs.Add(new PsSeg(kind, a, b, c, d, e, f));
        }
        Seg(SegKind.Move, x3: start.X, y3: start.Y);
        var cur = start;
        var offStart = On(s) ? 0 : 1;
        for (var k = 1 + (On(s) ? 0 : 0); k <= n; k++)
        {
            var idx = s + k;
            var pt = P(idx);
            if (On(idx))
            {
                Seg(SegKind.Line, x3: pt.Item1, y3: pt.Item2);
                cur = pt;
                continue;
            }
            // An off-curve control point: to the next point on the curve, or the middle of the next off one.
            var next = P(idx + 1);
            var end = On(idx + 1) ? next : ((pt.Item1 + next.Item1) / 2, (pt.Item2 + next.Item2) / 2);
            var (c1x, c1y) = (cur.X + 2.0 / 3 * (pt.Item1 - cur.X), cur.Y + 2.0 / 3 * (pt.Item2 - cur.Y));
            var (c2x, c2y) = (end.Item1 + 2.0 / 3 * (pt.Item1 - end.Item1), end.Item2 + 2.0 / 3 * (pt.Item2 - end.Item2));
            Seg(SegKind.Curve, c1x, c1y, c2x, c2y, end.Item1, end.Item2);
            cur = end;
            if (On(idx + 1)) k++;
        }
        _ = offStart;
        path.Segs.Add(new PsSeg(SegKind.Close));
    }

    private void Composite(PsPath path, int at, PsMatrix m, int depth)
    {
        var p = at + 10;
        while (true)
        {
            var flags = U16(p);
            var glyph = U16(p + 2);
            p += 4;
            double dx, dy;
            if ((flags & 1) != 0) { dx = S16(p); dy = S16(p + 2); p += 4; }
            else { dx = (sbyte)_b[p]; dy = (sbyte)_b[p + 1]; p += 2; }
            double a = 1, b = 0, c = 0, d = 1;
            static double F2Dot14(int v) => (short)v / 16384.0;
            if ((flags & 8) != 0) { a = d = F2Dot14(U16(p)); p += 2; }
            else if ((flags & 0x40) != 0) { a = F2Dot14(U16(p)); d = F2Dot14(U16(p + 2)); p += 4; }
            else if ((flags & 0x80) != 0) { a = F2Dot14(U16(p)); b = F2Dot14(U16(p + 2)); c = F2Dot14(U16(p + 4)); d = F2Dot14(U16(p + 6)); p += 8; }
            // Offsets as x and y (the usual); matching points (flag 2 clear) are rare enough to place at the origin.
            var part = new PsMatrix(a, b, c, d, (flags & 2) != 0 ? dx : 0, (flags & 2) != 0 ? dy : 0);
            Add(path, glyph, part.Then(m), depth + 1);
            if ((flags & 0x20) == 0) break;
        }
    }
}
