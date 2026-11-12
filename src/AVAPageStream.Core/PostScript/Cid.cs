namespace AVAPageStream.Core.PostScript;

/// <summary>A CMap: the byte ranges codes are made of, and the CIDs ranges of codes stand for.</summary>
public sealed class CMapData
{
    public List<(byte[] Lo, byte[] Hi)> CodeSpaces { get; } = [];
    public List<(uint Lo, uint Hi, int Bytes, int Cid)> Ranges { get; } = [];

    public static CMapData Identity()
    {
        var m = new CMapData();
        m.CodeSpaces.Add(([0, 0], [255, 255]));
        m.Ranges.Add((0, 0xFFFF, 2, 0));
        return m;
    }

    /// <summary>The CID the code at a place in a string stands for, and how many bytes the code takes.</summary>
    public (int Cid, int Bytes) Lookup(PsString s, int at)
    {
        foreach (var (lo, hi) in CodeSpaces.OrderBy(c => c.Lo.Length))
        {
            if (at + lo.Length > s.Length) continue;
            var inside = true;
            uint code = 0;
            for (var k = 0; k < lo.Length; k++)
            {
                var b = s[at + k];
                if (b < lo[k] || b > hi[k]) { inside = false; break; }
                code = code << 8 | b;
            }
            if (!inside) continue;
            foreach (var r in Ranges)
                if (r.Bytes == lo.Length && code >= r.Lo && code <= r.Hi) return ((int)(r.Cid + code - r.Lo), lo.Length);
            return (0, lo.Length);
        }
        return (0, 1);
    }
}

public sealed partial class Interpreter
{
    private const string CMapKey = "AVAPageStream.CMap";

    /// <summary>A CMap given by name (Identity-H and -V are built in) or as its dictionary.</summary>
    private CMapData CMapOf(object cmap)
    {
        if (cmap is PsDict d && d.Get(CMapKey) is CMapData data) return data;
        var name = cmap is PsDict nd && nd.Get("CMapName") is { } n ? Show(n) : Show(cmap);
        if (name is "Identity-H" or "Identity-V") return CMapData.Identity();
        if (Resources("CMap").Get(name) is PsDict defined && defined.Get(CMapKey) is CMapData dd) return dd;
        if (PredefinedCMaps.Get(name) is { } predefined) return predefined;
        throw new PsError("unsupported", "the CMap " + name);
    }

    /// <summary>The CMap being defined (in the dictionary on top of the dictionary stack).</summary>
    private CMapData Building()
    {
        if (CurrentDict.Get(CMapKey) is CMapData m) return m;
        var made = new CMapData();
        CurrentDict.Put(CMapKey, made);
        return made;
    }

    private static byte[] Bytes(object o) => o is PsString s ? s.Span.ToArray() : BitConverter.GetBytes((ushort)ToNum(o)).Reverse().ToArray();

    private static uint Value(byte[] b) => b.Aggregate(0u, (v, x) => v << 8 | x);

    /// <summary>The CIDInit procedure set: the operators a CMap or a CIDFont program uses as it is defined.</summary>
    private PsDict CidInit()
    {
        if (SystemDict.Get("AVAPageStream.CIDInit") is PsDict made) return made;
        var d = new PsDict(32);
        void Op(string name, Action<Interpreter> run) => d.Put(name, new PsOperator(name, run));
        Op("begincmap", _ => { });
        Op("endcmap", i => i.Building());
        Op("usecmap", i => { var other = i.CMapOf(i.Pop()); var m = i.Building(); m.CodeSpaces.AddRange(other.CodeSpaces); m.Ranges.AddRange(other.Ranges); });
        Op("usefont", i => i.Pop());
        foreach (var start in new[] { "begincodespacerange", "begincidrange", "begincidchar", "beginnotdefrange", "beginnotdefchar", "beginbfchar", "beginbfrange", "beginusematrix" })
            Op(start, i => { i.Pop(); i.Push(PsMark.Instance); });
        Op("endcodespacerange", i =>
        {
            var items = i.ToMark();
            for (var k = 0; k + 1 < items.Count; k += 2) i.Building().CodeSpaces.Add((Bytes(items[k]), Bytes(items[k + 1])));
        });
        Op("endcidrange", i =>
        {
            var items = i.ToMark();
            for (var k = 0; k + 2 < items.Count; k += 3)
            {
                var lo = Bytes(items[k]);
                i.Building().Ranges.Add((Value(lo), Value(Bytes(items[k + 1])), lo.Length, (int)ToNum(items[k + 2])));
            }
        });
        Op("endcidchar", i =>
        {
            var items = i.ToMark();
            for (var k = 0; k + 1 < items.Count; k += 2)
            {
                var c = Bytes(items[k]);
                i.Building().Ranges.Add((Value(c), Value(c), c.Length, (int)ToNum(items[k + 1])));
            }
        });
        foreach (var end in new[] { "endnotdefrange", "endnotdefchar", "endbfchar", "endbfrange", "endusematrix" })
            Op(end, i => i.ToMark());
        Op("StartData", i => i.StartData());
        Op("GlyphDirectory", i => i.Push(new PsDict()));
        SystemDict.Put("AVAPageStream.CIDInit", d);
        return d;
    }

    /// <summary>Everything down to the mark, in order, and the mark taken off.</summary>
    private List<object> ToMark()
    {
        var n = CountToMark();
        var items = _stack.GetRange(_stack.Count - n, n);
        _stack.RemoveRange(_stack.Count - n - 1, n + 1);
        return items;
    }

    /// <summary>
    /// StartData (CIDInit's): the CIDFont's binary or hex GlyphData, read from the program, and the CIDFont defined
    /// from the dictionary being built.
    /// </summary>
    private void StartData()
    {
        var count = Int();
        var kind = Show(Pop());
        var file = File ?? throw new PsError("undefined", "StartData");
        var sc = file.Scanner;
        byte[] data;
        if (kind == "Hex")
        {
            var bytes = new List<byte>(count);
            var half = -1;
            while (bytes.Count < count && sc.Read() is var c and >= 0)
            {
                var v = Scanner.Hex(c);
                if (v < 0) continue;
                if (half < 0) half = v; else { bytes.Add((byte)(half << 4 | v)); half = -1; }
            }
            data = [.. bytes];
        }
        else
        {
            data = new byte[count];
            for (var k = 0; k < count && sc.Read() is var c and >= 0; k++) data[k] = (byte)c;
        }
        var font = CurrentDict;
        font.Put("GlyphData", PsString.Of(data));
        font.Put("FID", new PsFontId(font.Get("CIDFontName") is { } n ? Show(n) : "CIDFont"));
        if (font.Get("CIDFontName") is { } name) Resources("CIDFont").Put(name, font);
        if (_dicts.Count > 2) _dicts.RemoveAt(_dicts.Count - 1);
    }

    /// <summary>composefont: a Type 0 font of CIDFonts (or fonts) with a CMap.</summary>
    private PsDict ComposeFont(object key, object cmap, PsArray fonts)
    {
        var font = new PsDict(12);
        font.Put("FontType", 0);
        font.Put("FMapType", 9);
        font.Put("FontName", key is PsName n ? n : new PsName(Show(key)));
        font.Put("FontMatrix", NewMatrix(PsMatrix.Identity));
        font.Put("CMap", cmap is PsDict ? cmap : Resources("CMap").Get(cmap) ?? cmap);
        var descendants = fonts.All().Select(f => f is PsDict d ? d : Resources("CIDFont").Get(f) as PsDict ?? FindFont(f)).Select(o => (object)o).ToList();
        font.Put("FDepVector", new PsArray(descendants));
        font.Put("Encoding", new PsArray(Enumerable.Range(0, descendants.Count).Select(k => (object)k).ToList()));
        font.Put("FID", new PsFontId(Show(key)));
        Resources("Font").Put(key, font);
        FontDirectory.Put(key, font);
        return font;
    }

    /// <summary>
    /// A CIDFont's glyph for a CID. CIDFontType 2: a TrueType glyph, by its CIDMap. CIDFontType 0: a Type 1 (or CFF)
    /// charstring from its GlyphDirectory or its GlyphData, with its font dictionary's subroutines, in that
    /// dictionary's glyph space.
    /// </summary>
    private (PsPath Path, double Wx, double Wy) CidGlyph(PsDict font, int cid)
    {
        var key = (font.Get("GlyphData") ?? font.Get("sfnts") ?? font.Get("GlyphDirectory") ?? (object)font, "cid" + cid);
        if (_glyphs.TryGetValue(key, out var cached)) return cached;
        var type = (int)ToNum(font.Get("CIDFontType")!);
        (PsPath, double, double) r;
        if (type == 2)
        {
            var sfnts = font.Get("sfnts") as PsArray ?? throw new PsError("invalidfont", "sfnts");
            if (!_trueTypes.TryGetValue(sfnts.Items, out var tt))
                _trueTypes[sfnts.Items] = tt = new TrueType(TrueType.FromStrings(sfnts.All().OfType<PsString>()));
            var gdBytes = font.Get("GDBytes") is { } gb ? (int)ToNum(gb) : 2;
            var gid = font.Get("CIDMap") switch
            {
                int offset => cid + offset,
                double offset => cid + (int)offset,
                PsString s => Entry(s.Span.ToArray(), cid, gdBytes),
                PsArray a => Entry(a.All().OfType<PsString>().SelectMany(s => s.Span.ToArray()).ToArray(), cid, gdBytes),
                PsDict d => d.Get(cid) is { } g ? (int)ToNum(g) : 0,
                _ => cid,
            };
            r = (tt.Outline(gid), tt.Advance(gid), 0);
        }
        else if (type == 0)
        {
            var fdBytes = font.Get("FDBytes") is { } fb ? (int)ToNum(fb) : 1;
            var gdBytes = font.Get("GDBytes") is { } gb ? (int)ToNum(gb) : 2;
            var fdArray = font.Get("FDArray") as PsArray ?? throw new PsError("invalidfont", "FDArray");
            byte[] glyph;
            int fd;
            if (font.Get("GlyphDirectory") is { } dir)
            {
                var entry = dir switch { PsDict d => d.Get(cid) as PsString, PsArray a when cid < a.Length => a[cid] as PsString, _ => null };
                if (entry is null || entry.Length <= fdBytes) { r = (new PsPath(), 0, 0); _glyphs[key] = r; return r; }
                fd = fdBytes == 0 ? 0 : (int)Value(entry.Span[..fdBytes].ToArray());
                glyph = entry.Span[fdBytes..].ToArray();
            }
            else
            {
                var data = GlyphData(font);
                var mapOffset = font.Get("CIDMapOffset") is { } mo ? (int)ToNum(mo) : 0;
                var width = fdBytes + gdBytes;
                var at = mapOffset + cid * width;
                if (at + 2 * width > data.Length) { r = (new PsPath(), 0, 0); _glyphs[key] = r; return r; }
                fd = fdBytes == 0 ? 0 : (int)Value(data[at..(at + fdBytes)]);
                var start = (int)Value(data[(at + fdBytes)..(at + width)]);
                var end = (int)Value(data[(at + width + fdBytes)..(at + 2 * width)]);
                glyph = end > start && end <= data.Length ? data[start..end] : [];
            }
            if (fd >= fdArray.Length || fdArray[fd] is not PsDict fdDict || glyph.Length == 0) { r = (new PsPath(), 0, 0); _glyphs[key] = r; return r; }
            var priv = fdDict.Get("Private") as PsDict;
            var lenIV = priv?.Get("lenIV") is { } l ? (int)ToNum(l) : -1;
            var subrs = new List<byte[]>();
            if (priv?.Get("SubrMapOffset") is { } so && priv.Get("SubrCount") is { } sc)
            {
                var data = GlyphData(font);
                var sd = priv.Get("SDBytes") is { } sdb ? (int)ToNum(sdb) : 2;
                for (var k = 0; k < (int)ToNum(sc); k++)
                {
                    var p = (int)ToNum(so) + k * sd;
                    if (p + 2 * sd > data.Length) break;
                    var a = (int)Value(data[p..(p + sd)]);
                    var b = (int)Value(data[(p + sd)..(p + 2 * sd)]);
                    subrs.Add(b > a && b <= data.Length ? data[a..b] : []);
                }
            }
            else if (priv?.Get("Subrs") is PsArray sa) subrs.AddRange(sa.All().Select(o => o is PsString s ? s.Span.ToArray() : []));
            var fdType = fdDict.Get("FontType") is { } ft ? (int)ToNum(ft) : 1;
            var drawn = fdType == 2 ? Cff.Run(glyph, subrs, GlobalSubrs(font), priv) : Type1.Run(glyph, subrs, _ => null, lenIV) is var t1 ? new Type1.Glyph(t1.Path, t1.AdvanceX, t1.AdvanceY) : null;
            var fm = FontMatrix(fdDict);
            var (wx, wy) = fm.Delta(drawn!.AdvanceX, drawn.AdvanceY);
            r = (drawn.Path.Transformed(fm), wx, wy);
        }
        else throw new PsError("unsupported", $"CIDFontType {type}");
        _glyphs[key] = r;
        return r;
    }

    private static int Entry(byte[] map, int cid, int bytes) =>
        cid * bytes + bytes <= map.Length ? (int)Value(map[(cid * bytes)..(cid * bytes + bytes)]) : 0;

    private static byte[] GlyphData(PsDict font) => font.Get("GlyphData") switch
    {
        PsString s => s.Span.ToArray(),
        PsArray a => a.All().OfType<PsString>().SelectMany(s => s.Span.ToArray()).ToArray(),
        _ => throw new PsError("invalidfont", "GlyphData"),
    };

    private static List<byte[]> GlobalSubrs(PsDict font) =>
        (font.Get("GlobalSubrs") as PsArray)?.All().Select(o => o is PsString s ? s.Span.ToArray() : []).ToList() ?? [];
}
