using System.IO.Compression;
using System.Text;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>
/// Makes small PDFs for tests: objects by number (1 is the catalogue), streams given their data, and a correct
/// cross-reference, as a table or as a stream with the objects packed in an object stream.
/// </summary>
internal sealed class PdfMaker
{
    private readonly SortedDictionary<int, (string Body, byte[]? Stream)> _objects = [];

    public PdfMaker Add(int n, string body, byte[]? stream = null) { _objects[n] = (body, stream); return this; }

    public PdfMaker Add(int n, string body, string stream) => Add(n, body, Encoding.Latin1.GetBytes(stream));

    /// <summary>A one-page PDF: the page's content, its resources (as dictionary text), and its media box.</summary>
    public static PdfMaker Page(string content, string resources = "<< >>", string box = "[0 0 200 100]", string extra = "")
    {
        var m = new PdfMaker();
        m.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        m.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        m.Add(3, $"<< /Type /Page /Parent 2 0 R /MediaBox {box} /Resources {resources} /Contents 4 0 R {extra} >>");
        m.Add(4, "<< >>", content);
        return m;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return o.ToArray();
    }

    public byte[] Build(bool xrefStream = false)
    {
        var o = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); o.Write(b); }
        W("%PDF-1.7\n%\xe2\xe3\xcf\xd3\n");
        var offsets = new Dictionary<int, long>();
        var packed = new List<int>();
        void Write(int n, string body, byte[]? stream)
        {
            offsets[n] = o.Position;
            if (stream is null) { W($"{n} 0 obj\n{body}\nendobj\n"); return; }
            var dict = body.TrimEnd();
            dict = dict[..^2] + $" /Length {stream.Length} >>";
            W($"{n} 0 obj\n{dict}\nstream\n");
            o.Write(stream);
            W("\nendstream\nendobj\n");
        }
        var max = _objects.Keys.Max();
        if (!xrefStream)
        {
            foreach (var (n, (body, stream)) in _objects) Write(n, body, stream);
            var at = o.Position;
            W($"xref\n0 {max + 1}\n0000000000 65535 f \n");
            for (var n = 1; n <= max; n++) W(offsets.TryGetValue(n, out var off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
            W($"trailer\n<< /Size {max + 1} /Root 1 0 R >>\nstartxref\n{at}\n%%EOF\n");
            return o.ToArray();
        }
        // Objects that aren't streams go in an object stream; the cross-reference is a stream too.
        foreach (var (n, (body, stream)) in _objects) if (stream is not null) Write(n, body, stream);
        var head = new StringBuilder();
        var data = new StringBuilder();
        foreach (var (n, (body, stream)) in _objects)
        {
            if (stream is not null) continue;
            head.Append($"{n} {data.Length} ");
            packed.Add(n);
            data.Append(body).Append('\n');
        }
        var objStm = max + 1;
        var first = head.Length;
        var all = Encoding.Latin1.GetBytes(head.ToString() + data);
        Write(objStm, $"<< /Type /ObjStm /N {packed.Count} /First {first} /Filter /FlateDecode >>", Deflate(all));
        var xref = objStm + 1;
        offsets[xref] = o.Position;
        var rows = new MemoryStream();
        for (var n = 0; n <= xref; n++)
        {
            if (n == 0) rows.Write([0, 0, 0, 0, 0xFF]);
            else if (packed.Contains(n)) rows.Write([2, (byte)(objStm >> 16), (byte)(objStm >> 8), (byte)objStm, (byte)packed.IndexOf(n)]);
            else if (offsets.TryGetValue(n, out var off)) rows.Write([1, (byte)(off >> 16), (byte)(off >> 8), (byte)off, 0]);
            else rows.Write([0, 0, 0, 0, 0]);
        }
        var rowBytes = rows.ToArray();
        var xdict = $"<< /Type /XRef /Size {xref + 1} /W [1 3 1] /Root 1 0 R /Filter /FlateDecode >>";
        var at2 = o.Position;
        Write(xref, xdict, Deflate(rowBytes));
        W($"startxref\n{at2}\n%%EOF\n");
        return o.ToArray();
    }
}
