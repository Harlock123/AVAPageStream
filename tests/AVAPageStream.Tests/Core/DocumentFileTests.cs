using System.IO.Compression;
using System.Text;
using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class DocumentFileTests
{
    // A 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    private static string TempFile() => Path.Combine(Directory.CreateTempSubdirectory("avaps-").FullName, "test.avps");

    [Fact]
    public void A_document_saves_and_loads_with_its_text_pictures_and_pages()
    {
        var e = new Editor(Document.New(new Setup(595.28, 841.89, Unit: Unit.Millimetre)));
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { X = 36, Y = 36, W = 200, H = 300, Columns = 2 });
        e.ReplaceText(frame.StoryId, 0, 0, "Hello\nworld");
        e.FormatChars(frame.StoryId, 0, 5, f => f with { Bold = true });
        var key = e.Doc.Images.Add(Png);
        e.Add(SheetRef.PageAt(0), new PictureObject { Image = key, W = 50, H = 50, Fit = Fit.Fill });
        e.Add(SheetRef.MasterAt(0), new LineObject { Rising = true, W = 100, H = 0, Stroke = "#FF0000" });
        e.AddPage(1);
        e.Change("Guide", d => d.Pages[0].Guides.Add(new Guide(true, 100)));

        var path = TempFile();
        DocumentFile.Save(e.Doc, path);
        var back = DocumentFile.Load(path);

        Assert.Equal(e.Doc.ToJson(), back.ToJson());
        Assert.Equal(Png, back.Images.Get(key));
        Assert.Equal(Unit.Millimetre, back.Setup.Unit);
        Assert.True(back.Stories[frame.StoryId].IsWellFormed());
        Assert.IsType<LineObject>(back.Masters[0].Objects[0]);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!).ToList()); // no temporary file left behind
    }

    [Fact]
    public void Images_nothing_uses_are_left_out()
    {
        var doc = Document.New();
        doc.Images.Add(Png);
        using var ms = new MemoryStream();
        DocumentFile.Write(doc, ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms);
        Assert.Equal(["document.json"], zip.Entries.Select(x => x.FullName));
    }

    [Fact]
    public void Newer_and_foreign_files_are_refused_with_a_reason()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var w = new StreamWriter(zip.CreateEntry("document.json").Open()))
            w.Write("{\"Version\": 99}");
        ms.Position = 0;
        var e = Assert.Throws<InvalidDataException>(() => DocumentFile.Read(ms));
        Assert.Contains("newer", e.Message);

        var junk = new MemoryStream(Encoding.UTF8.GetBytes("not a zip"));
        Assert.Contains("isn't an AVAPageStream document", Assert.Throws<InvalidDataException>(() => DocumentFile.Read(junk)).Message);
    }

    [Fact]
    public void Damaged_stories_and_missing_stories_are_repaired_on_reading()
    {
        var doc = Document.New();
        doc.Pages[0].Objects.Add(new TextFrame { Id = "frame7", StoryId = "gone" });
        var story = Story.Create("story3", doc.Style("Body"));
        story.Text = "runs don't cover this";
        doc.Stories["story3"] = story;
        using var ms = new MemoryStream();
        DocumentFile.Write(doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms);
        Assert.True(back.Stories["story3"].IsWellFormed());
        Assert.Equal("runs don't cover this", back.Stories["story3"].Text);
        var frame = (TextFrame)back.Find("frame7")!;
        Assert.True(back.Stories.ContainsKey(frame.StoryId));
        Assert.True(back.NextId > 7);
    }
}
