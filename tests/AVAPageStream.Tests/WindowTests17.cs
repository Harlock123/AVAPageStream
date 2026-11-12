using Avalonia.Headless.XUnit;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 7: gradients, transparency, shadows and corners.</summary>
public partial class WindowTests
{
    /// <summary>A page of effects, drawn as a PDF and a PNG would be.</summary>
    private static Document EffectsPage()
    {
        var doc = Document.New(new Setup(400, 300));
        doc.Pages[0].Objects.Add(new RectangleObject
        {
            Id = "grad", X = 20, Y = 20, W = 200, H = 120, Stroke = null,
            Gradient = new Gradient(false, [new ColourStop(0, "@Blue"), new ColourStop(1, "@Yellow")]),
            Shadow = new Shadow(Colours.Black, 0.6, 8, 8, 8),
        });
        doc.Pages[0].Objects.Add(new EllipseObject
        {
            Id = "radial", X = 150, Y = 60, W = 160, H = 160, Stroke = null,
            Gradient = new Gradient(true, [new ColourStop(0, "#FFFFFF"), new ColourStop(1, "@Red")]),
            Opacity = 0.8, Blend = BlendMode.Multiply,
        });
        doc.Pages[0].Objects.Add(new RectangleObject
        {
            Id = "corners", X = 20, Y = 190, W = 120, H = 80, Fill = "@Green", Stroke = Colours.Black, StrokeWidth = 2,
            Corners = new Corners(30, 0, 30, 10, CornerStyle.Round), Shadow = new Shadow("@Yellow", 1, 0, 0, 12, Glow: true),
        });
        doc.Pages[0].Objects.Add(new RectangleObject
        {
            Id = "cut", X = 260, Y = 220, W = 120, H = 60, Fill = "@Magenta", Stroke = null, FillOpacity = 0.5,
            Corners = Corners.All(15, CornerStyle.Cut),
        });
        return doc;
    }

    private static Avalonia.Media.Imaging.RenderTargetBitmap DrawPage(Document doc)
    {
        var renderer = new PageRenderer(new Layouts(FontMeasurer.Shared)) { Screen = false };
        var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)doc.Setup.PageWidth, (int)doc.Setup.PageHeight));
        bitmap.Render(new PageVisual(doc, doc.Pages[0], renderer));
        return bitmap;
    }

    private static (byte R, byte G, byte B) Pixel(Avalonia.Media.Imaging.RenderTargetBitmap b, int x, int y)
    {
        var w = b.PixelSize.Width;
        var px = new byte[w * b.PixelSize.Height * 4];
        unsafe { fixed (byte* p = px) b.CopyPixels(new Avalonia.PixelRect(0, 0, w, b.PixelSize.Height), (nint)p, px.Length, w * 4); }
        var o = (y * w + x) * 4;
        return (px[o + 2], px[o + 1], px[o]);
    }

    [AvaloniaFact]
    public void Gradients_shadows_blends_and_corners_are_drawn()
    {
        var doc = EffectsPage();
        using var bitmap = DrawPage(doc);
        if (Shots is { } dir) { Directory.CreateDirectory(dir); bitmap.Save(Path.Combine(dir, "effects.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        var left = Pixel(bitmap, 25, 30);
        var right = Pixel(bitmap, 140, 30);
        Assert.True(left.B > left.R && right.R > right.B, $"{left} {right}");           // blue to yellow, left to right
        Assert.True(Pixel(bitmap, 225, 145).R < 220, "the shadow below the rectangle's corner");
        var blended = Pixel(bitmap, 190, 100);                                            // the ellipse over the rectangle, multiplied
        Assert.True(blended.R < 230 && blended.G < 230, $"{blended}");
        Assert.True(Pixel(bitmap, 22, 192).R > 200, "the rounded corner: not the green box (the glow shows there)");
        Assert.True(Pixel(bitmap, 15, 230).R > 200 && Pixel(bitmap, 15, 230).B < 150, "the glow round the green box");
        Assert.Equal((255, 255, 255), Pixel(bitmap, 262, 222));                           // the cut corner
    }

    [AvaloniaFact]
    public void Effects_go_into_a_pdf()
    {
        var pdf = Path.Combine(Shots ?? Folder(), "AVAPageStream-effects.pdf");
        Wait(Export.Pdf(EffectsPage(), pdf));
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Assert.Contains("/ShadingType", text);   // the gradients, as PDF shadings
        Assert.Contains("/BM /Multiply", text);  // the blend mode
        Assert.Contains("/ca .8", text);          // the ellipse's opacity
    }

    [AvaloniaFact]
    public void The_object_tab_sets_gradients_opacity_shadows_and_corners()
    {
        var w = Open();
        var r = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 72, Y = 72, W = 200, H = 100, Fill = "@Blue" });
        w.View.Select([r.Id]);
        w.Panel.SelectedIndex = 0;
        w.Refresh();
        Pump();
        void Commit(Avalonia.Controls.TextBox box, string text) { box.Focus(); box.Text = text; Press(w, Avalonia.Input.Key.Enter); }
        w.Panel.FillKindChoice.SelectedIndex = 1;                  // a linear gradient, from the fill's colour to white
        Pump();
        var o = w.Editor.Doc.Find(r.Id)!;
        Assert.Equal(["@Blue", "@White"], o.Gradient!.Stops.Select(s => s.Colour)); // White is a swatch
        Commit(w.Panel.GradientStopsBox, "Red, Yellow at 40, Green");
        Commit(w.Panel.GradientAngleBox, "90");
        o = w.Editor.Doc.Find(r.Id)!;
        Assert.Equal((3, 90.0, false), (o.Gradient!.Stops.Count, o.Gradient.Angle, o.Gradient.Radial));
        w.Panel.FillKindChoice.SelectedIndex = 2;
        Pump();
        Assert.True(w.Editor.Doc.Find(r.Id)!.Gradient!.Radial);
        Commit(w.Panel.OpacityBox, "60%");
        w.Panel.BlendChoice.SelectedIndex = (int)BlendMode.Screen;
        w.Panel.ShadowChoice.SelectedIndex = 1;
        Pump();
        Commit(w.Panel.ShadowBlurBox, "10");
        Commit(w.Panel.CornerBox, "12 0 12 0");
        w.Panel.CornerStyleChoice.SelectedIndex = 1;
        Pump();
        o = w.Editor.Doc.Find(r.Id)!;
        Assert.Equal((0.6, BlendMode.Screen), (o.Opacity, o.Blend));
        Assert.Equal((false, 10.0), (o.Shadow!.Glow, o.Shadow.Blur));
        Assert.Equal(new Corners(12, 0, 12, 0, CornerStyle.Cut), o.Corners);
        Assert.Equal("Screen", w.Panel.BlendChoice.SelectedItem);
        Commit(w.Panel.OpacityBox, "150");
        Assert.Contains("from 0% (clear) to 100%", w.Message.Text);
        w.Panel.FillKindChoice.SelectedIndex = 0;                  // back to a plain colour
        Pump();
        Assert.Null(w.Editor.Doc.Find(r.Id)!.Gradient);
        Done(w);
    }
}
