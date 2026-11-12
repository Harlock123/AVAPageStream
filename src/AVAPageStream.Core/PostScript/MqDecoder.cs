namespace AVAPageStream.Core.PostScript;

/// <summary>
/// The MQ arithmetic decoder JBIG2 and JPEG 2000 share (T.88 Annex E, T.800 Annex C). A context is a byte: its state's
/// index in the probability table times two, plus its more probable symbol.
/// </summary>
public sealed class MqDecoder
{
    /// <summary>The probability table: Qe, the next state after the more and the less probable symbol, and whether to switch.</summary>
    internal static readonly (int Qe, byte Nmps, byte Nlps, bool Switch)[] Table =
    [
        (0x5601, 1, 1, true), (0x3401, 2, 6, false), (0x1801, 3, 9, false), (0x0AC1, 4, 12, false),
        (0x0521, 5, 29, false), (0x0221, 38, 33, false), (0x5601, 7, 6, true), (0x5401, 8, 14, false),
        (0x4801, 9, 14, false), (0x3801, 10, 14, false), (0x3001, 11, 17, false), (0x2401, 12, 18, false),
        (0x1C01, 13, 20, false), (0x1601, 29, 21, false), (0x5601, 15, 14, true), (0x5401, 16, 14, false),
        (0x5101, 17, 15, false), (0x4801, 18, 16, false), (0x3801, 19, 17, false), (0x3401, 20, 18, false),
        (0x3001, 21, 19, false), (0x2801, 22, 19, false), (0x2401, 23, 20, false), (0x2201, 24, 21, false),
        (0x1C01, 25, 22, false), (0x1801, 26, 23, false), (0x1601, 27, 24, false), (0x1401, 28, 25, false),
        (0x1201, 29, 26, false), (0x1101, 30, 27, false), (0x0AC1, 31, 28, false), (0x09C1, 32, 29, false),
        (0x08A1, 33, 30, false), (0x0521, 34, 31, false), (0x0441, 35, 32, false), (0x02A1, 36, 33, false),
        (0x0221, 37, 34, false), (0x0141, 38, 35, false), (0x0111, 39, 36, false), (0x0085, 40, 37, false),
        (0x0049, 41, 38, false), (0x0025, 42, 39, false), (0x0015, 43, 40, false), (0x0009, 44, 41, false),
        (0x0005, 45, 42, false), (0x0001, 45, 43, false), (0x5601, 46, 46, false),
    ];

    private readonly byte[] _data;
    private readonly int _end;
    private int _bp, _ct;
    private uint _chigh, _clow, _a;

    public MqDecoder(byte[] data, int start = 0, int end = -1)
    {
        _data = data;
        _end = end < 0 ? data.Length : end;
        _bp = start;
        _chigh = start < _end ? data[start] : 0xFFu;
        _clow = 0;
        ByteIn();
        _chigh = ((_chigh << 7) & 0xFFFF) | ((_clow >> 9) & 0x7F);
        _clow = (_clow << 7) & 0xFFFF;
        _ct -= 7;
        _a = 0x8000;
    }

    private int At(int i) => i < _end ? _data[i] : 0xFF;

    private void ByteIn()
    {
        if (At(_bp) == 0xFF)
        {
            if (At(_bp + 1) > 0x8F) { _clow += 0xFF00; _ct = 8; }
            else { _bp++; _clow += (uint)At(_bp) << 9; _ct = 7; }
        }
        else
        {
            _bp++;
            _clow += _bp < _end ? (uint)_data[_bp] << 8 : 0xFF00u;
            _ct = 8;
        }
        if (_clow > 0xFFFF)
        {
            _chigh += _clow >> 16;
            _clow &= 0xFFFF;
        }
    }

    /// <summary>Decodes a bit in a context (updating the context).</summary>
    public int Bit(byte[] contexts, int cx)
    {
        var index = contexts[cx] >> 1;
        var mps = contexts[cx] & 1;
        var (qe, nmps, nlps, sw) = Table[index];
        var a = _a - (uint)qe;
        int d;
        if (_chigh < qe)
        {
            // The less probable symbol's interval, or the exchange.
            if (a < qe) { a = (uint)qe; d = mps; index = nmps; }
            else { a = (uint)qe; d = 1 ^ mps; if (sw) mps = d; index = nlps; }
        }
        else
        {
            _chigh -= (uint)qe;
            if ((a & 0x8000) != 0) { _a = a; return mps; }
            if (a < qe) { d = 1 ^ mps; if (sw) mps = d; index = nlps; }
            else { d = mps; index = nmps; }
        }
        do
        {
            if (_ct == 0) ByteIn();
            a <<= 1;
            _chigh = ((_chigh << 1) & 0xFFFF) | ((_clow >> 15) & 1);
            _clow = (_clow << 1) & 0xFFFF;
            _ct--;
        } while ((a & 0x8000) == 0);
        _a = a;
        contexts[cx] = (byte)(index << 1 | mps);
        return d;
    }
}
