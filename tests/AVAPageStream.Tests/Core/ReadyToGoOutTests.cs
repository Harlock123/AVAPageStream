using System.Text;
using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 5: preflight, packaging, and what a PDF gets beyond its pages.</summary>
public class ReadyToGoOutTests
{
    private static readonly FixedMeasurer M = new();

    private static PreflightOptions Options(Func<string, PictureSize?>? pixels = null, Func<string, FontState>? fonts = null, string? folder = null) =>
        new(M, pixels ?? (_ => null), fonts ?? (f => Fonts.Bundled.Contains(f) ? FontState.Bundled : FontState.Installed), folder);

    [Fact]
    public void Preflight_finds_text_that_doesnt_fit_and_rows_with_nowhere_to_go()
    {
        var e = new Editor(Document.New());
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 100, H = 20 });
        e.ReplaceText(f.StoryId, 0, 0, string.Join(" ", Enumerable.Repeat("word", 40)));
        var t = e.AddTable(SheetRef.PageAt(0), new Box(72, 680, 200, 120), 6, 2);
        var next = e.RunOnTable(t.Id)!;
        e.Change("Short", d => d.Find(next)!.Bounds = d.Find(next)!.Bounds with { H = 30 });
        var issues = Preflight.Check(e.Doc, Options());
        var over = Assert.Single(issues, i => i.Kind == Problem.Overset);
        Assert.Equal((f.Id, f.StoryId, SheetRef.PageAt(0)), (over.ObjectId, over.StoryId, over.Sheet));
        Assert.True(over.At > 0 && over.Serious);
        Assert.StartsWith("Page 1:", over.Message);
        var rows = Assert.Single(issues, i => i.Kind == Problem.RowsOverset);
        Assert.Equal((next, SheetRef.PageAt(1)), (rows.ObjectId, rows.Sheet));
        Assert.Contains("2 table rows", rows.Message); // a frame takes one row however short it is: two are left
    }

    [Fact]
    public void Preflight_finds_pictures_missing_or_with_too_few_pixels()
    {
        var e = new Editor(Document.New());
        var empty = e.Add(SheetRef.PageAt(0), new PictureObject { X = 72, Y = 72, W = 100, H = 100 });
        var key = e.Doc.Images.Add(AVAPageStream.Tests.WindowTests.Png);
        var small = e.Add(SheetRef.PageAt(0), new PictureObject { X = 72, Y = 200, W = 200, H = 200, Image = key });
        var fine = e.Add(SheetRef.PageAt(0), new PictureObject { X = 300, Y = 200, W = 20, H = 20, Image = key, Fill = null });
        // 300 pixels over 100 pt: at 200 pt (2.8 in) wide it is 108 dpi; at 20 pt, 1080.
        var issues = Preflight.Check(e.Doc, Options(pixels: _ => new PictureSize(300, 300, 100, 100)));
        Assert.Equal(empty.Id, Assert.Single(issues, i => i.Kind == Problem.NoPicture).ObjectId);
        var low = Assert.Single(issues, i => i.Kind == Problem.LowResolution);
        Assert.Equal(small.Id, low.ObjectId);
        Assert.Contains("only 108 dots an inch", low.Message);
        Assert.DoesNotContain(issues, i => i.ObjectId == fine.Id);
        Assert.DoesNotContain(Preflight.Check(e.Doc, Options(pixels: _ => null)), i => i.Kind == Problem.LowResolution); // a drawing
    }

    [Fact]
    public void Preflight_finds_artwork_at_the_edge_that_doesnt_run_out_to_the_bleed()
    {
        var e = new Editor(Document.New());
        var atEdge = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 0, Y = 100, W = 100, H = 100, Fill = "#FF0000" });
        e.Add(SheetRef.PageAt(0), new RectangleObject { X = 100, Y = 300, W = 100, H = 100, Fill = "#FF0000" }); // well inside
        e.Add(SheetRef.PageAt(0), new RectangleObject { X = 0, Y = 500, W = 100, H = 100, Fill = null });        // no fill: nothing shows
        Assert.Equal(atEdge.Id, Assert.Single(Preflight.Check(e.Doc, Options())).ObjectId);
        Assert.Equal(Problem.NoBleed, Preflight.Check(e.Doc, Options())[0].Kind);

        e.Change("Bleed", d => d.Setup = d.Setup with { Bleed = 9 });
        Assert.Equal(Problem.ShortOfBleed, Assert.Single(Preflight.Check(e.Doc, Options())).Kind);
        e.Change("Out", d => d.Find(atEdge.Id)!.Bounds = new Box(-9, 100, 109, 100));
        Assert.Empty(Preflight.Check(e.Doc, Options()));

        // A hidden layer, or one that doesn't print, isn't checked.
        e.Change("In", d => d.Find(atEdge.Id)!.Bounds = new Box(0, 100, 100, 100));
        e.SetLayer("Layer 1", l => l with { Print = false });
        Assert.Empty(Preflight.Check(e.Doc, Options()));
    }

    [Fact]
    public void Preflight_finds_fonts_missing_or_not_to_be_embedded()
    {
        var e = new Editor(Document.New());
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { W = 300, H = 100 });
        e.ReplaceText(f.StoryId, 0, 0, "one two three");
        e.FormatChars(f.StoryId, 0, 3, c => c with { Font = "Nowhere Sans" });
        e.FormatChars(f.StoryId, 4, 3, c => c with { Font = "Locked Serif" });
        e.FormatChars(f.StoryId, 8, 5, c => c with { Font = "Nowhere Sans" });
        var issues = Preflight.Check(e.Doc, Options(fonts: n => n switch { "Nowhere Sans" => FontState.Missing, "Locked Serif" => FontState.NoEmbedding, _ => FontState.Bundled }));
        Assert.Equal([Problem.MissingFont, Problem.NoEmbedding], issues.Select(i => i.Kind)); // each font once
        Assert.Equal(4, issues[1].At);
    }

    [Fact]
    public void A_package_gathers_the_document_its_linked_pictures_and_fonts()
    {
        var home = Directory.CreateTempSubdirectory("avaps-package-").FullName;
        var picture = Path.Combine(home, "logo.png");
        File.WriteAllBytes(picture, AVAPageStream.Tests.WindowTests.Png);
        var font = Path.Combine(home, "Fancy-Regular.ttf");
        File.WriteAllText(font, "not really a font");
        var e = new Editor(Document.New());
        var key = e.Doc.Images.Add(AVAPageStream.Tests.WindowTests.Png);
        var p = e.Add(SheetRef.PageAt(0), new PictureObject { W = 50, H = 50, Image = key, Source = picture, Linked = true });
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { W = 300, H = 100 });
        e.ReplaceText(f.StoryId, 0, 0, "Fancy and gone");
        e.FormatChars(f.StoryId, 0, 5, c => c with { Font = "Fancy" });
        e.FormatChars(f.StoryId, 10, 4, c => c with { Font = "Gone" });

        var out_ = Path.Combine(home, "out");
        var result = Package.Make(e.Doc, "Leaflet", home, out_, name => name == "Fancy" ? [font] : [], []);
        Assert.True(File.Exists(Path.Combine(out_, "Links", "logo.png")));
        Assert.True(File.Exists(Path.Combine(out_, "Fonts", "Fancy-Regular.ttf")));
        Assert.Equal((1, 1), (result.Pictures, result.Fonts));
        Assert.Contains("Font Gone: not on this computer", result.Missing);
        var report = File.ReadAllText(Path.Combine(out_, "Report.txt"));
        Assert.Contains("Liberation Serif: built into AVAPageStream", report);
        Assert.Contains("Gone: NOT FOUND", report);
        Assert.Contains("Preflight found nothing wrong.", report);

        // The packaged copy is linked to its own copy of the picture; the original document is left as it was.
        var copy = DocumentFile.Load(result.Document);
        Assert.Equal(Path.Combine(out_, "Links", "logo.png"), ((PictureObject)copy.Find(p.Id)!).Source);
        Assert.Equal(picture, ((PictureObject)e.Doc.Find(p.Id)!).Source);
    }

    [Fact]
    public void Headings_become_bookmarks_and_contents_entries_and_addresses_become_links()
    {
        var e = new Editor(Document.New());
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 400, H = 600 });
        e.ReplaceText(f.StoryId, 0, 0, "Tides\t3\nThe harbour\nTides\nSee www.example.org.\nTimes");
        e.Change("Contents style", d => d.Styles.Add(Contents.MakeStyle(d, 0, 300)));
        e.ApplyStyle(f.StoryId, 0, 0, "Contents 1");
        e.ApplyStyle(f.StoryId, 8, 0, "Heading");
        e.ApplyStyle(f.StoryId, 20, 0, "Heading");
        e.ApplyStyle(f.StoryId, 47, 0, "Subheading");
        Assert.Equal(["Heading", "Subheading"], PdfExtras.OutlineStyles(e.Doc));
        var marks = PdfExtras.Bookmarks(e.Doc, M);
        Assert.Equal([("The harbour", 0), ("Tides", 0), ("Times", 1)], marks.Select(b => (b.Title, b.Level)));
        Assert.All(marks, b => Assert.Equal(0, b.Page));
        Assert.True(marks[1].Y > marks[0].Y);

        var story = e.Doc.Stories[f.StoryId];
        var links = PdfExtras.Links(e.Doc, 0, marks, id => TextFlow.Layout(e.Doc.Stories[id], Chains.Areas(e.Doc, e.Doc.Stories[id]), M));
        Assert.Equal(1, Assert.Single(links, l => l.Bookmark is not null).Bookmark); // the contents line goes to "Tides"
        Assert.Equal("https://www.example.org", Assert.Single(links, l => l.Url is not null).Url); // the full stop left off
    }

    // A small PDF, as SkiaSharp lays them out: numbered objects, a cross-reference table and a trailer.
    private static byte[] MinimalPdf()
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }
        Obj("<</Type /Catalog\n/Pages 2 0 R>>");
        Obj("<</Type /Pages\n/Count 2\n/Kids [3 0 R 4 0 R]>>");
        Obj("<</Type /Page\n/MediaBox [0 0 600 800]\n/Parent 2 0 R>>");
        Obj("<</Type /Page\n/MediaBox [0 0 600 800]\n/Parent 2 0 R>>");
        var xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size 5\n/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void An_outline_and_printers_boxes_are_added_to_a_finished_pdf()
    {
        var before = MinimalPdf();
        var after = PdfExtras.Finish(before,
            [new OutlineEntry("Tides", 0, 0, 700), new OutlineEntry("High water", 1, 1, 500), new OutlineEntry("Café", 0, 1, 300)],
            [(new Box(9, 9, 582, 782), new Box(0, 0, 600, 800)), (new Box(9, 9, 582, 782), new Box(0, 0, 600, 800))]);
        Assert.Equal(before, after[..before.Length]); // the file as it was, with an update after it
        var t = Encoding.Latin1.GetString(after);
        Assert.Contains("/Outlines 8 0 R\n/PageMode /UseOutlines", t); // after its entries, 5 to 7
        Assert.Contains("/Title (Tides)", t);
        Assert.Contains("/Dest [4 0 R /XYZ 0 500 null]", t);
        Assert.Contains("/Title <FEFF00430061006600E9>", t); // Café, as UTF-16
        Assert.Contains("/TrimBox [9 9 591 791]\n/BleedBox [0 0 600 800]", t);
        Assert.Contains($"/Prev {Encoding.Latin1.GetString(before).IndexOf("xref", StringComparison.Ordinal)}", t);

        // Every object the update writes is where its cross-reference says.
        var update = t[before.Length..];
        var xrefAt = int.Parse(update[(update.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", t[xrefAt..]);
        foreach (var line in t[xrefAt..t.LastIndexOf("trailer", StringComparison.Ordinal)].Split('\n').Skip(1).Chunk(2).Where(c => c.Length == 2))
        {
            var n = line[0].Split(' ')[0];
            var at = int.Parse(line[1][..10]);
            Assert.StartsWith($"{n} 0 obj", t[at..]);
        }
        // The first top-level entry has the second as its next, and its child under it.
        Assert.Matches(@"/Title \(Tides\)\n/Parent 8 0 R\n/Dest \[3 0 R /XYZ 0 700 null\]\n/Next 7 0 R\n/First 6 0 R\n/Last 6 0 R\n/Count 1", t);

        Assert.Equal(before, PdfExtras.Finish(before, []));                                     // nothing to add
        Assert.Equal("not a pdf"u8.ToArray(), PdfExtras.Finish("not a pdf"u8.ToArray(), [new OutlineEntry("x", 0, 0, 0)]));
    }
}
