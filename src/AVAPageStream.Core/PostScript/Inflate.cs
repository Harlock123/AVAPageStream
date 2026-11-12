namespace AVAPageStream.Core.PostScript;

/// <summary>
/// Inflating zlib data read a byte at a time, taking no more than its own (the 2-byte header, the deflate blocks
/// and the 4-byte check), so what follows it in a program is left to read. RFC 1950 and 1951.
/// </summary>
public static class Inflate
{
    private sealed class Bits(Func<int> read)
    {
        private int _byte, _left;

        public int Bit()
        {
            if (_left == 0) { _byte = read(); if (_byte < 0) throw new InvalidDataException("deflate data ends early"); _left = 8; }
            var b = _byte & 1;
            _byte >>= 1;
            _left--;
            return b;
        }

        public int Take(int n) { var v = 0; for (var k = 0; k < n; k++) v |= Bit() << k; return v; }

        public void Align() => _left = 0;

        public int Byte() { Align(); var b = read(); if (b < 0) throw new InvalidDataException("deflate data ends early"); return b; }
    }

    private sealed class Huffman
    {
        private readonly int[] _counts = new int[16], _symbols;

        public Huffman(int[] lengths)
        {
            foreach (var l in lengths) _counts[l]++;
            _counts[0] = 0;
            var offsets = new int[16];
            for (var k = 1; k < 16; k++) offsets[k] = offsets[k - 1] + _counts[k - 1];
            _symbols = new int[lengths.Length];
            for (var s = 0; s < lengths.Length; s++) if (lengths[s] != 0) _symbols[offsets[lengths[s]]++] = s;
        }

        public int Decode(Bits b)
        {
            int code = 0, first = 0, index = 0;
            for (var len = 1; len < 16; len++)
            {
                code |= b.Bit();
                var count = _counts[len];
                if (code - count < first) return _symbols[index + (code - first)];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }
            throw new InvalidDataException("bad Huffman code");
        }
    }

    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    /// <summary>A zlib stream (header, deflate data and check) read from <paramref name="read"/>, a byte a call.</summary>
    public static byte[] Zlib(Func<int> read, int limit = 200_000_000)
    {
        var b = new Bits(read);
        var cmf = b.Byte();
        var flg = b.Byte();
        if ((cmf & 15) != 8 || (cmf << 8 | flg) % 31 != 0) throw new InvalidDataException("not zlib data");
        if ((flg & 32) != 0) for (var k = 0; k < 4; k++) b.Byte(); // a preset dictionary's check: none is ever given
        var o = new List<byte>();
        bool last;
        do
        {
            last = b.Bit() == 1;
            var type = b.Take(2);
            if (type == 0)
            {
                b.Align();
                var len = b.Byte() | b.Byte() << 8;
                b.Byte(); b.Byte();
                for (var k = 0; k < len; k++) o.Add((byte)b.Byte());
                continue;
            }
            Huffman lit, dist;
            if (type == 1)
            {
                var l = new int[288];
                for (var k = 0; k < 288; k++) l[k] = k < 144 ? 8 : k < 256 ? 9 : k < 280 ? 7 : 8;
                lit = new Huffman(l);
                dist = new Huffman(Enumerable.Repeat(5, 30).ToArray());
            }
            else if (type == 2)
            {
                var hlit = b.Take(5) + 257;
                var hdist = b.Take(5) + 1;
                var hclen = b.Take(4) + 4;
                int[] order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
                var cl = new int[19];
                for (var k = 0; k < hclen; k++) cl[order[k]] = b.Take(3);
                var clh = new Huffman(cl);
                var lengths = new int[hlit + hdist];
                for (var k = 0; k < lengths.Length;)
                {
                    var sym = clh.Decode(b);
                    if (sym < 16) { lengths[k++] = sym; continue; }
                    var (rep, value) = sym switch { 16 => (3 + b.Take(2), k > 0 ? lengths[k - 1] : 0), 17 => (3 + b.Take(3), 0), _ => (11 + b.Take(7), 0) };
                    for (var r = 0; r < rep && k < lengths.Length; r++) lengths[k++] = value;
                }
                lit = new Huffman(lengths[..hlit]);
                dist = new Huffman(lengths[hlit..]);
            }
            else throw new InvalidDataException("bad deflate block");
            while (true)
            {
                var sym = lit.Decode(b);
                if (sym < 256) { o.Add((byte)sym); continue; }
                if (sym == 256) break;
                sym -= 257;
                if (sym >= LengthBase.Length) throw new InvalidDataException("bad length");
                var len = LengthBase[sym] + b.Take(LengthExtra[sym]);
                var d = dist.Decode(b);
                var distance = DistBase[d] + b.Take(DistExtra[d]);
                var start = o.Count - distance;
                if (start < 0) throw new InvalidDataException("bad distance");
                for (var k = 0; k < len; k++) o.Add(o[start + k]);
                if (o.Count > limit) throw new InvalidDataException("too much data");
            }
        } while (!last);
        for (var k = 0; k < 4; k++) b.Byte(); // the Adler-32 check
        return [.. o];
    }
}
