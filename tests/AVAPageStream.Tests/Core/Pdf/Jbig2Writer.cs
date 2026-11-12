using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>
/// Writes JBIG2 for the tests, as T.88 says an encoder does (Annex E's MQ encoder, the integer and symbol ID
/// procedures, generic region coding), so its streams can be checked against Poppler's decoder as well as ours.
/// </summary>
internal sealed class MqEncoder
{
    private readonly List<byte> _out = [0]; // the byte before the first, as B starts pointing at
    private uint _a = 0x8000, _c;
    private int _ct = 12;

    public void Encode(byte[] contexts, int cx, int d)
    {
        var index = contexts[cx] >> 1;
        var mps = contexts[cx] & 1;
        var (qe, nmps, nlps, sw) = MqDecoder.Table[index];
        _a -= (uint)qe;
        if (d == mps)
        {
            if ((_a & 0x8000) == 0)
            {
                if (_a < qe) _a = (uint)qe;
                else _c += (uint)qe;
                index = nmps;
                Renorm();
            }
            else _c += (uint)qe;
        }
        else
        {
            if (_a < qe) _c += (uint)qe;
            else _a = (uint)qe;
            if (sw) mps = 1 - mps;
            index = nlps;
            Renorm();
        }
        contexts[cx] = (byte)(index << 1 | mps);
    }

    private void Renorm()
    {
        do
        {
            _a <<= 1;
            _c <<= 1;
            if (--_ct == 0) ByteOut();
        } while ((_a & 0x8000) == 0);
    }

    private void ByteOut()
    {
        var b = _out[^1];
        if (b == 0xFF)
        {
            _out.Add((byte)(_c >> 20));
            _c &= 0xFFFFF;
            _ct = 7;
        }
        else if (_c < 0x8000000)
        {
            _out.Add((byte)(_c >> 19));
            _c &= 0x7FFFF;
            _ct = 8;
        }
        else
        {
            _out[^1] = (byte)(b + 1);
            if (_out[^1] == 0xFF)
            {
                _c &= 0x7FFFFFF;
                _out.Add((byte)(_c >> 20));
                _c &= 0xFFFFF;
                _ct = 7;
            }
            else
            {
                _out.Add((byte)(_c >> 19));
                _c &= 0x7FFFF;
                _ct = 8;
            }
        }
    }

    /// <summary>Flushes (E.2.9) and ends with the 0xFF 0xAC marker; the bytes written.</summary>
    public byte[] Finish()
    {
        var temp = _c + _a;
        _c |= 0xFFFF;
        if (_c >= temp) _c -= 0x8000;
        _c <<= _ct;
        ByteOut();
        _c <<= _ct;
        ByteOut();
        if (_out[^1] == 0xFF) _out.RemoveAt(_out.Count - 1);
        _out.AddRange([0xFF, 0xAC]);
        return [.. _out.Skip(1)];
    }
}

internal sealed class Jbig2Coder
{
    public MqEncoder Mq { get; } = new();
    private readonly Dictionary<string, byte[]> _contexts = [];
    public byte[] Contexts(string name, int size = 1 << 16)
    {
        if (!_contexts.TryGetValue(name, out var c)) _contexts[name] = c = new byte[size];
        return c;
    }

    /// <summary>An integer by the IAx procedure, or out of band (null).</summary>
    public void Integer(string name, int? value)
    {
        var cx = Contexts(name, 512);
        var prev = 1;
        void Bits(long v, int n)
        {
            for (var i = n - 1; i >= 0; i--)
            {
                var bit = (int)(v >> i & 1);
                Mq.Encode(cx, prev, bit);
                prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
            }
        }
        if (value is null) { Bits(1, 1); Bits(0, 1); Bits(0, 2); return; }
        var v = Math.Abs(value.Value);
        Bits(value < 0 ? 1 : 0, 1);
        if (v < 4) { Bits(0, 1); Bits(v, 2); }
        else if (v < 20) { Bits(0b10, 2); Bits(v - 4, 4); }
        else if (v < 84) { Bits(0b110, 3); Bits(v - 20, 6); }
        else if (v < 340) { Bits(0b1110, 4); Bits(v - 84, 8); }
        else if (v < 4436) { Bits(0b11110, 5); Bits(v - 340, 12); }
        else { Bits(0b11111, 5); Bits(v - 4436, 32); }
    }

    public void Id(int id, int length)
    {
        var cx = Contexts("IAID", 1 << Math.Min(length + 1, 24));
        var prev = 1;
        for (var i = length - 1; i >= 0; i--)
        {
            var bit = id >> i & 1;
            Mq.Encode(cx, prev, bit);
            prev = (prev << 1) | bit;
        }
    }

    private static readonly (int X, int Y)[][] Templates =
    [
        [(-1, -2), (0, -2), (1, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (2, -1), (-4, 0), (-3, 0), (-2, 0), (-1, 0)],
        [(-1, -2), (0, -2), (1, -2), (2, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (2, -1), (-3, 0), (-2, 0), (-1, 0)],
        [(-1, -2), (0, -2), (1, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (-2, 0), (-1, 0)],
        [(-3, -1), (-2, -1), (-1, -1), (0, -1), (1, -1), (-4, 0), (-3, 0), (-2, 0), (-1, 0)],
    ];

    private static readonly int[] Sltp = [0x9B25, 0x0795, 0x00E5, 0x0195];

    /// <summary>A generic region's bitmap (a byte a pixel, 1 black), by its template, with typical prediction if asked.</summary>
    public void Generic(Jbig2.Bitmap b, int template, bool tpgdon, (int X, int Y)[] at)
    {
        var pixels = Templates[template].Concat(at).OrderBy(p => p.Y).ThenBy(p => p.X).ToArray();
        var cx = Contexts("GB");
        var ltp = 0;
        for (var y = 0; y < b.H; y++)
        {
            if (tpgdon)
            {
                var same = y > 0 && b.P.AsSpan(y * b.W, b.W).SequenceEqual(b.P.AsSpan((y - 1) * b.W, b.W))
                           || y == 0 && b.P.AsSpan(0, b.W).IndexOfAnyExcept((byte)0) < 0;
                var sltp = (same ? 1 : 0) ^ ltp;
                Mq.Encode(cx, Sltp[template], sltp);
                ltp ^= sltp;
                if (ltp == 1) continue;
            }
            for (var x = 0; x < b.W; x++)
            {
                var ctx = 0;
                foreach (var (px, py) in pixels) ctx = (ctx << 1) | b.Get(x + px, y + py);
                Mq.Encode(cx, ctx, b.P[y * b.W + x]);
            }
        }
    }
}

/// <summary>JBIG2 segments, as embedded in PDF (no file header), on page 1.</summary>
internal sealed class Jbig2Stream
{
    private readonly MemoryStream _o = new();
    private int _number;

    private void U8(int v) => _o.WriteByte((byte)v);
    private void I32(int v) { U8(v >> 24); U8(v >> 16); U8(v >> 8); U8(v); }
    private static byte[] Be32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    public static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];

    /// <summary>Adds a segment; its number.</summary>
    public int Add(int type, byte[] data, params int[] refers)
    {
        var n = _number++;
        I32(n);
        U8(type);
        U8(refers.Length << 5);
        foreach (var r in refers) U8(r);
        U8(1);
        I32(data.Length);
        _o.Write(data);
        return n;
    }

    /// <summary>Numbers continue from another stream's (the globals').</summary>
    public Jbig2Stream From(Jbig2Stream before) { _number = before._number; return this; }

    public static byte[] PageInfo(int w, int h) => [.. Be32(w), .. Be32(h), .. Be32(0), .. Be32(0), 0, 0, 0];

    public static byte[] RegionInfo(int w, int h, int x, int y, int op = 0) => [.. Be32(w), .. Be32(h), .. Be32(x), .. Be32(y), (byte)op];

    public byte[] Bytes => _o.ToArray();
}
