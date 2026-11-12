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

    [Fact]
    public void A_gradient_s_ends_are_where_it_is_dragged()
    {
        var box = new Box(100, 50, 200, 100);
        var g = new Gradient(false, [new ColourStop(0, "@Red"), new ColourStop(1, "@Blue")]);
        Assert.Equal((new Pt(100, 100), new Pt(300, 100)), g.Ends(box));       // across the box, as before
        var down = g with { Angle = 90 };
        var (top, foot) = down.Ends(box);
        Assert.Equal((200, 50, 200, 150), (Math.Round(top.X, 6), Math.Round(top.Y, 6), Math.Round(foot.X, 6), Math.Round(foot.Y, 6)));
        var dragged = g.Dragged(box, new Pt(120, 60), new Pt(220, 160));        // 45°, from near a corner
        Assert.Equal(45, dragged.Angle);
        var (from, to) = dragged.Ends(box);
        Assert.Equal(120, from.X, 6);
        Assert.Equal(60, from.Y, 6);
        Assert.Equal(220, to.X, 6);
        Assert.Equal(160, to.Y, 6);
        Assert.Same(g, g.Dragged(box, new Pt(5, 5), new Pt(5.1, 5)));          // a click is no drag
        var radial = new Gradient(true, g.Stops).Dragged(box, new Pt(150, 75), new Pt(150, 125));
        Assert.Equal((0.25, 0.25, 90.0), (radial.X, radial.Y, radial.Angle));
        var (c, edge) = radial.Ends(box);
        Assert.Equal(new Pt(150, 75), c);
        Assert.Equal(150, edge.X, 6);
        Assert.Equal(125, edge.Y, 6);
    }
}
