namespace AVAPageStream.Core.PostScript;

/// <summary>A glyph of a font the program supplies (the bundled Liberation fonts): its outline and advance, 1000 to the em, y up.</summary>
public sealed record SystemGlyph(PsPath Path, double Advance);

public sealed partial class Interpreter
{
    /// <summary>
    /// The outlines of the fonts printers have built in, which EPS files name without carrying (Times, Helvetica,
    /// Courier): given a family (Liberation Serif, Sans or Mono), bold, italic and a character, its glyph.
    /// </summary>
    public static Func<string, bool, bool, int, SystemGlyph?>? SystemGlyphs { get; set; }

    /// <summary>This interpreter's printer-font outlines: <see cref="SystemGlyphs"/> unless given others (as tests do).</summary>
    public Func<string, bool, bool, int, SystemGlyph?>? Glyphs { get; init; } = SystemGlyphs;

    /// <summary>Works out the outline of a stroke (for strokepath): its path in user space and the pen. Given by the program.</summary>
    public static Func<PsPath, PsPen, PsPath?>? Outliner { get; set; }

    public Func<PsPath, PsPen, PsPath?>? StrokeOutliner { get; init; } = Outliner;

    private const string SystemKey = "AVAPageStream.Font";
    private readonly Dictionary<(object, string), (PsPath Path, double Wx, double Wy)> _glyphs = [];
    private readonly Dictionary<object, TrueType> _trueTypes = [];
    private (double X, double Y)? _charWidth;

    private PsDict FontDirectory => (PsDict)SystemDict.Get("FontDirectory")!;

    private void RegisterFonts()
    {
        SystemDict.Put("StandardEncoding", Encodings.Array(Encodings.Standard));
        SystemDict.Put("ISOLatin1Encoding", Encodings.Array(Encodings.IsoLatin1));
        Op("findencoding", i =>
        {
            var name = Show(i.Pop());
            i.Push(name switch
            {
                "StandardEncoding" => i.SystemDict.Get("StandardEncoding")!,
                "ISOLatin1Encoding" => i.SystemDict.Get("ISOLatin1Encoding")!,
                _ => i.Resources("Encoding").Get(name) ?? throw new PsError("undefinedresource", name),
            });
        });
        Op("findfont", i => i.Push(i.FindFont(i.Pop())));
        Op("definefont", i =>
        {
            var font = i.Pop<PsDict>("definefont");
            var key = i.Pop();
            if (font.Get("FontType") is { } t && (int)ToNum(t) is 9 or 10 or 11 or 32) throw new PsError("unsupported", "CFF-based CIDFonts made by definefont");
            font.Put("FID", new PsFontId(Show(key)));
            i.FontDirectory.Put(key, font);
            i.Resources("Font").Put(key, font);
            i.Push(font);
        });
        Op("undefinefont", i => { var k = i.Pop(); i.FontDirectory.Remove(k); i.Resources("Font").Remove(k); });
        Op("scalefont", i => { var s = i.Num(); i.Push(Transformed(i.Pop<PsDict>("scalefont"), new PsMatrix(s, 0, 0, s, 0, 0))); });
        Op("makefont", i => { var m = ToMatrix(i.Pop<PsArray>("makefont")); i.Push(Transformed(i.Pop<PsDict>("makefont"), m)); });
        Op("setfont", i => i.G.Font = i.Pop<PsDict>("setfont"));
        Op("currentfont", i => i.Push(i.G.Font ?? i.FindFont(new PsName("Courier"))));
        Op("rootfont", i => i.Push(i.G.Font ?? i.FindFont(new PsName("Courier"))));
        Op("selectfont", i =>
        {
            var size = i.Pop();
            var key = i.Pop();
            var font = key as PsDict ?? i.FindFont(key);
            i.G.Font = size is PsArray m ? Transformed(font, ToMatrix(m)) : Transformed(font, new PsMatrix(ToNum(size), 0, 0, ToNum(size), 0, 0));
        });
        Op("composefont", i =>
        {
            var fonts = i.Pop<PsArray>("composefont");
            var cmap = i.Pop();
            var key = i.Pop();
            i.Push(i.ComposeFont(key, cmap, fonts));
        });
        Op("setcachedevice", i => { for (var k = 0; k < 4; k++) i.Pop(); var wy = i.Num(); var wx = i.Num(); i._charWidth = (wx, wy); });
        Op("setcachedevice2", i => { for (var k = 0; k < 8; k++) i.Pop(); var wy = i.Num(); var wx = i.Num(); i._charWidth = (wx, wy); });
        Op("setcharwidth", i => { var wy = i.Num(); var wx = i.Num(); i._charWidth = (wx, wy); });
        Op("eexec", i => i.Eexec(i.Pop()));

        // ---- Showing text
        Op("show", i => i.Show(i.Pop<PsString>("show")));
        Op("ashow", i => { var s = i.Pop<PsString>("ashow"); var ay = i.Num(); var ax = i.Num(); i.Show(s, extra: (_, _) => (ax, ay)); });
        Op("widthshow", i =>
        {
            var s = i.Pop<PsString>("widthshow"); var ch = i.Int(); var cy = i.Num(); var cx = i.Num();
            i.Show(s, extra: (_, c) => c == ch ? (cx, cy) : (0, 0));
        });
        Op("awidthshow", i =>
        {
            var s = i.Pop<PsString>("awidthshow"); var ay = i.Num(); var ax = i.Num(); var ch = i.Int(); var cy = i.Num(); var cx = i.Num();
            i.Show(s, extra: (_, c) => c == ch ? (ax + cx, ay + cy) : (ax, ay));
        });
        void Positioned(string name, bool xs, bool ys) => Op(name, i =>
        {
            var numbers = i.Pop() switch
            {
                PsArray a => a.All().Select(ToNum).ToArray(),
                PsString packed => EncodedNumbers(packed),
                _ => throw new PsError("typecheck", name),
            };
            var s = i.Pop<PsString>(name);
            var per = xs && ys ? 2 : 1;
            i.Show(s, advance: k =>
            {
                double At(int n) => n < numbers.Length ? numbers[n] : 0;
                return xs && ys ? (At(k * per), At(k * per + 1)) : xs ? (At(k), 0) : (0, At(k));
            });
        });
        Positioned("xshow", true, false);
        Positioned("yshow", false, true);
        Positioned("xyshow", true, true);
        Op("kshow", i =>
        {
            var s = i.Pop<PsString>("kshow");
            var proc = i.Proc();
            i.Show(s, between: (a, b) => { i.Push(a); i.Push(b); i.RunProc(proc); });
        });
        Op("cshow", i =>
        {
            // Each character's code and advance given to the procedure, which shows it (or not) itself.
            var s = i.Pop<PsString>("cshow");
            var proc = i.Proc();
            foreach (var g in i.Codes(i.Font(), s))
            {
                var (gx, gy) = i.Width(g);
                var (wx, wy) = g.Matrix.Delta(gx, gy);
                i.Push(g.Code);
                i.Push(wx);
                i.Push(wy);
                i.RunProc(proc);
            }
        });
        Op("glyphshow", i =>
        {
            var g = i.Pop();
            var font = i.Font();
            // A CID for a CIDFont, a name for anything else.
            var target = font.Get("FontType") is { } t && (int)ToNum(t) == 0 && font.Get("FDepVector") is PsArray v && v.Length > 0 && v[0] is PsDict d ? d : font;
            var matrix = ReferenceEquals(target, font) ? FontMatrix(font) : FontMatrix(target).Then(FontMatrix(font));
            i.ShowGlyphs([new GlyphRef(target, matrix, -1, g is int ? "" : Show(g), g is int cid ? cid : -1)], null, null, null, path: false);
        });
        Op("charpath", i => { i.Bool(); var s = i.Pop<PsString>("charpath"); i.ShowGlyphs(i.Codes(i.Font(), s), null, null, null, path: true); });
        Op("stringwidth", i =>
        {
            var s = i.Pop<PsString>("stringwidth");
            double wx = 0, wy = 0;
            foreach (var g in i.Codes(i.Font(), s))
            {
                var (gx, gy) = i.Width(g);
                var (ux, uy) = g.Matrix.Delta(gx, gy);
                (wx, wy) = (wx + ux, wy + uy);
            }
            i.Push(wx);
            i.Push(wy);
        });
    }

    /// <summary>
    /// An encoded number string, as xshow and the like may take: a header (149, its representation, its count) and
    /// the numbers, as 32-bit or 16-bit fixed point, or 32-bit reals, either byte order.
    /// </summary>
    public static double[] EncodedNumbers(PsString s)
    {
        if (s.Length < 4 || s[0] != 149) throw new PsError("typecheck", "number string");
        var r = (int)s[1];
        var little = r >= 128;
        var rep = r % 128;
        var count = little ? s[2] | s[3] << 8 : s[2] << 8 | s[3];
        var numbers = new double[count];
        var at = 4;
        for (var k = 0; k < count; k++)
        {
            uint U(int bytes)
            {
                uint v = 0;
                for (var b = 0; b < bytes; b++) v = little ? v | (uint)s[at + b] << (8 * b) : v << 8 | s[at + b];
                at += bytes;
                return v;
            }
            if (at + (rep < 32 || rep >= 48 ? 4 : 2) > s.Length) break;
            numbers[k] = rep switch
            {
                < 32 => (int)U(4) / Math.Pow(2, rep),
                < 48 => (short)U(2) / Math.Pow(2, rep - 32),
                48 or 49 => BitConverter.UInt32BitsToSingle(U(4)),
                _ => throw new PsError("typecheck", "number string"),
            };
        }
        return numbers;
    }

    private PsDict Font() => G.Font ?? FindFont(new PsName("Courier"));

    private static PsMatrix FontMatrix(PsDict font) => font.Get("FontMatrix") is PsArray m ? ToMatrix(m) : new PsMatrix(0.001, 0, 0, 0.001, 0, 0);

    /// <summary>A copy of a font with its matrix transformed (scalefont, makefont): the same glyphs, larger or turned.</summary>
    private static PsDict Transformed(PsDict font, PsMatrix m)
    {
        var copy = new PsDict(font.Count + 2);
        foreach (var (k, v) in font.Entries) copy.Put(k, v);
        copy.Put("FontMatrix", NewMatrix(FontMatrix(font).Then(m)));
        if (font.Get("ScaleMatrix") is null) copy.Put("OrigFont", font);
        return copy;
    }

    /// <summary>A glyph to set: the (base) font it is in, the matrix from its glyph space, its code, its name, and its CID (or -1).</summary>
    private readonly record struct GlyphRef(PsDict Font, PsMatrix Matrix, int Code, string Name, int Cid);

    private static bool IsCid(PsDict font) => font.Get("CIDFontType") is not null;

    /// <summary>
    /// The glyphs a string shows in a font. A composite (Type 0) font's bytes are split by its mapping into codes of
    /// its descendant fonts: 8/8, 1/7, 9/7 or SubsVector, or a CMap giving CIDs for a CIDFont.
    /// </summary>
    private List<GlyphRef> Codes(PsDict font, PsString s)
    {
        var fm = FontMatrix(font);
        if (font.Get("FontType") is not { } t || (int)ToNum(t) != 0)
        {
            var encoding = font.Get("Encoding") as PsArray;
            var list = new List<GlyphRef>(s.Length);
            for (var k = 0; k < s.Length; k++)
            {
                var c = (int)s[k];
                var name = encoding is not null && c < encoding.Length && encoding[c] is PsName n ? n.Value
                    : encoding is null ? Encodings.Standard[c] ?? ".notdef" : ".notdef";
                list.Add(new GlyphRef(font, fm, c, name, -1));
            }
            return list;
        }
        var map = font.Get("FMapType") is { } m ? (int)ToNum(m) : 2;
        var fdep = font.Get("FDepVector") as PsArray ?? throw new PsError("invalidfont", "FDepVector");
        var fencoding = font.Get("Encoding") as PsArray;
        var glyphs = new List<GlyphRef>();
        PsDict Descendant(int index)
        {
            var e = fencoding is not null && index < fencoding.Length ? (int)ToNum(fencoding[index]) : index;
            return e < fdep.Length && fdep[e] is PsDict d ? d : throw new PsError("rangecheck", "font number");
        }
        void Add(PsDict d, int code, int cid = -1)
        {
            if (d.Get("FontType") is { } dt && (int)ToNum(dt) == 0) throw new PsError("unsupported", "composite fonts within composite fonts");
            var matrix = FontMatrix(d).Then(fm);
            if (IsCid(d)) { glyphs.Add(new GlyphRef(d, matrix, code, "", cid < 0 ? code : cid)); return; }
            var enc = d.Get("Encoding") as PsArray;
            var name = enc is not null && code < enc.Length && enc[code] is PsName n ? n.Value : Encodings.Standard[code & 255] ?? ".notdef";
            glyphs.Add(new GlyphRef(d, matrix, code, name, -1));
        }
        switch (map)
        {
            case 2: // 8/8: a font number, then a code
                for (var k = 0; k + 1 < s.Length; k += 2) Add(Descendant(s[k]), s[k + 1]);
                break;
            case 4: // 1/7
                for (var k = 0; k < s.Length; k++) Add(Descendant(s[k] >> 7), s[k] & 127);
                break;
            case 5: // 9/7
                for (var k = 0; k + 1 < s.Length; k += 2) { var v = s[k] << 8 | s[k + 1]; Add(Descendant(v >> 7), v & 127); }
                break;
            case 6: // SubsVector: the code's range says the font
            {
                var sv = font.Get("SubsVector") as PsString ?? throw new PsError("invalidfont", "SubsVector");
                var width = sv[0] + 1;
                var ranges = new List<long>();
                for (var k = 1; k + width <= sv.Length; k += width)
                {
                    long r = 0;
                    for (var b = 0; b < width; b++) r = r << 8 | sv[k + b];
                    ranges.Add(r);
                }
                for (var k = 0; k + width <= s.Length; k += width)
                {
                    long code = 0;
                    for (var b = 0; b < width; b++) code = code << 8 | s[k + b];
                    var f = 0;
                    while (f < ranges.Count && code >= ranges[f]) { code -= ranges[f]; f++; }
                    Add(Descendant(f), (int)code);
                }
                break;
            }
            case 9: // a CMap: codes to CIDs, in the first descendant (a CIDFont)
            {
                var cmap = CMapOf(font.Get("CMap") ?? throw new PsError("invalidfont", "CMap"));
                var d = (PsDict)fdep[0];
                for (var k = 0; k < s.Length;)
                {
                    var (cid, used) = cmap.Lookup(s, k);
                    Add(d, cid, cid);
                    k += Math.Max(1, used);
                }
                break;
            }
            default:
                throw new PsError("unsupported", $"composite fonts with FMapType {map}");
        }
        return glyphs;
    }

    /// <summary>
    /// findfont: a font defined in the program, or else a printer's font by its name, drawn with the bundled
    /// Liberation fonts made to the same widths (Times to Serif, Helvetica and Arial to Sans, Courier to Mono).
    /// </summary>
    public PsDict FindFont(object key)
    {
        if (FontDirectory.Get(key) is PsDict known) return known;
        if (Resources("Font").Get(key) is PsDict resource) return resource;
        var name = Show(key);
        var n = name.ToLowerInvariant();
        // The Symbol and Dingbats fonts have characters of their own at each code, drawn from fonts that have them.
        var special = n.Contains("symbol") ? "Symbol" : n.Contains("dingbat") ? "Dingbats" : null;
        var family = special is not null ? "Liberation Serif" : n.Contains("courier") || n.Contains("mono") || n.Contains("typewriter") || n.Contains("consol") ? "Liberation Mono"
            : new[] { "helvetica", "arial", "sans", "gothic", "univers", "futura", "verdana", "tahoma", "calibri", "segoe", "frutiger", "avant", "myriad", "gill" }.Any(n.Contains) ? "Liberation Sans"
            : "Liberation Serif";
        var bold = new[] { "bold", "black", "heavy", "demi", "semibold" }.Any(n.Contains);
        var italic = new[] { "italic", "oblique", "slant", "kursiv" }.Any(n.Contains);
        var font = new PsDict(12);
        font.Put("FontType", 1);
        font.Put("FontName", new PsName(name));
        font.Put("FontMatrix", NewMatrix(new PsMatrix(0.001, 0, 0, 0.001, 0, 0)));
        font.Put("FontBBox", new PsArray([-200, -250, 1200, 950]));
        font.Put("Encoding", SystemDict.Get("StandardEncoding")!);
        font.Put("PaintType", 0);
        font.Put(SystemKey, new PsString($"{family}|{bold}|{italic}{(special is null ? "" : "|" + special)}"));
        font.Put("FID", new PsFontId(name));
        FontDirectory.Put(key, font);
        return font;
    }

    /// <summary>A glyph's outline and advance in glyph space: from a printer font, a Type 1 or a Type 42 font.</summary>
    private (PsPath Path, double Wx, double Wy) Glyph(PsDict font, int code, string name)
    {
        if (font.Get(SystemKey) is PsString sys)
        {
            var key = (sys.Text, name + "|" + code);
            if (_glyphs.TryGetValue(key, out var g)) return g;
            var parts = sys.Text.Split('|');
            var unicode = parts.Length > 3
                ? parts[3] == "Symbol" ? Encodings.Symbol(code) : Encodings.Dingbat(code)
                : Encodings.Unicode(name) ?? (code is >= 32 and < 127 ? code : null);
            var glyph = unicode is { } u ? Glyphs?.Invoke(parts[0], parts[1] == "True", parts[2] == "True", u) : null;
            if (unicode is not null && Glyphs is null) throw new PsError("unsupported", "printer fonts without their outlines");
            var r = glyph is null ? (new PsPath(), 500.0, 0.0) : (glyph.Path, glyph.Advance, 0.0);
            _glyphs[key] = r;
            return r;
        }
        var type = font.Get("FontType") is { } t ? (int)ToNum(t) : 1;
        if (font.Get(CffKey) is Cff cff) return CffGlyph(font, cff, code, name);
        if (IsCid(font)) return CidGlyph(font, code);
        if (type == 42) return TrueTypeGlyph(font, name);
        if (type == 1 || type == 4) return Type1Glyph(font, name);
        throw new PsError("unsupported", $"fonts of type {type}");
    }

    private (PsPath, double, double) Type1Glyph(PsDict font, string name)
    {
        var charStrings = font.Get("CharStrings") as PsDict ?? throw new PsError("invalidfont", "CharStrings");
        var key = ((object)charStrings, name);
        if (_glyphs.TryGetValue(key, out var g)) return g;
        var priv = font.Get("Private") as PsDict;
        var subrs = (priv?.Get("Subrs") as PsArray)?.All().Select(o => o is PsString s ? s.Span.ToArray() : []).ToList() ?? [];
        var lenIV = priv?.Get("lenIV") is { } l ? (int)ToNum(l) : 4;
        byte[]? Lookup(string n) => charStrings.Get(n) is PsString s ? s.Span.ToArray() : null;
        var cs = Lookup(name) ?? Lookup(".notdef");
        var r = cs is null ? (new PsPath(), 0.0, 0.0) : Type1.Run(cs, subrs, Lookup, lenIV) is var glyph ? (glyph.Path, glyph.AdvanceX, glyph.AdvanceY) : default;
        _glyphs[key] = r;
        return r;
    }

    private (PsPath, double, double) TrueTypeGlyph(PsDict font, string name)
    {
        var sfnts = font.Get("sfnts") as PsArray ?? throw new PsError("invalidfont", "sfnts");
        var key = ((object)sfnts.Items, name);
        if (_glyphs.TryGetValue(key, out var g)) return g;
        if (!_trueTypes.TryGetValue(sfnts.Items, out var tt))
            _trueTypes[sfnts.Items] = tt = new TrueType(TrueType.FromStrings(sfnts.All().OfType<PsString>()));
        var charStrings = font.Get("CharStrings") as PsDict;
        var index = charStrings?.Get(name) is { } i ? (int)ToNum(i) : charStrings?.Get(".notdef") is { } nd ? (int)ToNum(nd) : 0;
        var r = (tt.Outline(index), tt.Advance(index), 0.0);
        _glyphs[key] = r;
        return r;
    }

    /// <summary>A glyph's advance in glyph space; a Type 3 glyph's is what its procedure sets, so it is run (and what it paints thrown away).</summary>
    private (double X, double Y) Width(GlyphRef g)
    {
        var (font, code, name) = (g.Font, g.Cid >= 0 ? g.Cid : g.Code, g.Name);
        if (font.Get("FontType") is { } t && (int)ToNum(t) == 3)
        {
            var ops = Ops.Count;
            var w = RunType3(font, g.Code, name, PsMatrix.Identity);
            Ops.RemoveRange(ops, Ops.Count - ops);
            return w;
        }
        var (_, wx, wy) = Glyph(font, code, name);
        return (wx, wy);
    }

    /// <summary>Runs a Type 3 glyph's BuildGlyph (or BuildChar) with the glyph space put at the current point. Returns its width.</summary>
    private (double, double) RunType3(PsDict font, int code, string name, PsMatrix glyphToDevice)
    {
        Gsave();
        var keepWidth = _charWidth;
        _charWidth = null;
        try
        {
            G.Ctm = glyphToDevice;
            NewPath();
            Push(font);
            if (font.Get("BuildGlyph") is PsArray buildGlyph)
            {
                Push(new PsName(name));
                RunProc(buildGlyph);
            }
            else if (font.Get("BuildChar") is PsArray buildChar)
            {
                Push(code < 0 ? 0 : code);
                RunProc(buildChar);
            }
            else throw new PsError("invalidfont", "BuildChar");
            return _charWidth ?? (0, 0);
        }
        finally
        {
            _charWidth = keepWidth;
            GrestoreOne();
        }
    }

    private void Show(PsString s, Func<int, int, (double X, double Y)>? extra = null, Func<int, (double X, double Y)>? advance = null, Action<int, int>? between = null) =>
        ShowGlyphs(Codes(Font(), s), extra, advance, between, path: false);

    /// <summary>
    /// Sets glyphs from the current point: each drawn (or, for charpath, added to the path) and the point moved on by
    /// its advance, or by what xshow and the like give, plus what ashow and widthshow add. In user space throughout.
    /// </summary>
    private void ShowGlyphs(List<GlyphRef> glyphs, Func<int, int, (double X, double Y)>? extra, Func<int, (double X, double Y)>? advance, Action<int, int>? between, bool path)
    {
        var top = Font();
        var stroked = top.Get("PaintType") is { } pt && (int)ToNum(pt) == 2;
        var outline = new PsPath();
        for (var k = 0; k < glyphs.Count; k++)
        {
            var g = glyphs[k];
            var type3 = g.Font.Get("FontType") is { } t && (int)ToNum(t) == 3;
            if (type3 && path) throw new PsError("unsupported", "charpath with a Type 3 font");
            var cpu = Inverse.Apply(Current.X, Current.Y);
            var toDevice = g.Matrix.Then(new PsMatrix(1, 0, 0, 1, cpu.X, cpu.Y)).Then(G.Ctm);
            double wx, wy;
            if (type3) (wx, wy) = RunType3(g.Font, g.Code, g.Name, toDevice);
            else
            {
                var (glyphPath, gx, gy) = Glyph(g.Font, g.Cid >= 0 ? g.Cid : g.Code, g.Name);
                outline.Segs.AddRange(glyphPath.Transformed(toDevice).Segs);
                (wx, wy) = (gx, gy);
            }
            var (ux, uy) = advance?.Invoke(k) ?? g.Matrix.Delta(wx, wy);
            if (extra?.Invoke(k, g.Code) is var (ex, ey)) (ux, uy) = (ux + ex, uy + ey);
            var (dx, dy) = G.Ctm.Delta(ux, uy);
            G.Point = (Current.X + dx, Current.Y + dy);
            if (between is not null && k + 1 < glyphs.Count) between(g.Code, glyphs[k + 1].Code);
        }
        if (outline.IsEmpty) return;
        if (path)
        {
            // charpath: the outlines join the current path, which carries on from where the text ends.
            var point = G.Point;
            G.Path.Segs.AddRange(outline.Segs);
            G.Point = point;
            return;
        }
        if (G.Space == "Pattern") { FillWithPattern(outline, false); return; }
        if (stroked && G.Ctm.Inverse() is { } inv)
        {
            var width = top.Get("StrokeWidth") is { } sw ? ToNum(sw) : 0;
            Ops.Add(new PsStroke(outline.Transformed(inv), G.Ctm, Pen with { Width = width * Math.Abs(FontMatrix(top).A) }, G.Colour, [.. G.Clips]));
        }
        else Ops.Add(new PsFill(outline, G.Colour, false, [.. G.Clips]));
    }

    /// <summary>
    /// eexec: the encrypted part of a Type 1 font that follows, decrypted and run (with systemdict to hand), up to the
    /// zeros that end it; the program carries on after them.
    /// </summary>
    private void Eexec(object source)
    {
        byte[] cipher;
        Scanner? outer = null;
        int end = 0;
        if (source is PsFile f)
        {
            var sc = f.Scanner;
            outer = sc;
            sc.SkipBlank();
            var start = sc.Position;
            var data = sc.Data;
            // The encrypted part ends where a line of zeros (the 512 that follow it) starts.
            end = sc.End;
            for (var k = start; k + 64 <= sc.End; k++)
            {
                if (data[k] != '0') continue;
                var run = 0;
                while (k + run < sc.End && (data[k + run] == '0' || data[k + run] is 10 or 13 or 32 or 9) && run < 200) run++;
                if (data.AsSpan(k, run).ToArray().Count(b => b == '0') >= 64) { end = k; break; }
                k += run;
            }
            var hex = Enumerable.Range(start, Math.Min(4, end - start)).All(k => Scanner.Hex(data[k]) >= 0);
            cipher = hex ? Filters.AsciiHex(data.AsSpan(start, end - start)) : data.AsSpan(start, end - start).ToArray();
        }
        else if (source is PsString s) cipher = s.Span.ToArray();
        else throw new PsError("typecheck", "eexec");
        var plain = Type1.Decrypt(cipher, 55665, 4);
        if (outer is not null) outer.Position = end;
        var keep = File;
        var file = new PsFile(new Scanner(plain));
        File = file;
        _dicts.Add(SystemDict);
        var depth = _dicts.Count;
        try
        {
            RunFile(file.Scanner);
        }
        finally
        {
            File = keep;
            if (_dicts.Count >= depth && ReferenceEquals(_dicts[depth - 1], SystemDict)) _dicts.RemoveAt(depth - 1);
        }
    }
}
