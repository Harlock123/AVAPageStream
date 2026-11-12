using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AVAPageStream.Core;

/// <summary>
/// Makes a finished CMYK PDF a PDF/X-4 (ISO 15930-7) one, by an update: an output intent with AVAPageStream's own
/// CMYK profile (as a condition of its own, since it is no registered printing condition), XMP metadata and an
/// information dictionary that agree with it, an /ID, PDF 1.6, a trim box on every page, transparency blended in
/// CMYK, and no annotations (links) on the page, which PDF/X doesn't allow there.
/// </summary>
public static class PdfX
{
    public const string Version = "PDF/X-4";

    /// <param name="title">The document's title (PDF/X wants one).</param>
    public static byte[]? Make(byte[] pdf, string title, string author, DateTimeOffset when)
    {
        if (PdfFile.Read(pdf) is not { } file || !file.Objects.TryGetValue(file.Root, out var catalog)) return null;
        var written = new Dictionary<int, (string Body, byte[]? Stream)>();
        var next = file.Size;
        string Body(int n) => written.TryGetValue(n, out var w) ? w.Body : file.Objects[n].Body;
        byte[]? Stream(int n) => written.TryGetValue(n, out var w) ? w.Stream : file.Objects[n].Stream;

        // Pages: a trim box (its media box, if it hasn't one), no annotations.
        foreach (var page in file.Pages())
        {
            var b = Body(page);
            b = Regex.Replace(b, @"/Annots\s*(\[[^\]]*\]|\d+\s+0\s+R)", "");
            if (!Regex.IsMatch(b, @"/TrimBox") && Regex.Match(b, @"/MediaBox\s*(\[[^\]]*\])") is { Success: true } media)
                b = b.EndsWith(">>", StringComparison.Ordinal) ? b[..^2] + $"\n/TrimBox {media.Groups[1].Value}>>" : b;
            written[page] = (b, Stream(page));
        }
        // Transparency groups blended in CMYK, not the RGB the drawing was made in.
        foreach (var o in file.Objects.Values)
        {
            var b = Body(o.Number);
            var changed = Regex.Replace(b, @"(/S\s*/Transparency[^>]*?/CS\s*)/DeviceRGB|(/CS\s*)/DeviceRGB(?=[^>]*?/S\s*/Transparency)", m => m.Value.Replace("/DeviceRGB", "/DeviceCMYK"));
            if (changed != b) written[o.Number] = (changed, Stream(o.Number));
        }
        // The output intent and its profile.
        var profile = next++;
        written[profile] = ("<</N 4\n/Alternate /DeviceCMYK\n/Filter /FlateDecode>>", PdfFile.Deflate(IccCmyk.Profile));
        var intent = next++;
        written[intent] = ($"<</Type /OutputIntent\n/S /GTS_PDFX\n/OutputConditionIdentifier (Custom)\n/OutputCondition ({IccCmyk.Description})\n/Info ({IccCmyk.Description})\n/RegistryName ()\n/DestOutputProfile {profile} 0 R>>", null);
        // Metadata: XMP, and the information dictionary saying the same.
        var stamp = when.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        var pdfDate = "D:" + when.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + (when.Offset == TimeSpan.Zero ? "Z" : when.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "'") + "'");
        var id = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(title + "|" + stamp + "|" + pdf.Length)));
        var uuid = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(id))).ToString();
        var xmp = Xmp(title, author, stamp, uuid);
        var metadata = next++;
        written[metadata] = ("<</Type /Metadata\n/Subtype /XML>>", Encoding.UTF8.GetBytes(xmp));
        var infoBody = $"<</Title {PdfExtras.PdfString(title)}{(author.Length > 0 ? $"\n/Author {PdfExtras.PdfString(author)}" : "")}\n/Creator (AVAPageStream)\n/Producer (AVAPageStream)\n/CreationDate ({pdfDate})\n/ModDate ({pdfDate})\n/Trapped /False\n/GTS_PDFXVersion ({Version})>>";
        var info = file.Info ?? next++;
        written[info] = (infoBody, null);
        var cat = Body(file.Root);
        cat = Regex.Replace(cat, @"/Metadata\s+\d+\s+0\s+R|/OutputIntents\s*\[[^\]]*\]|/Version\s*/[\d.]+", "");
        cat = cat.EndsWith(">>", StringComparison.Ordinal) ? cat[..^2] + $"\n/Version /1.6\n/OutputIntents [{intent} 0 R]\n/Metadata {metadata} 0 R>>" : cat;
        written[file.Root] = (cat, Stream(file.Root));
        var updated = file.Update(written, (file.Info is null ? $"\n/Info {info} 0 R" : "") + $"\n/ID [<{id}> <{id}>]");
        // The header says 1.6 too: the same eight bytes, rewritten.
        if (updated.Length > 8 && Encoding.ASCII.GetString(updated, 0, 5) == "%PDF-") Encoding.ASCII.GetBytes("%PDF-1.6").CopyTo(updated, 0);
        return updated;
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Xmp(string title, string author, string stamp, string uuid) => $"""
        <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:dc="http://purl.org/dc/elements/1.1/"
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:pdf="http://ns.adobe.com/pdf/1.3/"
            xmlns:xmpMM="http://ns.adobe.com/xap/1.0/mm/"
            xmlns:pdfxid="http://www.npes.org/pdfx/ns/id/">
           <dc:format>application/pdf</dc:format>
           <dc:title><rdf:Alt><rdf:li xml:lang="x-default">{Esc(title)}</rdf:li></rdf:Alt></dc:title>
        {(author.Length > 0 ? $"   <dc:creator><rdf:Seq><rdf:li>{Esc(author)}</rdf:li></rdf:Seq></dc:creator>\n" : "")}   <xmp:CreateDate>{stamp}</xmp:CreateDate>
           <xmp:ModifyDate>{stamp}</xmp:ModifyDate>
           <xmp:MetadataDate>{stamp}</xmp:MetadataDate>
           <xmp:CreatorTool>AVAPageStream</xmp:CreatorTool>
           <pdf:Producer>AVAPageStream</pdf:Producer>
           <pdf:Trapped>False</pdf:Trapped>
           <xmpMM:DocumentID>uuid:{uuid}</xmpMM:DocumentID>
           <xmpMM:InstanceID>uuid:{uuid}</xmpMM:InstanceID>
           <xmpMM:VersionID>1</xmpMM:VersionID>
           <xmpMM:RenditionClass>default</xmpMM:RenditionClass>
           <pdfxid:GTS_PDFXVersion>{Version}</pdfxid:GTS_PDFXVersion>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;
}
