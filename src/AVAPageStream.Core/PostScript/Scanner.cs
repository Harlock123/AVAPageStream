using System.Globalization;
using System.Text;

namespace AVAPageStream.Core.PostScript;

/// <summary>
/// Reads PostScript tokens from bytes: numbers, names, strings in all their forms, procedures, and the delimiters
/// [ ] &lt;&lt; &gt;&gt; as names. Also the file the program is read from, for currentfile.
/// </summary>
public sealed class Scanner(byte[] data, int start = 0, int end = -1)
{
    private readonly int _end = end < 0 ? data.Length : end;

    public byte[] Data { get; } = data;
    public int Position { get; set; } = start;
    public bool AtEnd => Position >= _end;

    private static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>The next byte, or -1 at the end (for reading the file directly).</summary>
    public int Read() => Position < _end ? Data[Position++] : -1;

    public int Peek() => Position < _end ? Data[Position] : -1;

    public int End => _end;

    /// <summary>Ends the file (closefile): nothing more is read from it.</summary>
    public void Close() => Position = _end;

    /// <summary>
    /// The next token, or null at the end. <paramref name="immediate"/> looks up a //name at once.
    /// </summary>
    public object? Next(Func<string, object>? immediate = null)
    {
        {
            SkipBlank();
            if (Position >= _end) return null;
            var c = Data[Position];
            Position++;
            switch (c)
            {
                case (byte)'(':
                    return LiteralString();
                case (byte)'<':
                    if (Peek() == '<') { Position++; return new PsName("<<", true); }
                    if (Peek() == '~') { Position++; return Ascii85String(); }
                    return HexString();
                case (byte)'>':
                    if (Peek() == '>') { Position++; return new PsName(">>", true); }
                    throw new PsError("syntaxerror", ">");
                case (byte)'[':
                    return new PsName("[", true);
                case (byte)']':
                    return new PsName("]", true);
                case (byte)'{':
                    return Procedure(immediate);
                case (byte)'}':
                    throw new PsError("syntaxerror", "}");
                case (byte)')':
                    throw new PsError("syntaxerror", ")");
                case (byte)'/':
                    if (Peek() == '/')
                    {
                        Position++;
                        var n = Regular();
                        return immediate is null ? new PsName(n, true) : immediate(n);
                    }
                    return new PsName(Regular());
                default:
                    if (c >= 128 && c <= 159) throw new PsError("syntaxerror", "binary token");
                    Position--;
                    var word = Regular();
                    // The white-space character that ends a token is part of it (so data read after it starts clean).
                    if (Position < _end && IsSpace(Data[Position]))
                    {
                        if (Data[Position++] == 13 && Position < _end && Data[Position] == 10) Position++;
                    }
                    return Number(word) ?? new PsName(word, true);
            }
        }
    }

    /// <summary>Past white space and comments.</summary>
    public void SkipBlank()
    {
        while (Position < _end)
        {
            if (IsSpace(Data[Position])) { Position++; continue; }
            if (Data[Position] != '%') return;
            while (Position < _end && Data[Position] is not (10 or 13)) Position++;
        }
    }

    private string Regular()
    {
        var start = Position;
        while (Position < _end && !IsSpace(Data[Position]) && !IsDelimiter(Data[Position])) Position++;
        return Encoding.Latin1.GetString(Data, start, Position - start);
    }

    /// <summary>A number in any of PostScript's forms (123, -4.5, 1e3, .5, 16#FF), or null for a name.</summary>
    public static object? Number(string w)
    {
        if (w.Length == 0) return null;
        var c = w[0];
        if (!(char.IsAsciiDigit(c) || c is '+' or '-' or '.')) return null;
        var hash = w.IndexOf('#');
        if (hash > 0)
        {
            if (!int.TryParse(w[..hash], NumberStyles.None, CultureInfo.InvariantCulture, out var radix) || radix is < 2 or > 36) return null;
            long v = 0;
            foreach (var ch in w[(hash + 1)..])
            {
                var d = char.IsAsciiDigit(ch) ? ch - '0' : char.IsAsciiLetter(ch) ? char.ToLowerInvariant(ch) - 'a' + 10 : 99;
                if (d >= radix) return null;
                v = v * radix + d;
                if (v > uint.MaxValue) return null;
            }
            return v > int.MaxValue ? (double)(uint)v : (object)(int)v;
        }
        if (int.TryParse(w, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)) return i;
        foreach (var ch in w)
            if (!(char.IsAsciiDigit(ch) || ch is '+' or '-' or '.' or 'e' or 'E')) return null;
        if (!w.Any(char.IsAsciiDigit)) return null;
        return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    private PsString LiteralString()
    {
        var bytes = new List<byte>();
        var depth = 1;
        while (Position < _end)
        {
            var c = Data[Position++];
            if (c == '\\')
            {
                if (Position >= _end) break;
                var e = Data[Position++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case 13:
                        if (Peek() == 10) Position++;
                        break;
                    case 10: break; // a line continued
                    default:
                        if (e is >= (byte)'0' and <= (byte)'7')
                        {
                            var v = e - '0';
                            for (var k = 0; k < 2 && Peek() is >= '0' and <= '7'; k++) v = v * 8 + (Data[Position++] - '0');
                            bytes.Add((byte)v);
                        }
                        else bytes.Add(e);
                        break;
                }
                continue;
            }
            if (c == '(') depth++;
            if (c == ')' && --depth == 0) return PsString.Of([.. bytes]);
            bytes.Add(c);
        }
        throw new PsError("syntaxerror", "string");
    }

    private PsString HexString()
    {
        var bytes = new List<byte>();
        var half = -1;
        while (Position < _end)
        {
            var c = Data[Position++];
            if (c == '>')
            {
                if (half >= 0) bytes.Add((byte)(half << 4));
                return PsString.Of([.. bytes]);
            }
            if (IsSpace(c)) continue;
            var d = Hex(c);
            if (d < 0) throw new PsError("syntaxerror", "hex string");
            if (half < 0) half = d;
            else { bytes.Add((byte)(half << 4 | d)); half = -1; }
        }
        throw new PsError("syntaxerror", "hex string");
    }

    public static int Hex(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private PsString Ascii85String()
    {
        var start = Position;
        while (Position < _end - 1 && !(Data[Position] == '~' && Data[Position + 1] == '>')) Position++;
        var body = Data.AsSpan(start, Position - start).ToArray();
        Position = Math.Min(_end, Position + 2);
        return PsString.Of(Filters.Ascii85(body));
    }

    private PsArray Procedure(Func<string, object>? immediate)
    {
        var items = new List<object>();
        while (true)
        {
            SkipBlank();
            if (Position >= _end) throw new PsError("syntaxerror", "procedure");
            if (Data[Position] == '}') { Position++; return new PsArray(items, exec: true); }
            if (Next(immediate) is { } t) items.Add(t);
        }
    }
}
