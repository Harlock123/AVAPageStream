using System.IO.Compression;

namespace AVAPageStream.Core;

/// <summary>A picture's pixels in colour: red, green, blue and alpha for each, a row at a time.</summary>
public sealed record ColourImage(int Width, int Height, byte[] Rgba);

/// <summary>
/// Reading baseline TIFF, enough for the previews EPS files carry: the first image, in strips, black-and-white,
/// grey, palette, RGB or CMYK, 1 to 8 bits a sample, uncompressed, PackBits, LZW or Deflate, with or without the
/// horizontal predictor. Anything else (tiles, 16-bit samples, planes stored apart, JPEG) gives null.
/// </summary>
public static class Tiff
{
    public static ColourImage? Read(byte[] b)
    {
        try
        {
            return Decode(b);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            return null; // damaged
        }
    }

    private static ColourImage? Decode(byte[] b)
    {
        if (b.Length < 8) return null;
        var little = b[0] == 'I' && b[1] == 'I';
        if (!little && !(b[0] == 'M' && b[1] == 'M')) return null;
        int U16(int at) => little ? b[at] | b[at + 1] << 8 : b[at] << 8 | b[at + 1];
        long U32(int at) => little ? (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24) : (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
        if (U16(2) != 42) return null;
        var ifd = (int)U32(4);
        var count = U16(ifd);
        var tags = new Dictionary<int, long[]>();
        for (var i = 0; i < count; i++)
        {
            var e = ifd + 2 + i * 12;
            var (tag, type, n) = (U16(e), U16(e + 2), (int)U32(e + 4));
            var size = type switch { 3 => 2, 4 => 4, 1 or 2 or 7 => 1, _ => 0 };
            if (size == 0 || n <= 0 || n > 1 << 24) continue;
            var at = size * n <= 4 ? e + 8 : (int)U32(e + 8);
            var values = new long[n];
            for (var k = 0; k < n; k++) values[k] = size switch { 2 => U16(at + k * 2), 4 => U32(at + k * 4), _ => b[at + k] };
            tags[tag] = values;
        }
        long One(int tag, long otherwise) => tags.TryGetValue(tag, out var v) && v.Length > 0 ? v[0] : otherwise;
        var width = (int)One(256, 0);
        var height = (int)One(257, 0);
        if (width <= 0 || height <= 0 || width * (long)height > 50_000_000) return null;
        if (tags.ContainsKey(322) || One(284, 1) != 1) return null; // tiled, or planes stored apart
        var samples = (int)One(277, 1);
        var bits = (int)One(258, 1);
        if (tags.TryGetValue(258, out var all) && all.Any(x => x != bits)) return null;
        if (bits is not (1 or 2 or 4 or 8)) return null;
        var compression = One(259, 1);
        var photometric = One(262, samples >= 3 ? 2 : 1);
        var predictor = One(317, 1);
        if (!tags.TryGetValue(273, out var offsets) || !tags.TryGetValue(279, out var counts)) return null;
        var rowsPerStrip = (int)Math.Min(One(278, height), height);

        // The strips, uncompressed, one after another.
        var rowBytes = (width * samples * bits + 7) / 8;
        var data = new byte[rowBytes * height];
        var filled = 0;
        for (var s = 0; s < offsets.Length && filled < data.Length; s++)
        {
            var strip = b.AsSpan((int)offsets[s], (int)Math.Min(counts[s], b.Length - offsets[s]));
            var want = Math.Min(rowsPerStrip * rowBytes, data.Length - filled);
            var raw = compression switch
            {
                1 => strip.ToArray(),
                32773 => PackBits(strip, want),
                5 => Lzw(strip, want),
                8 or 32946 => Inflate(strip.ToArray(), want),
                _ => null,
            };
            if (raw is null) return null;
            Array.Copy(raw, 0, data, filled, Math.Min(raw.Length, want));
            filled += want;
        }
        if (predictor == 2 && bits == 8)
            for (var y = 0; y < height; y++)
                for (var x = samples; x < width * samples; x++)
                    data[y * rowBytes + x] += data[y * rowBytes + x - samples];

        var rgba = new byte[width * height * 4];
        var max = (1 << bits) - 1;
        int Sample(int row, int index)
        {
            var bit = index * bits;
            var v = data[row * rowBytes + bit / 8];
            return bits == 8 ? v : (v >> (8 - bits - bit % 8)) & max;
        }
        tags.TryGetValue(320, out var map);
        var alpha = samples > photometric switch { 2 => 3, 5 => 4, _ => 1 } && One(338, 0) is 1 or 2;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = x * samples;
                byte r, g, bl;
                switch (photometric)
                {
                    case 0 or 1: // white or black is zero
                        var v = Sample(y, i) * 255 / max;
                        r = g = bl = (byte)(photometric == 0 ? 255 - v : v);
                        break;
                    case 2:
                        (r, g, bl) = ((byte)(Sample(y, i) * 255 / max), (byte)(Sample(y, i + 1) * 255 / max), (byte)(Sample(y, i + 2) * 255 / max));
                        break;
                    case 3 when map is not null && map.Length >= 3 << bits:
                        var p = Sample(y, i);
                        var n = 1 << bits;
                        (r, g, bl) = ((byte)(map[p] >> 8), (byte)(map[n + p] >> 8), (byte)(map[2 * n + p] >> 8));
                        break;
                    case 5 when samples >= 4:
                        double C(int k) => Sample(y, i + k) / (double)max;
                        var k4 = 1 - C(3);
                        (r, g, bl) = ((byte)Math.Round(255 * (1 - C(0)) * k4), (byte)Math.Round(255 * (1 - C(1)) * k4), (byte)Math.Round(255 * (1 - C(2)) * k4));
                        break;
                    default:
                        return null;
                }
                var o = (y * width + x) * 4;
                (rgba[o], rgba[o + 1], rgba[o + 2]) = (r, g, bl);
                rgba[o + 3] = alpha ? (byte)(Sample(y, i + samples - 1) * 255 / max) : (byte)255;
            }
        return new ColourImage(width, height, rgba);
    }

    private static byte[] PackBits(ReadOnlySpan<byte> s, int want)
    {
        var o = new List<byte>(want);
        for (var i = 0; i < s.Length && o.Count < want;)
        {
            var n = (sbyte)s[i++];
            if (n >= 0) { for (var k = 0; k <= n && i < s.Length; k++) o.Add(s[i++]); }
            else if (n != -128 && i < s.Length) { var v = s[i++]; for (var k = 0; k < 1 - n; k++) o.Add(v); }
        }
        return [.. o];
    }

    /// <summary>TIFF's LZW: codes most significant bit first, 256 to clear, 257 to end, the code width growing early.</summary>
    private static byte[] Lzw(ReadOnlySpan<byte> s, int want)
    {
        var o = new List<byte>(want);
        var table = new List<byte[]>(4096);
        void Reset() { table.Clear(); for (var i = 0; i < 256; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); }
        Reset();
        var (bitPos, width) = (0, 9);
        byte[]? previous = null;
        while (o.Count < want && bitPos + width <= s.Length * 8)
        {
            var code = 0;
            for (var k = 0; k < width; k++, bitPos++) code = code << 1 | (s[bitPos >> 3] >> (7 - (bitPos & 7)) & 1);
            if (code == 257) break;
            if (code == 256) { Reset(); width = 9; previous = null; continue; }
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else if (previous is not null) entry = [.. previous, previous[0]];
            else break;
            o.AddRange(entry);
            if (previous is not null && table.Count < 4096) table.Add([.. previous, entry[0]]);
            previous = entry;
            width = table.Count >= 2047 ? 12 : table.Count >= 1023 ? 11 : table.Count >= 511 ? 10 : 9;
        }
        return [.. o];
    }

    private static byte[] Inflate(byte[] s, int want)
    {
        using var z = new ZLibStream(new MemoryStream(s), CompressionMode.Decompress);
        var o = new byte[want];
        var read = 0;
        while (read < want && z.Read(o, read, want - read) is var n and > 0) read += n;
        return o;
    }
}
