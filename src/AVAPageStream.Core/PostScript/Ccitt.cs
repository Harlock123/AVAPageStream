namespace AVAPageStream.Core.PostScript;

/// <summary>
/// CCITT fax decoding, as PDF's CCITTFaxDecode filter does it: Group 3 one-dimensional (K = 0) and mixed with
/// two-dimensional lines (K &gt; 0), and Group 4 (K &lt; 0). Gives rows of bits, each to a byte boundary: by default 0 for
/// black and 1 for white, or the other way round with BlackIs1.
/// </summary>
public static class Ccitt
{
    // Run lengths by code (as written in T.4), white and black; makeup codes of 1792 and up are both colours'.
    private const string WhiteCodes =
        "00110101 000111 0111 1000 1011 1100 1110 1111 10011 10100 00111 01000 001000 000011 110100 110101 101010 101011 0100111 0001100 0001000 0010111 0000011 0000100 0101000 0101011 0010011 0100100 0011000 00000010 00000011 00011010 00011011 00010010 00010011 00010100 00010101 00010110 00010111 00101000 00101001 00101010 00101011 00101100 00101101 00000100 00000101 00001010 00001011 01010010 01010011 01010100 01010101 00100100 00100101 01011000 01011001 01011010 01011011 01001010 01001011 00110010 00110011 00110100";
    private const string WhiteMakeup =
        "11011 10010 010111 0110111 00110110 00110111 01100100 01100101 01101000 01100111 011001100 011001101 011010010 011010011 011010100 011010101 011010110 011010111 011011000 011011001 011011010 011011011 010011000 010011001 010011010 011000 010011011";
    private const string BlackCodes =
        "0000110111 010 11 10 011 0011 0010 00011 000101 000100 0000100 0000101 0000111 00000100 00000111 000011000 0000010111 0000011000 0000001000 00001100111 00001101000 00001101100 00000110111 00000101000 00000010111 00000011000 000011001010 000011001011 000011001100 000011001101 000001101000 000001101001 000001101010 000001101011 000011010010 000011010011 000011010100 000011010101 000011010110 000011010111 000001101100 000001101101 000011011010 000011011011 000001010100 000001010101 000001010110 000001010111 000001100100 000001100101 000001010010 000001010011 000000100100 000000110111 000000111000 000000100111 000000101000 000001011000 000001011001 000000101011 000000101100 000001011010 000001100110 000001100111";
    private const string BlackMakeup =
        "0000001111 000011001000 000011001001 000001011011 000000110011 000000110100 000000110101 0000001101100 0000001101101 0000001001010 0000001001011 0000001001100 0000001001101 0000001110010 0000001110011 0000001110100 0000001110101 0000001110110 0000001110111 0000001010010 0000001010011 0000001010100 0000001010101 0000001011010 0000001011011 0000001100100 0000001100101";
    private const string ExtendedMakeup =
        "00000001000 00000001100 00000001101 000000010010 000000010011 000000010100 000000010101 000000010110 000000010111 000000011100 000000011101 000000011110 000000011111";

    /// <summary>Each colour's codes: for each code length (to 13), code to run length.</summary>
    private static readonly Dictionary<int, int>[] White = Table(WhiteCodes, WhiteMakeup), Black = Table(BlackCodes, BlackMakeup);

    private static Dictionary<int, int>[] Table(string terminating, string makeup)
    {
        var t = new Dictionary<int, int>[14];
        for (var i = 0; i < t.Length; i++) t[i] = [];
        void Add(string code, int run) => t[code.Length][Convert.ToInt32(code, 2)] = run;
        var term = terminating.Split(' ');
        for (var i = 0; i < term.Length; i++) Add(term[i], i);
        var make = makeup.Split(' ');
        for (var i = 0; i < make.Length; i++) Add(make[i], 64 * (i + 1));
        var ext = ExtendedMakeup.Split(' ');
        for (var i = 0; i < ext.Length; i++) Add(ext[i], 1792 + 64 * i);
        return t;
    }

    private sealed class Bits(byte[] data)
    {
        private long _bit;
        public bool End => _bit >= data.Length * 8L;
        public int Read()
        {
            if (End) { _bit++; return 0; }
            var b = data[_bit >> 3] >> (7 - (int)(_bit & 7)) & 1;
            _bit++;
            return b;
        }
        public int Peek(int n)
        {
            var keep = _bit;
            var v = 0;
            for (var i = 0; i < n; i++) v = v << 1 | Read();
            _bit = keep;
            return v;
        }
        public void Skip(int n) => _bit += n;
        public void Align() => _bit = (_bit + 7) & ~7L;
        public bool Overrun => _bit > data.Length * 8L + 32;
    }

    /// <summary>A run of one colour: makeup codes, then a terminating one. -1 if the data has no code here.</summary>
    private static int Run(Bits bits, bool white)
    {
        var table = white ? White : Black;
        var total = 0;
        while (true)
        {
            var code = 0;
            var found = -1;
            for (var len = 1; len <= 13; len++)
            {
                code = code << 1 | bits.Read();
                if (len >= 2 && table[len].TryGetValue(code, out var run)) { found = run; break; }
            }
            if (found < 0) return -1;
            total += found;
            if (found < 64) return total;
            if (bits.Overrun) return -1;
        }
    }

    /// <summary>Skips an end-of-line code (with any fill bits before it) if one is here; whether it was.</summary>
    private static bool SkipEol(Bits bits)
    {
        // Eleven or more zeros and a one.
        if (bits.Peek(11) != 0) return false;
        var zeros = 0;
        while (!bits.End && bits.Peek(1) == 0) { bits.Skip(1); zeros++; }
        if (bits.End) return false;
        bits.Skip(1);
        return zeros >= 11;
    }

    public static byte[] Decode(byte[] data, int k = 0, int columns = 1728, int rows = 0, bool byteAlign = false, bool blackIs1 = false, bool endOfBlock = true)
    {
        columns = Math.Max(1, columns);
        var rowBytes = (columns + 7) / 8;
        var bits = new Bits(data);
        var output = new List<byte>(rowBytes * Math.Max(rows, 64));
        // The reference line's changing elements (where the colour changes, white to black first), and the line now.
        var reference = new List<int> { columns, columns };
        var changes = new List<int>();
        var row = new byte[rowBytes];
        for (var line = 0; rows <= 0 || line < rows; line++)
        {
            if (bits.End) break;
            if (k < 0 && byteAlign) bits.Align();
            var twoD = k < 0;
            if (k >= 0)
            {
                // Group 3: an end of line (or two, at the end of the page), then for K > 0 a bit saying how it is coded.
                var eols = 0;
                while (SkipEol(bits)) eols++;
                if (eols >= 2 && line > 0 && endOfBlock) break;
                if (k > 0) twoD = bits.Read() == 0;
            }
            else if (bits.Peek(24) == 0x001001) break; // Group 4's end of block
            changes.Clear();
            var ok = twoD ? TwoD(bits, reference, changes, columns) : OneD(bits, changes, columns);
            if (!ok) break;
            // The row's pixels from its changes: white (1) until the first, then turn about.
            Array.Fill(row, (byte)0xFF);
            for (var c = 0; c + 1 <= changes.Count; c += 2)
            {
                var from = changes[c];
                var to = c + 1 < changes.Count ? changes[c + 1] : columns;
                for (var x = Math.Max(0, from); x < Math.Min(columns, to); x++) row[x >> 3] &= (byte)~(0x80 >> (x & 7));
            }
            // The last byte's spare bits stay as written (white), as other decoders leave them.
            if (blackIs1) for (var i = 0; i < rowBytes; i++) row[i] = (byte)~row[i];
            output.AddRange(row);
            reference.Clear();
            reference.AddRange(changes);
            reference.Add(columns);
            reference.Add(columns);
            // (For Group 3, byte alignment is fill bits before each end of line, which skipping it passes over.)
            if (bits.Overrun) break;
        }
        return [.. output];
    }

    private static bool OneD(Bits bits, List<int> changes, int columns)
    {
        var at = 0;
        var white = true;
        while (at < columns)
        {
            var run = Run(bits, white);
            if (run < 0) return changes.Count > 0 || at > 0;
            at += run;
            changes.Add(Math.Min(at, columns));
            white = !white;
        }
        while (changes.Count > 0 && changes[^1] >= columns) changes.RemoveAt(changes.Count - 1); // at the end: no change
        return true;
    }

    /// <summary>A two-dimensionally coded line, against the reference line: pass, horizontal and vertical modes.</summary>
    private static bool TwoD(Bits bits, List<int> reference, List<int> changes, int columns)
    {
        var a0 = -1;
        var white = true;
        var r = 0; // where to start looking for b1 on the reference line
        while (a0 < columns)
        {
            // b1: the first change on the reference line after a0 to the colour a0 isn't; b2 the one after it.
            while (r > 0 && reference[r - 1] > a0) r--;
            while (r < reference.Count && (reference[r] <= a0 || (r % 2 == 0) != white)) r++;
            var b1 = r < reference.Count ? reference[r] : columns;
            var b2 = r + 1 < reference.Count ? reference[r + 1] : columns;
            int mode;
            if (bits.Read() == 1) mode = 0; // V0
            else if (bits.Read() == 1) mode = bits.Read() == 1 ? 1 : -1; // VR1 011, VL1 010
            else if (bits.Read() == 1) mode = 10; // horizontal 001
            else if (bits.Read() == 1) mode = 20; // pass 0001
            else if (bits.Read() == 1) mode = bits.Read() == 1 ? 2 : -2; // VR2 000011, VL2 000010
            else if (bits.Read() == 1) mode = bits.Read() == 1 ? 3 : -3; // VR3 0000011, VL3 0000010
            else return changes.Count > 0; // an extension or an end of line: no more of this line
            switch (mode)
            {
                case 20:
                    a0 = b2;
                    break;
                case 10:
                {
                    var r1 = Run(bits, white);
                    var r2 = Run(bits, !white);
                    if (r1 < 0 || r2 < 0) return false;
                    var start = Math.Max(a0, 0);
                    var a1 = Math.Min(start + r1, columns);
                    var a2 = Math.Min(a1 + r2, columns);
                    changes.Add(a1);
                    changes.Add(a2);
                    a0 = a2;
                    break;
                }
                default:
                {
                    var a1 = Math.Clamp(b1 + mode, 0, columns);
                    changes.Add(a1);
                    a0 = a1;
                    white = !white;
                    break;
                }
            }
            if (bits.Overrun) return false;
        }
        while (changes.Count > 0 && changes[^1] >= columns) changes.RemoveAt(changes.Count - 1); // at the end: no change
        return true;
    }
}
