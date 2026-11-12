using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 7: gradients, transparency, shadows and corners in the document.</summary>
public class EffectsTests
{
    private static readonly List<Swatch> Swatches = Colours.DefaultSwatches();

    [Fact]
    public void Gradient_stops_are_read_as_typed_and_spaced_where_not_placed()
    {
        Assert.Null(Gradient.ReadStops("Red, Gold at 30, #FFFFFF, Blue", [.. Swatches, new Swatch("Gold", "#FFD800")], out var stops));
        Assert.Equal([0.0, 0.3, 0.65, 1], stops.Select(s => Math.Round(s.At, 4)));
        Assert.Equal(["@Red", "@Gold", "#FFFFFF", "@Blue"], stops.Select(s => s.Colour));
        Assert.Null(Gradient.ReadStops("Blue 40% at 100, Blue at 0", Swatches, out var tints)); // a tint, and out of order
        Assert.Equal(["@Blue", "@Blue 40%"], tints.Select(s => s.Colour));
        Assert.Contains("two colours", Gradient.ReadStops("Red", Swatches, out _));
        Assert.Contains("isn't a colour", Gradient.ReadStops("Red, Mauve", Swatches, out _));
        Assert.Contains("from 0 to 100", Gradient.ReadStops("Red at 120, Blue", Swatches, out _));
        Assert.Equal("Red at 0, Blue at 100", new Gradient(false, [new ColourStop(0, "@Red"), new ColourStop(1, "@Blue")]).Describe(Swatches));
    }

    [Fact]
    public void Corners_are_read_one_or_four_and_never_cross()
    {
        Assert.True(Corners.TryRead("6", Unit.Point, out var one));
        Assert.Equal([6.0, 6, 6, 6], one);
        Assert.True(Corners.TryRead("1 2 3 4", Unit.Point, out var four));
        Assert.Equal([1.0, 2, 3, 4], four);
        Assert.False(Corners.TryRead("1 2", Unit.Point, out _));
        Assert.Equal(new Corners(20, 20, 5, 20), new Corners(50, 30, 5, 20).Fitted(100, 40)); // half the short side at most
        Assert.Equal("6 pt", Corners.All(6).Describe(Unit.Point));
        Assert.True(new Corners(0, 0, 0, 0).IsSquare);
    }

    [Fact]
    public void Effects_are_kept_in_the_file_and_in_undo()
    {
        var e = new Editor(Document.New());
        var r = e.Add(SheetRef.PageAt(0), new RectangleObject { W = 50, H = 50 });
        e.Change("Effects", d =>
        {
            var o = d.Find(r.Id)!;
            o.Gradient = new Gradient(true, [new ColourStop(0, "@Red"), new ColourStop(1, "#FFFFFF")], 0, 0.3, 0.4, 0.8);
            (o.Opacity, o.FillOpacity, o.Blend) = (0.5, 0.7, BlendMode.Multiply);
            o.Shadow = new Shadow("@Blue", 0.4, 2, 3, 6, Glow: true);
            o.Corners = new Corners(1, 2, 3, 4, CornerStyle.Cut);
        });
        using var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms).Find(r.Id)!;
        Assert.Equal(e.Doc.Find(r.Id)!.Gradient, back.Gradient);
        Assert.Equal((0.5, 0.7, BlendMode.Multiply), (back.Opacity, back.FillOpacity, back.Blend));
        Assert.Equal(new Shadow("@Blue", 0.4, 2, 3, 6, true), back.Shadow);
        Assert.Equal(new Corners(1, 2, 3, 4, CornerStyle.Cut), back.Corners);
        Assert.True(back.HasEffects);
        e.Undo();
        Assert.Null(e.Doc.Find(r.Id)!.Gradient);
        Assert.False(e.Doc.Find(r.Id)!.HasEffects);
    }
}
