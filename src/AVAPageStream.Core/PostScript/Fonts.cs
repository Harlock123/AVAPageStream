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
            if (font.Get("FontType") is { } t && (int)ToNum(t) is 0 or 2 or 9 or 10 or 11 or 32) throw new PsError("unsupported", "composite and CFF fonts");
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
        Op("composefont", _ => throw new PsError("unsupported", "composite fonts"));
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
                PsString => throw new PsError("unsupported", "encoded number strings"),
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
        Op("cshow", _ => throw new PsError("unsupported", "cshow"));
        Op("glyphshow", i => i.ShowGlyphs([(-1, Show(i.Pop()))], null, null, null, path: false));
        Op("charpath", i => { i.Bool(); var s = i.Pop<PsString>("charpath"); i.ShowGlyphs(Codes(i.Font(), s), null, null, null, path: true); });
        Op("stringwidth", i =>
        {
            var s = i.Pop<PsString>("stringwidth");
            var font = i.Font();
            var fm = FontMatrix(font);
            double wx = 0, wy = 0;
            foreach (var (code, name) in Codes(font, s))
            {
                var (gx, gy) = i.Width(font, code, name);
                var (ux, uy) = fm.Delta(gx, gy);
                (wx, wy) = (wx + ux, wy + uy);
            }
            i.Push(wx);
            i.Push(wy);
        });
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

    /// <summary>The characters of a string, each with its glyph's name in the font's encoding.</summary>
    private static List<(int Code, string Name)> Codes(PsDict font, PsString s)
    {
        if (font.Get("FontType") is { } t && (int)ToNum(t) == 0) throw new PsError("unsupported", "composite fonts");
        var encoding = font.Get("Encoding") as PsArray;
        var list = new List<(int, string)>(s.Length);
        for (var k = 0; k < s.Length; k++)
        {
            var c = (int)s[k];
            var name = encoding is not null && c < encoding.Length && encoding[c] is PsName n ? n.Value
                : encoding is null ? Encodings.Standard[c] ?? ".notdef" : ".notdef";
            list.Add((c, name));
        }
        return list;
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
        if (n.Contains("symbol") || n.Contains("dingbat")) throw new PsError("unsupported", "the " + name + " font");
        var family = n.Contains("courier") || n.Contains("mono") || n.Contains("typewriter") || n.Contains("consol") ? "Liberation Mono"
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
        font.Put(SystemKey, new PsString($"{family}|{bold}|{italic}"));
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
            var unicode = Encodings.Unicode(name) ?? (code is >= 32 and < 127 ? code : null);
            var glyph = unicode is { } u ? Glyphs?.Invoke(parts[0], parts[1] == "True", parts[2] == "True", u) : null;
            if (unicode is not null && Glyphs is null) throw new PsError("unsupported", "printer fonts without their outlines");
            var r = glyph is null ? (new PsPath(), 500.0, 0.0) : (glyph.Path, glyph.Advance, 0.0);
            _glyphs[key] = r;
            return r;
        }
        var type = font.Get("FontType") is { } t ? (int)ToNum(t) : 1;
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
    private (double X, double Y) Width(PsDict font, int code, string name)
    {
        if (font.Get("FontType") is { } t && (int)ToNum(t) == 3)
        {
            var ops = Ops.Count;
            var w = RunType3(font, code, name, PsMatrix.Identity);
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
    private void ShowGlyphs(List<(int Code, string Name)> glyphs, Func<int, int, (double X, double Y)>? extra, Func<int, (double X, double Y)>? advance, Action<int, int>? between, bool path)
    {
        var font = Font();
        var fm = FontMatrix(font);
        var type3 = font.Get("FontType") is { } t && (int)ToNum(t) == 3;
        if (type3 && path) throw new PsError("unsupported", "charpath with a Type 3 font");
        var stroked = font.Get("PaintType") is { } pt && (int)ToNum(pt) == 2;
        var outline = new PsPath();
        for (var k = 0; k < glyphs.Count; k++)
        {
            var (code, name) = glyphs[k];
            var cpu = Inverse.Apply(Current.X, Current.Y);
            var toDevice = fm.Then(new PsMatrix(1, 0, 0, 1, cpu.X, cpu.Y)).Then(G.Ctm);
            double wx, wy;
            if (type3)
            {
                (wx, wy) = RunType3(font, code, name, toDevice);
            }
            else
            {
                var (glyphPath, gx, gy) = Glyph(font, code, name);
                outline.Segs.AddRange(glyphPath.Transformed(toDevice).Segs);
                (wx, wy) = (gx, gy);
            }
            var (ux, uy) = advance?.Invoke(k) ?? fm.Delta(wx, wy);
            if (extra?.Invoke(k, code) is var (ex, ey)) (ux, uy) = (ux + ex, uy + ey);
            var (dx, dy) = G.Ctm.Delta(ux, uy);
            G.Point = (Current.X + dx, Current.Y + dy);
            if (between is not null && k + 1 < glyphs.Count) between(code, glyphs[k + 1].Code);
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
            var width = font.Get("StrokeWidth") is { } sw ? ToNum(sw) : 0;
            Ops.Add(new PsStroke(outline.Transformed(inv), G.Ctm, Pen with { Width = width * Math.Abs(fm.A) }, G.Colour, [.. G.Clips]));
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
