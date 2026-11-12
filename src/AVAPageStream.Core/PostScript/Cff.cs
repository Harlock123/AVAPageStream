namespace AVAPageStream.Core.PostScript;

/// <summary>
/// CFF fonts (as a FontSet resource, or a CIDFont's FDArray): the font set read, and its Type 2 charstrings drawn.
/// </summary>
public sealed class Cff
{
    /// <summary>The CFF standard strings 0 to 228 (the ISOAdobe set); higher standard strings are Expert names, kept as their numbers.</summary>
    private static readonly string[] Standard = BuildStandard();

    private static string[] BuildStandard()
    {
        var list = new List<string> { ".notdef" };
        for (var c = 32; c <= 126; c++) list.Add(Encodings.Standard[c]!);
        for (var c = 161; c <= 251; c++) if (Encodings.Standard[c] is { } n) list.Add(n);
        list.AddRange(["onesuperior", "logicalnot", "mu", "trademark", "Eth", "onehalf", "plusminus", "Thorn", "onequarter", "divide", "brokenbar", "degree", "thorn",
            "threequarters", "twosuperior", "registered", "minus", "eth", "multiply", "threesuperior", "copyright",
            "Aacute", "Acircumflex", "Adieresis", "Agrave", "Aring", "Atilde", "Ccedilla", "Eacute", "Ecircumflex", "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave",
            "Ntilde", "Oacute", "Ocircumflex", "Odieresis", "Ograve", "Otilde", "Scaron", "Uacute", "Ucircumflex", "Udieresis", "Ugrave", "Yacute", "Ydieresis", "Zcaron",
            "aacute", "acircumflex", "adieresis", "agrave", "aring", "atilde", "ccedilla", "eacute", "ecircumflex", "edieresis", "egrave", "iacute", "icircumflex", "idieresis", "igrave",
            "ntilde", "oacute", "ocircumflex", "odieresis", "ograve", "otilde", "scaron", "uacute", "ucircumflex", "udieresis", "ugrave", "yacute", "ydieresis", "zcaron"]);
        return [.. list];
    }

    public const int StandardCount = 391;

    public string Name { get; private init; } = "";
    public PsMatrix FontMatrix { get; private init; } = new(0.001, 0, 0, 0.001, 0, 0);
    public bool IsCid { get; private init; }
    public List<byte[]> CharStrings { get; private init; } = [];
    public List<byte[]> GlobalSubrs { get; private init; } = [];
    /// <summary>Per font dictionary (one for a plain font): its subroutines, widths and matrix.</summary>
    public List<(List<byte[]> Subrs, double DefaultWidth, double NominalWidth, PsMatrix Matrix)> Privates { get; private init; } = [];
    public byte[] FdSelect { get; private init; } = [];
    /// <summary>Glyph number to name (plain fonts) or to CID (CID-keyed).</summary>
    public string[] Names { get; private init; } = [];
    public int[] Cids { get; private init; } = [];
    /// <summary>Code to glyph number, for a plain font's own encoding (null: Standard).</summary>
    public Dictionary<int, int>? Encoding { get; private init; }

    /// <summary>Reads every font in a CFF font set.</summary>
    public static List<Cff> Read(byte[] b)
    {
        int Card(int at, int n) { var v = 0; for (var k = 0; k < n; k++) v = v << 8 | b[at + k]; return v; }
        List<byte[]> Index(ref int at)
        {
            var count = Card(at, 2);
            at += 2;
            var items = new List<byte[]>(count);
            if (count == 0) return items;
            var off = b[at++];
            var offsets = new int[count + 1];
            for (var k = 0; k <= count; k++) offsets[k] = Card(at + k * off, off);
            var data = at + (count + 1) * off - 1;
            for (var k = 0; k < count; k++) items.Add(b[(data + offsets[k])..(data + offsets[k + 1])]);
            at = data + offsets[count];
            return items;
        }
        int p = b[2];
        var names = Index(ref p);
        var tops = Index(ref p);
        var strings = Index(ref p);
        var gsubrs = Index(ref p);
        string Sid(int sid) => sid < Standard.Length ? Standard[sid] : sid < StandardCount ? "sid" + sid : sid - StandardCount < strings.Count ? System.Text.Encoding.Latin1.GetString(strings[sid - StandardCount]) : ".notdef";
        var fonts = new List<Cff>();
        for (var f = 0; f < tops.Count; f++)
        {
            var top = Dict(tops[f]);
            var csAt = (int)Get(top, 17, 0)[0];
            var chars = Index(ref csAt);
            var n = chars.Count;
            var cid = top.ContainsKey(1230);
            // The charset: each glyph's name (or CID).
            var sids = new int[n];
            var charset = (int)Get(top, 15, 0)[0];
            if (charset == 0 || charset > 2 && b.Length > charset)
            {
                if (charset == 0) for (var g = 0; g < n; g++) sids[g] = g;
                else
                {
                    var at = charset;
                    var format = b[at++];
                    var g = 1;
                    while (g < n)
                    {
                        if (format == 0) { sids[g++] = Card(at, 2); at += 2; continue; }
                        var first = Card(at, 2);
                        var left = format == 1 ? b[at + 2] : Card(at + 2, 2);
                        at += format == 1 ? 3 : 4;
                        for (var k = 0; k <= left && g < n; k++) sids[g++] = first + k;
                    }
                }
            }
            (List<byte[]>, double, double, PsMatrix) Private(Dictionary<int, double[]> d, PsMatrix m)
            {
                if (!d.TryGetValue(18, out var pr) || pr.Length < 2) return ([], 0, 0, m);
                var (size, offset) = ((int)pr[0], (int)pr[1]);
                var priv = Dict(b[offset..(offset + size)]);
                var subrs = new List<byte[]>();
                if (priv.TryGetValue(19, out var so)) { var at = offset + (int)so[0]; subrs = Index(ref at); }
                return (subrs, Get(priv, 20, 0)[0], Get(priv, 21, 0)[0], m);
            }
            var matrix = top.TryGetValue(1207, out var fm) && fm.Length == 6 ? new PsMatrix(fm[0], fm[1], fm[2], fm[3], fm[4], fm[5]) : new PsMatrix(0.001, 0, 0, 0.001, 0, 0);
            var privates = new List<(List<byte[]>, double, double, PsMatrix)>();
            var fdSelect = Array.Empty<byte>();
            if (cid)
            {
                var fdAt = (int)Get(top, 1236, 0)[0];
                foreach (var fd in Index(ref fdAt))
                {
                    var d = Dict(fd);
                    var m = d.TryGetValue(1207, out var dm) && dm.Length == 6 ? new PsMatrix(dm[0], dm[1], dm[2], dm[3], dm[4], dm[5]) : PsMatrix.Identity;
                    privates.Add(Private(d, m));
                }
                fdSelect = new byte[n];
                var sel = (int)Get(top, 1237, 0)[0];
                if (sel > 0)
                {
                    var format = b[sel];
                    if (format == 0) for (var g = 0; g < n; g++) fdSelect[g] = b[sel + 1 + g];
                    else if (format == 3)
                    {
                        var ranges = Card(sel + 1, 2);
                        for (var r = 0; r < ranges; r++)
                        {
                            var first = Card(sel + 3 + r * 3, 2);
                            var fdi = b[sel + 5 + r * 3];
                            var next = Card(sel + 3 + (r + 1) * 3, 2);
                            for (var g = first; g < next && g < n; g++) fdSelect[g] = fdi;
                        }
                    }
                }
            }
            else privates.Add(Private(top, PsMatrix.Identity));
            // A plain font's encoding: its own, or the standard one.
            Dictionary<int, int>? encoding = null;
            var enc = (int)Get(top, 16, 0)[0];
            if (!cid && enc > 1)
            {
                encoding = [];
                var at = enc;
                var format = b[at++];
                if ((format & 127) == 0) { var count = b[at++]; for (var k = 0; k < count; k++) encoding[b[at++]] = k + 1; }
                else
                {
                    var ranges = b[at++];
                    var g = 1;
                    for (var r = 0; r < ranges; r++) { var first = b[at++]; var left = b[at++]; for (var k = 0; k <= left; k++) encoding[first + k] = g++; }
                }
            }
            fonts.Add(new Cff
            {
                Name = f < names.Count ? System.Text.Encoding.Latin1.GetString(names[f]) : "CFF",
                FontMatrix = matrix,
                IsCid = cid,
                CharStrings = chars,
                GlobalSubrs = gsubrs,
                Privates = privates,
                FdSelect = fdSelect,
                Names = cid ? sids.Select(s => "cid" + s).ToArray() : sids.Select(Sid).ToArray(),
                Cids = sids,
                Encoding = encoding,
            });
        }
        return fonts;
    }

    private static double[] Get(Dictionary<int, double[]> d, int key, double otherwise) => d.TryGetValue(key, out var v) && v.Length > 0 ? v : [otherwise];

    /// <summary>A DICT: operators (12 x as 1200 + x) with their operands.</summary>
    private static Dictionary<int, double[]> Dict(byte[] d)
    {
        var dict = new Dictionary<int, double[]>();
        var args = new List<double>();
        for (var i = 0; i < d.Length;)
        {
            int v = d[i++];
            if (v <= 21)
            {
                var op = v == 12 ? 1200 + d[i++] : v;
                dict[op] = [.. args];
                args.Clear();
            }
            else if (v == 28) { args.Add((short)(d[i] << 8 | d[i + 1])); i += 2; }
            else if (v == 29) { args.Add(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]); i += 4; }
            else if (v == 30)
            {
                var s = new System.Text.StringBuilder();
                var done = false;
                while (!done && i < d.Length)
                {
                    var bt = d[i++];
                    foreach (var nib in new[] { bt >> 4, bt & 15 })
                    {
                        if (nib == 15) { done = true; break; }
                        s.Append(nib switch { < 10 => (char)('0' + nib), 10 => ".", 11 => "E", 12 => "E-", 14 => "-", _ => "" });
                    }
                }
                args.Add(double.TryParse(s.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0);
            }
            else if (v >= 32 && v <= 246) args.Add(v - 139);
            else if (v >= 247 && v <= 250) args.Add((v - 247) * 256 + d[i++] + 108);
            else if (v >= 251 && v <= 254) args.Add(-(v - 251) * 256 - d[i++] - 108);
        }
        return dict;
    }

    /// <summary>A glyph by its number: its outline and advance, in the font's glyph space (before FontMatrix).</summary>
    public Type1.Glyph Glyph(int gid)
    {
        if (gid < 0 || gid >= CharStrings.Count) return new Type1.Glyph(new PsPath(), 0, 0);
        var fd = IsCid && gid < FdSelect.Length ? FdSelect[gid] : 0;
        var (subrs, dw, nw, m) = fd < Privates.Count ? Privates[fd] : ([], 0, 0, PsMatrix.Identity);
        var g = Run(CharStrings[gid], subrs, GlobalSubrs, dw, nw, n => Array.IndexOf(Names, n) is var i and >= 0 ? CharStrings[i] : null);
        // A CID-keyed font's dictionaries may scale their glyphs further.
        if (IsCid && m != PsMatrix.Identity)
        {
            var (wx, wy) = m.Delta(g.AdvanceX, g.AdvanceY);
            return new Type1.Glyph(g.Path.Transformed(m), wx, wy);
        }
        return g;
    }

    /// <summary>A Type 2 charstring with a CIDFont's PostScript Private dictionary (defaultWidthX, nominalWidthX).</summary>
    public static Type1.Glyph Run(byte[] cs, List<byte[]> local, List<byte[]> global, PsDict? priv) =>
        Run(cs, local, global, priv?.Get("defaultWidthX") is { } d ? Interpreter.ToNum(d) : 0, priv?.Get("nominalWidthX") is { } n ? Interpreter.ToNum(n) : 0, _ => null);

    /// <summary>Runs a Type 2 charstring.</summary>
    public static Type1.Glyph Run(byte[] cs, List<byte[]> local, List<byte[]> global, double defaultWidth, double nominalWidth, Func<string, byte[]?> lookup)
    {
        var s = new State(local, global, defaultWidth, nominalWidth, lookup);
        s.Execute(cs, 0);
        if (s.Open) s.Path.Segs.Add(new PsSeg(SegKind.Close));
        return new Type1.Glyph(s.Path, s.Width ?? defaultWidth, 0);
    }

    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private sealed class State(List<byte[]> local, List<byte[]> global, double defaultWidth, double nominalWidth, Func<string, byte[]?> lookup)
    {
        public readonly PsPath Path = new();
        public double? Width;
        public bool Open;
        private readonly List<double> _a = [];
        private readonly double[] _transient = new double[32];
        private double _x, _y;
        private int _stems;
        private bool _widthDone, _done;
        private int _calls;

        private void MoveTo(double dx, double dy)
        {
            if (Open) Path.Segs.Add(new PsSeg(SegKind.Close));
            (_x, _y) = (_x + dx, _y + dy);
            Path.Segs.Add(new PsSeg(SegKind.Move, X3: _x, Y3: _y));
            Open = false;
        }

        private void LineTo(double dx, double dy)
        {
            (_x, _y) = (_x + dx, _y + dy);
            Path.Segs.Add(new PsSeg(SegKind.Line, X3: _x, Y3: _y));
            Open = true;
        }

        private void CurveTo(double a, double b, double c, double d, double e, double f)
        {
            var (x1, y1) = (_x + a, _y + b);
            var (x2, y2) = (x1 + c, y1 + d);
            (_x, _y) = (x2 + e, y2 + f);
            Path.Segs.Add(new PsSeg(SegKind.Curve, x1, y1, x2, y2, _x, _y));
            Open = true;
        }

        /// <summary>The width, if this is the first operator to clear the stack and it has one argument more than it takes.</summary>
        private void TakeWidth(bool odd)
        {
            if (_widthDone) return;
            _widthDone = true;
            if (odd && _a.Count > 0) { Width = nominalWidth + _a[0]; _a.RemoveAt(0); }
        }

        public void Execute(byte[] b, int depth)
        {
            if (depth > 10 || ++_calls > 20000) throw new PsError("invalidfont", "Type 2 charstring");
            for (var i = 0; i < b.Length && !_done;)
            {
                int v = b[i++];
                if (v >= 32 || v == 28)
                {
                    if (v == 28) { _a.Add((short)(b[i] << 8 | b[i + 1])); i += 2; }
                    else if (v <= 246) _a.Add(v - 139);
                    else if (v <= 250) _a.Add((v - 247) * 256 + b[i++] + 108);
                    else if (v <= 254) _a.Add(-(v - 251) * 256 - b[i++] - 108);
                    else { _a.Add((b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]) / 65536.0); i += 4; }
                    continue;
                }
                switch (v)
                {
                    case 1 or 3 or 18 or 23: // stems
                        TakeWidth(_a.Count % 2 == 1);
                        _stems += _a.Count / 2;
                        _a.Clear();
                        break;
                    case 19 or 20: // hintmask, cntrmask: stems given just before count too
                        TakeWidth(_a.Count % 2 == 1);
                        _stems += _a.Count / 2;
                        _a.Clear();
                        i += (_stems + 7) / 8;
                        break;
                    case 21: TakeWidth(_a.Count > 2); MoveTo(_a[^2], _a[^1]); _a.Clear(); break;
                    case 22: TakeWidth(_a.Count > 1); MoveTo(_a[^1], 0); _a.Clear(); break;
                    case 4: TakeWidth(_a.Count > 1); MoveTo(0, _a[^1]); _a.Clear(); break;
                    case 5:
                        for (var k = 0; k + 1 < _a.Count; k += 2) LineTo(_a[k], _a[k + 1]);
                        _a.Clear();
                        break;
                    case 6 or 7: // alternating horizontal and vertical lines
                    {
                        var horizontal = v == 6;
                        foreach (var d in _a) { if (horizontal) LineTo(d, 0); else LineTo(0, d); horizontal = !horizontal; }
                        _a.Clear();
                        break;
                    }
                    case 8:
                        for (var k = 0; k + 5 < _a.Count; k += 6) CurveTo(_a[k], _a[k + 1], _a[k + 2], _a[k + 3], _a[k + 4], _a[k + 5]);
                        _a.Clear();
                        break;
                    case 24: // rcurveline
                    {
                        var k = 0;
                        for (; k + 7 < _a.Count; k += 6) CurveTo(_a[k], _a[k + 1], _a[k + 2], _a[k + 3], _a[k + 4], _a[k + 5]);
                        if (k + 1 < _a.Count) LineTo(_a[k], _a[k + 1]);
                        _a.Clear();
                        break;
                    }
                    case 25: // rlinecurve
                    {
                        var k = 0;
                        for (; k + 7 < _a.Count; k += 2) LineTo(_a[k], _a[k + 1]);
                        if (k + 5 < _a.Count) CurveTo(_a[k], _a[k + 1], _a[k + 2], _a[k + 3], _a[k + 4], _a[k + 5]);
                        _a.Clear();
                        break;
                    }
                    case 26: // vvcurveto
                    {
                        var k = 0;
                        var dx1 = 0.0;
                        if (_a.Count % 2 == 1) dx1 = _a[k++];
                        for (; k + 3 < _a.Count; k += 4) { CurveTo(dx1, _a[k], _a[k + 1], _a[k + 2], 0, _a[k + 3]); dx1 = 0; }
                        _a.Clear();
                        break;
                    }
                    case 27: // hhcurveto
                    {
                        var k = 0;
                        var dy1 = 0.0;
                        if (_a.Count % 2 == 1) dy1 = _a[k++];
                        for (; k + 3 < _a.Count; k += 4) { CurveTo(_a[k], dy1, _a[k + 1], _a[k + 2], _a[k + 3], 0); dy1 = 0; }
                        _a.Clear();
                        break;
                    }
                    case 30 or 31: // vhcurveto, hvcurveto
                    {
                        var horizontal = v == 31;
                        var k = 0;
                        while (k + 3 < _a.Count)
                        {
                            var last = k + 4 >= _a.Count - 1 && k + 5 == _a.Count;
                            var extra = last ? _a[k + 4] : 0;
                            if (horizontal) CurveTo(_a[k], 0, _a[k + 1], _a[k + 2], extra, _a[k + 3]);
                            else CurveTo(0, _a[k], _a[k + 1], _a[k + 2], _a[k + 3], extra);
                            horizontal = !horizontal;
                            k += last ? 5 : 4;
                        }
                        _a.Clear();
                        break;
                    }
                    case 10 or 29: // callsubr, callgsubr
                    {
                        var subrs = v == 10 ? local : global;
                        var n = (int)_a[^1] + Bias(subrs.Count);
                        _a.RemoveAt(_a.Count - 1);
                        if (n >= 0 && n < subrs.Count) Execute(subrs[n], depth + 1);
                        break;
                    }
                    case 11: return; // return
                    case 14: // endchar (with four arguments: an accented letter, as seac)
                        TakeWidth(_a.Count is 1 or 5);
                        if (_a.Count >= 4)
                        {
                            var (adx, ady, bc, ac) = (_a[^4], _a[^3], (int)_a[^2], (int)_a[^1]);
                            void Part(int code, double dx, double dy)
                            {
                                if (code is < 0 or > 255 || Encodings.Standard[code] is not { } name || lookup(name) is not { } part) return;
                                var g = Run(part, local, global, defaultWidth, nominalWidth, lookup);
                                Path.Segs.AddRange(g.Path.Transformed(new PsMatrix(1, 0, 0, 1, dx, dy)).Segs);
                            }
                            Part(bc, 0, 0);
                            Part(ac, adx, ady);
                        }
                        _done = true;
                        _a.Clear();
                        break;
                    case 12:
                        Escape(b[i++]);
                        break;
                    default:
                        _a.Clear();
                        break;
                }
            }
        }

        private void Escape(int op)
        {
            double Pop() { var x = _a[^1]; _a.RemoveAt(_a.Count - 1); return x; }
            switch (op)
            {
                case 35: // flex
                    CurveTo(_a[0], _a[1], _a[2], _a[3], _a[4], _a[5]);
                    CurveTo(_a[6], _a[7], _a[8], _a[9], _a[10], _a[11]);
                    _a.Clear();
                    break;
                case 34: // hflex
                {
                    var y0 = _y;
                    CurveTo(_a[0], 0, _a[1], _a[2], _a[3], 0);
                    CurveTo(_a[4], 0, _a[5], y0 - (_y + 0), _a[6], 0);
                    _a.Clear();
                    break;
                }
                case 36: // hflex1
                {
                    var y0 = _y;
                    CurveTo(_a[0], _a[1], _a[2], _a[3], _a[4], 0);
                    CurveTo(_a[5], 0, _a[6], _a[7], _a[8], y0 - (_y + _a[7]));
                    _a.Clear();
                    break;
                }
                case 37: // flex1
                {
                    var (x0, y0) = (_x, _y);
                    double dx = 0, dy = 0;
                    for (var k = 0; k < 10; k += 2) { dx += _a[k]; dy += _a[k + 1]; }
                    CurveTo(_a[0], _a[1], _a[2], _a[3], _a[4], _a[5]);
                    var last = _a[10];
                    if (Math.Abs(dx) > Math.Abs(dy)) CurveTo(_a[6], _a[7], _a[8], _a[9], last, y0 - (_y + _a[7] + _a[9]));
                    else CurveTo(_a[6], _a[7], _a[8], _a[9], x0 - (_x + _a[6] + _a[8]), last);
                    _a.Clear();
                    break;
                }
                case 3: { var b2 = Pop(); var a2 = Pop(); _a.Add(a2 != 0 && b2 != 0 ? 1 : 0); break; }
                case 4: { var b2 = Pop(); var a2 = Pop(); _a.Add(a2 != 0 || b2 != 0 ? 1 : 0); break; }
                case 5: _a.Add(Pop() == 0 ? 1 : 0); break;
                case 9: _a.Add(Math.Abs(Pop())); break;
                case 10: { var b2 = Pop(); _a.Add(Pop() + b2); break; }
                case 11: { var b2 = Pop(); _a.Add(Pop() - b2); break; }
                case 12: { var b2 = Pop(); var a2 = Pop(); _a.Add(b2 == 0 ? 0 : a2 / b2); break; }
                case 14: _a.Add(-Pop()); break;
                case 15: { var b2 = Pop(); _a.Add(Pop() == b2 ? 1 : 0); break; }
                case 18: Pop(); break;
                case 20: { var k = (int)Pop(); var val = Pop(); if (k is >= 0 and < 32) _transient[k] = val; break; }
                case 21: { var k = (int)Pop(); _a.Add(k is >= 0 and < 32 ? _transient[k] : 0); break; }
                case 22: { var v2 = Pop(); var v1 = Pop(); var s2 = Pop(); var s1 = Pop(); _a.Add(v1 <= v2 ? s1 : s2); break; }
                case 23: _a.Add(0.5); break;
                case 24: { var b2 = Pop(); _a.Add(Pop() * b2); break; }
                case 26: _a.Add(Math.Sqrt(Math.Max(0, Pop()))); break;
                case 27: _a.Add(_a[^1]); break;
                case 28: { var b2 = Pop(); var a2 = Pop(); _a.Add(b2); _a.Add(a2); break; }
                case 29: { var k = (int)Pop(); _a.Add(_a[Math.Clamp(_a.Count - 1 - Math.Max(0, k), 0, _a.Count - 1)]); break; }
                case 30:
                {
                    var j = (int)Pop();
                    var n = (int)Pop();
                    if (n > 0 && n <= _a.Count)
                    {
                        j = ((j % n) + n) % n;
                        var part = _a.GetRange(_a.Count - n, n);
                        _a.RemoveRange(_a.Count - n, n);
                        _a.AddRange(part.Skip(n - j).Concat(part.Take(n - j)));
                    }
                    break;
                }
                default:
                    _a.Clear();
                    break;
            }
        }
    }
}
