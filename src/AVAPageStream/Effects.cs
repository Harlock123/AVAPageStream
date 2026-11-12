using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using AVAPageStream.Core;
using SkiaSharp;

namespace AVAPageStream;

/// <summary>
/// An object's effects (opacity, blend mode, a shadow or glow): what it draws goes into a Skia layer, opened by one
/// custom operation and closed by another, so the text and pictures Avalonia draws between them are in it too. On
/// screen, in PNGs and in PDFs alike.
/// </summary>
internal static class Effects
{
    // Never culled: the opening and closing must both run, or the canvas's layers would not match.
    private static readonly Rect Everywhere = new(-1e6, -1e6, 2e6, 2e6);

    private sealed class Open(SKPaint paint) : ICustomDrawOperation
    {
        public Rect Bounds => Everywhere;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() => paint.Dispose();

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            lease.SkCanvas.SaveLayer(paint);
        }
    }

    private sealed class Close : ICustomDrawOperation
    {
        public Rect Bounds => Everywhere;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            lease.SkCanvas.Restore();
        }
    }

    private sealed class Layer(DrawingContext dc) : IDisposable
    {
        public void Dispose() => dc.Custom(new Close());
    }

    /// <summary>Opens the layer an object with effects is drawn into; disposing it closes it. Null for an object without.</summary>
    public static IDisposable? Begin(DrawingContext dc, PageObject o, Func<string?, SKColor?> colour)
    {
        if (!o.HasEffects) return null;
        var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(o.Opacity, 0, 1) * 255)), BlendMode = Blend(o.Blend) };
        if (o.Shadow is { } s && colour(s.Colour) is { } c)
        {
            var tint = c.WithAlpha((byte)Math.Round(Math.Clamp(s.Opacity, 0, 1) * 255));
            var sigma = (float)Math.Max(0, s.Blur / 2);
            paint.ImageFilter = s.Glow
                // A glow: the object's shape spread a little, blurred, in the glow's colour, under the object.
                ? SKImageFilter.CreateMerge(
                    SKImageFilter.CreateBlur(sigma, sigma, SKImageFilter.CreateColorFilter(SKColorFilter.CreateBlendMode(tint, SKBlendMode.SrcIn),
                        SKImageFilter.CreateDilate((float)(s.Blur / 3), (float)(s.Blur / 3)))),
                    null)
                : SKImageFilter.CreateDropShadow((float)s.X, (float)s.Y, sigma, sigma, tint);
        }
        dc.Custom(new Open(paint));
        return new Layer(dc);
    }

    public static SKBlendMode Blend(BlendMode b) => b switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };

    /// <summary>A rectangle's outline with its corners rounded or cut.</summary>
    public static Geometry Outline(Rect r, Corners corners)
    {
        var c = corners.Fitted(r.Width, r.Height);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var round = c.Style == CornerStyle.Round;
            void Corner(double size, Point from, Point to)
            {
                if (size <= 0) { ctx.LineTo(to); return; }
                if (round) ctx.ArcTo(to, new Size(size, size), 0, false, SweepDirection.Clockwise);
                else ctx.LineTo(to);
                _ = from;
            }
            ctx.BeginFigure(new Point(r.Left + c.TopLeft, r.Top), true);
            ctx.LineTo(new Point(r.Right - c.TopRight, r.Top));
            Corner(c.TopRight, new Point(r.Right - c.TopRight, r.Top), new Point(r.Right, r.Top + c.TopRight));
            ctx.LineTo(new Point(r.Right, r.Bottom - c.BottomRight));
            Corner(c.BottomRight, default, new Point(r.Right - c.BottomRight, r.Bottom));
            ctx.LineTo(new Point(r.Left + c.BottomLeft, r.Bottom));
            Corner(c.BottomLeft, default, new Point(r.Left, r.Bottom - c.BottomLeft));
            ctx.LineTo(new Point(r.Left, r.Top + c.TopLeft));
            Corner(c.TopLeft, default, new Point(r.Left + c.TopLeft, r.Top));
            ctx.EndFigure(true);
        }
        return g;
    }

    /// <summary>A gradient as a brush across a rectangle, its colours resolved and its opacity given.</summary>
    public static IBrush Brush(Gradient gr, Rect r, Func<string?, Color?> colour, double opacity)
    {
        var stops = new GradientStops();
        foreach (var s in gr.Stops) stops.Add(new Avalonia.Media.GradientStop(colour(s.Colour) ?? Colors.Transparent, s.At));
        if (gr.Radial)
        {
            var centre = new Point(r.X + r.Width * gr.X, r.Y + r.Height * gr.Y);
            var far = new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight }.Max(p => Math.Sqrt((p.X - centre.X) * (p.X - centre.X) + (p.Y - centre.Y) * (p.Y - centre.Y)));
            var radius = Math.Max(0.01, far * gr.Size);
            return new RadialGradientBrush
            {
                GradientStops = stops,
                Center = new RelativePoint(centre, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(centre, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(radius, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(radius, RelativeUnit.Absolute),
                Opacity = opacity,
            };
        }
        var a = gr.Angle * Math.PI / 180;
        var (dx, dy) = (Math.Cos(a), Math.Sin(a));
        var half = Math.Abs(r.Width / 2 * dx) + Math.Abs(r.Height / 2 * dy);
        var mid = r.Center;
        return new LinearGradientBrush
        {
            GradientStops = stops,
            StartPoint = new RelativePoint(mid.X - dx * half, mid.Y - dy * half, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(mid.X + dx * half, mid.Y + dy * half, RelativeUnit.Absolute),
            Opacity = opacity,
        };
    }
}
