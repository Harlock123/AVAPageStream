using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AVAPageStream.Core;

/// <summary>
/// The .avps file: a ZIP holding document.json and images/&lt;sha256&gt;.&lt;ext&gt; for each picture the document uses.
/// </summary>
public static class DocumentFile
{
    public const string Extension = ".avps";

    public static void Save(Document doc, string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(folder, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var stream = File.Create(temp))
                Write(doc, stream);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void Write(Document doc, Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var entry = zip.CreateEntry("document.json", CompressionLevel.Optimal);
        using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            w.Write(doc.ToJson());
        var used = doc.AllObjects.OfType<PictureObject>().Select(p => p.Image).Where(k => k.Length > 0).Distinct();
        foreach (var key in used)
        {
            if (doc.Images.Get(key) is not { } bytes) continue;
            var img = zip.CreateEntry($"images/{key}.{ImageStore.Extension(bytes)}", CompressionLevel.NoCompression);
            using var s = img.Open();
            s.Write(bytes);
        }
    }

    public static Document Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Reads a document, refusing files that aren't one or that are from a newer version of the program.</summary>
    public static Document Read(Stream stream)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("This isn't an AVAPageStream document.");
        }
        using (zip)
        {
            var entry = zip.GetEntry("document.json") ?? throw new InvalidDataException("This isn't an AVAPageStream document: it has no document.json.");
            string json;
            using (var r = new StreamReader(entry.Open(), Encoding.UTF8))
                json = r.ReadToEnd();
            int version;
            try
            {
                using var probe = JsonDocument.Parse(json);
                version = probe.RootElement.TryGetProperty("Version", out var v) && v.TryGetInt32(out var n) ? n : 0;
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("The document is damaged: " + e.Message);
            }
            if (version > Document.FormatVersion)
                throw new InvalidDataException($"This document was made by a newer AVAPageStream (format {version}); this one reads format {Document.FormatVersion}. Update the program to open it.");

            var images = new ImageStore();
            foreach (var img in zip.Entries.Where(e => e.FullName.StartsWith("images/", StringComparison.Ordinal) && e.Length > 0))
            {
                using var s = img.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                images.Put(Path.GetFileNameWithoutExtension(img.Name), ms.ToArray());
            }

            Document doc;
            try
            {
                doc = Document.FromJson(json, images);
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("The document is damaged: " + e.Message);
            }
            Repair(doc);
            return doc;
        }
    }

    /// <summary>Puts right what a hand-edited or damaged file may have wrong, so the program never meets it.</summary>
    private static void Repair(Document doc)
    {
        if (doc.Pages.Count == 0) doc.Pages.Add(new Page());
        if (doc.Masters.Count == 0) doc.Masters.Add(new MasterPage());
        if (doc.Setup.Facing) doc.EnsureSides();
        if (doc.Styles.Count == 0) doc.Styles = Document.DefaultStyles();
        if (doc.Swatches.Count == 0) doc.Swatches = Colours.DefaultSwatches();
        var highest = 0;
        foreach (var id in doc.AllObjects.Select(o => o.Id).Concat(doc.Stories.Keys))
            if (int.TryParse(new string(id.SkipWhile(c => !char.IsDigit(c)).ToArray()), out var n)) highest = Math.Max(highest, n);
        doc.NextId = Math.Max(doc.NextId, highest + 1);
        foreach (var (key, story) in doc.Stories)
        {
            story.Id = key;
            if (!story.IsWellFormed())
            {
                var style = doc.Style(story.Paras.FirstOrDefault()?.Style ?? "Body");
                var text = story.Text;
                story.Runs = [new Run(1, style.Char)];
                story.Paras = [style.Para with { Style = style.Name }];
                story.Text = "";
                story.Insert(0, text);
            }
        }
        foreach (var frame in doc.AllObjects.OfType<TextFrame>())
            if (!doc.Stories.ContainsKey(frame.StoryId)) Chains.NewStory(doc, frame, doc.Style("Body"));
    }
}
