using System.IO.Compression;

namespace AVAPageStream.Core.PostScript;

/// <summary>The decoding filters EPS files use for their data.</summary>
public static class Filters
{
    /// <summary>ASCII85 (base 85, 'z' for four zeros), up to its ~&gt; if it has one.</summary>
    public static byte[] Ascii85(ReadOnlySpan<byte> s)
    {
        var o = new List<byte>(s.Length * 4 / 5);
        long v = 0;
        var n = 0;
        foreach (var c in s)
        {
            if (c == '~') break;
            if (c is 0 or 9 or 10 or 12 or 13 or 32) continue;
            if (c == 'z' && n == 0) { o.AddRange([0, 0, 0, 0]); continue; }
            if (c is < (byte)'!' or > (byte)'u') throw new PsError("ioerror", "ASCII85");
            v = v * 85 + (c - '!');
            if (++n == 5)
            {
                o.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
                (v, n) = (0, 0);
            }
        }
        if (n > 1)
        {
            for (var k = n; k < 5; k++) v = v * 85 + 84;
            for (var k = 0; k < n - 1; k++) o.Add((byte)(v >> (24 - 8 * k)));
        }
        return [.. o];
    }

    /// <summary>Hexadecimal pairs, up to a '&gt;' if there is one.</summary>
    public static byte[] AsciiHex(ReadOnlySpan<byte> s)
    {
        var o = new List<byte>(s.Length / 2);
        var half = -1;
        foreach (var c in s)
        {
            if (c == '>') break;
            var d = Scanner.Hex(c);
            if (d < 0) continue;
            if (half < 0) half = d;
            else { o.Add((byte)(half << 4 | d)); half = -1; }
        }
        if (half >= 0) o.Add((byte)(half << 4));
        return [.. o];
    }

    /// <summary>Run length: a length byte under 128 for so many bytes after it plus one, over 128 for a byte repeated, 128 to end.</summary>
    public static byte[] RunLength(ReadOnlySpan<byte> s)
    {
        var o = new List<byte>(s.Length * 2);
        for (var i = 0; i < s.Length;)
        {
            var n = s[i++];
            if (n == 128) break;
            if (n < 128) { for (var k = 0; k <= n && i < s.Length; k++) o.Add(s[i++]); }
            else if (i < s.Length) { var b = s[i++]; for (var k = 0; k < 257 - n; k++) o.Add(b); }
        }
        return [.. o];
    }

    /// <summary>LZW as PostScript and PDF write it (EarlyChange 1).</summary>
    public static byte[] Lzw(ReadOnlySpan<byte> s)
    {
        var o = new List<byte>(s.Length * 3);
        var table = new List<byte[]>(4096);
        void Reset() { table.Clear(); for (var i = 0; i < 256; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); }
        Reset();
        var (bit, width) = (0, 9);
        byte[]? previous = null;
        while (bit + width <= s.Length * 8)
        {
            var code = 0;
            for (var k = 0; k < width; k++, bit++) code = code << 1 | (s[bit >> 3] >> (7 - (bit & 7)) & 1);
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

    /// <summary>Run length, read from a file up to its end-of-data byte.</summary>
    public static byte[] RunLength(Scanner sc)
    {
        var o = new List<byte>();
        while (sc.Read() is var n and >= 0 and not 128)
        {
            if (n < 128) { for (var k = 0; k <= n && sc.Read() is var b and >= 0; k++) o.Add((byte)b); }
            else if (sc.Read() is var b and >= 0) { for (var k = 0; k < 257 - n; k++) o.Add((byte)b); }
        }
        return [.. o];
    }

    /// <summary>LZW, read from a file up to its end-of-data code (and no further).</summary>
    public static byte[] Lzw(Scanner sc)
    {
        var bytes = new List<byte>();
        var o = new List<byte>();
        var table = new List<byte[]>(4096);
        void Reset() { table.Clear(); for (var i = 0; i < 256; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); }
        Reset();
        var (bit, width) = (0, 9);
        byte[]? previous = null;
        while (true)
        {
            while (bytes.Count * 8 < bit + width && sc.Read() is var b and >= 0) bytes.Add((byte)b);
            if (bytes.Count * 8 < bit + width) break;
            var code = 0;
            for (var k = 0; k < width; k++, bit++) code = code << 1 | (bytes[bit >> 3] >> (7 - (bit & 7)) & 1);
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

    /// <summary>Flate, read from a file to the end of its zlib stream (and no further).</summary>
    public static byte[] Flate(Scanner sc)
    {
        try { return Inflate.Zlib(sc.Read); }
        catch (InvalidDataException) { return []; }
    }

    /// <summary>A JPEG read from a file: its segments, then its scans' data, up to the end-of-image marker.</summary>
    public static byte[] Jpeg(Scanner sc)
    {
        var o = new List<byte>();
        int Next() { var b = sc.Read(); if (b >= 0) o.Add((byte)b); return b; }
        if (Next() != 0xFF || Next() != 0xD8) return [.. o];
        while (true)
        {
            int marker;
            do marker = Next(); while (marker == 0xFF);
            if (marker < 0 || marker == 0xD9) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue; // no length
            var hi = Next();
            var lo = Next();
            if (lo < 0) break;
            for (var k = 0; k < (hi << 8 | lo) - 2; k++) if (Next() < 0) return [.. o];
            if (marker != 0xDA) continue;
            // The scan's data: to the next marker that isn't a stuffed byte or a restart.
            while (true)
            {
                var b = Next();
                if (b < 0) return [.. o];
                if (b != 0xFF) continue;
                var c = Next();
                if (c == 0 || c is >= 0xD0 and <= 0xD7) continue;
                if (c == 0xD9) return [.. o];
                // Another segment (a progressive JPEG's next tables and scan): its length, then on.
                var h2 = Next();
                var l2 = Next();
                if (l2 < 0) return [.. o];
                for (var k = 0; k < (h2 << 8 | l2) - 2; k++) if (Next() < 0) return [.. o];
                if (c != 0xDA) { marker = -2; break; }
            }
            if (marker == -2) continue;
        }
        return [.. o];
    }

    /// <summary>Flate (zlib).</summary>
    public static byte[] Flate(byte[] s)
    {
        using var z = new ZLibStream(new MemoryStream(s), CompressionMode.Decompress);
        using var o = new MemoryStream();
        try { z.CopyTo(o); }
        catch (InvalidDataException) { } // keep what came before damage, as interpreters do
        return o.ToArray();
    }
}
