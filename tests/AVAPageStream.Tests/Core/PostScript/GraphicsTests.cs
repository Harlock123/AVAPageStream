using AVAPageStream.Core;
using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.PostScript;

/// <summary>The graphics: what a program paints, in device space (points, y up).</summary>
public class GraphicsTests
{
    private static Interpreter Run(string program)
    {
        var i = new Interpreter();
        Assert.Null(i.Run(program));
        return i;
    }

    private static string Points(PsPath p) => string.Join(" ", p.Segs.Select(s => s.Kind switch
    {
        SegKind.Move => $"M{Math.Round(s.X3, 3)},{Math.Round(s.Y3, 3)}",
        SegKind.Line => $"L{Math.Round(s.X3, 3)},{Math.Round(s.Y3, 3)}",
        SegKind.Curve => $"C{Math.Round(s.X3, 3)},{Math.Round(s.Y3, 3)}",
        _ => "Z",
    }));

    [Fact]
    public void A_path_is_filled_in_the_current_colour_through_the_current_transformation()
    {
        var i = Run("10 20 translate 2 2 scale 0 0 moveto 5 0 lineto 0 5 rlineto closepath 1 0 0 setrgbcolor fill");
        var f = Assert.IsType<PsFill>(Assert.Single(i.Ops));
        Assert.Equal("M10,20 L20,20 L20,30 Z", Points(f.Path));
        Assert.Equal(new PsColour(1, 0, 0), f.Colour);
        Assert.False(f.EvenOdd);
        Assert.True(i.G.Path.IsEmpty); // fill uses up the path
    }

    [Fact]
    public void A_stroke_keeps_its_path_in_user_space_with_the_matrix_that_draws_it()
    {
        var i = Run("3 3 scale 0 0 moveto 10 0 lineto 2 setlinewidth 1 setlinecap 2 setlinejoin [4 2] 1 setdash 0.5 setgray stroke");
        var s = Assert.IsType<PsStroke>(Assert.Single(i.Ops));
        Assert.Equal("M0,0 L10,0", Points(s.Path));
        Assert.Equal(new PsMatrix(3, 0, 0, 3, 0, 0), s.Matrix);
        Assert.Equal((2.0, 1, 2), (s.Pen.Width, s.Pen.Cap, s.Pen.Join));
        Assert.Equal([4.0, 2.0], s.Pen.Dash);
        Assert.Equal(new PsColour(0.5, 0.5, 0.5), s.Colour);
    }

    [Fact]
    public void Gsave_and_grestore_keep_the_state_and_clips_apply_to_what_comes_after()
    {
        var i = Run("gsave 0 0 50 50 rectclip 1 setgray 0 0 100 100 rectfill grestore 0 0 100 100 rectfill");
        Assert.Equal(2, i.Ops.Count);
        Assert.Single(i.Ops[0].Clips);
        Assert.Equal(new PsColour(1, 1, 1), ((PsFill)i.Ops[0]).Colour);
        Assert.Empty(i.Ops[1].Clips);
        Assert.Equal(PsColour.Black, ((PsFill)i.Ops[1]).Colour);
    }

    [Fact]
    public void Arcs_are_curves_on_the_circle()
    {
        var i = Run("newpath 50 50 10 0 90 arc currentpoint");
        Assert.Equal("M60,50 C50,60", Points(i.G.Path));
        Assert.Equal(50.0, (double)i.Stack[0], 6);
        Assert.Equal(60.0, (double)i.Stack[1], 6);
        // A full circle: four quarters; the middle of the first is on the circle.
        var j = Run("newpath 0 0 10 0 360 arc");
        var curves = j.G.Path.Segs.Where(s => s.Kind == SegKind.Curve).ToList();
        Assert.Equal(4, curves.Count);
        var c = curves[0];
        var (mx, my) = (0.125 * 10 + 0.375 * c.X1 + 0.375 * c.X2 + 0.125 * c.X3, 0.125 * 0 + 0.375 * c.Y1 + 0.375 * c.Y2 + 0.125 * c.Y3);
        Assert.Equal(10, Math.Sqrt(mx * mx + my * my), 2);
        // Clockwise, with arcn.
        Assert.Equal("M10,0 C0,-10", Points(Run("newpath 0 0 10 0 -90 arcn").G.Path));
    }

    [Fact]
    public void Arcto_rounds_a_corner_and_gives_its_tangent_points()
    {
        var i = Run("newpath 0 0 moveto 10 0 10 10 4 arcto");
        Assert.Equal(new[] { 6.0, 0, 10, 4 }, i.Stack.Select(o => Math.Round(Interpreter.ToNum(o), 6)));
        Assert.Equal("M0,0 L6,0 C10,4", Points(i.G.Path));
    }

    [Fact]
    public void Matrices_transform_points_and_back()
    {
        var i = Run("10 20 translate 90 rotate 1 0 transform 0 1 dtransform matrix currentmatrix 10 21 itransform");
        var n = i.Stack.Where(o => o is not PsArray).Select(o => Math.Round(Interpreter.ToNum(o), 6)).ToArray();
        Assert.Equal(new[] { 10.0, 21, -1, 0, 1, 0 }, n);
    }

    [Fact]
    public void Colour_spaces_give_their_colours()
    {
        Assert.Equal(new PsColour(0, 1, 1), Run("1 0 0 0 setcmykcolor").G.Colour);
        Assert.Equal(new PsColour(1, 0, 0), Run("0 1 1 sethsbcolor").G.Colour);
        // Indexed, from a string of RGB triples.
        Assert.Equal(new PsColour(0, 0, 1), Run("[/Indexed /DeviceRGB 1 <FF0000 0000FF>] setcolorspace 1 setcolor").G.Colour);
        // A spot colour: its tint through the tint transform into CMYK.
        var spot = Run("[/Separation /Gold /DeviceCMYK {0 exch 0 exch 0}] setcolorspace 1 setcolor").G.Colour;
        Assert.Equal((1.0, 1.0, 0.0), (spot.R, spot.G, Math.Round(spot.B, 6)));
        Assert.Equal(new PsColour(0.5, 0.5, 0.5), Run("/DeviceGray setcolorspace 0.5 setcolor").G.Colour);
    }

    [Fact]
    public void What_isnt_drawn_yet_is_an_error_to_fall_back_on()
    {
        Assert.Equal("unsupported", new Interpreter().Run("<< /PatternType 1 /PaintType 1 >> matrix makepattern")!.Name);
        Assert.Equal("unsupported", new Interpreter().Run("<< /ShadingType 4 >> shfill")!.Name);
        Assert.Equal("unsupported", new Interpreter().Run("currentfile /FlateDecode filter")!.Name); // binary data in an EPS's text
    }

    [Fact]
    public void An_eps_is_drawn_inside_its_bounding_box_with_the_device_left_alone()
    {
        var eps = System.Text.Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 10 10 110 60\n<< /PageSize [100 50] >> setpagedevice\n0 0 1 setrgbcolor 10 10 100 50 rectfill showpage\n%%EOF\n");
        var d = Eps.Draw(eps, out var why)!;
        Assert.Null(why);
        Assert.Equal(new Box(10, 10, 100, 50), d.BoundingBox);
        Assert.IsType<PsFill>(Assert.Single(d.Ops));
        Assert.Null(Eps.Draw(System.Text.Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 10 10\n<< /PatternType 1 >> matrix makepattern\n"), out why));
        Assert.Contains("tiling patterns", why);
    }
}
