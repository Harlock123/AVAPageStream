namespace AVAPageStream.Core;

/// <summary>A document to start from: the program's own, or a .avpt file someone saved.</summary>
public sealed record Template(string Name, string Description, Func<Document> Make);

/// <summary>Templates: the built-in ones, and files saved as templates (.avpt, the same format as a document).</summary>
public static class Templates
{
    public const string Extension = ".avpt";

    public static readonly Template[] BuiltIn =
    [
        new("Newsletter", "Letter, a masthead and two linked columns, page numbers in the footer", Newsletter),
        new("Booklet", "A5 facing pages, eight of them, one story through them all, page numbers outside", Booklet),
    ];

    /// <summary>The templates saved in a folder, by name.</summary>
    public static IEnumerable<string> InFolder(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*" + Extension).Order(StringComparer.OrdinalIgnoreCase) : [];

    private static Document Newsletter()
    {
        var e = new Editor(Document.New(new Setup(Margins: new Margins(54, 54, 54, 54), Columns: 2, Gutter: 18)));
        var footer = e.Add(SheetRef.MasterAt(0), new TextFrame { X = 54, Y = 750, W = 504, H = 18 });
        e.ReplaceText(footer.StoryId, 0, 0, $"Newsletter  ·  Page {Fields.PageNumber}", new CharFormat(Fonts.Sans, 8, Colour: "@Grey"));
        e.FormatParas(footer.StoryId, 0, 0, p => p with { Align = Align.Centre });
        e.Add(SheetRef.MasterAt(0), new LineObject { X = 54, Y = 744, W = 504, H = 0, Stroke = "@Grey", StrokeWidth = 0.5 });
        var head = e.Add(SheetRef.PageAt(0), new TextFrame { X = 54, Y = 54, W = 504, H = 70 });
        e.ReplaceText(head.StoryId, 0, 0, "Newsletter title");
        e.ApplyStyle(head.StoryId, 0, 0, "Heading");
        e.FormatChars(head.StoryId, 0, 17, f => f with { Size = 40 });
        e.Add(SheetRef.PageAt(0), new RectangleObject { X = 54, Y = 128, W = 504, H = 3, Fill = "@Black", Stroke = null });
        var left = e.Add(SheetRef.PageAt(0), new TextFrame { X = 54, Y = 144, W = 243, H = 594 });
        var right = e.Add(SheetRef.PageAt(0), new TextFrame { X = 315, Y = 144, W = 243, H = 594 });
        e.Change("Link", d => Chains.Link(d, (TextFrame)d.Find(left.Id)!, (TextFrame)d.Find(right.Id)!));
        e.ReplaceText(left.StoryId, 0, 0, "Headline\nType or place the story here. It flows from the left column into the right.");
        e.ApplyStyle(left.StoryId, 0, 0, "Subheading");
        e.FormatParas(left.StoryId, 9, 0, p => p with { Align = Align.Justify, Hyphenate = true });
        return e.Doc;
    }

    private static Document Booklet()
    {
        var a5 = PaperSize.All.First(p => p.Name == "A5");
        var e = new Editor(Document.New(new Setup(a5.Width, a5.Height, new Margins(42, 48, 48, 36), Facing: true)));
        e.Doc.EnsureSides();
        // Page numbers in the outside bottom corners: the left page's on its left, the right page's on its right.
        foreach (var left in new[] { true, false })
        {
            var folio = e.Add(SheetRef.MasterAt(0, left), new TextFrame { X = left ? 36 : a5.Width - 36 - 60, Y = a5.Height - 34, W = 60, H = 14 });
            e.ReplaceText(folio.StoryId, 0, 0, Fields.PageNumber.ToString(), new CharFormat(Fonts.Serif, 9));
            e.FormatParas(folio.StoryId, 0, 0, p => p with { Align = left ? Align.Left : Align.Right });
        }
        for (var i = 1; i < 8; i++) e.AddPage(i);
        string? previous = null;
        for (var i = 0; i < 8; i++)
        {
            var (inside, outside) = (48.0, 36.0);
            var x = e.Doc.IsLeft(e.Doc.Pages[i]) ? outside : inside;
            var frame = e.Add(SheetRef.PageAt(i), new TextFrame { X = x, Y = 42, W = a5.Width - inside - outside, H = a5.Height - 42 - 48 });
            if (previous is { } p) e.Change("Link", d => Chains.Link(d, (TextFrame)d.Find(p)!, (TextFrame)d.Find(frame.Id)!));
            previous = frame.Id;
        }
        var first = (TextFrame)e.Doc.Pages[0].Objects[0];
        e.ReplaceText(first.StoryId, 0, 0, "Booklet title\nThe story runs on through all eight pages. Place a text file into it, or type.");
        e.ApplyStyle(first.StoryId, 0, 0, "Heading");
        e.FormatParas(first.StoryId, 14, 0, p => p with { Align = Align.Justify, Hyphenate = true, FirstIndent = 12 });
        return e.Doc;
    }
}
