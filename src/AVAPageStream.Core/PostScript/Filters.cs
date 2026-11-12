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
