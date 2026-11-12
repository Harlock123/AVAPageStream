using System.IO.Compression;
using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 4: the baseline grid, bleed and crop marks, autosave, and linked pictures.</summary>
public class PrintAndFilesTests
{
    private static readonly FixedMeasurer M = new();

    private static double[] Baselines(Story s, BaselineGrid? grid) =>
        TextFlow.Layout(s, [new Area(new Box(0, 0, 100, 400), "f", 0)], M, null, grid).Lines.Select(l => Math.Round(l.Baseline, 2)).ToArray();

    [Fact]
    public void Text_on_the_baseline_grid_sits_on_its_lines()
    {
        var s = Story.Create("s", new ParagraphStyle("Body", new CharFormat(Size: 10), new ParaFormat()));
        s.Insert(0, "one\ntwo\nthree");
        var grid = new BaselineGrid(0, 15);
        Assert.Equal([8, 20, 32], Baselines(s, grid)); // not on the grid: as they set
        s.FormatParas(0, s.Length, p => p with { OnGrid = true });
        Assert.Equal([15, 30, 45], Baselines(s, grid));
        Assert.Equal([8, 20, 32], Baselines(s, null)); // no grid, nothing to sit on

        // From a first line other than the top: 10, 25, 40…
        Assert.Equal([10, 25, 40], Baselines(s, new BaselineGrid(10, 15)));
        Assert.Equal(new BaselineGrid(36, 14), new Setup(BaselineGrid: 14).Baselines);
        Assert.Null(new Setup().Baselines);
    }

    [Fact]
    public void Crop_marks_sit_outside_the_page_and_its_bleed()
    {
        var setup = new Setup(PageWidth: 400, PageHeight: 600, Bleed: 9);
        var slug = PrintMarks.Slug(setup);
        Assert.Equal((400 + 2 * slug, 600 + 2 * slug), PrintMarks.Sheet(setup));
        var lines = PrintMarks.Lines(setup);
        Assert.Equal(8, lines.Count);
        var bleed = new Box(slug - 9, slug - 9, 418, 618);
        foreach (var (from, to) in lines)
        {
            Assert.False(bleed.Contains(from) || bleed.Contains(to));
            Assert.True(from.X == to.X || from.Y == to.Y);                       // straight across or down
            Assert.Equal(PrintMarks.Length, Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y), 6);
            // In line with an edge of the trim.
            Assert.True(new[] { slug, slug + 400 }.Contains(from.X) || new[] { slug, slug + 600 }.Contains(from.Y));
        }
    }

    [Fact]
    public void The_setup_keeps_its_bleed_and_grid_through_a_file()
    {
        var doc = Document.New(new Setup(Bleed: 9, BaselineGrid: 13, BaselineFrom: 50));
        using var ms = new MemoryStream();
        DocumentFile.Write(doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms).Setup;
        Assert.Equal((9.0, 13.0, (double?)50), (back.Bleed, back.BaselineGrid, back.BaselineFrom));
    }

    [Fact]
    public void Recovery_copies_are_found_newest_first_and_discarded()
    {
        var folder = Directory.CreateTempSubdirectory("avaps-recovery-").FullName;
        var r = new Recovery(Path.Combine(folder, "Recovery"));
        Assert.Empty(r.Found());
        r.Save(Document.New(), "/somewhere/Old.avps", "a", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        r.Save(Document.New(), null, "b", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(["b", "a"], r.Found().Select(f => f.Id));
        Assert.Equal("a new document", r.Found()[0].Name);
        Assert.Equal("Old.avps", r.Found()[1].Name);
        Assert.Equal(["a"], r.Found(except: "b").Select(f => f.Id));
        Assert.Single(DocumentFile.Load(r.Found()[0].Copy).Pages);
        r.Adopt("a", "c");
        Assert.Equal(["b", "c"], r.Found().Select(f => f.Id));
        Assert.Equal("/somewhere/Old.avps", r.Found()[1].Original);
        r.Discard("b");
        r.Discard("c");
        Assert.Empty(r.Found());
    }

    private static readonly byte[] Other = Convert.FromBase64String( // a 1×1 black PNG
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABc3UBGAAAAABJRU5ErkJggg==");

    [Fact]
    public void A_linked_picture_is_read_from_its_file_and_follows_it()
    {
        var folder = Directory.CreateTempSubdirectory("avaps-links-").FullName;
        var file = Path.Combine(folder, "logo.png");
        File.WriteAllBytes(file, AVAPageStream.Tests.WindowTests.Png);
        var e = new Editor(Document.New());
        var pic = e.Add(SheetRef.PageAt(0), new PictureObject { W = 50, H = 50 });
        var key = e.Doc.Images.Add(AVAPageStream.Tests.WindowTests.Png);
        e.Change("Place", d => { var p = (PictureObject)d.Find(pic.Id)!; p.Image = key; });
        Assert.NotNull(e.SetLinked(pic.Id, true)); // placed from no file: nothing to link to
        e.Change("Place", d => ((PictureObject)d.Find(pic.Id)!).Source = file);
        Assert.Null(e.SetLinked(pic.Id, true));
        Assert.Equal(LinkState.UpToDate, Links.StateOf(e.Doc, (PictureObject)e.Doc.Find(pic.Id)!, null));

        // Saved without the picture in it.
        var path = Path.Combine(folder, "doc.avps");
        DocumentFile.Save(e.Doc, path);
        using (var zip = ZipFile.OpenRead(path))
            Assert.DoesNotContain(zip.Entries, x => x.FullName.StartsWith("images/", StringComparison.Ordinal));
        Assert.Equal(AVAPageStream.Tests.WindowTests.Png, DocumentFile.Load(path).Images.Get(key));

        // The file changes: the picture follows, as one undo step.
        File.WriteAllBytes(file, Other);
        Assert.Equal(LinkState.Changed, Links.StateOf(e.Doc, (PictureObject)e.Doc.Find(pic.Id)!, null));
        Assert.Equal(1, e.UpdateLinks(null));
        Assert.Equal(0, e.UpdateLinks(null));
        Assert.Equal(ImageStore.Key(Other), ((PictureObject)e.Doc.Find(pic.Id)!).Image);
        e.Undo();
        Assert.Equal(key, ((PictureObject)e.Doc.Find(pic.Id)!).Image);

        // Moved with the document to another folder, it is found beside it; gone, it is missing.
        var moved = Directory.CreateTempSubdirectory("avaps-moved-").FullName;
        File.Move(path, Path.Combine(moved, "doc.avps"));
        File.Move(file, Path.Combine(moved, "logo.png"));
        var opened = DocumentFile.Load(Path.Combine(moved, "doc.avps"));
        var p2 = (PictureObject)opened.Find(pic.Id)!;
        Assert.Equal(Path.Combine(moved, "logo.png"), p2.Source);
        Assert.Equal(Other, opened.Images.Get(p2.Image));
        File.Delete(Path.Combine(moved, "logo.png"));
        Assert.Single(Links.Missing(opened, moved));
        Assert.Equal(LinkState.Missing, Links.StateOf(opened, p2, moved));
    }
}
