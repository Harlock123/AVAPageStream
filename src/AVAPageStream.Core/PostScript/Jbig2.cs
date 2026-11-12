namespace AVAPageStream.Core.PostScript;

/// <summary>
/// JBIG2 decoding (T.88), as PDF embeds it: segments without the file header, the globals stream's first. Generic
/// regions (arithmetic, every template, typical prediction and adaptive pixels; or MMR), refinement regions, symbol
/// dictionaries and text regions, pattern dictionaries and halftone regions, all arithmetic coded. The Huffman-coded
/// forms of symbols and text, rarely written, aren't read.
/// </summary>
public static class Jbig2
{
    /// <summary>A bitmap: a byte a pixel, 1 black.</summary>
    public sealed class Bitmap(int w, int h, byte fill = 0)
    {
        public int W { get; } = Math.Max(0, w);
        public int H { get; } = Math.Max(0, h);
        public byte[] P { get; } = Fill(Math.Max(0, w) * Math.Max(0, h), fill);
        private static byte[] Fill(int n, byte v) { var b = new byte[n]; if (v != 0) Array.Fill(b, v); return b; }
        public int Get(int x, int y) => x < 0 || y < 0 || x >= W || y >= H ? 0 : P[y * W + x];

        /// <summary>Draws another bitmap onto this one at (x, y) by a combination operator: or, and, xor, xnor, replace.</summary>
        public void Combine(Bitmap b, int x, int y, int op)
        {
            for (var j = 0; j < b.H; j++)
            {
                var ty = y + j;
                if (ty < 0 || ty >= H) continue;
                for (var i = 0; i < b.W; i++)
                {
                    var tx = x + i;
                    if (tx < 0 || tx >= W) continue;
                    ref var d = ref P[ty * W + tx];
                    var s = b.P[j * b.W + i];
                    d = op switch { 0 => (byte)(d | s), 1 => (byte)(d & s), 2 => (byte)(d ^ s), 3 => (byte)(1 - (d ^ s)), _ => s };
                }
            }
        }
    }

    /// <summary>The arithmetic decoder of a region and its contexts, by procedure, kept for as long as the region is decoded.</summary>
    private sealed class Coder(byte[] data, int start, int end)
    {
        public MqDecoder Mq { get; } = new(data, start, end);
        private readonly Dictionary<string, byte[]> _contexts = [];
        public byte[] Contexts(string name, int size = 1 << 16)
        {
            if (!_contexts.TryGetValue(name, out var c)) _contexts[name] = c = new byte[size];
            return c;
        }

        /// <summary>The integer decoding procedure (A.2) of one of the IAx contexts: a value, or null for out of band.</summary>
        public int? Integer(string name)
        {
            var cx = Contexts(name, 512);
            var prev = 1;
            int Bits(int n)
            {
                var v = 0;
                for (var i = 0; i < n; i++)
                {
                    var bit = Mq.Bit(cx, prev);
                    prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
                    v = (v << 1) | bit;
                }
                return v;
            }
            var sign = Bits(1);
            var value = Bits(1) == 0 ? Bits(2)
                : Bits(1) == 0 ? Bits(4) + 4
                : Bits(1) == 0 ? Bits(6) + 20
                : Bits(1) == 0 ? Bits(8) + 84
                : Bits(1) == 0 ? Bits(12) + 340
                : (int)Math.Min(int.MaxValue, (long)(uint)Bits(32) + 4436);
            if (sign == 0) return value;
            return value > 0 ? -value : null;
        }

        /// <summary>The symbol ID procedure (A.3), with codes of so many bits.</summary>
        public int Id(int length)
        {
            var cx = Contexts("IAID", 1 << Math.Min(length + 1, 24));
            var prev = 1;
            for (var i = 0; i < length; i++) prev = (prev << 1) | Mq.Bit(cx, prev);
            return length < 31 ? prev & ((1 << length) - 1) : prev & 0x7FFFFFFF;
        }
    }

    private static readonly (int X, int Y)[][] Templates =
    [
        [(-1, -2), (0, -2), (1, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (2, -1), (-4, 0), (-3, 0), (-2, 0), (-1, 0)],
        [(-1, -2), (0, -2), (1, -2), (2, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (2, -1), (-3, 0), (-2, 0), (-1, 0)],
        [(-1, -2), (0, -2), (1, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (-2, 0), (-1, 0)],
        [(-3, -1), (-2, -1), (-1, -1), (0, -1), (1, -1), (-4, 0), (-3, 0), (-2, 0), (-1, 0)],
    ];

    /// <summary>The typical-prediction pseudo-pixel's context for each template.</summary>
    private static readonly int[] Sltp = [0x9B25, 0x0795, 0x00E5, 0x0195];

    /// <summary>The default adaptive pixels of a template.</summary>
    public static (int X, int Y)[] DefaultAt(int template) => template == 0 ? [(3, -1), (-3, -1), (2, -2), (-2, -2)] : template == 1 ? [(3, -1)] : [(2, -1)];

    /// <summary>
    /// Generic region decoding (6.2.5): each pixel from its neighbours above and to the left (the template, sorted
    /// top to bottom and left to right, the first the context's highest bit), with typical prediction if asked.
    /// </summary>
    private static Bitmap Generic(Coder c, int w, int h, int template, bool tpgdon, (int X, int Y)[] at, Bitmap? skip = null, string contexts = "GB")
    {
        var b = new Bitmap(w, h);
        var pixels = Templates[template].Concat(at).OrderBy(p => p.Y).ThenBy(p => p.X).ToArray();
        var cx = c.Contexts(contexts);
        var ltp = 0;
        for (var y = 0; y < h; y++)
        {
            if (tpgdon)
            {
                ltp ^= c.Mq.Bit(cx, Sltp[template]);
                if (ltp == 1)
                {
                    if (y > 0) Array.Copy(b.P, (y - 1) * w, b.P, y * w, w);
                    continue;
                }
            }
            for (var x = 0; x < w; x++)
            {
                if (skip is not null && skip.Get(x, y) == 1) continue;
                var ctx = 0;
                foreach (var (px, py) in pixels) ctx = (ctx << 1) | b.Get(x + px, y + py);
                b.P[y * w + x] = (byte)c.Mq.Bit(cx, ctx);
            }
        }
        return b;
    }

    /// <summary>An MMR (Group 4 fax) coded generic region, black as 1.</summary>
    private static Bitmap Mmr(byte[] data, int w, int h)
    {
        var rows = Ccitt.Decode(data, -1, w, h, false, blackIs1: true);
        var b = new Bitmap(w, h);
        var rowBytes = (w + 7) / 8;
        for (var y = 0; y < h && (y + 1) * rowBytes <= rows.Length; y++)
            for (var x = 0; x < w; x++) b.P[y * w + x] = (byte)(rows[y * rowBytes + x / 8] >> (7 - x % 8) & 1);
        return b;
    }

    private static readonly (int X, int Y)[][] RefineCoding = [[(0, -1), (1, -1), (-1, 0)], [(-1, -1), (0, -1), (1, -1), (-1, 0)]];
    private static readonly (int X, int Y)[][] RefineReference =
    [
        [(0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (-1, 1), (0, 1), (1, 1)],
        [(0, -1), (-1, 0), (0, 0), (1, 0), (0, 1), (1, 1)],
    ];

    /// <summary>Generic refinement (6.3): a bitmap decoded against a reference one, offset by (dx, dy), with prediction if asked.</summary>
    private static Bitmap Refine(Coder c, int w, int h, int template, Bitmap reference, int dx, int dy, bool tpgron, (int X, int Y)[] at)
    {
        var b = new Bitmap(w, h);
        var coding = template == 0 ? [.. RefineCoding[0], at[0]] : RefineCoding[1];
        var refs = template == 0 ? [.. RefineReference[0], at[1]] : RefineReference[1];
        var cx = c.Contexts("GR", 1 << 14);
        var ltp = 0;
        for (var y = 0; y < h; y++)
        {
            if (tpgron) ltp ^= c.Mq.Bit(cx, template == 0 ? 0x0100 : 0x0080);
            for (var x = 0; x < w; x++)
            {
                var (rx, ry) = (x - dx, y - dy);
                if (ltp == 1)
                {
                    // Where the reference is all one colour round the pixel, the pixel is that colour.
                    var v = reference.Get(rx, ry);
                    var same = true;
                    for (var j = -1; j <= 1 && same; j++)
                        for (var i = -1; i <= 1 && same; i++)
                            if (reference.Get(rx + i, ry + j) != v) same = false;
                    if (same) { b.P[y * w + x] = (byte)v; continue; }
                }
                var ctx = 0;
                foreach (var (px, py) in coding) ctx = (ctx << 1) | b.Get(x + px, y + py);
                foreach (var (px, py) in refs) ctx = (ctx << 1) | reference.Get(rx + px, ry + py);
                b.P[y * w + x] = (byte)c.Mq.Bit(cx, ctx);
            }
        }
        return b;
    }

    // ---- Segments

    private sealed record Segment(int Number, int Type, int[] Refers, int Page, int Start, int Length);

    private sealed class Reader(byte[] b, int at)
    {
        public int At = at;
        public int U8() => At < b.Length ? b[At++] : 0;
        public int U16() => U8() << 8 | U8();
        public int I32() => U8() << 24 | U8() << 16 | U8() << 8 | U8();
        public sbyte S8() => (sbyte)U8();
    }

    private static List<Segment> Segments(byte[] b)
    {
        var list = new List<Segment>();
        var r = new Reader(b, 0);
        while (r.At + 11 <= b.Length)
        {
            var number = r.I32();
            var flags = r.U8();
            var type = flags & 63;
            var refByte = r.U8();
            int count;
            if (refByte >> 5 == 7)
            {
                r.At--;
                count = r.I32() & 0x1FFFFFFF;
                r.At += (count + 8) / 8;
            }
            else count = refByte >> 5;
            var refs = new int[count];
            for (var i = 0; i < count; i++) refs[i] = number <= 256 ? r.U8() : number <= 65536 ? r.U16() : r.I32();
            var page = (flags & 64) != 0 ? r.I32() : r.U8();
            var length = r.I32();
            if (length == -1)
            {
                // An immediate generic region of unknown length: it runs to its end mark (0xFF 0xAC for arithmetic data, 0x00 0x00 for MMR) and its row count.
                var end = r.At + 18;
                var mmr = end < b.Length && (b[r.At + 17] & 1) == 1;
                var found = -1;
                for (var i = end; i + 1 < b.Length; i++)
                    if (mmr ? b[i] == 0 && b[i + 1] == 0 : b[i] == 0xFF && b[i + 1] == 0xAC) { found = i + 2 + 4; break; }
                length = (found < 0 ? b.Length : Math.Min(found, b.Length)) - r.At;
            }
            if (length < 0 || r.At + length > b.Length) length = Math.Max(0, b.Length - r.At);
            list.Add(new Segment(number, type, refs, page, r.At, length));
            r.At += length;
            if (type == 51) break; // end of file
        }
        return list;
    }

    private sealed record Region(int W, int H, int X, int Y, int Op);

    private static Region RegionInfo(Reader r) => new(r.I32(), r.I32(), r.I32(), r.I32(), r.U8() & 7);

    /// <summary>
    /// Decodes a JBIG2 stream (with its globals, if any) to the first page's rows of bits, each to a byte, 1 white and
    /// 0 black as PDF has it. Throws <see cref="PdfException"/> for what it doesn't read.
    /// </summary>
    public static byte[] Decode(byte[] data, byte[]? globals, int width, int height)
    {
        var all = globals is { Length: > 0 } ? Segments(globals).Select(s => (s, globals)).ToList() : [];
        all.AddRange(Segments(data).Select(s => (s, data)));
        var symbols = new Dictionary<int, List<Bitmap>>();
        var patterns = new Dictionary<int, List<Bitmap>>();
        var regions = new Dictionary<int, Bitmap>();
        Bitmap? page = null;
        var pageDefault = 0;
        var pageOp = 0;
        var striped = false;
        foreach (var (seg, bytes) in all)
        {
            var r = new Reader(bytes, seg.Start);
            var end = seg.Start + seg.Length;
            switch (seg.Type)
            {
                case 48: // page information
                {
                    var pw = r.I32();
                    var ph = r.I32();
                    r.I32();
                    r.I32();
                    var flags = r.U8();
                    var stripe = r.U16();
                    pageDefault = flags >> 2 & 1;
                    pageOp = flags >> 3 & 3;
                    striped = ph == -1;
                    page = new Bitmap(pw, striped ? Math.Max(height, stripe & 0x7FFF) : ph, (byte)pageDefault);
                    break;
                }
                case 50: // end of stripe: a striped page's height so far
                    break;
                case 0: // symbol dictionary
                    symbols[seg.Number] = SymbolDictionary(r, bytes, end, seg.Refers.SelectMany(n => symbols.GetValueOrDefault(n) ?? []).ToList());
                    break;
                case 4 or 6 or 7: // text region
                {
                    var info = RegionInfo(r);
                    var input = seg.Refers.SelectMany(n => symbols.GetValueOrDefault(n) ?? []).ToList();
                    var region = TextRegion(r, bytes, end, info, input);
                    if (seg.Type == 4) regions[seg.Number] = region;
                    else Place(ref page, region, info, pageOp);
                    break;
                }
                case 16: // pattern dictionary
                    patterns[seg.Number] = PatternDictionary(r, bytes, end);
                    break;
                case 20 or 22 or 23: // halftone region
                {
                    var info = RegionInfo(r);
                    var region = Halftone(r, bytes, end, info, seg.Refers.SelectMany(n => patterns.GetValueOrDefault(n) ?? []).ToList());
                    if (seg.Type == 20) regions[seg.Number] = region;
                    else Place(ref page, region, info, pageOp);
                    break;
                }
                case 36 or 38 or 39: // generic region
                {
                    var info = RegionInfo(r);
                    var flags = r.U8();
                    var mmr = (flags & 1) == 1;
                    var template = flags >> 1 & 3;
                    var tpgdon = (flags & 8) != 0;
                    var h = info.H == -1 ? (page?.H ?? height) - info.Y : info.H;
                    Bitmap region;
                    if (mmr) region = Mmr(bytes[r.At..end], info.W, h);
                    else
                    {
                        var at = new (int, int)[template == 0 ? 4 : 1];
                        for (var i = 0; i < at.Length; i++) at[i] = (r.S8(), r.S8());
                        region = Generic(new Coder(bytes, r.At, end), info.W, h, template, tpgdon, at);
                    }
                    if (seg.Type == 36) regions[seg.Number] = region;
                    else Place(ref page, region, info with { H = h }, pageOp);
                    break;
                }
                case 40 or 42 or 43: // generic refinement region
                {
                    var info = RegionInfo(r);
                    var flags = r.U8();
                    var template = flags & 1;
                    var tpgron = (flags & 2) != 0;
                    var at = template == 0 ? new[] { ((int)r.S8(), (int)r.S8()), ((int)r.S8(), (int)r.S8()) } : [];
                    // Refining an intermediate region it refers to, or the page where the region lies.
                    Bitmap reference;
                    if (seg.Refers.Length > 0 && regions.TryGetValue(seg.Refers[0], out var referred)) reference = referred;
                    else
                    {
                        reference = new Bitmap(info.W, info.H);
                        if (page is not null)
                            for (var y = 0; y < info.H; y++)
                                for (var x = 0; x < info.W; x++) reference.P[y * info.W + x] = (byte)page.Get(info.X + x, info.Y + y);
                    }
                    var region = Refine(new Coder(bytes, r.At, end), info.W, info.H, template, reference, 0, 0, tpgron, at);
                    if (seg.Type == 40) regions[seg.Number] = region;
                    else Place(ref page, region, info, seg.Refers.Length == 0 ? 4 : pageOp);
                    break;
                }
                case 49 or 51: // end of page, end of file
                    goto done;
                case 53 or 52 or 62: // tables, profiles, extensions
                    break;
            }
        }
    done:
        if (page is null) return [];
        // The page as PDF wants it: 1 for white.
        var w = page.W;
        var rowBytes = (w + 7) / 8;
        var rows = height > 0 ? Math.Min(height, page.H) : page.H;
        var o = new byte[rowBytes * Math.Max(rows, height)];
        Array.Fill(o, (byte)0xFF);
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < w; x++)
                if (page.P[y * w + x] == 1) o[y * rowBytes + x / 8] &= (byte)~(0x80 >> (x % 8));
        return o;
    }

    private static void Place(ref Bitmap? page, Bitmap region, Region info, int pageOp)
    {
        page ??= new Bitmap(info.X + region.W, info.Y + region.H);
        page.Combine(region, info.X, info.Y, info.Op is var op and <= 4 ? op : pageOp);
    }

    private static int CodeLength(int n)
    {
        var l = 0;
        while (1L << l < n) l++;
        return l;
    }

    /// <summary>A symbol dictionary (6.5): its new symbols, height class by height class, and which of them (and the input's) it exports.</summary>
    private static List<Bitmap> SymbolDictionary(Reader r, byte[] b, int end, List<Bitmap> input)
    {
        var flags = r.U16();
        if ((flags & 1) == 1) throw new PdfException("its JBIG2 pictures use Huffman-coded symbols, which AVAPageStream doesn't read yet");
        var refagg = (flags & 2) != 0;
        var template = flags >> 10 & 3;
        var rtemplate = flags >> 12 & 1;
        var at = new (int, int)[template == 0 ? 4 : 1];
        for (var i = 0; i < at.Length; i++) at[i] = (r.S8(), r.S8());
        var rat = refagg && rtemplate == 0 ? new[] { ((int)r.S8(), (int)r.S8()), ((int)r.S8(), (int)r.S8()) } : [];
        var exported = r.I32();
        var count = r.I32();
        var c = new Coder(b, r.At, end);
        var news = new List<Bitmap>();
        var codeLength = CodeLength(input.Count + count);
        var height = 0;
        while (news.Count < count)
        {
            var dh = c.Integer("IADH") ?? throw new PdfException("a damaged JBIG2 symbol dictionary");
            height += dh;
            var width = 0;
            while (news.Count < count)
            {
                var dw = c.Integer("IADW");
                if (dw is null) break;
                width += dw.Value;
                if (width < 0 || height < 0 || (long)width * height > 1 << 24) throw new PdfException("a damaged JBIG2 symbol dictionary");
                Bitmap bitmap;
                if (!refagg) bitmap = Generic(c, width, height, template, false, at);
                else
                {
                    var instances = c.Integer("IAAI") ?? 1;
                    var known = input.Concat(news).ToList();
                    if (instances > 1)
                        bitmap = TextInstances(c, width, height, 0, instances, 1, known, codeLength, false, 0, 1, 0, true, rtemplate, rat);
                    else
                    {
                        var id = c.Id(codeLength);
                        var rdx = c.Integer("IARDX") ?? 0;
                        var rdy = c.Integer("IARDY") ?? 0;
                        bitmap = Refine(c, width, height, rtemplate, known[Math.Clamp(id, 0, known.Count - 1)], rdx, rdy, false, rat);
                    }
                }
                news.Add(bitmap);
            }
        }
        // The export flags: runs, alternately not exported and exported.
        var all = input.Concat(news).ToList();
        var result = new List<Bitmap>(exported);
        var flag = false;
        for (int i = 0, runs = 0; i < all.Count && runs < all.Count * 2 + 2; runs++)
        {
            var run = c.Integer("IAEX") ?? 0;
            for (var k = 0; k < run && i < all.Count; k++, i++) if (flag) result.Add(all[i]);
            flag = !flag;
        }
        return result;
    }

    /// <summary>A text region's segment (7.4.4): its flags, then its symbol instances.</summary>
    private static Bitmap TextRegion(Reader r, byte[] b, int end, Region info, List<Bitmap> symbols)
    {
        var flags = r.U16();
        if ((flags & 1) == 1) throw new PdfException("its JBIG2 pictures use Huffman-coded text, which AVAPageStream doesn't read yet");
        var refine = (flags & 2) != 0;
        var strips = 1 << (flags >> 2 & 3);
        var corner = flags >> 4 & 3;
        var transposed = (flags & 64) != 0;
        var op = flags >> 7 & 3;
        var defPixel = flags >> 9 & 1;
        var dsOffset = (flags >> 10 & 31) is var ds && ds >= 16 ? ds - 32 : ds;
        var rtemplate = flags >> 15 & 1;
        var rat = refine && rtemplate == 0 ? new[] { ((int)r.S8(), (int)r.S8()), ((int)r.S8(), (int)r.S8()) } : [];
        var instances = r.I32();
        var c = new Coder(b, r.At, end);
        return TextInstances(c, info.W, info.H, defPixel, instances, strips, symbols, CodeLength(symbols.Count), transposed, dsOffset, corner, op, refine, rtemplate, rat);
    }

    /// <summary>Text region decoding (6.4): symbols placed strip by strip, each by its corner, combined into the region.</summary>
    private static Bitmap TextInstances(Coder c, int w, int h, int defPixel, int instances, int strips, List<Bitmap> symbols, int codeLength,
        bool transposed, int dsOffset, int corner, int op, bool refine, int rtemplate, (int X, int Y)[] rat)
    {
        var region = new Bitmap(w, h, (byte)defPixel);
        var stripT = -(c.Integer("IADT") ?? 0) * strips;
        var firstS = 0;
        var placed = 0;
        var top = corner is 1 or 3;
        var right = corner is 2 or 3;
        while (placed < instances)
        {
            stripT += (c.Integer("IADT") ?? 0) * strips;
            firstS += c.Integer("IAFS") ?? 0;
            var s = firstS;
            var first = true;
            while (placed < instances)
            {
                if (!first)
                {
                    var ids = c.Integer("IADS");
                    if (ids is null) break;
                    s += ids.Value + dsOffset;
                }
                first = false;
                var t = stripT + (strips > 1 ? c.Integer("IAIT") ?? 0 : 0);
                var id = c.Id(codeLength);
                if (symbols.Count == 0) throw new PdfException("a JBIG2 text region without its symbols");
                var symbol = symbols[Math.Clamp(id, 0, symbols.Count - 1)];
                if (refine && (c.Integer("IARI") ?? 0) != 0)
                {
                    var rdw = c.Integer("IARDW") ?? 0;
                    var rdh = c.Integer("IARDH") ?? 0;
                    var rdx = c.Integer("IARDX") ?? 0;
                    var rdy = c.Integer("IARDY") ?? 0;
                    symbol = Refine(c, symbol.W + rdw, symbol.H + rdh, rtemplate, symbol, (rdw >> 1) + rdx, (rdh >> 1) + rdy, false, rat);
                }
                var (sw, sh) = (symbol.W, symbol.H);
                if (!transposed && right) s += sw - 1;
                else if (transposed && !top) s += sh - 1;
                int x, y;
                if (!transposed) { x = right ? s - (sw - 1) : s; y = top ? t : t - (sh - 1); }
                else { x = right ? t - (sw - 1) : t; y = top ? s : s - (sh - 1); }
                region.Combine(symbol, x, y, op);
                if (!transposed && !right) s += sw - 1;
                else if (transposed && top) s += sh - 1;
                placed++;
            }
        }
        return region;
    }

    /// <summary>A pattern dictionary (6.7): one bitmap of all the patterns side by side, cut into them.</summary>
    private static List<Bitmap> PatternDictionary(Reader r, byte[] b, int end)
    {
        var flags = r.U8();
        var mmr = (flags & 1) == 1;
        var template = flags >> 1 & 3;
        var pw = r.U8();
        var ph = r.U8();
        var grayMax = r.I32();
        var width = (grayMax + 1) * pw;
        (int, int)[] at = template == 0 ? [(-pw, 0), (-3, -1), (2, -2), (-2, -2)] : [(-pw, 0)];
        var all = mmr ? Mmr(b[r.At..end], width, ph) : Generic(new Coder(b, r.At, end), width, ph, template, false, at);
        var list = new List<Bitmap>(grayMax + 1);
        for (var g = 0; g <= grayMax; g++)
        {
            var p = new Bitmap(pw, ph);
            for (var y = 0; y < ph; y++)
                for (var x = 0; x < pw; x++) p.P[y * pw + x] = (byte)all.Get(g * pw + x, y);
            list.Add(p);
        }
        return list;
    }

    /// <summary>A halftone region (6.6): a grid of gray values (in Gray-coded bit planes), each drawn as its pattern.</summary>
    private static Bitmap Halftone(Reader r, byte[] b, int end, Region info, List<Bitmap> patterns)
    {
        var flags = r.U8();
        var mmr = (flags & 1) == 1;
        var template = flags >> 1 & 3;
        var enableSkip = (flags & 8) != 0;
        var op = flags >> 4 & 7;
        var defPixel = flags >> 7 & 1;
        var gw = r.I32();
        var gh = r.I32();
        var gx = r.I32();
        var gy = r.I32();
        var rx = r.U16();
        var ry = r.U16();
        var region = new Bitmap(info.W, info.H, (byte)defPixel);
        if (patterns.Count == 0 || gw <= 0 || gh <= 0) return region;
        var (pw, ph) = (patterns[0].W, patterns[0].H);
        // Cells that fall wholly outside the region are skipped.
        Bitmap? skip = null;
        if (enableSkip)
        {
            skip = new Bitmap(gw, gh);
            for (var m = 0; m < gh; m++)
                for (var n = 0; n < gw; n++)
                {
                    var x = (gx + m * ry + n * rx) >> 8;
                    var y = (gy + m * rx - n * ry) >> 8;
                    if (x + pw <= 0 || x >= info.W || y + ph <= 0 || y >= info.H) skip.P[m * gw + n] = 1;
                }
        }
        var bits = CodeLength(patterns.Count);
        var gray = new int[gw * gh];
        (int, int)[] at = template == 0 ? [(template <= 1 ? 3 : 2, -1), (-3, -1), (2, -2), (-2, -2)] : [(template <= 1 ? 3 : 2, -1)];
        var c = mmr ? null : new Coder(b, r.At, end);
        var planes = new Bitmap[bits];
        for (var j = bits - 1; j >= 0; j--)
        {
            planes[j] = mmr ? Mmr(b[r.At..end], gw, gh) : Generic(c!, gw, gh, template, false, at, skip);
            if (j < bits - 1)
                for (var i = 0; i < gray.Length; i++) planes[j].P[i] ^= planes[j + 1].P[i];
        }
        for (var i = 0; i < gray.Length; i++)
        {
            var v = 0;
            for (var j = bits - 1; j >= 0; j--) v = v << 1 | planes[j].P[i];
            gray[i] = v;
        }
        for (var m = 0; m < gh; m++)
            for (var n = 0; n < gw; n++)
            {
                var x = (gx + m * ry + n * rx) >> 8;
                var y = (gy + m * rx - n * ry) >> 8;
                region.Combine(patterns[Math.Min(gray[m * gw + n], patterns.Count - 1)], x, y, op);
            }
        return region;
    }
}
