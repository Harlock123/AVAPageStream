using System.Text;

namespace AVAPageStream.Core.PostScript;

/// <summary>A reference to a PDF's indirect object: its number and generation.</summary>
public readonly record struct PdfRef(int Number, int Generation);

/// <summary>A word of a PDF that isn't data: an operator in a content stream, or obj, R, stream, [, &lt;&lt; and the like.</summary>
public sealed record PdfKeyword(string Value);

/// <summary>A stream's bytes as they are in the file, before its filters.</summary>
public sealed class PdfStreamBytes(byte[] file, int offset, int length)
{
    public ReadOnlySpan<byte> Span => file.AsSpan(offset, length);
    public byte[] ToArray() => file[offset..(offset + length)];
}

/// <summary>Why a PDF can't be read, or what in it isn't drawn: said to the person placing it.</summary>
public sealed class PdfException(string message) : Exception(message);

/// <summary>
/// Reads the words of a PDF: numbers, names, strings, and keywords (operators, delimiters and the file's own words).
/// </summary>
public sealed class PdfLexer(byte[] b, int pos = 0, int end = -1)
{
    public byte[] Bytes => b;
    public int Pos { get; set; } = pos;
    public int End { get; } = end < 0 ? b.Length : end;

    public static bool White(int c) => c is 0 or 9 or 10 or 12 or 13 or 32;
    public static bool Delimiter(int c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    public void SkipWhite()
    {
        while (Pos < End)
        {
            var c = b[Pos];
            if (White(c)) Pos++;
            else if (c == '%') { while (Pos < End && b[Pos] is not ((byte)'\n' or (byte)'\r')) Pos++; }
            else break;
        }
    }

    /// <summary>The next word, or null at the end.</summary>
    public object? Next()
    {
        SkipWhite();
        if (Pos >= End) return null;
        var c = b[Pos];
        switch (c)
        {
            case (byte)'/':
            {
                Pos++;
                var sb = new StringBuilder();
                while (Pos < End && !White(b[Pos]) && !Delimiter(b[Pos]))
                {
                    if (b[Pos] == '#' && Pos + 2 < End && Hex(b[Pos + 1]) >= 0 && Hex(b[Pos + 2]) >= 0)
                    {
                        sb.Append((char)(Hex(b[Pos + 1]) * 16 + Hex(b[Pos + 2])));
                        Pos += 3;
                    }
                    else sb.Append((char)b[Pos++]);
                }
                return new PsName(sb.ToString());
            }
            case (byte)'(':
                return LiteralString();
            case (byte)'<':
                if (Pos + 1 < End && b[Pos + 1] == '<') { Pos += 2; return new PdfKeyword("<<"); }
                return HexString();
            case (byte)'>':
                if (Pos + 1 < End && b[Pos + 1] == '>') { Pos += 2; return new PdfKeyword(">>"); }
                Pos++;
                return new PdfKeyword(">");
            case (byte)'[' or (byte)']' or (byte)'{' or (byte)'}':
                Pos++;
                return new PdfKeyword(((char)c).ToString());
            case (byte)')':
                Pos++;
                return Next();
        }
        var start = Pos;
        while (Pos < End && !White(b[Pos]) && !Delimiter(b[Pos])) Pos++;
        var word = Encoding.Latin1.GetString(b, start, Pos - start);
        if (Number(word) is { } n) return n;
        return word switch
        {
            "true" => true,
            "false" => false,
            "null" => PsNull.Instance,
            _ => new PdfKeyword(word),
        };
    }

    private static object? Number(string w)
    {
        if (w.Length == 0) return null;
        var c = w[0];
        if (!(char.IsAsciiDigit(c) || c is '+' or '-' or '.')) return null;
        if (int.TryParse(w, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var i)) return i;
        // Reals as PDF writes them, and as broken writers do ("--5", "1.2.3", "5-"): what can be read of it.
        var clean = new StringBuilder();
        var dot = false;
        for (var k = 0; k < w.Length; k++)
        {
            var ch = w[k];
            if (ch is '-' or '+') { if (clean.Length == 0) clean.Append(ch == '-' ? '-' : ""); else if (clean is not ['-']) break; }
            else if (ch == '.') { if (dot) break; dot = true; clean.Append(ch); }
            else if (char.IsAsciiDigit(ch)) clean.Append(ch);
            else return null;
        }
        return double.TryParse(clean.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static int Hex(int c) => c switch { >= '0' and <= '9' => c - '0', >= 'a' and <= 'f' => c - 'a' + 10, >= 'A' and <= 'F' => c - 'A' + 10, _ => -1 };

    private PsString LiteralString()
    {
        Pos++;
        var o = new List<byte>();
        var depth = 1;
        while (Pos < End)
        {
            var c = b[Pos++];
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;
            else if (c == '\\' && Pos < End)
            {
                var e = b[Pos++];
                switch (e)
                {
                    case (byte)'n': o.Add(10); continue;
                    case (byte)'r': o.Add(13); continue;
                    case (byte)'t': o.Add(9); continue;
                    case (byte)'b': o.Add(8); continue;
                    case (byte)'f': o.Add(12); continue;
                    case (byte)'\r': if (Pos < End && b[Pos] == '\n') Pos++; continue;
                    case (byte)'\n': continue;
                    case >= (byte)'0' and <= (byte)'7':
                    {
                        var v = e - '0';
                        for (var k = 0; k < 2 && Pos < End && b[Pos] is >= (byte)'0' and <= (byte)'7'; k++) v = v * 8 + (b[Pos++] - '0');
                        o.Add((byte)v);
                        continue;
                    }
                    default: o.Add(e); continue;
                }
            }
            o.Add(c);
        }
        return PsString.Of([.. o]);
    }

    private PsString HexString()
    {
        Pos++;
        var o = new List<byte>();
        var hi = -1;
        while (Pos < End && b[Pos] != '>')
        {
            var h = Hex(b[Pos++]);
            if (h < 0) continue;
            if (hi < 0) hi = h;
            else { o.Add((byte)(hi * 16 + h)); hi = -1; }
        }
        Pos++;
        if (hi >= 0) o.Add((byte)(hi * 16));
        return PsString.Of([.. o]);
    }

    /// <summary>
    /// The next object: a number, name, string, array, dictionary, reference (number generation R), or keyword.
    /// </summary>
    public object? ReadObject()
    {
        var t = Next();
        switch (t)
        {
            case PdfKeyword { Value: "[" }:
            {
                var items = new List<object>();
                while (true)
                {
                    var keep = Pos;
                    var o = ReadObject();
                    if (o is null || o is PdfKeyword { Value: "]" }) break;
                    if (o is PdfKeyword { Value: ">>" or "endobj" or "stream" }) { Pos = keep; break; }
                    items.Add(o);
                }
                return new PsArray(items);
            }
            case PdfKeyword { Value: "<<" }:
            {
                var d = new PsDict();
                while (true)
                {
                    var keep = Pos;
                    var k = ReadObject();
                    if (k is null || k is PdfKeyword { Value: ">>" }) break;
                    if (k is PdfKeyword { Value: "endobj" or "stream" or "]" }) { Pos = keep; break; }
                    if (k is not PsName key) continue; // junk where a key should be: passed over
                    keep = Pos;
                    var v = ReadObject();
                    if (v is PdfKeyword { Value: ">>" }) { d.Put(key, PsNull.Instance); break; }
                    if (v is null) break;
                    if (v is PdfKeyword) { Pos = keep; d.Put(key, PsNull.Instance); continue; }
                    d.Put(key, v);
                }
                return d;
            }
            case int n when n >= 0:
            {
                // number generation R: a reference.
                var keep = Pos;
                if (Next() is int g && g >= 0 && Next() is PdfKeyword { Value: "R" }) return new PdfRef(n, g);
                Pos = keep;
                return n;
            }
            default:
                return t;
        }
    }

    /// <summary>Finds a word from <paramref name="from"/> on, giving where it starts, or -1.</summary>
    public static int Find(byte[] b, string word, int from, int to = -1)
    {
        var w = Encoding.Latin1.GetBytes(word);
        var limit = (to < 0 ? b.Length : to) - w.Length;
        for (var i = Math.Max(0, from); i <= limit; i++)
        {
            if (b[i] != w[0]) continue;
            var k = 1;
            while (k < w.Length && b[i + k] == w[k]) k++;
            if (k == w.Length) return i;
        }
        return -1;
    }
}

/// <summary>
/// A PDF file opened for reading its pages: its cross-reference (tables, streams, and object streams, newest first),
/// or, if that is damaged, every object found by looking through the file; its objects, and its streams decoded.
/// </summary>
public sealed class PdfReader
{
    /// <summary>Where a stream's bytes are kept in its dictionary.</summary>
    public const string StreamKey = "AVAPageStream.Stream";

    private readonly byte[] _b;
    private readonly Dictionary<int, (int Offset, int Stream, int Index)> _xref = [];
    private readonly Dictionary<int, object> _objects = [];
    private readonly Dictionary<int, object> _deep = [];
    private readonly HashSet<int> _fetching = [];
    private Dictionary<int, int>? _scanned;

    public PsDict Trailer { get; private set; } = new();

    public List<PsDict> Pages { get; } = [];

    private PdfSecurity? _security;
    private int _encryptNumber = -1;

    /// <summary>Whether the file is protected (and was opened with its password, or the empty one).</summary>
    public bool Protected => _security is not null;

    /// <param name="password">For a protected file: its password (null tries the empty one, as most open with).</param>
    public PdfReader(byte[] pdf, string? password = null)
    {
        _b = pdf;
        if (PdfLexer.Find(pdf, "%PDF-", 0, Math.Min(pdf.Length, 1024)) < 0) throw new PdfException("it isn't a PDF");
        try { ReadXref(); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidCastException or PdfException or InvalidDataException) { _xref.Clear(); }
        if (Trailer.Get("Root") is null || Catalog() is null) Repair();
        if (Trailer.Get("Encrypt") is { } enc && enc is not PsNull)
        {
            if (enc is PdfRef er) _encryptNumber = er.Number;
            if (Fetch(enc) is not PsDict encrypt) throw new PdfException("its protection is damaged");
            var id = Trailer.Get("ID") is PsArray { Length: > 0 } ids && ids[0] is PsString first ? first.Span.ToArray() : [];
            _security = PdfSecurity.Open(encrypt, id, password);
            // What was read before the key was known is read again, decrypted.
            _objects.Clear();
            _deep.Clear();
        }
        if (Catalog() is not { } catalog) throw new PdfException("it has no catalogue of its pages");
        CollectPages(Fetch(catalog.Get("Pages")) as PsDict, null, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        if (Pages.Count == 0) throw new PdfException("it has no pages");
    }

    private PsDict? Catalog() => Fetch(Trailer.Get("Root")) as PsDict;

    // ---- The cross-reference

    private void ReadXref()
    {
        var at = PdfLexer.Find(_b, "startxref", Math.Max(0, _b.Length - 2048));
        // The last one in the file.
        for (var next = at; next >= 0; next = PdfLexer.Find(_b, "startxref", next + 9)) at = next;
        if (at < 0) throw new PdfException("no startxref");
        var lex = new PdfLexer(_b, at + 9);
        if (lex.Next() is not int offset) throw new PdfException("no xref offset");
        var seen = new HashSet<int>();
        PsDict? newest = null;
        while (offset > 0 && offset < _b.Length && seen.Add(offset))
        {
            var trailer = ReadSection(offset) ?? throw new PdfException("bad xref");
            newest ??= trailer;
            // A hybrid file's cross-reference stream, for objects its table leaves out.
            if (trailer.Get("XRefStm") is int stm && seen.Add(stm)) ReadSection(stm);
            offset = trailer.Get("Prev") is int prev ? prev : 0;
        }
        Trailer = newest ?? new PsDict();
    }

    /// <summary>Reads a cross-reference section (a table and its trailer, or a stream), keeping entries not already known.</summary>
    private PsDict? ReadSection(int offset)
    {
        var lex = new PdfLexer(_b, offset);
        var first = lex.Next();
        if (first is PdfKeyword { Value: "xref" })
        {
            while (true)
            {
                var keep = lex.Pos;
                var t = lex.Next();
                if (t is PdfKeyword { Value: "trailer" }) return lex.ReadObject() as PsDict;
                if (t is not int start || lex.Next() is not int count) { lex.Pos = keep; return null; }
                for (var k = 0; k < count; k++)
                {
                    if (lex.Next() is not int off || lex.Next() is not int _ || lex.Next() is not PdfKeyword kind) return null;
                    var num = start + k;
                    if (kind.Value == "n" && !_xref.ContainsKey(num) && off > 0) _xref[num] = (off, -1, 0);
                    else if (kind.Value == "f") _xref.TryAdd(num, (0, -1, -1));
                }
            }
        }
        // A cross-reference stream: number generation obj << … /Type /XRef … >> stream.
        if (first is not int || lex.Next() is not int || lex.Next() is not PdfKeyword { Value: "obj" }) return null;
        if (ReadBody(lex) is not PsDict d || d.Get("Type") is not PsName { Value: "XRef" }) return null;
        var data = Decode(d);
        var w = (d.Get("W") as PsArray)?.All().Select(o => (int)Interpreter.ToNum(o)).ToArray() ?? [1, 2, 1];
        var size = d.Get("Size") is int s ? s : 0;
        var index = (d.Get("Index") as PsArray)?.All().Select(o => (int)Interpreter.ToNum(o)).ToArray() ?? [0, size];
        var row = w.Sum();
        var p = 0;
        long Field(int k, long otherwise)
        {
            if (w[k] == 0) return otherwise;
            long v = 0;
            for (var i = 0; i < w[k]; i++) v = v << 8 | (long)(p < data.Length ? data[p++] : 0);
            return v;
        }
        for (var r = 0; r + 1 < index.Length; r += 2)
            for (var k = 0; k < index[r + 1] && p + row <= data.Length; k++)
            {
                var num = index[r] + k;
                var type = Field(0, 1);
                var f2 = Field(1, 0);
                var f3 = Field(2, 0);
                if (_xref.ContainsKey(num)) continue;
                if (type == 1) _xref[num] = ((int)f2, -1, 0);
                else if (type == 2) _xref[num] = (0, (int)f2, (int)f3);
                else _xref[num] = (0, -1, -1);
            }
        return d;
    }

    /// <summary>A damaged file: every "n g obj" in it (the last of each number winning), and its trailer or catalogue.</summary>
    private void Repair()
    {
        _xref.Clear();
        _objects.Clear();
        _scanned = [];
        for (var i = 0; i + 3 < _b.Length; i++)
        {
            if (_b[i] != 'o' || _b[i + 1] != 'b' || _b[i + 2] != 'j') continue;
            // Back over "n g " to the object's number.
            var j = i - 1;
            while (j >= 0 && PdfLexer.White(_b[j])) j--;
            var g = j;
            while (j >= 0 && char.IsAsciiDigit((char)_b[j])) j--;
            if (j == g) continue;
            while (j >= 0 && PdfLexer.White(_b[j])) j--;
            var n = j;
            while (j >= 0 && char.IsAsciiDigit((char)_b[j])) j--;
            if (j == n || n - j > 9) continue;
            var number = int.Parse(Encoding.Latin1.GetString(_b, j + 1, n - j));
            _scanned[number] = j + 1;
        }
        foreach (var (num, off) in _scanned) _xref[num] = (off, -1, 0);
        // The trailer: the last one in the file with a Root, or else the catalogue found among the objects.
        for (var at = PdfLexer.Find(_b, "trailer", 0); at >= 0; at = PdfLexer.Find(_b, "trailer", at + 7))
            if (new PdfLexer(_b, at + 7).ReadObject() is PsDict t && t.Get("Root") is not null) Trailer = t;
        // Objects in object streams, too.
        foreach (var num in _scanned.Keys.ToList())
            if (Fetch(new PdfRef(num, 0)) is PsDict { } d && d.Get("Type") is PsName { Value: "ObjStm" } && d.Get(StreamKey) is not null)
                ListObjectStream(num, d);
        if (Trailer.Get("Root") is null || Catalog() is null)
            foreach (var num in _xref.Keys.ToList())
                if (Fetch(new PdfRef(num, 0)) is PsDict { } d && d.Get("Type") is PsName { Value: "Catalog" })
                {
                    Trailer = new PsDict();
                    Trailer.Put("Root", new PdfRef(num, 0));
                    break;
                }
    }

    private void ListObjectStream(int number, PsDict d)
    {
        var data = Decode(d);
        var n = d.Get("N") is int c ? c : 0;
        var lex = new PdfLexer(data);
        for (var k = 0; k < n; k++)
        {
            if (lex.Next() is not int num || lex.Next() is not int) break;
            _xref.TryAdd(num, (0, number, k));
        }
    }

    // ---- Objects

    /// <summary>An object itself: a reference followed (one level), anything else as it is.</summary>
    public object? Fetch(object? o)
    {
        if (o is not PdfRef r) return o;
        if (_objects.TryGetValue(r.Number, out var known)) return known;
        if (!_fetching.Add(r.Number)) return null;
        try
        {
            var v = Load(r.Number) ?? PsNull.Instance;
            _objects[r.Number] = v;
            return v;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidCastException or InvalidDataException)
        {
            _objects[r.Number] = PsNull.Instance;
            return PsNull.Instance;
        }
        finally { _fetching.Remove(r.Number); }
    }

    private object? Load(int number)
    {
        if (!_xref.TryGetValue(number, out var e) || e.Index < 0) return null;
        if (e.Stream >= 0)
        {
            if (Fetch(new PdfRef(e.Stream, 0)) is not PsDict stm) return null;
            var data = Decode(stm);
            var n = stm.Get("N") is int c ? c : 0;
            var first = stm.Get("First") is int f ? f : 0;
            var head = new PdfLexer(data);
            for (var k = 0; k < n; k++)
            {
                if (head.Next() is not int num || head.Next() is not int off) break;
                if (k == e.Index || num == number) return new PdfLexer(data, first + off).ReadObject();
            }
            return null;
        }
        var lex = new PdfLexer(_b, e.Offset);
        if (lex.Next() is int got && lex.Next() is int && lex.Next() is PdfKeyword { Value: "obj" } && got == number) return Decrypted(ReadBody(lex), number, e.Offset);
        // The offset is wrong: look for the object where it really is.
        if (_scanned is null)
        {
            var keep = new Dictionary<int, (int, int, int)>(_xref);
            Repair();
            foreach (var (k, v) in keep) _xref.TryAdd(k, v);
        }
        if (_scanned!.TryGetValue(number, out var at) && at != e.Offset)
        {
            lex = new PdfLexer(_b, at);
            if (lex.Next() is int && lex.Next() is int && lex.Next() is PdfKeyword { Value: "obj" }) return Decrypted(ReadBody(lex), number, at);
        }
        return null;
    }

    /// <summary>
    /// An object read from the file, its strings and stream decrypted if the file is protected: all but the
    /// protection's own dictionary, cross-reference streams, and metadata left plain.
    /// </summary>
    private object? Decrypted(object? o, int number, int offset)
    {
        if (_security is not { } s || number == _encryptNumber || o is null) return o;
        var generation = new PdfLexer(_b, offset) is var l && l.Next() is int && l.Next() is int g ? g : 0;
        if (o is PsDict d && (d.Get("Type") as PsName)?.Value is "XRef") return o;
        if (o is PsDict md && (md.Get("Type") as PsName)?.Value is "Metadata" && s.PlainMetadata) return o;
        object Walk(object x) => x switch
        {
            PsString str => PsString.Of(s.Decrypt(str.Span.ToArray(), number, generation, stream: false)),
            PsArray a => DoArray(a),
            PsDict dict => DoDict(dict),
            _ => x,
        };
        PsArray DoArray(PsArray a)
        {
            for (var i = 0; i < a.Length; i++) a[i] = Walk(a[i]);
            return a;
        }
        PsDict DoDict(PsDict dict)
        {
            foreach (var (k, v) in dict.Entries)
            {
                if (v is PdfStreamBytes raw)
                {
                    // A stream with an Identity crypt filter of its own is left as it is.
                    var identity = dict.Get("Filter") is PsArray fs && fs.All().Any(f => f is PsName { Value: "Crypt" });
                    if (!identity) { var plain = s.Decrypt(raw.ToArray(), number, generation, stream: true); dict.Put(k, new PdfStreamBytes(plain, 0, plain.Length)); }
                }
                else dict.Put(k, Walk(v));
            }
            return dict;
        }
        return Walk(o);
    }

    /// <summary>An object's body after "obj": the object, and its stream's bytes if it has one.</summary>
    private object? ReadBody(PdfLexer lex)
    {
        var o = lex.ReadObject();
        if (o is not PsDict d) return o;
        var keep = lex.Pos;
        if (lex.Next() is not PdfKeyword { Value: "stream" }) { lex.Pos = keep; return d; }
        var start = lex.Pos;
        if (start < _b.Length && _b[start] == '\r') start++;
        if (start < _b.Length && _b[start] == '\n') start++;
        var length = _fetching.Count < 50 && Fetch(d.Get("Length")) is int len ? len : -1;
        // A length that doesn't end at "endstream" is wrong (or missing): the stream runs to the word.
        if (length < 0 || start + length > _b.Length || PdfLexer.Find(_b, "endstream", start + length, Math.Min(_b.Length, start + length + 40)) < 0)
        {
            var end = PdfLexer.Find(_b, "endstream", start);
            if (end < 0) end = _b.Length;
            length = end - start;
            while (length > 0 && _b[start + length - 1] is (byte)'\n' or (byte)'\r') length--;
        }
        d.Put(StreamKey, new PdfStreamBytes(_b, start, Math.Max(0, length)));
        return d;
    }

    /// <summary>
    /// An object with every reference in it followed (each object once, so shared ones stay shared), for handing to the
    /// interpreter: a parent's or a page's own reference is left as it is, so a page doesn't bring in the whole file.
    /// </summary>
    public object Deep(object? o)
    {
        switch (o)
        {
            case PdfRef r:
                if (_deep.TryGetValue(r.Number, out var known)) return known;
                var target = Fetch(r);
                if (target is PsDict td)
                {
                    var copy = new PsDict(td.Count + 1);
                    _deep[r.Number] = copy;
                    Fill(td, copy);
                    return copy;
                }
                if (target is PsArray ta)
                {
                    var list = new List<object>(ta.Length);
                    var copyA = new PsArray(list);
                    _deep[r.Number] = copyA;
                    foreach (var x in ta.All()) list.Add(Deep(x));
                    return new PsArray(list);
                }
                var v = target ?? PsNull.Instance;
                _deep[r.Number] = v;
                return v;
            case PsDict d:
            {
                var copy = new PsDict(d.Count + 1);
                Fill(d, copy);
                return copy;
            }
            case PsArray a:
                return new PsArray(a.All().Select(Deep).ToList(), a.Exec);
            case null:
                return PsNull.Instance;
            default:
                return o;
        }
    }

    private void Fill(PsDict from, PsDict to)
    {
        foreach (var (k, v) in from.Entries)
            to.Put(k, k is PsName { Value: "Parent" or "P" or "StructParent" or "StructParents" or "Annots" or "B" } ? v : Deep(v));
    }

    // ---- Streams

    /// <summary>A stream's data with its filters undone, up to any picture filter (DCT, JPX, JBIG2, CCITT) it ends with.</summary>
    public byte[] Decode(PsDict d) => Decode(d, out _);

    /// <param name="left">The picture filter still to undo, with its parameters, or null.</param>
    public byte[] Decode(PsDict d, out (string Filter, PsDict? Parms)? left)
    {
        left = null;
        if (d.Get(StreamKey) is not PdfStreamBytes raw) return [];
        var data = raw.ToArray();
        var filters = Fetch(d.Get("Filter")) switch
        {
            PsName n => [n.Value],
            PsArray a => a.All().Select(Fetch).OfType<PsName>().Select(n => n.Value).ToArray(),
            _ => Array.Empty<string>(),
        };
        var parms = Fetch(d.Get("DecodeParms") ?? d.Get("DP")) switch
        {
            PsDict p => [p],
            PsArray a => a.All().Select(x => Fetch(x) as PsDict).ToArray(),
            _ => Array.Empty<PsDict?>(),
        };
        for (var k = 0; k < filters.Length; k++)
        {
            var p = k < parms.Length ? parms[k] : null;
            switch (filters[k])
            {
                case "FlateDecode" or "Fl":
                    data = Inflate(data);
                    if (p?.Get("Predictor") is int fp && fp >= 2) data = Interpreter.Predict(data, p, fp);
                    break;
                case "LZWDecode" or "LZW":
                    data = Filters.Lzw(data);
                    if (p?.Get("Predictor") is int lp && lp >= 2) data = Interpreter.Predict(data, p, lp);
                    break;
                case "ASCIIHexDecode" or "AHx": data = Filters.AsciiHex(data); break;
                case "ASCII85Decode" or "A85": data = Filters.Ascii85(data); break;
                case "RunLengthDecode" or "RL": data = Filters.RunLength(data); break;
                case "Crypt": break;
                case "DCTDecode" or "DCT" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode" or "CCF":
                    left = (filters[k], p);
                    return data;
                default:
                    throw new PdfException($"it uses the {filters[k]} filter, which AVAPageStream doesn't read yet");
            }
        }
        return data;
    }

    /// <summary>Flate: zlib, or raw deflate from writers that leave out its header; what can be read of a damaged one.</summary>
    private static byte[] Inflate(byte[] data)
    {
        if (data.Length == 0) return data;
        try
        {
            var at = 0;
            var z = PostScript.Inflate.Zlib(() => at < data.Length ? data[at++] : -1);
            if (z.Length > 0) return z;
        }
        catch (InvalidDataException) { }
        try
        {
            using var raw = new System.IO.Compression.DeflateStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
            using var o = new MemoryStream();
            raw.CopyTo(o);
            return o.ToArray();
        }
        catch (InvalidDataException) { return Filters.Flate(data); }
    }

    // ---- Pages

    private static readonly string[] Inherited = ["Resources", "MediaBox", "CropBox", "Rotate"];

    private void CollectPages(PsDict? node, PsDict? inherit, HashSet<object> seen, int depth)
    {
        if (node is null || depth > 64 || !seen.Add(node)) return;
        var own = new PsDict(4);
        if (inherit is not null) foreach (var (k, v) in inherit.Entries) own.Put(k, v);
        foreach (var key in Inherited)
            if (node.Get(key) is { } v and not PsNull) own.Put(key, v);
        var kids = Fetch(node.Get("Kids")) as PsArray;
        var type = (node.Get("Type") as PsName)?.Value;
        if (type == "Page" || (kids is null && type != "Pages"))
        {
            var page = new PsDict(node.Count + 4);
            foreach (var (k, v) in node.Entries) page.Put(k, v);
            foreach (var (k, v) in own.Entries) if (page.Get(k) is null or PsNull) page.Put(k, v);
            Pages.Add(page);
            return;
        }
        if (kids is null) return;
        foreach (var kid in kids.All()) CollectPages(Fetch(kid) as PsDict, own, seen, depth + 1);
    }

    /// <summary>A page's box (CropBox, else MediaBox), in its own points, its corners in order.</summary>
    public Box PageBox(PsDict page)
    {
        Box? Read(string key)
        {
            if (Deep(page.Get(key)) is not PsArray { Length: 4 } a) return null;
            var v = a.All().Select(o => o is int or double ? Interpreter.ToNum(o) : 0).ToArray();
            var box = Box.FromCorners(new Pt(Math.Min(v[0], v[2]), Math.Min(v[1], v[3])), new Pt(Math.Max(v[0], v[2]), Math.Max(v[1], v[3])));
            return box.W > 0 && box.H > 0 ? box : null;
        }
        var media = Read("MediaBox") ?? new Box(0, 0, 612, 792);
        if (Read("CropBox") is not { } crop) return media;
        var shown = Box.FromCorners(new Pt(Math.Max(crop.X, media.X), Math.Max(crop.Y, media.Y)), new Pt(Math.Min(crop.Right, media.Right), Math.Min(crop.Bottom, media.Bottom)));
        return shown is { W: > 0, H: > 0 } ? shown : media;
    }

    /// <summary>A page's content: its streams' data joined, each ending in a space so their words don't run together.</summary>
    public byte[] Contents(PsDict page)
    {
        var o = new List<byte>();
        void Add(object? s)
        {
            if (Fetch(s) is PsDict d) { o.AddRange(Decode(d)); o.Add((byte)'\n'); }
        }
        switch (Fetch(page.Get("Contents")))
        {
            case PsArray a: foreach (var s in a.All()) Add(s); break;
            case var one: Add(one); break;
        }
        return [.. o];
    }
}
