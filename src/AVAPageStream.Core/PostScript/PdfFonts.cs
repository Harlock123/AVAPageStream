namespace AVAPageStream.Core.PostScript;

/// <summary>
/// A PDF font, ready to set: how a string's bytes split into codes, each code's outline (in text space, at a size of
/// 1) and its width (thousandths of text space; a Type 3 font's in its own glyph space).
/// </summary>
internal sealed class PdfFont
{
    public string Name = "";
    /// <summary>A composite font's CMap (codes of one or more bytes to CIDs); null for a simple font's single bytes.</summary>
    public CMapData? CMap;
    /// <summary>A Type 3 font: its dictionary, whose glyphs are content streams.</summary>
    public PsDict? Type3;
    public string?[]? Names;  // a Type 3 font's code to glyph name
    /// <summary>Written top to bottom (a -V CMap): each CID's advance down and its origin's place, in thousandths.</summary>
    public bool Vertical;
    public Func<int, (double W1, double Vx, double Vy)> VerticalMetrics = _ => (-1000, 500, 880);
    public PsMatrix FontMatrix = new(0.001, 0, 0, 0.001, 0, 0);
    public Func<int, PsPath> Outline = _ => new PsPath();
    public Func<int, double> Width = _ => 0;
    /// <summary>What it couldn't do, if it was drawn some other way (a printer font for a missing one, say).</summary>
    public string? Note;
    private readonly Dictionary<int, PsPath> _outlines = [];

    public PsPath Glyph(int code)
    {
        if (_outlines.TryGetValue(code, out var p)) return p;
        try { p = Outline(code); }
        catch (Exception e) when (e is PsError or IndexOutOfRangeException or ArgumentException or InvalidCastException or NullReferenceException or KeyNotFoundException)
        {
            p = new PsPath();
            Note ??= $"some of the glyphs of its font {Name} couldn't be read ({e.Message})";
        }
        _outlines[code] = p;
        return p;
    }

    /// <summary>The codes a string shows: each with the bytes it took, so a single-byte 32 can be widened as a space.</summary>
    public List<(int Code, int Bytes)> Codes(PsString s)
    {
        var list = new List<(int, int)>(s.Length);
        if (CMap is null)
        {
            for (var k = 0; k < s.Length; k++) list.Add((s[k], 1));
            return list;
        }
        for (var k = 0; k < s.Length;)
        {
            var (cid, used) = CMap.Lookup(s, k);
            list.Add((cid, Math.Max(1, used)));
            k += Math.Max(1, used);
        }
        return list;
    }
}

public sealed partial class Interpreter
{
    private readonly Dictionary<object, PdfFont> _pdfFonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Dictionary<int, int>> _byCid = new(ReferenceEqualityComparer.Instance);

    private static double PdfNum(object? o, double otherwise = 0) => o is int or double ? ToNum(o) : otherwise;

    private static string? PdfName(object? o) => (o as PsName)?.Value;

    /// <summary>A font resource as a PdfFont (each dictionary once).</summary>
    private PdfFont PdfFontOf(PsDict d)
    {
        if (_pdfFonts.TryGetValue(d, out var known)) return known;
        PdfFont font;
        try { font = LoadPdfFont(d); }
        catch (Exception e) when (e is PsError or IndexOutOfRangeException or ArgumentException or InvalidCastException or PdfException or InvalidDataException)
        {
            font = SystemPdfFont(d, Base(d)) ?? new PdfFont { Name = Base(d) };
            font.Note = $"its font {Base(d)} couldn't be read, so a printer font stands in";
        }
        _pdfFonts[d] = font;
        return font;
    }

    /// <summary>A font's name without the six letters and + of a subset.</summary>
    private static string Base(PsDict d)
    {
        var name = PdfName(d.Get("BaseFont")) ?? PdfName(d.Get("Name")) ?? "Helvetica";
        return name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper) ? name[7..] : name;
    }

    private PdfFont LoadPdfFont(PsDict d)
    {
        var subtype = PdfName(d.Get("Subtype")) ?? "Type1";
        if (subtype == "Type3") return Type3Font(d);
        if (subtype == "Type0") return CompositeFont(d);
        return SimpleFont(d, subtype);
    }

    // ---- Simple fonts

    /// <summary>A simple font's code to glyph name: its base encoding (or the font's own), then its Differences.</summary>
    private static string?[] PdfEncoding(PsDict d, string?[]? builtIn)
    {
        var enc = d.Get("Encoding");
        string?[] names = builtIn is not null ? [.. builtIn] : [.. Encodings.Standard];
        switch (enc)
        {
            case PsName n when PdfEncodings.Named(n.Value) is { } named:
                names = [.. named];
                break;
            case PsDict ed:
                if (PdfName(ed.Get("BaseEncoding")) is { } bn && PdfEncodings.Named(bn) is { } based) names = [.. based];
                if (ed.Get("Differences") is PsArray diffs)
                {
                    var code = 0;
                    foreach (var item in diffs.All())
                    {
                        if (item is int or double) code = (int)ToNum(item);
                        else if (item is PsName name && code is >= 0 and < 256) names[code++] = name.Value;
                    }
                }
                break;
        }
        return names;
    }

    /// <summary>
    /// Whether an embedded font's codes start from its own encoding: when the PDF gives none, gives differences with no
    /// base encoding, or (for a symbolic font) only names one.
    /// </summary>
    private static bool OwnEncodingFirst(PsDict d, bool symbolic) => d.Get("Encoding") switch
    {
        null => true,
        PsDict e => e.Get("BaseEncoding") is null,
        _ => symbolic,
    };

    private static (int First, double[] Widths, double Missing) SimpleWidths(PsDict d)
    {
        var first = (int)PdfNum(d.Get("FirstChar"));
        var widths = (d.Get("Widths") as PsArray)?.All().Select(o => PdfNum(o)).ToArray() ?? [];
        var missing = (d.Get("FontDescriptor") as PsDict)?.Get("MissingWidth") is { } m ? PdfNum(m) : 0;
        return (first, widths, missing);
    }

    private PdfFont SimpleFont(PsDict d, string subtype)
    {
        var desc = d.Get("FontDescriptor") as PsDict;
        var (first, widths, missing) = SimpleWidths(d);
        var hasWidths = widths.Length > 0;
        double Given(int code, Func<double> otherwise) =>
            hasWidths ? code - first is var i && i >= 0 && i < widths.Length ? widths[i] : missing : otherwise();
        var symbolic = desc?.Get("Flags") is { } fl && ((int)PdfNum(fl) & 4) != 0;
        var font = new PdfFont { Name = Base(d) };

        if (desc?.Get("FontFile") is PsDict ff1 && Program(ff1) is { } t1)
        {
            var ps = RunType1Program(t1);
            var builtIn = (ps.Get("Encoding") as PsArray)?.All().Select(o => (o as PsName)?.Value).ToArray();
            var names = PdfEncoding(d, OwnEncodingFirst(d, symbolic) ? builtIn : null);
            var fm = FontMatrix(ps);
            string Name(int c) => c < names.Length ? names[c] ?? ".notdef" : ".notdef";
            font.Outline = c => Type1Glyph(ps, Name(c)) is var (p, _, _) ? p.Transformed(fm) : new PsPath();
            font.Width = c => Given(c, () => Type1Glyph(ps, Name(c)) is var (_, wx, _) ? fm.Delta(wx, 0).X * 1000 : 0);
            return font;
        }
        if (desc?.Get("FontFile3") is PsDict ff3 && Program(ff3) is { } data3)
        {
            var sub = PdfName(ff3.Get("Subtype"));
            if (sub == "OpenType" && OpenTypeCff(data3) is { } inner) data3 = inner;
            else if (sub == "OpenType") return TrueTypeSimple(d, font, new TrueType(data3), symbolic, Given);
            var cff = Cff.Read(data3)[0];
            var builtIn = new string?[256];
            for (var c = 0; c < 256; c++)
                builtIn[c] = cff.Encoding is { } ce ? ce.TryGetValue(c, out var g) && g < cff.Names.Length ? cff.Names[g] : null : Encodings.Standard[c];
            var names = PdfEncoding(d, OwnEncodingFirst(d, symbolic) ? builtIn : null);
            int Gid(int c)
            {
                var name = c < names.Length ? names[c] : null;
                var g = name is null ? -1 : Array.IndexOf(cff.Names, name);
                if (g < 0 && cff.Encoding is { } e && e.TryGetValue(c, out var eg)) g = eg;
                if (g < 0 && cff.IsCid) g = Array.IndexOf(cff.Cids, c);
                return Math.Max(0, g);
            }
            font.Outline = c => cff.Glyph(Gid(c)).Path.Transformed(cff.FontMatrix);
            font.Width = c => Given(c, () => cff.FontMatrix.Delta(cff.Glyph(Gid(c)).AdvanceX, 0).X * 1000);
            return font;
        }
        if (desc?.Get("FontFile2") is PsDict ff2 && Program(ff2) is { } data2)
            return TrueTypeSimple(d, font, new TrueType(data2), symbolic, Given);
        return SystemPdfFont(d, Base(d)) ?? font;
    }

    /// <summary>
    /// A simple TrueType font: codes to glyphs by its (3,0) symbol map, or by glyph names through its (3,1) Unicode
    /// map, its (1,0) Mac map or its post table; failing all of them, the code is the glyph's number.
    /// </summary>
    private PdfFont TrueTypeSimple(PsDict d, PdfFont font, TrueType tt, bool symbolic, Func<int, Func<double>, double> given)
    {
        var win = tt.Cmap(3, 1);
        var sym = tt.Cmap(3, 0);
        var mac = tt.Cmap(1, 0);
        var names = PdfEncoding(d, null);
        var hasEncoding = d.Get("Encoding") is not null;
        Dictionary<string, int>? post = null;
        int Gid(int c)
        {
            if ((symbolic || !hasEncoding) && sym is not null)
            {
                if (sym.TryGetValue(0xF000 + c, out var g) || sym.TryGetValue(c, out g) || sym.TryGetValue(0xF100 + c, out g) || sym.TryGetValue(0xF200 + c, out g)) return g;
            }
            var name = c < names.Length ? names[c] : null;
            if (!symbolic || hasEncoding)
            {
                if (name is not null && Encodings.Unicode(name) is { } u && win is not null && win.TryGetValue(u, out var wg)) return wg;
                if (name is not null && mac is not null && Array.IndexOf(PdfEncodings.MacRoman, name) is var mc and >= 0 && mac.TryGetValue(mc, out var mg)) return mg;
                if (name is not null && (post ??= tt.PostNames()).TryGetValue(name, out var pg)) return pg;
            }
            if (mac is not null && mac.TryGetValue(c, out var m2)) return m2;
            if (win is not null && win.TryGetValue(c, out var w2)) return w2;
            if (sym is not null && sym.TryGetValue(0xF000 + c, out var s2)) return s2;
            return c;
        }
        font.Outline = c => tt.Outline(Gid(c));
        font.Width = c => given(c, () => tt.Advance(Gid(c)) * 1000);
        return font;
    }

    /// <summary>A font not embedded: the printer font of its name, drawn with the bundled Liberation fonts.</summary>
    private PdfFont? SystemPdfFont(PsDict d, string name)
    {
        var desc = d.Get("FontDescriptor") as PsDict;
        var flags = desc?.Get("Flags") is { } f ? (int)PdfNum(f) : 0;
        var weight = desc?.Get("FontWeight") is { } w ? PdfNum(w) : 400;
        // The name says how it looks; failing that, its descriptor does (fixed pitch, serif, italic, bold).
        var look = name.Replace(",", "-");
        if ((flags & 1) != 0 && !look.Contains("Courier", StringComparison.OrdinalIgnoreCase)) look += "-Mono";
        else if ((flags & 2) == 0 && (flags & 32) != 0 && !look.Contains("Times", StringComparison.OrdinalIgnoreCase) && !look.Contains("Serif", StringComparison.OrdinalIgnoreCase)) look += "-Sans";
        if ((flags & 64) != 0) look += "-Italic";
        if (weight >= 600 || (flags & 0x40000) != 0) look += "-Bold";
        var ps = FindFont(new PsName(look));
        var symbolFont = name.Contains("Symbol", StringComparison.OrdinalIgnoreCase) || name.Contains("Dingbat", StringComparison.OrdinalIgnoreCase);
        // The base of a printer font's encoding: Standard for a Type 1 font, WinAnsi as a TrueType one is usually written.
        var trueType = PdfName(d.Get("Subtype")) == "TrueType";
        var names = PdfEncoding(d, symbolFont ? null : trueType ? PdfEncodings.WinAnsi : Encodings.Standard);
        var (first, widths, missing) = SimpleWidths(d);
        var fm = FontMatrix(ps);
        string Name(int c) => c < names.Length ? names[c] ?? ".notdef" : ".notdef";
        return new PdfFont
        {
            Name = name,
            Outline = c => Glyph(ps, c, Name(c)) is var (p, _, _) ? p.Transformed(fm) : new PsPath(),
            Width = c => widths.Length > 0
                ? c - first is var i && i >= 0 && i < widths.Length ? widths[i] : missing
                : Glyph(ps, c, Name(c)) is var (_, wx, _) ? fm.Delta(wx, 0).X * 1000 : 500,
        };
    }

    private byte[]? Program(PsDict stream) => _pdf is { } r && r.Decode(stream) is { Length: > 0 } b ? b : null;

    /// <summary>Runs a Type 1 font program (the clear part, then its eexec part, binary or hex) and gives the font it defines.</summary>
    private PsDict RunType1Program(byte[] program)
    {
        // A PFB's segment headers, if the font was embedded with them.
        if (program is [0x80, 1, ..])
        {
            var plain = new List<byte>();
            for (var at = 0; at + 6 <= program.Length && program[at] == 0x80 && program[at + 1] is 1 or 2;)
            {
                var len = BitConverter.ToInt32(program, at + 2);
                plain.AddRange(program.AsSpan(at + 6, Math.Min(len, program.Length - at - 6)).ToArray());
                at += 6 + len;
            }
            program = [.. plain];
        }
        var before = FontDirectory.Entries.Select(e => e.Value).ToHashSet(ReferenceEqualityComparer.Instance);
        var keepFile = File;
        var keepStack = _stack.Count;
        var error = Run(program);
        File = keepFile;
        if (_stack.Count > keepStack) _stack.RemoveRange(keepStack, _stack.Count - keepStack);
        var made = FontDirectory.Entries.Select(e => e.Value).OfType<PsDict>().LastOrDefault(f => !before.Contains(f));
        return made ?? throw new PdfException(error is null ? "its Type 1 font defined no font" : $"its Type 1 font stopped with {error.Message}");
    }

    /// <summary>The CFF table of an OpenType font, if it has one (else it has TrueType outlines).</summary>
    private static byte[]? OpenTypeCff(byte[] otf)
    {
        if (otf.Length < 12) return null;
        var count = otf[4] << 8 | otf[5];
        for (var i = 0; i < count; i++)
        {
            var e = 12 + i * 16;
            if (e + 16 > otf.Length) break;
            if (otf[e] == 'C' && otf[e + 1] == 'F' && otf[e + 2] == 'F' && otf[e + 3] == ' ')
            {
                var off = otf[e + 8] << 24 | otf[e + 9] << 16 | otf[e + 10] << 8 | otf[e + 11];
                var len = otf[e + 12] << 24 | otf[e + 13] << 16 | otf[e + 14] << 8 | otf[e + 15];
                if (off >= 0 && len > 0 && off + len <= otf.Length) return otf[off..(off + len)];
            }
        }
        return null;
    }

    // ---- Type 3

    private PdfFont Type3Font(PsDict d)
    {
        var fm = d.Get("FontMatrix") is PsArray m ? ToMatrix(m) : new PsMatrix(0.001, 0, 0, 0.001, 0, 0);
        var (first, widths, missing) = SimpleWidths(d);
        return new PdfFont
        {
            Name = Base(d),
            Type3 = d,
            FontMatrix = fm,
            Width = c => c - first is var i && i >= 0 && i < widths.Length ? widths[i] : missing,
        };
    }

    // ---- Composite fonts

    private PdfFont CompositeFont(PsDict d)
    {
        var cmap = d.Get("Encoding") switch
        {
            PsName { Value: "Identity-H" or "Identity-V" } => CMapData.Identity(),
            PsDict s when _pdf?.Decode(s) is { } data => ReadCMap(data, cid: true),
            PsName n => PredefinedCMaps.Get(n.Value) ?? throw new PdfException($"its font {Base(d)} uses the CMap {n.Value}, which AVAPageStream doesn't have"),
            _ => CMapData.Identity(),
        };
        var vertical = d.Get("Encoding") switch
        {
            PsName n => PredefinedCMaps.Vertical(n.Value),
            PsDict s => PdfNum(s.Get("WMode")) == 1,
            _ => false,
        };
        var cidFont = (d.Get("DescendantFonts") as PsArray)?.All().OfType<PsDict>().FirstOrDefault() ?? throw new PdfException("a Type 0 font without its CIDFont");
        var desc = cidFont.Get("FontDescriptor") as PsDict;
        var dw = cidFont.Get("DW") is { } dwv ? PdfNum(dwv) : 1000;
        var w = new Dictionary<int, double>();
        if (cidFont.Get("W") is PsArray wa)
        {
            var items = wa.All().ToList();
            for (var k = 0; k + 1 < items.Count;)
            {
                var c0 = (int)PdfNum(items[k]);
                if (items[k + 1] is PsArray each) { var i = 0; foreach (var v in each.All()) w[c0 + i++] = PdfNum(v); k += 2; }
                else if (k + 2 < items.Count) { var c1 = (int)PdfNum(items[k + 1]); var v = PdfNum(items[k + 2]); for (var c = c0; c <= c1 && c - c0 < 65536; c++) w[c] = v; k += 3; }
                else break;
            }
        }
        var font = new PdfFont { Name = Base(d), CMap = cmap, Vertical = vertical };
        double Width(int cid) => w.TryGetValue(cid, out var v) ? v : dw;
        if (vertical)
        {
            // DW2 (the origin's height and the advance down) and W2 (each CID's advance and origin).
            var dw2 = (cidFont.Get("DW2") as PsArray)?.All().Select(o => PdfNum(o)).ToArray() is { Length: 2 } given ? given : [880, -1000];
            var w2 = new Dictionary<int, (double, double, double)>();
            if (cidFont.Get("W2") is PsArray wa2)
            {
                var items = wa2.All().ToList();
                for (var k = 0; k + 1 < items.Count;)
                {
                    var c0 = (int)PdfNum(items[k]);
                    if (items[k + 1] is PsArray each)
                    {
                        var e = each.All().Select(o => PdfNum(o)).ToArray();
                        for (var i = 0; i + 2 < e.Length; i += 3) w2[c0 + i / 3] = (e[i], e[i + 1], e[i + 2]);
                        k += 2;
                    }
                    else if (k + 4 < items.Count)
                    {
                        var c1 = (int)PdfNum(items[k + 1]);
                        var m = (PdfNum(items[k + 2]), PdfNum(items[k + 3]), PdfNum(items[k + 4]));
                        for (var c = c0; c <= c1 && c - c0 < 65536; c++) w2[c] = m;
                        k += 5;
                    }
                    else break;
                }
            }
            font.VerticalMetrics = cid => w2.TryGetValue(cid, out var m) ? m : (dw2[1], Width(cid) / 2, dw2[0]);
        }
        if (desc?.Get("FontFile2") is PsDict ff2 && Program(ff2) is { } ttf)
        {
            var tt = new TrueType(ttf);
            byte[]? map = cidFont.Get("CIDToGIDMap") is PsDict ms ? _pdf!.Decode(ms) : null;
            int Gid(int cid) => map is null ? cid : cid * 2 + 1 < map.Length ? map[cid * 2] << 8 | map[cid * 2 + 1] : 0;
            font.Outline = cid => tt.Outline(Gid(cid));
            font.Width = Width;
            return font;
        }
        if (desc?.Get("FontFile3") is PsDict ff3 && Program(ff3) is { } cffData)
        {
            if (PdfName(ff3.Get("Subtype")) == "OpenType")
            {
                if (OpenTypeCff(cffData) is { } inner) cffData = inner;
                else
                {
                    var tt = new TrueType(cffData);
                    font.Outline = cid => tt.Outline(cid);
                    font.Width = Width;
                    return font;
                }
            }
            var cff = Cff.Read(cffData)[0];
            int Gid(int cid) => cff.IsCid ? Math.Max(0, Array.IndexOf(cff.Cids, cid)) : cid;
            font.Outline = cid => cff.Glyph(Gid(cid)).Path.Transformed(cff.FontMatrix);
            font.Width = Width;
            return font;
        }
        if (desc?.Get("FontFile") is PsDict) throw new PdfException("a CIDFont from a Type 1 program");
        // Not embedded: its characters (by its ToUnicode map, or by its character collection's), in the nearest
        // printer font, or a font on the computer that has them (a Japanese, Chinese or Korean one for those).
        var unicode = d.Get("ToUnicode") is PsDict tu && _pdf?.Decode(tu) is { } tud ? ReadToUnicode(tud) : null;
        var ordering = ((cidFont.Get("CIDSystemInfo") as PsDict)?.Get("Ordering") as PsString)?.Text;
        // A ToUnicode map is by code; the collection's by CID.
        var byCode = unicode;
        var ps = FindFont(new PsName(Base(d)));
        var fm = FontMatrix(ps);
        int? Char(int cid) => byCode is not null && Reverse(byCode).TryGetValue(cid, out var u) ? u : ordering is not null ? PredefinedCMaps.Unicode(ordering, cid) : null;
        font.Outline = cid => Char(cid) is { } u && Glyphs?.Invoke(SystemFamily(ps), Bold(ps), Italic(ps), u) is { } g ? g.Path.Transformed(fm) : new PsPath();
        font.Width = Width;
        font.Note = unicode is null && ordering is not ("Japan1" or "GB1" or "CNS1" or "Korea1" or "KR") ? $"its font {Base(d)} isn't in it, and has no list of its characters" : null;
        return font;

        // The ToUnicode map gives characters by code; the glyphs are asked for by CID, so codes are turned to CIDs.
        Dictionary<int, int> Reverse(Dictionary<int, int> byCodeMap)
        {
            if (_byCid.TryGetValue(byCodeMap, out var made)) return made;
            made = [];
            foreach (var (code, u) in byCodeMap)
            {
                var bytes = code > 0xFFFF ? new[] { (byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code } : code > 0xFF || cmap.CodeSpaces.All(s => s.Lo.Length == 2) ? new[] { (byte)(code >> 8), (byte)code } : new[] { (byte)code };
                var (cid, _) = cmap.Lookup(PsString.Of(bytes), 0);
                made.TryAdd(cid, u);
            }
            return _byCid[byCodeMap] = made;
        }
    }

    private static string SystemFamily(PsDict ps) => (ps.Get(SystemKey) as PsString)?.Text.Split('|')[0] ?? "Liberation Sans";
    private static bool Bold(PsDict ps) => (ps.Get(SystemKey) as PsString)?.Text.Split('|')[1] == "True";
    private static bool Italic(PsDict ps) => (ps.Get(SystemKey) as PsString)?.Text.Split('|')[2] == "True";

    /// <summary>
    /// An embedded CMap: its code space ranges, and its codes to CIDs (cidrange, cidchar) — or, read as a ToUnicode map,
    /// codes to the first character of what they stand for (bfrange, bfchar).
    /// </summary>
    private static CMapData ReadCMap(byte[] data, bool cid)
    {
        var map = new CMapData();
        var lex = new PdfLexer(data);
        var words = new List<object>();
        while (lex.ReadObject() is { } o)
        {
            if (o is not PdfKeyword k) { words.Add(o); continue; }
            switch (k.Value)
            {
                case "usecmap":
                    if (words.LastOrDefault() is PsName { Value: "Identity-H" or "Identity-V" })
                    {
                        map.CodeSpaces.Add(([0, 0], [255, 255]));
                        map.Ranges.Add((0, 0xFFFF, 2, 0));
                    }
                    break;
                case "endcodespacerange":
                    for (var i = 0; i + 1 < words.Count; i += 2)
                        if (words[i] is PsString lo && words[i + 1] is PsString hi) map.CodeSpaces.Add((lo.Span.ToArray(), hi.Span.ToArray()));
                    break;
                case "endcidrange":
                    for (var i = 0; i + 2 < words.Count; i += 3)
                        if (words[i] is PsString lo && words[i + 1] is PsString hi && words[i + 2] is int c) map.Ranges.Add((Value(lo), Value(hi), lo.Length, c));
                    break;
                case "endcidchar":
                    for (var i = 0; i + 1 < words.Count; i += 2)
                        if (words[i] is PsString code && words[i + 1] is int c) map.Ranges.Add((Value(code), Value(code), code.Length, c));
                    break;
            }
            words.Clear();
        }
        if (map.CodeSpaces.Count == 0)
        {
            map.CodeSpaces.Add(([0, 0], [255, 255]));
            if (map.Ranges.Count == 0) map.Ranges.Add((0, 0xFFFF, 2, 0));
        }
        return map;
    }

    private static uint Value(PsString s)
    {
        uint v = 0;
        for (var k = 0; k < s.Length; k++) v = v << 8 | s[k];
        return v;
    }

    /// <summary>A ToUnicode map: each code's character (the first, where a code stands for several).</summary>
    private static Dictionary<int, int> ReadToUnicode(byte[] data)
    {
        var map = new Dictionary<int, int>();
        var lex = new PdfLexer(data);
        var words = new List<object>();
        static int Char(PsString s) => s.Length >= 2 ? s[0] << 8 | s[1] : s.Length == 1 ? s[0] : 0;
        while (lex.ReadObject() is { } o)
        {
            if (o is not PdfKeyword k) { words.Add(o); continue; }
            if (k.Value == "endbfchar")
                for (var i = 0; i + 1 < words.Count; i += 2)
                {
                    if (words[i] is PsString code && words[i + 1] is PsString u) map[(int)Value(code)] = Char(u);
                }
            else if (k.Value == "endbfrange")
                for (var i = 0; i + 2 < words.Count; i += 3)
                {
                    if (words[i] is not PsString lo || words[i + 1] is not PsString hi) continue;
                    var (a, b) = ((int)Value(lo), (int)Value(hi));
                    for (var c = a; c <= b && c - a < 65536; c++)
                    {
                        if (words[i + 2] is PsString start) map[c] = Char(start) + c - a;
                        else if (words[i + 2] is PsArray each && c - a < each.Length && each[c - a] is PsString one) map[c] = Char(one);
                    }
                }
            if (k.Value is "endbfchar" or "endbfrange" or "beginbfchar" or "beginbfrange") words.Clear();
        }
        return map;
    }
}
