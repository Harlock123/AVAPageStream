using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 7: object styles.</summary>
public class ObjectStyleTests
{
    private static ObjectStyle Card => new("Card", "@Yellow", "@Blue", 2,
        new Gradient(false, [new ColourStop(0, "@Blue"), new ColourStop(1, "@White")], 45), 0.9, 1, 1, BlendMode.Multiply,
        new Shadow("@Red", 0.5, 3, 3, 5), Corners.All(8), Wrap: true, WrapGap: 9);

    [Fact]
    public void A_style_gives_objects_its_look_and_redefining_it_changes_them()
    {
        var e = new Editor(Document.New());
        var a = e.Add(SheetRef.PageAt(0), new RectangleObject { W = 50, H = 50, CornerRadius = 4 });
        var b = e.Add(SheetRef.PageAt(0), new EllipseObject { W = 50, H = 50 });
        var c = e.Add(SheetRef.PageAt(0), new RectangleObject { W = 50, H = 50, Fill = "@Green" });
        Assert.Null(e.DefineObjectStyle(Card, [a.Id]));
        Assert.Null(e.ApplyObjectStyle([b.Id], "Card"));
        var ra = e.Doc.Find(a.Id)!;
        Assert.Equal(("Card", "@Yellow", 2.0, 0.9, BlendMode.Multiply, true, 9.0), (ra.ObjectStyle, ra.Fill, ra.StrokeWidth, ra.Opacity, ra.Blend, ra.Wrap, ra.WrapGap));
        Assert.Equal(Corners.All(8), ra.Corners);
        Assert.Equal(0, ((RectangleObject)ra).CornerRadius);
        Assert.Null(e.Doc.Find(b.Id)!.Corners);                   // an ellipse has no corners
        Assert.True(e.Doc.ObjectStyle("Card")!.Matches(e.Doc.Find(b.Id)!));
        Assert.Equal("@Green", e.Doc.Find(c.Id)!.Fill);            // not in the style

        // Changed by hand: no longer matches, until the style is put back.
        e.Change("Fill", d => d.Find(a.Id)!.Fill = "@Red");
        Assert.False(e.Doc.ObjectStyle("Card")!.Matches(e.Doc.Find(a.Id)!));
        e.ApplyObjectStyle([a.Id], "Card");
        Assert.Equal("@Yellow", e.Doc.Find(a.Id)!.Fill);

        // Redefined: both objects follow; the third doesn't.
        e.DefineObjectStyle(Card with { Fill = "@Cyan", Shadow = null });
        Assert.All(new[] { a.Id, b.Id }, id => Assert.Equal(("@Cyan", (Shadow?)null), (e.Doc.Find(id)!.Fill, e.Doc.Find(id)!.Shadow)));
        Assert.Equal("@Green", e.Doc.Find(c.Id)!.Fill);
        e.Undo();
        Assert.Equal("@Yellow", e.Doc.Find(b.Id)!.Fill);

        // Renamed and deleted: the objects follow the name, then keep their look.
        Assert.Contains("already", e.RenameObjectStyle("Card", "Card") ?? "already");
        e.DefineObjectStyle(new ObjectStyle("Plain"));
        Assert.Contains("already", e.RenameObjectStyle("Card", "Plain"));
        Assert.Null(e.RenameObjectStyle("Card", "Panel"));
        Assert.Equal("Panel", e.Doc.Find(b.Id)!.ObjectStyle);
        Assert.Null(e.DeleteObjectStyle("Panel"));
        Assert.Equal((null, "@Yellow"), (e.Doc.Find(b.Id)!.ObjectStyle, e.Doc.Find(b.Id)!.Fill));
        Assert.NotNull(e.DeleteObjectStyle("Panel"));
    }

    [Fact]
    public void Object_styles_are_kept_in_the_file_and_follow_swatches()
    {
        var e = new Editor(Document.New());
        var a = e.Add(SheetRef.PageAt(0), new RectangleObject { W = 50, H = 50 });
        e.DefineObjectStyle(Card, [a.Id]);
        e.Change("A dangling style", d => d.Pages[0].Objects.Add(new RectangleObject { Id = "x9", ObjectStyle = "Gone" }));
        using var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms);
        Assert.Equal(Card, back.ObjectStyle("Card"));
        Assert.True(back.ObjectStyle("Card")!.Matches(back.Find(a.Id)!)); // the gradient's stops compare by value
        Assert.Null(back.Find("x9")!.ObjectStyle);                     // a style the file doesn't have is dropped
        Assert.Null(e.RenameSwatch("Blue", "Navy"));
        var style = e.Doc.ObjectStyle("Card")!;
        Assert.Equal(("@Navy", "@Navy"), (style.Stroke, style.Gradient!.Stops[0].Colour));
        Assert.Equal("@Red", style.Shadow!.Colour);
        Assert.Contains("gradient Navy", style.Describe(e.Doc.Swatches));
    }
}
