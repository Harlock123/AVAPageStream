namespace AVAPageStream.Core.PostScript;

/// <summary>
/// Type 1 fonts: the encryption eexec and charstrings use, and the charstring language that draws a glyph: its
/// outline in glyph space (y up) and its advance.
/// </summary>
public static class Type1
{
    /// <summary>Decrypts with a Type 1 key (55665 for eexec, 4330 for charstrings), dropping the first <paramref name="skip"/> bytes.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> cipher, ushort key, int skip)
    {
        var r = key;
        var plain = new byte[Math.Max(0, cipher.Length - skip)];
        for (var i = 0; i < cipher.Length; i++)
        {
            var c = cipher[i];
            var p = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            if (i >= skip) plain[i - skip] = p;
        }
        return plain;
    }

    /// <summary>Encrypts (for tests, and nothing else).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ushort key)
    {
        var r = key;
        var cipher = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            var c = (byte)(plain[i] ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            cipher[i] = c;
        }
        return cipher;
    }

    /// <summary>A glyph drawn by its charstring: its outline and advance, in glyph space.</summary>
    public sealed record Glyph(PsPath Path, double AdvanceX, double AdvanceY);

    /// <summary>Runs a charstring. <paramref name="lookup"/> finds another glyph's charstring by name, for accented letters (seac).</summary>
    public static Glyph Run(byte[] charstring, IReadOnlyList<byte[]> subrs, Func<string, byte[]?> lookup, int lenIV = 4)
    {
        var state = new State(subrs, lookup, lenIV);
        state.Execute(charstring, 0);
        return new Glyph(state.Path, state.Wx, state.Wy);
    }

    private sealed class State(IReadOnlyList<byte[]> subrs, Func<string, byte[]?> lookup, int lenIV)
    {
        public readonly PsPath Path = new();
        public double Wx, Wy;
        private readonly List<double> _args = [];
        private readonly Stack<double> _ps = new();
        private double _x, _y, _sbx, _sby;
        private bool _open, _flex, _done;
        private readonly List<(double X, double Y)> _flexPoints = [];
        private int _calls;

        private byte[] Plain(byte[] s) => lenIV < 0 ? s : Decrypt(s, 4330, lenIV);

        public void Execute(byte[] encrypted, int depth)
        {
            if (depth > 16 || ++_calls > 10000) throw new PsError("invalidfont", "charstring");
            var b = Plain(encrypted);
            for (var i = 0; i < b.Length && !_done;)
            {
                int v = b[i++];
                if (v >= 32)
                {
                    if (v <= 246) _args.Add(v - 139);
                    else if (v <= 250) _args.Add((v - 247) * 256 + b[i++] + 108);
                    else if (v <= 254) _args.Add(-(v - 251) * 256 - b[i++] - 108);
                    else { _args.Add(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]); i += 4; }
                    continue;
                }
                if (v == 12) v = 100 + b[i++];
                if (Op(v, depth)) return; // return from a subroutine
            }
        }

        private double Arg(int k) => k < _args.Count ? _args[k] : 0;

        private void MoveTo(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            if (_flex) { _flexPoints.Add((_x, _y)); return; }
            if (_open) Path.Segs.Add(new PsSeg(SegKind.Close));
            Path.Segs.Add(new PsSeg(SegKind.Move, X3: _x, Y3: _y));
            _open = false;
        }

        private void LineTo(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            Path.Segs.Add(new PsSeg(SegKind.Line, X3: _x, Y3: _y));
            _open = true;
        }

        private void CurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            var (x1, y1) = (_x + dx1, _y + dy1);
            var (x2, y2) = (x1 + dx2, y1 + dy2);
            (_x, _y) = (x2 + dx3, y2 + dy3);
            Path.Segs.Add(new PsSeg(SegKind.Curve, x1, y1, x2, y2, _x, _y));
            _open = true;
        }

        /// <summary>Runs one operator; true for return.</summary>
        private bool Op(int op, int depth)
        {
            switch (op)
            {
                case 13: // hsbw
                    (_sbx, _sby, Wx, Wy) = (Arg(0), 0, Arg(1), 0);
                    (_x, _y) = (_sbx, 0);
                    break;
                case 107: // sbw
                    (_sbx, _sby, Wx, Wy) = (Arg(0), Arg(1), Arg(2), Arg(3));
                    (_x, _y) = (_sbx, _sby);
                    break;
                case 21: MoveTo(Arg(0), Arg(1)); break;
                case 22: MoveTo(Arg(0), 0); break;
                case 4: MoveTo(0, Arg(0)); break;
                case 5: LineTo(Arg(0), Arg(1)); break;
                case 6: LineTo(Arg(0), 0); break;
                case 7: LineTo(0, Arg(0)); break;
                case 8: CurveTo(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5)); break;
                case 30: CurveTo(0, Arg(0), Arg(1), Arg(2), Arg(3), 0); break; // vhcurveto
                case 31: CurveTo(Arg(0), 0, Arg(1), Arg(2), 0, Arg(3)); break; // hvcurveto
                case 9: // closepath
                    if (_open) Path.Segs.Add(new PsSeg(SegKind.Close));
                    _open = false;
                    break;
                case 10: // callsubr
                {
                    var n = (int)_args[^1];
                    _args.RemoveAt(_args.Count - 1);
                    if (n >= 0 && n < subrs.Count) Execute(subrs[n], depth + 1);
                    return false;
                }
                case 11: return true; // return
                case 14: // endchar
                    if (_open) Path.Segs.Add(new PsSeg(SegKind.Close));
                    _open = false;
                    _done = true;
                    break;
                case 106: Seac(depth); break;
                case 112: // div
                {
                    var d = _args[^1];
                    var n = _args[^2];
                    _args.RemoveRange(_args.Count - 2, 2);
                    _args.Add(d == 0 ? 0 : n / d);
                    return false;
                }
                case 116: OtherSubr(); return false;
                case 117: // pop: a value an other-subroutine left
                    _args.Add(_ps.Count > 0 ? _ps.Pop() : 0);
                    return false;
                case 133: // setcurrentpoint
                    (_x, _y) = (Arg(0), Arg(1));
                    break;
                // Hints: nothing to draw.
                case 1: case 3: case 100: case 101: case 102: break;
            }
            _args.Clear();
            return false;
        }

        /// <summary>The other subroutines every Type 1 font uses: flex (0 to 2) and hint replacement (3).</summary>
        private void OtherSubr()
        {
            var number = (int)_args[^1];
            var count = (int)_args[^2];
            _args.RemoveRange(_args.Count - 2, 2);
            var given = _args.GetRange(Math.Max(0, _args.Count - count), Math.Min(count, _args.Count));
            _args.RemoveRange(_args.Count - given.Count, given.Count);
            switch (number)
            {
                case 1: // start flex: the point it starts from is the first to collect
                    _flex = true;
                    _flexPoints.Clear();
                    break;
                case 2: break; // a flex point was added by the rmoveto before
                case 0: // end flex: two curves through the seven points, then where it ends
                    _flex = false;
                    if (_flexPoints.Count >= 7)
                    {
                        var p = _flexPoints;
                        Path.Segs.Add(new PsSeg(SegKind.Curve, p[1].X, p[1].Y, p[2].X, p[2].Y, p[3].X, p[3].Y));
                        Path.Segs.Add(new PsSeg(SegKind.Curve, p[4].X, p[4].Y, p[5].X, p[5].Y, p[6].X, p[6].Y));
                        (_x, _y) = p[6];
                        _open = true;
                    }
                    _ps.Push(_y);
                    _ps.Push(_x);
                    break;
                case 3: // hint replacement: gives back the subroutine to call
                    _ps.Push(3);
                    break;
                default:
                    for (var k = given.Count - 1; k >= 0; k--) _ps.Push(given[k]);
                    break;
            }
        }

        /// <summary>An accented letter: a letter of StandardEncoding with an accent over it, placed by the accent's offset.</summary>
        private void Seac(int depth)
        {
            var (asb, adx, ady, bchar, achar) = (Arg(0), Arg(1), Arg(2), (int)Arg(3), (int)Arg(4));
            var (wx, wy) = (Wx, Wy);
            var baseCs = bchar is >= 0 and < 256 ? lookup(Encodings.Standard[bchar]) : null;
            var accentCs = achar is >= 0 and < 256 ? lookup(Encodings.Standard[achar]) : null;
            if (baseCs is not null)
            {
                var b = new State(subrs, lookup, lenIV);
                b.Execute(baseCs, depth + 1);
                Path.Segs.AddRange(b.Path.Segs);
            }
            if (accentCs is not null)
            {
                var a = new State(subrs, lookup, lenIV);
                a.Execute(accentCs, depth + 1);
                Path.Segs.AddRange(a.Path.Transformed(new PsMatrix(1, 0, 0, 1, adx - asb + _sbx, ady)).Segs);
            }
            (Wx, Wy) = (wx, wy);
            _done = true;
        }
    }
}
