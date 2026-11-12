namespace AVAPageStream.Core.PostScript;

/// <summary>
/// JPEG 2000 decoding (T.800 Part 1), for PDF's JPXDecode: JP2 files and bare codestreams. Tiles and tile-parts,
/// every progression order (and progression changes), precincts, packed packet headers (PPM, PPT), SOP and EPH
/// markers, every code-block style (bypass, reset, termination, vertically causal, segmentation symbols),
/// quantisation, the 5/3 and 9/7 wavelets, the reversible and irreversible colour transforms, region of interest
/// (maximum shift), subsampled components, and a JP2 file's colour, palette and channels.
/// </summary>
public static class Jpx
{
    /// <summary>A decoded picture: its components at full size, each a byte a sample (scaled to 0–255), and what the file says they are.</summary>
    public sealed record Picture(int Width, int Height, byte[][] Components, string? Space, int AlphaChannel, byte[]? Icc);

    private sealed class Comp { public int Depth; public bool Signed; public int Dx, Dy; }

    private sealed class Coding
    {
        public bool Sop, Eph;
        public int Progression, Layers, Mct;
        public int Levels = 5, Xcb = 6, Ycb = 6, Style, Transform;
        public int[] Ppx = [], Ppy = [];
        public Coding Copy() => (Coding)MemberwiseClone();
    }

    private sealed class Quant { public int Guard = 2, Style; public List<(int Exp, int Mant)> Steps = []; public Quant Copy() => new() { Guard = Guard, Style = Style, Steps = [.. Steps] }; }

    private sealed class Header
    {
        public int X1, Y1, X0, Y0, Tw, Th, Tx0, Ty0;
        public Comp[] Comps = [];
        public Coding Cod = new();
        public Dictionary<int, Coding> Coc = [];
        public Quant Qcd = new();
        public Dictionary<int, Quant> Qcc = [];
        public Dictionary<int, int> Roi = [];
        public List<int[]> Poc = [];
        public List<byte[]> Ppm = [];
    }

    private static int U16(byte[] b, int at) => at + 1 < b.Length ? b[at] << 8 | b[at + 1] : 0;
    private static int U32(byte[] b, int at) => U16(b, at) << 16 | U16(b, at + 2);
    private static int CeilDiv(int a, int b) => (int)Math.Ceiling(a / (double)b);
    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    /// <summary>Decodes a JP2 file or a codestream; reduce (0) for full size.</summary>
    public static Picture Decode(byte[] data)
    {
        string? space = null;
        byte[]? icc = null;
        var alpha = -1;
        int[][]? palette = null;
        int[]? paletteDepth = null;
        List<(int Comp, int Type, int Col)>? cmap = null;
        var stream = data;
        if (data.Length > 12 && U32(data, 4) == 0x6A502020)
        {
            // Boxes: the header superbox's colour, palette, component mapping and channel definitions; then the codestream.
            void Boxes(int start, int end)
            {
                for (var at = start; at + 8 <= end;)
                {
                    long len = (uint)U32(data, at);
                    var type = U32(data, at + 4);
                    var head = 8;
                    if (len == 1) { len = (long)(uint)U32(data, at + 8) << 32 | (uint)U32(data, at + 12); head = 16; }
                    else if (len == 0) len = end - at;
                    var body = at + head;
                    var bodyEnd = (int)Math.Min(end, at + len);
                    switch (type)
                    {
                        case 0x6A703268: Boxes(body, bodyEnd); break; // jp2h
                        case 0x636F6C72: // colr
                            if (data[body] == 1)
                                space = U32(data, body + 3) switch { 16 => "DeviceRGB", 17 => "DeviceGray", 18 => "sYCC", 12 => "DeviceCMYK", _ => space };
                            else if (data[body] == 2) icc = data[(body + 3)..bodyEnd];
                            break;
                        case 0x70636C72: // pclr: entries, columns, their depths, then the table
                        {
                            var entries = U16(data, body);
                            var cols = data[body + 2];
                            paletteDepth = new int[cols];
                            for (var c = 0; c < cols; c++) paletteDepth[c] = (data[body + 3 + c] & 0x7F) + 1;
                            var p = body + 3 + cols;
                            palette = new int[cols][];
                            for (var c = 0; c < cols; c++) palette[c] = new int[entries];
                            for (var e = 0; e < entries; e++)
                                for (var c = 0; c < cols; c++)
                                {
                                    var bytes = (paletteDepth[c] + 7) / 8;
                                    var v = 0;
                                    for (var k = 0; k < bytes; k++) v = v << 8 | (p < data.Length ? data[p++] : 0);
                                    palette[c][e] = v;
                                }
                            break;
                        }
                        case 0x636D6170: // cmap
                            cmap = [];
                            for (var p = body; p + 4 <= bodyEnd; p += 4) cmap.Add((U16(data, p), data[p + 2], data[p + 3]));
                            break;
                        case 0x63646566: // cdef: the channel whose type is opacity
                            for (int n = U16(data, body), k = 0; k < n; k++)
                                if (U16(data, body + 2 + k * 6 + 2) is 1 or 2) alpha = U16(data, body + 2 + k * 6);
                            break;
                        case 0x6A703263: // jp2c
                            stream = data[body..bodyEnd];
                            break;
                    }
                    if (len <= 0) break;
                    at += (int)len;
                }
            }
            Boxes(0, data.Length);
        }
        var (w, h, comps, depths) = Codestream(stream);
        // A palette: each index to its colours.
        if (palette is not null && paletteDepth is not null && comps.Length > 0)
        {
            var mapped = new List<int[]>();
            var mappedDepth = new List<int>();
            var map = cmap ?? Enumerable.Range(0, palette.Length).Select(c => (0, 1, c)).ToList();
            foreach (var (comp, type, col) in map)
            {
                var src = comps[Math.Min(comp, comps.Length - 1)];
                if (type == 0) { mapped.Add(src); mappedDepth.Add(depths[comp]); continue; }
                var table = palette[Math.Min(col, palette.Length - 1)];
                mapped.Add(src.Select(i => table[Math.Clamp(i, 0, table.Length - 1)]).ToArray());
                mappedDepth.Add(paletteDepth[Math.Min(col, paletteDepth.Length - 1)]);
            }
            comps = [.. mapped];
            depths = [.. mappedDepth];
        }
        var bytesOut = new byte[comps.Length][];
        for (var c = 0; c < comps.Length; c++)
        {
            var max = (1 << depths[c]) - 1;
            var src = comps[c];
            var o = new byte[src.Length];
            for (var i = 0; i < o.Length; i++) o[i] = (byte)Math.Clamp((int)Math.Round(src[i] * 255.0 / max), 0, 255);
            bytesOut[c] = o;
        }
        if (space == "sYCC" && bytesOut.Length >= 3)
        {
            for (var i = 0; i < bytesOut[0].Length; i++)
            {
                double y = bytesOut[0][i], cb = bytesOut[1][i] - 128, cr = bytesOut[2][i] - 128;
                bytesOut[0][i] = (byte)Math.Clamp(Math.Round(y + 1.402 * cr), 0, 255);
                bytesOut[1][i] = (byte)Math.Clamp(Math.Round(y - 0.344136 * cb - 0.714136 * cr), 0, 255);
                bytesOut[2][i] = (byte)Math.Clamp(Math.Round(y + 1.772 * cb), 0, 255);
            }
            space = "DeviceRGB";
        }
        return new Picture(w, h, bytesOut, space, alpha, icc);
    }

    // ---- The codestream

    private sealed class TileData { public List<byte> Bytes = []; public List<byte> Ppt = []; public Coding? Cod; public Dictionary<int, Coding> Coc = []; public Quant? Qcd; public Dictionary<int, Quant> Qcc = []; public Dictionary<int, int> Roi = []; public List<int[]> Poc = []; public List<byte[]> PpmParts = []; }

    private static (int W, int H, int[][] Comps, int[] Depths) Codestream(byte[] b)
    {
        if (U16(b, 0) != 0xFF4F) throw new PdfException("its JPEG 2000 picture is damaged");
        var hdr = new Header();
        var tiles = new Dictionary<int, TileData>();
        var at = 2;
        TileData? current = null;
        var inMain = true;
        var ppmIndex = 0;
        while (at + 4 <= b.Length)
        {
            var marker = U16(b, at);
            if (marker == 0xFFD9) break; // EOC
            if (marker == 0xFF93) // SOD: the tile-part's data, to its end
                throw new PdfException("its JPEG 2000 picture is damaged");
            var len = U16(b, at + 2);
            var seg = at + 4;
            var segEnd = at + 2 + len;
            switch (marker)
            {
                case 0xFF51: // SIZ
                {
                    hdr.X1 = U32(b, seg + 2); hdr.Y1 = U32(b, seg + 6); hdr.X0 = U32(b, seg + 10); hdr.Y0 = U32(b, seg + 14);
                    hdr.Tw = U32(b, seg + 18); hdr.Th = U32(b, seg + 22); hdr.Tx0 = U32(b, seg + 26); hdr.Ty0 = U32(b, seg + 30);
                    var n = U16(b, seg + 34);
                    hdr.Comps = new Comp[n];
                    for (var c = 0; c < n; c++)
                        hdr.Comps[c] = new Comp { Depth = (b[seg + 36 + c * 3] & 0x7F) + 1, Signed = (b[seg + 36 + c * 3] & 0x80) != 0, Dx = Math.Max(1, (int)b[seg + 37 + c * 3]), Dy = Math.Max(1, (int)b[seg + 38 + c * 3]) };
                    break;
                }
                case 0xFF52: // COD
                {
                    var cod = ReadCod(b, seg, segEnd, (inMain ? hdr.Cod : current!.Cod ?? hdr.Cod).Copy(), full: true);
                    if (inMain) hdr.Cod = cod; else current!.Cod = cod;
                    break;
                }
                case 0xFF53: // COC
                {
                    var c = hdr.Comps.Length < 257 ? b[seg] : U16(b, seg);
                    var off = hdr.Comps.Length < 257 ? 1 : 2;
                    var basis = (inMain ? hdr.Coc.GetValueOrDefault(c) ?? hdr.Cod : current!.Coc.GetValueOrDefault(c) ?? current.Cod ?? hdr.Coc.GetValueOrDefault(c) ?? hdr.Cod).Copy();
                    var coc = ReadCod(b, seg + off - 1, segEnd, basis, full: false);
                    if (inMain) hdr.Coc[c] = coc; else current!.Coc[c] = coc;
                    break;
                }
                case 0xFF5C: // QCD
                {
                    var q = ReadQuant(b, seg, segEnd);
                    if (inMain) hdr.Qcd = q; else current!.Qcd = q;
                    break;
                }
                case 0xFF5D: // QCC
                {
                    var c = hdr.Comps.Length < 257 ? b[seg] : U16(b, seg);
                    var q = ReadQuant(b, seg + (hdr.Comps.Length < 257 ? 1 : 2), segEnd);
                    if (inMain) hdr.Qcc[c] = q; else current!.Qcc[c] = q;
                    break;
                }
                case 0xFF5E: // RGN
                {
                    var c = hdr.Comps.Length < 257 ? b[seg] : U16(b, seg);
                    var shift = b[seg + (hdr.Comps.Length < 257 ? 2 : 3)];
                    if (inMain) hdr.Roi[c] = shift; else current!.Roi[c] = shift;
                    break;
                }
                case 0xFF5F: // POC
                {
                    var wide = hdr.Comps.Length >= 257;
                    var size = wide ? 9 : 7;
                    var list = inMain ? hdr.Poc : current!.Poc;
                    for (var p = seg; p + size <= segEnd; p += size)
                        list.Add(wide
                            ? [b[p], U16(b, p + 1), U16(b, p + 3), b[p + 5], U16(b, p + 6), b[p + 8]]
                            : [b[p], b[p + 1], U16(b, p + 2), b[p + 4], b[p + 5], b[p + 6]]);
                    break;
                }
                case 0xFF60: // PPM: packed packet headers for every tile-part, in order
                    hdr.Ppm.Add(b[(seg + 1)..segEnd]);
                    break;
                case 0xFF61: // PPT
                    current?.Ppt.AddRange(b[(seg + 1)..segEnd]);
                    break;
                case 0xFF90: // SOT
                {
                    inMain = false;
                    var tile = U16(b, seg);
                    var psot = U32(b, seg + 2);
                    if (!tiles.TryGetValue(tile, out current)) tiles[tile] = current = new TileData();
                    // The tile-part's header markers, to SOD, then its data to the end of the tile-part.
                    var p = segEnd;
                    var partEnd = psot == 0 ? b.Length : Math.Min(b.Length, at + psot);
                    while (p + 4 <= partEnd && U16(b, p) != 0xFF93)
                    {
                        var m = U16(b, p);
                        var l = U16(b, p + 2);
                        // The markers a tile-part header may hold: read by the main switch, as if met here.
                        ReadTileMarker(b, m, p + 4, p + 2 + l, hdr, current);
                        p += 2 + l;
                    }
                    var dataStart = p + 2;
                    if (dataStart <= partEnd) current.Bytes.AddRange(b.AsSpan(dataStart, partEnd - dataStart).ToArray());
                    // With PPM, this tile-part's packet headers are the next of PPM's.
                    current.PpmParts.Add([]);
                    at = partEnd;
                    continue;
                }
            }
            at = segEnd;
        }
        // PPM's headers: lengths and data, one run for each tile-part in the order they came.
        if (hdr.Ppm.Count > 0)
        {
            var all = hdr.Ppm.SelectMany(x => x).ToArray();
            var p = 0;
            foreach (var t in tiles.OrderBy(t => t.Key).Select(t => t.Value))
                for (var k = 0; k < t.PpmParts.Count && p + 4 <= all.Length; k++)
                {
                    var n = U32(all, p);
                    p += 4;
                    t.Ppt.AddRange(all.AsSpan(p, Math.Min(n, all.Length - p)).ToArray());
                    p += n;
                }
            _ = ppmIndex;
        }
        var width = hdr.X1 - hdr.X0;
        var height = hdr.Y1 - hdr.Y0;
        if (width <= 0 || height <= 0 || (long)width * height > 80_000_000 || hdr.Comps.Length == 0) throw new PdfException("its JPEG 2000 picture is damaged");
        var comps = new int[hdr.Comps.Length][];
        for (var c = 0; c < comps.Length; c++) comps[c] = new int[width * height];
        var tilesX = CeilDiv(hdr.X1 - hdr.Tx0, Math.Max(1, hdr.Tw));
        var tilesY = CeilDiv(hdr.Y1 - hdr.Ty0, Math.Max(1, hdr.Th));
        for (var ty = 0; ty < tilesY; ty++)
            for (var tx = 0; tx < tilesX; tx++)
            {
                var index = ty * tilesX + tx;
                var td = tiles.GetValueOrDefault(index) ?? new TileData();
                DecodeTile(hdr, td, tx, ty, comps, width);
            }
        return (width, height, comps, hdr.Comps.Select(c => c.Depth).ToArray());
    }

    private static void ReadTileMarker(byte[] b, int m, int seg, int segEnd, Header hdr, TileData t)
    {
        var narrow = hdr.Comps.Length < 257;
        switch (m)
        {
            case 0xFF52: t.Cod = ReadCod(b, seg, segEnd, (t.Cod ?? hdr.Cod).Copy(), full: true); break;
            case 0xFF53:
            {
                var c = narrow ? b[seg] : U16(b, seg);
                var basis = (t.Coc.GetValueOrDefault(c) ?? (t.Cod is not null ? t.Cod : hdr.Coc.GetValueOrDefault(c) ?? hdr.Cod)).Copy();
                t.Coc[c] = ReadCod(b, seg + (narrow ? 0 : 1), segEnd, basis, full: false);
                break;
            }
            case 0xFF5C: t.Qcd = ReadQuant(b, seg, segEnd); break;
            case 0xFF5D: t.Qcc[narrow ? b[seg] : U16(b, seg)] = ReadQuant(b, seg + (narrow ? 1 : 2), segEnd); break;
            case 0xFF5E: t.Roi[narrow ? b[seg] : U16(b, seg)] = b[seg + (narrow ? 2 : 3)]; break;
            case 0xFF5F:
            {
                var size = narrow ? 7 : 9;
                for (var p = seg; p + size <= segEnd; p += size)
                    t.Poc.Add(narrow ? [b[p], b[p + 1], U16(b, p + 2), b[p + 4], b[p + 5], b[p + 6]] : [b[p], U16(b, p + 1), U16(b, p + 3), b[p + 5], U16(b, p + 6), b[p + 8]]);
                break;
            }
            case 0xFF61: t.Ppt.AddRange(b.AsSpan(seg + 1, Math.Max(0, segEnd - seg - 1)).ToArray()); break;
        }
    }

    /// <param name="seg">For COD, its Scod; for COC, the byte before its Scoc (so the same offsets serve).</param>
    private static Coding ReadCod(byte[] b, int seg, int segEnd, Coding c, bool full)
    {
        var p = seg;
        var scod = b[full ? p : p + 1];
        if (full)
        {
            c.Sop = (scod & 2) != 0;
            c.Eph = (scod & 4) != 0;
            c.Progression = b[p + 1];
            c.Layers = U16(b, p + 2);
            c.Mct = b[p + 4];
            p += 5;
        }
        else p += 2;
        c.Levels = b[p];
        c.Xcb = (b[p + 1] & 15) + 2;
        c.Ycb = (b[p + 2] & 15) + 2;
        c.Style = b[p + 3];
        c.Transform = b[p + 4];
        p += 5;
        c.Ppx = new int[c.Levels + 1];
        c.Ppy = new int[c.Levels + 1];
        for (var r = 0; r <= c.Levels; r++)
        {
            if ((scod & 1) != 0 && p < segEnd) { c.Ppx[r] = b[p] & 15; c.Ppy[r] = b[p] >> 4; p++; }
            else { c.Ppx[r] = 15; c.Ppy[r] = 15; }
        }
        return c;
    }

    private static Quant ReadQuant(byte[] b, int seg, int segEnd)
    {
        var q = new Quant { Guard = b[seg] >> 5, Style = b[seg] & 31 };
        if (q.Style == 0) for (var p = seg + 1; p < segEnd; p++) q.Steps.Add((b[p] >> 3, 0));
        else for (var p = seg + 1; p + 1 < segEnd; p += 2) q.Steps.Add((b[p] >> 3, (b[p] & 7) << 8 | b[p + 1]));
        return q;
    }

    // ---- Tiles

    private sealed class Block
    {
        public int X0, Y0, X1, Y1;       // in the band
        public bool Included;
        public int ZeroPlanes, Lblock = 3, Passes;
        public List<(List<byte> Data, int Passes)> Segments = [];
    }

    private sealed class Band
    {
        public int Type;                 // 0 LL, 1 HL, 2 LH, 3 HH
        public int X0, Y0, X1, Y1;
        public double[] Coefficients = [];
        public int Mb;                   // magnitude bit planes
        public double Step = 1;
        public int Xcb, Ycb;
    }

    private sealed class PrecinctBand(Band band, List<Block> blocks, int w, int h)
    {
        public Band Band { get; } = band;
        public List<Block> Blocks { get; } = blocks;
        public TagTree Inclusion { get; } = new(w, h);
        public TagTree Zero { get; } = new(w, h);
        public int W { get; } = w;
    }

    private sealed class Precinct { public List<PrecinctBand> Bands = []; public int X, Y; }

    private sealed class Resolution
    {
        public int X0, Y0, X1, Y1;
        public List<Band> Bands = [];
        public List<Precinct> Precincts = [];
    }

    private sealed class TagTree
    {
        private readonly List<(int W, int H, int[] Value, int[] Low)> _levels = [];
        public TagTree(int w, int h)
        {
            do
            {
                var n = Math.Max(1, w) * Math.Max(1, h);
                _levels.Add((Math.Max(1, w), Math.Max(1, h), Enumerable.Repeat(int.MaxValue, n).ToArray(), new int[n]));
                w = (w + 1) / 2;
                h = (h + 1) / 2;
            } while (_levels[^1].W > 1 || _levels[^1].H > 1);
        }

        /// <summary>Whether the leaf's value is below the threshold, reading what is needed to know.</summary>
        public bool Decode(HeaderBits bits, int x, int y, int threshold)
        {
            var low = 0;
            for (var l = _levels.Count - 1; l >= 0; l--)
            {
                var (w, _, value, lows) = _levels[l];
                var i = (y >> l) * w + (x >> l);
                if (low > lows[i]) lows[i] = low; else low = lows[i];
                while (low < threshold && low < value[i])
                {
                    if (bits.Bit() == 1) value[i] = low;
                    else low++;
                }
                lows[i] = low;
            }
            return _levels[0].Value[y * _levels[0].W + x] < threshold;
        }

        public int Value(int x, int y) => _levels[0].Value[y * _levels[0].W + x];
    }

    /// <summary>Packet header bits: after a byte of 0xFF, the next has only seven.</summary>
    private sealed class HeaderBits(byte[] data, int pos)
    {
        public int Pos = pos;
        private int _left, _byte;
        public bool Ended => Pos >= data.Length && _left == 0;
        public int Bit()
        {
            if (_left == 0)
            {
                var stuffed = _byte == 0xFF;
                _byte = Pos < data.Length ? data[Pos] : 0;
                Pos++;
                _left = stuffed ? 7 : 8;
            }
            _left--;
            return _byte >> _left & 1;
        }
        public int Bits(int n) { var v = 0; for (var i = 0; i < n; i++) v = v << 1 | Bit(); return v; }
        public void Align()
        {
            _left = 0;
            if (_byte == 0xFF) { Pos++; _byte = 0; }
            _byte = 0;
        }
    }

    private static void DecodeTile(Header hdr, TileData td, int p, int q, int[][] output, int width)
    {
        var tx0 = Math.Max(hdr.Tx0 + p * hdr.Tw, hdr.X0);
        var ty0 = Math.Max(hdr.Ty0 + q * hdr.Th, hdr.Y0);
        var tx1 = Math.Min(hdr.Tx0 + (p + 1) * hdr.Tw, hdr.X1);
        var ty1 = Math.Min(hdr.Ty0 + (q + 1) * hdr.Th, hdr.Y1);
        if (tx1 <= tx0 || ty1 <= ty0) return;
        var tileCod = td.Cod ?? hdr.Cod;
        var nc = hdr.Comps.Length;
        var cods = new Coding[nc];
        var resolutions = new List<Resolution>[nc];
        for (var c = 0; c < nc; c++)
        {
            var cod = td.Coc.GetValueOrDefault(c) ?? (td.Cod is not null ? td.Cod : hdr.Coc.GetValueOrDefault(c) ?? hdr.Cod);
            // A COC changes only the component's coding style; the rest is the tile's.
            cod = cod.Copy();
            (cod.Sop, cod.Eph, cod.Progression, cod.Layers, cod.Mct) = (tileCod.Sop, tileCod.Eph, tileCod.Progression, tileCod.Layers, tileCod.Mct);
            cods[c] = cod;
            var quant = td.Qcc.GetValueOrDefault(c) ?? td.Qcd ?? hdr.Qcc.GetValueOrDefault(c) ?? hdr.Qcd;
            var roi = td.Roi.GetValueOrDefault(c, hdr.Roi.GetValueOrDefault(c));
            resolutions[c] = Layout(hdr.Comps[c], cod, quant, roi, tx0, ty0, tx1, ty1);
        }
        // Packets, in the tile's progression order (or as its changes say), each read once.
        var headers = td.Ppt.Count > 0 ? [.. td.Ppt] : td.Bytes.ToArray();
        var body = td.Bytes.ToArray();
        var hb = new HeaderBits(headers, 0);
        var bodyPos = 0;
        var separate = td.Ppt.Count > 0;
        var done = new Dictionary<(int, int, int), int>(); // (c, r, precinct) → layers read
        var order = Packets(hdr, td, cods, resolutions, tx0, ty0);
        foreach (var (l, r, c, pi) in order)
        {
            if (!separate ? bodyPos >= body.Length : hb.Ended) break;
            if (done.GetValueOrDefault((c, r, pi)) != l) continue;
            done[(c, r, pi)] = l + 1;
            var cod = cods[c];
            var precinct = resolutions[c][r].Precincts[pi];
            if (!separate) hb.Pos = bodyPos;
            // An SOP marker before the packet: passed over.
            if (cod.Sop && bodyPos + 6 <= body.Length && U16(body, bodyPos) == 0xFF91)
            {
                bodyPos += 6;
                if (!separate) hb.Pos = bodyPos;
            }
            var contributions = ReadPacketHeader(hb, precinct, l, cod);
            if (cod.Eph && hb.Pos + 1 < headers.Length && U16(headers, hb.Pos) == 0xFF92) hb.Pos += 2;
            if (!separate) bodyPos = hb.Pos;
            foreach (var (block, segments) in contributions)
                foreach (var (segment, length) in segments)
                {
                    var n = Math.Min(length, Math.Max(0, body.Length - bodyPos));
                    block.Segments[segment].Data.AddRange(body.AsSpan(bodyPos, n).ToArray());
                    bodyPos += n;
                }
            if (!separate) hb = new HeaderBits(headers, bodyPos);
        }
        // Each code-block's coefficients, then each component back from its wavelets.
        var planes = new double[nc][];
        int[] cw = new int[nc], ch = new int[nc], cx0 = new int[nc], cy0 = new int[nc];
        for (var c = 0; c < nc; c++)
        {
            var cod = cods[c];
            foreach (var res in resolutions[c])
                foreach (var band in res.Bands)
                {
                    band.Coefficients = new double[Math.Max(0, (band.X1 - band.X0) * (band.Y1 - band.Y0))];
                }
            foreach (var res in resolutions[c])
                foreach (var precinct in res.Precincts)
                    foreach (var pb in precinct.Bands)
                        foreach (var block in pb.Blocks)
                            if (block.Passes > 0) Tier1.DecodeBlock(block.X0, block.Y0, block.X1, block.Y1, block.Segments.Select(s => (s.Data.ToArray(), s.Passes)).ToList(),
                                block.ZeroPlanes, pb.Band, cod, td.Roi.GetValueOrDefault(c, hdr.Roi.GetValueOrDefault(c)));
            var comp = hdr.Comps[c];
            (cx0[c], cy0[c]) = (CeilDiv(tx0, comp.Dx), CeilDiv(ty0, comp.Dy));
            (cw[c], ch[c]) = (CeilDiv(tx1, comp.Dx) - cx0[c], CeilDiv(ty1, comp.Dy) - cy0[c]);
            planes[c] = Wavelet.Inverse(resolutions[c], cod.Transform == 1);
        }
        // The colour transform, of the first three components.
        if (tileCod.Mct == 1 && nc >= 3 && planes[0].Length == planes[1].Length && planes[1].Length == planes[2].Length)
        {
            var (y0, y1, y2) = (planes[0], planes[1], planes[2]);
            if (cods[0].Transform == 1)
                for (var i = 0; i < y0.Length; i++)
                {
                    var g = y0[i] - Math.Floor((y2[i] + y1[i]) / 4);
                    var red = y2[i] + g;
                    var blue = y1[i] + g;
                    (y0[i], y1[i], y2[i]) = (red, g, blue);
                }
            else
                for (var i = 0; i < y0.Length; i++)
                {
                    var (yy, cb, cr) = (y0[i], y1[i], y2[i]);
                    (y0[i], y1[i], y2[i]) = (yy + 1.402 * cr, yy - 0.34413 * cb - 0.71414 * cr, yy + 1.772 * cb);
                }
        }
        // Back to the components' own range, and into the picture (subsampled ones spread over their pixels).
        for (var c = 0; c < nc; c++)
        {
            var comp = hdr.Comps[c];
            var shift = comp.Signed ? 0 : 1 << (comp.Depth - 1);
            var max = (1 << comp.Depth) - 1;
            var plane = planes[c];
            for (var y = ty0; y < ty1; y++)
            {
                var sy = FloorDiv(y, comp.Dy) - cy0[c];
                if (sy < 0 || sy >= ch[c]) continue;
                for (var x = tx0; x < tx1; x++)
                {
                    var sx = FloorDiv(x, comp.Dx) - cx0[c];
                    if (sx < 0 || sx >= cw[c]) continue;
                    var v = plane[sy * cw[c] + sx];
                    var iv = (int)(cods[c].Transform == 1 ? v : Math.Round(v)) + shift;
                    if (comp.Signed) iv += 1 << (comp.Depth - 1); // shown as if unsigned
                    output[c][(y - hdr.Y0) * width + (x - hdr.X0)] = Math.Clamp(iv, 0, max);
                }
            }
        }
    }

    /// <summary>A tile-component's resolutions, bands, precincts and code-blocks (B.5 to B.7).</summary>
    private static List<Resolution> Layout(Comp comp, Coding cod, Quant quant, int roi, int tx0, int ty0, int tx1, int ty1)
    {
        var (tcx0, tcy0, tcx1, tcy1) = (CeilDiv(tx0, comp.Dx), CeilDiv(ty0, comp.Dy), CeilDiv(tx1, comp.Dx), CeilDiv(ty1, comp.Dy));
        var nl = cod.Levels;
        var list = new List<Resolution>();
        var stepIndex = 0;
        for (var r = 0; r <= nl; r++)
        {
            var scale = 1 << (nl - r);
            var res = new Resolution { X0 = CeilDiv(tcx0, scale), Y0 = CeilDiv(tcy0, scale), X1 = CeilDiv(tcx1, scale), Y1 = CeilDiv(tcy1, scale) };
            int[] types = r == 0 ? [0] : [1, 2, 3];
            var nb = r == 0 ? nl : nl - r + 1;
            foreach (var type in types)
            {
                var (xob, yob) = type switch { 1 => (1, 0), 2 => (0, 1), 3 => (1, 1), _ => (0, 0) };
                var s = 1 << nb;
                var half = nb > 0 ? 1 << (nb - 1) : 0;
                var band = new Band
                {
                    Type = type,
                    X0 = CeilDiv(tcx0 - half * xob, s), Y0 = CeilDiv(tcy0 - half * yob, s),
                    X1 = CeilDiv(tcx1 - half * xob, s), Y1 = CeilDiv(tcy1 - half * yob, s),
                };
                // Its quantisation: the step for this band, by style.
                (int Exp, int Mant) step = quant.Style switch
                {
                    1 => quant.Steps.Count > 0 ? (quant.Steps[0].Exp - (r == 0 ? 0 : nl - nb), quant.Steps[0].Mant) : (8, 0),
                    _ => stepIndex < quant.Steps.Count ? quant.Steps[stepIndex] : quant.Steps.Count > 0 ? quant.Steps[^1] : (8, 0),
                };
                if (quant.Style == 1 && r > 0) step = (quant.Steps[0].Exp - nl + nb, quant.Steps[0].Mant);
                stepIndex++;
                band.Mb = quant.Guard + step.Exp - 1 + roi;
                var gain = type switch { 0 => 0, 3 => 2, _ => 1 };
                band.Step = cod.Transform == 1 ? 1 : (1 + step.Mant / 2048.0) * Math.Pow(2, comp.Depth + gain - step.Exp);
                res.Bands.Add(band);
            }
            // Precincts over the resolution, and each band's code-blocks in them.
            var (ppx, ppy) = (cod.Ppx[Math.Min(r, cod.Ppx.Length - 1)], cod.Ppy[Math.Min(r, cod.Ppy.Length - 1)]);
            var (bpx, bpy) = r == 0 ? (ppx, ppy) : (ppx - 1, ppy - 1);
            var xcb = Math.Min(cod.Xcb, bpx);
            var ycb = Math.Min(cod.Ycb, bpy);
            var pw = res.X1 > res.X0 ? CeilDiv(res.X1, 1 << ppx) - FloorDiv(res.X0, 1 << ppx) : 0;
            var ph = res.Y1 > res.Y0 ? CeilDiv(res.Y1, 1 << ppy) - FloorDiv(res.Y0, 1 << ppy) : 0;
            var px0 = FloorDiv(res.X0, 1 << ppx);
            var py0 = FloorDiv(res.Y0, 1 << ppy);
            for (var j = 0; j < ph; j++)
                for (var i = 0; i < pw; i++)
                {
                    var precinct = new Precinct
                    {
                        // Where it starts on the reference grid, for the position orders.
                        X = Math.Max(tx0, ((px0 + i) << ppx) * comp.Dx * scale),
                        Y = Math.Max(ty0, ((py0 + j) << ppy) * comp.Dy * scale),
                    };
                    foreach (var band in res.Bands)
                    {
                        band.Xcb = xcb;
                        band.Ycb = ycb;
                        // The precinct in the band's coordinates.
                        var bx0 = Math.Max(band.X0, (px0 + i) << bpx);
                        var by0 = Math.Max(band.Y0, (py0 + j) << bpy);
                        var bx1 = Math.Min(band.X1, (px0 + i + 1) << bpx);
                        var by1 = Math.Min(band.Y1, (py0 + j + 1) << bpy);
                        var blocks = new List<Block>();
                        int cbw = 0, cbh = 0;
                        if (bx1 > bx0 && by1 > by0)
                        {
                            var (cbx0, cby0) = (FloorDiv(bx0, 1 << xcb), FloorDiv(by0, 1 << ycb));
                            var (cbx1, cby1) = (CeilDiv(bx1, 1 << xcb), CeilDiv(by1, 1 << ycb));
                            (cbw, cbh) = (cbx1 - cbx0, cby1 - cby0);
                            for (var y = cby0; y < cby1; y++)
                                for (var x = cbx0; x < cbx1; x++)
                                    blocks.Add(new Block
                                    {
                                        X0 = Math.Max(bx0, x << xcb), Y0 = Math.Max(by0, y << ycb),
                                        X1 = Math.Min(bx1, (x + 1) << xcb), Y1 = Math.Min(by1, (y + 1) << ycb),
                                    });
                        }
                        precinct.Bands.Add(new PrecinctBand(band, blocks, cbw, cbh));
                    }
                    res.Precincts.Add(precinct);
                }
            list.Add(res);
        }
        return list;
    }

    /// <summary>
    /// The tile's packets in order: (layer, resolution, component, precinct), by its progression (B.12), or by its
    /// progression changes, each change's volume in its own order.
    /// </summary>
    private static List<(int L, int R, int C, int P)> Packets(Header hdr, TileData td, Coding[] cods, List<Resolution>[] res, int tx0, int ty0)
    {
        var layers = cods[0].Layers;
        var all = new List<(int L, int R, int C, int P, int X, int Y)>();
        for (var c = 0; c < res.Length; c++)
            for (var r = 0; r < res[c].Count; r++)
                for (var pi = 0; pi < res[c][r].Precincts.Count; pi++)
                    for (var l = 0; l < layers; l++)
                        all.Add((l, r, c, pi, res[c][r].Precincts[pi].X, res[c][r].Precincts[pi].Y));
        IEnumerable<(int L, int R, int C, int P, int X, int Y)> Ordered(IEnumerable<(int L, int R, int C, int P, int X, int Y)> list, int progression) => progression switch
        {
            1 => list.OrderBy(k => k.R).ThenBy(k => k.L).ThenBy(k => k.C).ThenBy(k => k.P),
            2 => list.OrderBy(k => k.R).ThenBy(k => k.Y).ThenBy(k => k.X).ThenBy(k => k.C).ThenBy(k => k.L),
            3 => list.OrderBy(k => k.Y).ThenBy(k => k.X).ThenBy(k => k.C).ThenBy(k => k.R).ThenBy(k => k.L),
            4 => list.OrderBy(k => k.C).ThenBy(k => k.Y).ThenBy(k => k.X).ThenBy(k => k.R).ThenBy(k => k.L),
            _ => list.OrderBy(k => k.L).ThenBy(k => k.R).ThenBy(k => k.C).ThenBy(k => k.P),
        };
        var pocs = td.Poc.Count > 0 ? td.Poc : hdr.Poc;
        if (pocs.Count == 0) return Ordered(all, cods[0].Progression).Select(k => (k.L, k.R, k.C, k.P)).ToList();
        var result = new List<(int, int, int, int)>();
        var taken = new HashSet<(int, int, int, int)>();
        foreach (var v in pocs)
        {
            var (rs, cs, lye, re, ce, pp) = (v[0], v[1], v[2], v[3], v[4], v[5]);
            var volume = all.Where(k => k.R >= rs && k.R < re && k.C >= cs && k.C < ce && k.L < lye && !taken.Contains((k.L, k.R, k.C, k.P)));
            foreach (var k in Ordered(volume.ToList(), pp)) { taken.Add((k.L, k.R, k.C, k.P)); result.Add((k.L, k.R, k.C, k.P)); }
        }
        foreach (var k in Ordered(all.Where(k => !taken.Contains((k.L, k.R, k.C, k.P))), cods[0].Progression)) result.Add((k.L, k.R, k.C, k.P));
        return result;
    }

    /// <summary>
    /// A packet's header (B.10): for each code-block of the precinct's bands, whether it is included, its zero bit
    /// planes the first time, its new coding passes and the lengths of their data, by codeword segment.
    /// </summary>
    private static List<(Block Block, List<(int Segment, int Length)> Segments)> ReadPacketHeader(HeaderBits bits, Precinct precinct, int layer, Coding cod)
    {
        var found = new List<(Block, List<(int, int)>)>();
        if (bits.Bit() == 0) { bits.Align(); return found; }
        var bypass = (cod.Style & 1) != 0;
        var termAll = (cod.Style & 4) != 0;
        foreach (var pb in precinct.Bands)
            for (var k = 0; k < pb.Blocks.Count; k++)
            {
                var block = pb.Blocks[k];
                var (x, y) = (k % Math.Max(1, pb.W), k / Math.Max(1, pb.W));
                bool included;
                var first = !block.Included;
                if (first) included = pb.Inclusion.Decode(bits, x, y, layer + 1);
                else included = bits.Bit() == 1;
                if (!included) continue;
                if (first)
                {
                    var t = 1;
                    while (!pb.Zero.Decode(bits, x, y, t)) t++;
                    block.ZeroPlanes = pb.Zero.Value(x, y);
                    block.Included = true;
                }
                // The number of new coding passes.
                int passes;
                if (bits.Bit() == 0) passes = 1;
                else if (bits.Bit() == 0) passes = 2;
                else
                {
                    var v = bits.Bits(2);
                    if (v < 3) passes = 3 + v;
                    else
                    {
                        v = bits.Bits(5);
                        passes = v < 31 ? 6 + v : 37 + bits.Bits(7);
                    }
                }
                while (bits.Bit() == 1) block.Lblock++;
                // The passes split into codeword segments, each with its own length.
                var segments = new List<(int, int)>();
                var start = block.Passes;
                var remaining = passes;
                var at = start;
                while (remaining > 0)
                {
                    var segEnd = SegmentEnd(at, bypass, termAll);
                    var inSeg = Math.Min(remaining, segEnd - at);
                    var segIndex = SegmentIndex(at, bypass, termAll);
                    while (block.Segments.Count <= segIndex) block.Segments.Add(([], 0));
                    var lengthBits = block.Lblock + (int)Math.Floor(Math.Log2(inSeg));
                    segments.Add((segIndex, bits.Bits(lengthBits)));
                    block.Segments[segIndex] = (block.Segments[segIndex].Data, block.Segments[segIndex].Passes + inSeg);
                    at += inSeg;
                    remaining -= inSeg;
                }
                block.Passes += passes;
                found.Add((block, segments));
            }
        bits.Align();
        return found;
    }

    /// <summary>The pass after the last of the codeword segment a pass is in.</summary>
    private static int SegmentEnd(int pass, bool bypass, bool termAll)
    {
        if (termAll) return pass + 1;
        if (!bypass) return int.MaxValue;
        if (pass < 10) return 10;
        // After the first ten: the raw significance and refinement passes, then the arithmetic cleanup pass.
        var k = (pass - 10) % 3;
        return k == 2 ? pass + 1 : pass - k + 2;
    }

    private static int SegmentIndex(int pass, bool bypass, bool termAll)
    {
        if (termAll) return pass;
        if (!bypass) return 0;
        if (pass < 10) return 0;
        var k = (pass - 10) / 3;
        return 1 + k * 2 + ((pass - 10) % 3 == 2 ? 1 : 0);
    }

    // ---- Tier 1: the coding passes (Annex D)

    private static class Tier1
    {
        private const int Run = 17, Uniform = 18;

        public static void DecodeBlock(int x0, int y0, int x1, int y1, List<(byte[] Data, int Passes)> segments, int zeroPlanes, Band band, Coding cod, int roi)
        {
            var (w, h) = (x1 - x0, y1 - y0);
            if (w <= 0 || h <= 0) return;
            var bypass = (cod.Style & 1) != 0;
            var reset = (cod.Style & 2) != 0;
            var termAll = (cod.Style & 4) != 0;
            var causal = (cod.Style & 8) != 0;
            var segmentation = (cod.Style & 32) != 0;
            var mag = new int[w * h];
            var sig = new byte[(w + 2) * (h + 2)];  // with a border, so neighbours never need checking
            var neg = new byte[w * h];
            var visited = new byte[w * h];
            var refined = new byte[w * h];
            var last = new sbyte[w * h]; // the plane of each coefficient's last decoded bit
            var contexts = new byte[19];
            void Reset()
            {
                Array.Clear(contexts);
                contexts[0] = 4 << 1;
                contexts[Run] = 3 << 1;
                contexts[Uniform] = 46 << 1;
            }
            Reset();
            int S(int x, int y) => sig[(y + 1) * (w + 2) + x + 1];
            var totalPasses = segments.Sum(s => s.Passes);
            var planes = band.Mb - zeroPlanes;
            if (planes <= 0 || totalPasses == 0) return;
            var plane = planes - 1;
            var passType = 2; // the first pass is a cleanup
            var segIndex = 0;
            var segLeft = segments.Count > 0 ? segments[0].Passes : 0;
            MqDecoder? mq = null;
            RawBits? raw = null;
            void Start()
            {
                var data = segIndex < segments.Count ? segments[segIndex].Data : [];
                // Arithmetic data, terminated by a marker the decoder reads as 0xFF bytes.
                mq = new MqDecoder([.. data, 0xFF, 0xFF], 0, data.Length + 2);
                raw = new RawBits(data);
            }
            Start();
            int Neighbours(int x, int y, int stripeBottom, out int hh, out int vv, out int dd)
            {
                var below = !causal || y + 1 < stripeBottom;
                hh = S(x - 1, y) + S(x + 1, y);
                vv = S(x, y - 1) + (below ? S(x, y + 1) : 0);
                dd = S(x - 1, y - 1) + S(x + 1, y - 1) + (below ? S(x - 1, y + 1) + S(x + 1, y + 1) : 0);
                return hh + vv + dd;
            }
            int ZeroContext(int hh, int vv, int dd)
            {
                switch (band.Type)
                {
                    case 3:
                        var hv = hh + vv;
                        return dd >= 3 ? 8 : dd == 2 ? (hv >= 1 ? 7 : 6) : dd == 1 ? (hv >= 2 ? 5 : hv == 1 ? 4 : 3) : (hv >= 2 ? 2 : hv == 1 ? 1 : 0);
                    case 1:
                        (hh, vv) = (vv, hh);
                        goto default;
                    default:
                        if (hh == 2) return 8;
                        if (hh == 1) return vv >= 1 ? 7 : dd >= 1 ? 6 : 5;
                        return vv == 2 ? 4 : vv == 1 ? 3 : dd >= 2 ? 2 : dd == 1 ? 1 : 0;
                }
            }
            int Contribution(int x1n, int y1n, int x2n, int y2n, bool second)
            {
                int One(int x, int y) => S(x, y) == 0 ? 0 : neg[y * w + x] == 1 ? -1 : 1;
                var a = x1n >= 0 && x1n < w && y1n >= 0 && y1n < h ? One(x1n, y1n) : 0;
                var b = second && x2n >= 0 && x2n < w && y2n >= 0 && y2n < h ? One(x2n, y2n) : 0;
                return Math.Sign(a + b);
            }
            void Sign(int x, int y, int stripeBottom, bool rawMode)
            {
                var i = y * w + x;
                if (rawMode) { neg[i] = (byte)raw!.Bit(); return; }
                var below = !causal || y + 1 < stripeBottom;
                var hc = Contribution(x - 1, y, x + 1, y, true);
                var vc = Contribution(x, y - 1, x, y + 1, below);
                var (ctx, xor) = (hc, vc) switch
                {
                    (1, 1) => (13, 0), (1, 0) => (12, 0), (1, -1) => (11, 0),
                    (0, 1) => (10, 0), (0, 0) => (9, 0), (0, -1) => (10, 1),
                    (-1, 1) => (11, 1), (-1, 0) => (12, 1), _ => (13, 1),
                };
                neg[i] = (byte)(mq!.Bit(contexts, ctx) ^ xor);
            }
            void Significant(int x, int y)
            {
                sig[(y + 1) * (w + 2) + x + 1] = 1;
                mag[y * w + x] |= 1 << plane;
                last[y * w + x] = (sbyte)plane;
            }
            for (var pass = 0; pass < totalPasses; pass++)
            {
                if (segLeft == 0)
                {
                    segIndex++;
                    if (segIndex >= segments.Count) break;
                    segLeft = segments[segIndex].Passes;
                    Start();
                }
                var rawMode = bypass && pass >= 10 && passType != 2;
                for (var sy = 0; sy < h; sy += 4)
                {
                    var stripeBottom = Math.Min(h, sy + 4);
                    for (var x = 0; x < w; x++)
                    {
                        var y = sy;
                        if (passType == 2 && sy + 4 <= h)
                        {
                            // The run mode: a column of four with nothing significant about them.
                            var quiet = true;
                            for (var k = 0; k < 4 && quiet; k++)
                                if (S(x, sy + k) != 0 || visited[(sy + k) * w + x] != 0 || Neighbours(x, sy + k, stripeBottom, out _, out _, out _) != 0) quiet = false;
                            if (quiet)
                            {
                                if (mq!.Bit(contexts, Run) == 0) continue;
                                var k = mq.Bit(contexts, Uniform) << 1 | mq.Bit(contexts, Uniform);
                                y = sy + k;
                                Significant(x, y);
                                Sign(x, y, stripeBottom, false);
                                y++;
                            }
                        }
                        for (; y < stripeBottom; y++)
                        {
                            var i = y * w + x;
                            switch (passType)
                            {
                                case 0: // significance propagation
                                {
                                    if (S(x, y) != 0) break;
                                    if (Neighbours(x, y, stripeBottom, out var hh, out var vv, out var dd) == 0) break;
                                    visited[i] = 1;
                                    var bit = rawMode ? raw!.Bit() : mq!.Bit(contexts, ZeroContext(hh, vv, dd));
                                    if (bit == 1) { Significant(x, y); Sign(x, y, stripeBottom, rawMode); }
                                    break;
                                }
                                case 1: // magnitude refinement
                                {
                                    if (S(x, y) == 0 || visited[i] != 0) break;
                                    int bit;
                                    if (rawMode) bit = raw!.Bit();
                                    else
                                    {
                                        var ctx = refined[i] != 0 ? 16 : Neighbours(x, y, stripeBottom, out _, out _, out _) > 0 ? 15 : 14;
                                        bit = mq!.Bit(contexts, ctx);
                                    }
                                    refined[i] = 1;
                                    last[i] = (sbyte)plane;
                                    if (bit == 1) mag[i] |= 1 << plane;
                                    break;
                                }
                                default: // cleanup
                                {
                                    if (S(x, y) != 0 || visited[i] != 0) break;
                                    Neighbours(x, y, stripeBottom, out var hh, out var vv, out var dd);
                                    if (mq!.Bit(contexts, ZeroContext(hh, vv, dd)) == 1) { Significant(x, y); Sign(x, y, stripeBottom, false); }
                                    break;
                                }
                            }
                        }
                    }
                }
                if (passType == 2)
                {
                    if (segmentation) for (var k = 0; k < 4; k++) mq!.Bit(contexts, Uniform);
                    Array.Clear(visited);
                    plane--;
                    if (plane < 0) { pass++; break; }
                }
                passType = (passType + 1) % 3;
                if (reset) Reset();
                segLeft--;
                if (termAll && segLeft == 0) { }
            }
            // The coefficients: region-of-interest ones shifted back down; then, for the planes below a coefficient's
            // last decoded bit, half of what they could add (to the middle of its range); signed, and scaled by the step.
            var bw = band.X1 - band.X0;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    var mi = mag[i];
                    if (mi == 0) continue;
                    var left = (int)last[i];
                    if (roi > 0 && mi >= 1 << roi) { mi >>= roi; left = Math.Max(0, left - roi); }
                    double m = mi;
                    if (left > 0) m += (1 << left) / 2.0;
                    var v = cod.Transform == 1 ? Math.Floor(m) : m * band.Step;
                    band.Coefficients[(y0 - band.Y0 + y) * bw + (x0 - band.X0 + x)] = neg[i] == 1 ? -v : v;
                }
        }
    }

    /// <summary>Raw (bypassed) bits: after 0xFF, the next byte's top bit is stuffing.</summary>
    private sealed class RawBits(byte[] data)
    {
        private int _pos, _left, _byte;
        public int Bit()
        {
            if (_left == 0)
            {
                var stuffed = _byte == 0xFF;
                _byte = _pos < data.Length ? data[_pos] : 0xFF;
                _pos++;
                _left = stuffed ? 7 : 8;
            }
            _left--;
            return _byte >> _left & 1;
        }
    }

    // ---- The wavelets (Annex F)

    private static class Wavelet
    {
        /// <summary>A tile-component's samples from its resolutions' bands, from the lowest resolution up.</summary>
        public static double[] Inverse(List<Resolution> res, bool reversible)
        {
            var ll = res[0].Bands[0];
            var (w, h) = (ll.X1 - ll.X0, ll.Y1 - ll.Y0);
            var a = (double[])ll.Coefficients.Clone();
            for (var r = 1; r < res.Count; r++)
            {
                var rs = res[r];
                var (u0, v0, u1, v1) = (rs.X0, rs.Y0, rs.X1, rs.Y1);
                var (nw, nh) = (u1 - u0, v1 - v0);
                var o = new double[Math.Max(0, nw * nh)];
                var (hl, lh, hh) = (rs.Bands[0], rs.Bands[1], rs.Bands[2]);
                // Interleave: even positions (on the absolute grid) low-pass, odd high-pass.
                for (var v = v0; v < v1; v++)
                    for (var u = u0; u < u1; u++)
                    {
                        var lowX = (u & 1) == 0;
                        var lowY = (v & 1) == 0;
                        double s;
                        if (lowX && lowY) s = At(a, w, h, u / 2 - CeilDiv(u0, 2), v / 2 - CeilDiv(v0, 2));
                        else if (!lowX && lowY) s = Coef(hl, (u - 1) / 2, v / 2);
                        else if (lowX) s = Coef(lh, u / 2, (v - 1) / 2);
                        else s = Coef(hh, (u - 1) / 2, (v - 1) / 2);
                        o[(v - v0) * nw + (u - u0)] = s;
                    }
                // Each row, then each column.
                var line = new double[Math.Max(nw, nh)];
                for (var y = 0; y < nh; y++)
                {
                    for (var x = 0; x < nw; x++) line[x] = o[y * nw + x];
                    OneD(line, nw, u0, reversible);
                    for (var x = 0; x < nw; x++) o[y * nw + x] = line[x];
                }
                for (var x = 0; x < nw; x++)
                {
                    for (var y = 0; y < nh; y++) line[y] = o[y * nw + x];
                    OneD(line, nh, v0, reversible);
                    for (var y = 0; y < nh; y++) o[y * nw + x] = line[y];
                }
                (a, w, h) = (o, nw, nh);
            }
            return a;
        }

        private static double At(double[] a, int w, int h, int x, int y) => x >= 0 && y >= 0 && x < w && y < h ? a[y * w + x] : 0;

        private static double Coef(Band b, int x, int y)
        {
            var (bx, by) = (x - b.X0, y - b.Y0);
            var bw = b.X1 - b.X0;
            return bx >= 0 && by >= 0 && bx < bw && by < b.Y1 - b.Y0 ? b.Coefficients[by * bw + bx] : 0;
        }

        /// <summary>One-dimensional synthesis (1D_SR) of n samples starting at absolute index i0, in place.</summary>
        private static void OneD(double[] x, int n, int i0, bool reversible)
        {
            if (n <= 0) return;
            if (n == 1)
            {
                if ((i0 & 1) == 1) x[0] = reversible ? Math.Floor(x[0] / 2) : x[0] / 2;
                return;
            }
            // Whole-sample symmetric extension: index k mirrored into [0, n).
            double X(int k)
            {
                while (k < 0 || k >= n) k = k < 0 ? -k : 2 * (n - 1) - k;
                return x[k];
            }
            bool Even(int k) => ((i0 + k) & 1) == 0;
            if (reversible)
            {
                var copy = (double[])x.Clone();
                double C(int k) { while (k < 0 || k >= n) k = k < 0 ? -k : 2 * (n - 1) - k; return copy[k]; }
                // Even samples first, from the odd ones round them; then the odd from the new even.
                for (var k = 0; k < n; k++)
                    if (Even(k)) copy[k] = x[k] - Math.Floor((Odd(k - 1) + Odd(k + 1) + 2) / 4);
                double Odd(int k) { while (k < 0 || k >= n) k = k < 0 ? -k : 2 * (n - 1) - k; return x[k]; }
                for (var k = 0; k < n; k++)
                    if (!Even(k)) copy[k] = x[k] + Math.Floor((C(k - 1) + C(k + 1)) / 2);
                Array.Copy(copy, x, n);
                return;
            }
            const double alpha = -1.586134342059924, beta = -0.052980118572961, gamma = 0.882911075530934, delta = 0.443506852043971, kk = 1.230174104914001;
            for (var k = 0; k < n; k++) x[k] = Even(k) ? x[k] * kk : x[k] / kk;
            void Lift(bool even, double c)
            {
                var y = (double[])x.Clone();
                double Y(int k) { while (k < 0 || k >= n) k = k < 0 ? -k : 2 * (n - 1) - k; return y[k]; }
                for (var k = 0; k < n; k++) if (Even(k) == even) x[k] = y[k] - c * (Y(k - 1) + Y(k + 1));
            }
            Lift(true, delta);
            Lift(false, gamma);
            Lift(true, beta);
            Lift(false, alpha);
            _ = X(0);
        }
    }
}
