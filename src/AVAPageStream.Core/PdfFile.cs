using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace AVAPageStream.Core;

/// <summary>An object of a PDF: its dictionary (or other body) as text, and its stream's bytes if it has one.</summary>
public sealed record PdfObject(int Number, string Body, byte[]? Stream)
{
    public bool Has(string key) => Regex.IsMatch(Body, @"/" + Regex.Escape(key) + @"(?![A-Za-z0-9])");

    /// <summary>A stream's data, decompressed if it is Flate (and nothing else); null for any other filter.</summary>
    public byte[]? Decoded()
    {
        if (Stream is null) return null;
        var filter = Regex.Match(Body, @"/Filter\s*(\[\s*)?/(\w+)(\s*\])?");
        if (!filter.Success) return Stream;
        if (filter.Groups[2].Value != "FlateDecode" || Regex.IsMatch(Body, @"/Filter\s*\[\s*/\w+\s+/") || Body.Contains("/DecodeParms")) return null;
        try
        {
            using var z = new ZLibStream(new MemoryStream(Stream), CompressionMode.Decompress);
            using var o = new MemoryStream();
            z.CopyTo(o);
            return o.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }
}

/// <summary>
/// Reading the objects of a finished PDF (the plain kind SkiaSharp writes: a cross-reference table, no object
/// streams) and adding an update after it that replaces some of them: the file before it is left as it was.
/// </summary>
public sealed class PdfFile
{
    public byte[] Bytes { get; }
    public Dictionary<int, PdfObject> Objects { get; } = [];
    public int Root { get; }
    public int? Info { get; }
    public int Size { get; }
    public long StartXref { get; }

    private PdfFile(byte[] bytes, int root, int? info, int size, long startxref)
    {
        Bytes = bytes;
        (Root, Info, Size, StartXref) = (root, info, size, startxref);
    }

    public static PdfFile? Read(byte[] pdf)
    {
        var t = Encoding.Latin1.GetString(pdf);
        var startxref = Regex.Matches(t, @"startxref\s+(\d+)").LastOrDefault();
        var trailer = Regex.Matches(t, @"trailer\s*<<(.*?)>>\s*startxref", RegexOptions.Singleline).LastOrDefault();
        if (startxref is null || trailer is null) return null;
        var tr = trailer.Groups[1].Value;
        int? Ref(string key) => Regex.Match(tr, @"/" + key + @"\s+(\d+)\s+0\s+R") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var size = Regex.Match(tr, @"/Size\s+(\d+)") is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        if (Ref("Root") is not { } root || size == 0) return null;
        var file = new PdfFile(pdf, root, Ref("Info"), size, long.Parse(startxref.Groups[1].Value, CultureInfo.InvariantCulture));
        var lengths = new List<(int Number, int BodyEnd, string Body, string LengthRef)>();
        var at = 0;
        foreach (RegexMatch m in Regex.Matches(t, @"(?<=^|[\r\n])(\d+) 0 obj\b"))
        {
            if (m.Index < at) continue; // inside the stream of one before
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var p = m.Index + m.Length;
            while (p < t.Length && char.IsWhiteSpace(t[p])) p++;
            var bodyStart = p;
            var bodyEnd = At(t, p, "<<") ? DictEnd(t, p) : t.IndexOf("endobj", p, StringComparison.Ordinal);
            if (bodyEnd < 0) break;
            var body = t[bodyStart..bodyEnd].TrimEnd();
            var q = bodyEnd;
            while (q < t.Length && char.IsWhiteSpace(t[q])) q++;
            byte[]? stream = null;
            if (At(t, q, "stream"))
            {
                q += 6;
                if (q < t.Length && t[q] == '\r') q++;
                if (q < t.Length && t[q] == '\n') q++;
                var len = Regex.Match(body, @"/Length\s+(\d+)(\s+0\s+R)?");
                if (!len.Success) return null;
                if (len.Groups[2].Success)
                {
                    // Its length is another object: found by looking for endstream instead.
                    var end = t.IndexOf("endstream", q, StringComparison.Ordinal);
                    var e = end;
                    while (e > q && t[e - 1] is '\r' or '\n') e--;
                    stream = pdf[q..e];
                    at = end;
                }
                else
                {
                    var l = int.Parse(len.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (q + l > pdf.Length) return null;
                    stream = pdf[q..(q + l)];
                    at = q + l;
                }
            }
            else at = bodyEnd;
            file.Objects[n] = new PdfObject(n, body, stream);
        }
        return file;
    }

    private static bool At(string t, int p, string s) => p + s.Length <= t.Length && string.CompareOrdinal(t, p, s, 0, s.Length) == 0;

    /// <summary>Where a dictionary starting at <paramref name="start"/> ends (after its closing &gt;&gt;), strings and nesting allowed for.</summary>
    private static int DictEnd(string t, int start)
    {
        var depth = 0;
        for (var i = start; i < t.Length - 1; i++)
        {
            var c = t[i];
            if (c == '(')
            {
                var paren = 1;
                for (i++; i < t.Length && paren > 0; i++)
                {
                    if (t[i] == '\\') { i++; continue; }
                    if (t[i] == '(') paren++;
                    else if (t[i] == ')') paren--;
                }
                i--;
                continue;
            }
            if (c == '<' && t[i + 1] == '<') { depth++; i++; continue; }
            if (c == '>' && t[i + 1] == '>') { depth--; i++; if (depth == 0) return i + 1; }
        }
        return t.Length;
    }

    /// <summary>The page objects, in order, from the catalog's page tree.</summary>
    public List<int> Pages()
    {
        var pages = new List<int>();
        if (!Objects.TryGetValue(Root, out var catalog) || Regex.Match(catalog.Body, @"/Pages\s+(\d+)\s+0\s+R") is not { Success: true } pm) return pages;
        void Walk(int n, int depth)
        {
            if (depth > 32 || !Objects.TryGetValue(n, out var o)) return;
            if (Regex.IsMatch(o.Body, @"/Type\s*/Pages\b"))
                foreach (RegexMatch k in Regex.Matches(Regex.Match(o.Body, @"/Kids\s*\[(.*?)\]", RegexOptions.Singleline).Groups[1].Value, @"(\d+)\s+0\s+R"))
                    Walk(int.Parse(k.Groups[1].Value, CultureInfo.InvariantCulture), depth + 1);
            else if (Regex.IsMatch(o.Body, @"/Type\s*/Page\b")) pages.Add(n);
        }
        Walk(int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture), 0);
        return pages;
    }

    /// <summary>
    /// The PDF with an update after it: the objects given (new numbers from <see cref="Size"/> up, or replacing ones
    /// there), each a body and, for a stream, its bytes (its /Length is set).
    /// </summary>
    /// <param name="trailer">More for the trailer, such as an /ID.</param>
    public byte[] Update(IReadOnlyDictionary<int, (string Body, byte[]? Stream)> written, string trailer = "")
    {
        if (written.Count == 0) return Bytes;
        using var o = new MemoryStream();
        o.Write(Bytes);
        void W(string s) => o.Write(Encoding.Latin1.GetBytes(s));
        W("\n");
        var offsets = new SortedDictionary<int, long>();
        foreach (var (n, (body, stream)) in written.OrderBy(kv => kv.Key))
        {
            offsets[n] = o.Position;
            if (stream is null) W($"{n} 0 obj\n{body}\nendobj\n");
            else
            {
                var dict = Regex.Replace(body, @"/Length\s+\d+(\s+0\s+R)?", "");
                dict = dict.EndsWith(">>", StringComparison.Ordinal) ? dict[..^2] + $"\n/Length {stream.Length}>>" : dict;
                W($"{n} 0 obj\n{dict} stream\n");
                o.Write(stream);
                W("\nendstream\nendobj\n");
            }
        }
        var xref = o.Position;
        W("xref\n");
        foreach (var (n, at) in offsets) W(string.Create(CultureInfo.InvariantCulture, $"{n} 1\n{at:D10} 00000 n \n"));
        var size = Math.Max(Size, written.Keys.Max() + 1);
        W(string.Create(CultureInfo.InvariantCulture, $"trailer\n<</Size {size}\n/Root {Root} 0 R{(Info is { } i ? $"\n/Info {i} 0 R" : "")}{trailer}\n/Prev {StartXref}>>\nstartxref\n{xref}\n%%EOF\n"));
        return o.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        using var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return o.ToArray();
    }
}
