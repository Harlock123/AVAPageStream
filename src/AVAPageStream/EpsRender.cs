using AVAPageStream.Core.PostScript;
using SkiaSharp;

namespace AVAPageStream;

/// <summary>
/// What AVAPageStream's PostScript interpreter drew from an EPS, recorded as a Skia picture: drawn like an SVG, so it
/// stays vector on screen and in PDFs.
/// </summary>
internal static class EpsRender
{
    /// <summary>The drawing as a picture of its bounding box's size, y down from its top-left, kept inside the box.</summary>
    /// <summary>Gives the interpreter a JPEG decoder (for DCTDecode): SkiaSharp's.</summary>
    static EpsRender() => Interpreter.Jpeg = DecodeJpeg;

    public static AVAPageStream.Core.ColourImage? DecodeJpeg(byte[] jpeg)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        if (bitmap is null) return null;
        using var rgba = bitmap.Copy(SKColorType.Rgba8888);
        var bytes = new byte[rgba.Width * rgba.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(rgba.GetPixels(), bytes, 0, bytes.Length);
        return new AVAPageStream.Core.ColourImage(rgba.Width, rgba.Height, bytes);
    }

    /// <summary>Makes sure the decoder is set before an EPS is run.</summary>
    public static void Ready() { }

    public static SKPicture Record(PsDrawing d)
    {
        var b = d.BoundingBox;
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, (float)b.W, (float)b.H));
        // From the EPS's own space (points, y up from its lower-left) to the picture's.
        canvas.Translate(-(float)b.X, (float)(b.Y + b.H));
        canvas.Scale(1, -1);
        canvas.ClipRect(new SKRect((float)b.X, (float)b.Y, (float)b.Right, (float)b.Bottom));
        using var paint = new SKPaint { IsAntialias = true };
        var seamless = ShadingRuns(d.Ops);
        for (var index = 0; index < d.Ops.Count; index++)
        {
            var op = d.Ops[index];
            canvas.Save();
            foreach (var clip in op.Clips)
            {
                using var cp = ToSkia(clip.Path, clip.EvenOdd);
                canvas.ClipPath(cp, antialias: true);
            }
            switch (op)
            {
                case PsFill f:
                {
                    using var path = ToSkia(f.Path, f.EvenOdd);
                    paint.Style = SKPaintStyle.Fill;
                    paint.PathEffect = null;
                    paint.Color = Colour(f.Colour);
                    paint.IsAntialias = !seamless[index];
                    canvas.DrawPath(path, paint);
                    paint.IsAntialias = true;
                    break;
                }
                case PsStroke s:
                {
                    using var path = ToSkia(s.Path, false);
                    canvas.Concat(Matrix(s.Matrix));
                    paint.Style = SKPaintStyle.Stroke;
                    paint.Color = Colour(s.Colour);
                    paint.StrokeWidth = (float)s.Pen.Width;
                    paint.StrokeCap = s.Pen.Cap switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
                    paint.StrokeJoin = s.Pen.Join switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
                    paint.StrokeMiter = (float)s.Pen.Miter;
                    using var dash = Dash(s.Pen);
                    paint.PathEffect = dash;
                    canvas.DrawPath(path, paint);
                    paint.PathEffect = null;
                    break;
                }
                case PsShading sh:
                {
                    if (sh.Area is { } area)
                    {
                        using var ap = ToSkia(area, sh.EvenOdd);
                        canvas.ClipPath(ap, antialias: true);
                    }
                    canvas.Concat(Matrix(sh.Matrix));
                    var colours = sh.Colours.Select(Colour).ToArray();
                    var stops = sh.Positions.Select(p => (float)p).ToArray();
                    // Extended at neither end: nothing past them; at one end or both: its colour carries on.
                    var mode = sh.ExtendStart || sh.ExtendEnd ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
                    var c = sh.Coords.Select(v => (float)v).ToArray();
                    using var shader = sh.Radial
                        ? SKShader.CreateTwoPointConicalGradient(new SKPoint(c[0], c[1]), c[2], new SKPoint(c[3], c[4]), c[5], colours, stops, mode)
                        : SKShader.CreateLinearGradient(new SKPoint(c[0], c[1]), new SKPoint(c[2], c[3]), colours, stops, mode);
                    paint.Style = SKPaintStyle.Fill;
                    paint.PathEffect = null;
                    paint.Shader = shader;
                    canvas.DrawPaint(paint);
                    paint.Shader = null;
                    break;
                }
                case PsImage img:
                {
                    // The image's unit square, its first row at the top as PostScript's image space has it.
                    canvas.Concat(Matrix(img.Matrix));
                    using var bitmap = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                    System.Runtime.InteropServices.Marshal.Copy(img.Rgba, 0, bitmap.GetPixels(), img.Rgba.Length);
                    using var image = SKImage.FromBitmap(bitmap);
                    var smooth = img.Width * img.Height > 64 * 64;
                    canvas.DrawImage(image, new SKRect(0, 0, 1, 1), new SKSamplingOptions(smooth ? SKFilterMode.Linear : SKFilterMode.Nearest));
                    break;
                }
            }
            canvas.Restore();
        }
        return recorder.EndRecording();
    }

    /// <summary>
    /// The fills that make up a gradient drawn as many bands or rings, as some programs (Poppler's pdftops) write
    /// smooth shading: a long run of fills under the same clip. Each is drawn without smoothing its edges, or the
    /// white behind shows through the seams between bands thinner than a pixel; the clip still smooths the outside.
    /// </summary>
    private static bool[] ShadingRuns(IReadOnlyList<PsOp> ops)
    {
        var seamless = new bool[ops.Count];
        for (var start = 0; start < ops.Count;)
        {
            var end = start;
            while (end < ops.Count && ops[end] is PsFill && ReferenceEquals(ops[end].Clips.LastOrDefault(), ops[start].Clips.LastOrDefault()) && ops[start].Clips.Count > 0) end++;
            if (end - start >= 32)
                for (var k = start; k < end; k++) seamless[k] = true;
            start = Math.Max(end, start + 1);
        }
        return seamless;
    }

    private static SKPath ToSkia(PsPath p, bool evenOdd)
    {
        var path = new SKPath { FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        foreach (var s in p.Segs)
            switch (s.Kind)
            {
                case SegKind.Move: path.MoveTo((float)s.X3, (float)s.Y3); break;
                case SegKind.Line: path.LineTo((float)s.X3, (float)s.Y3); break;
                case SegKind.Curve: path.CubicTo((float)s.X1, (float)s.Y1, (float)s.X2, (float)s.Y2, (float)s.X3, (float)s.Y3); break;
                default: path.Close(); break;
            }
        return path;
    }

    /// <summary>A PostScript matrix as Skia's: x' = a x + c y + e, y' = b x + d y + f.</summary>
    private static SKMatrix Matrix(PsMatrix m) => new((float)m.A, (float)m.C, (float)m.E, (float)m.B, (float)m.D, (float)m.F, 0, 0, 1);

    private static SKColor Colour(PsColour c) => new((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));

    private static SKPathEffect? Dash(PsPen pen)
    {
        if (pen.Dash.Length == 0 || pen.Dash.Sum() <= 0) return null;
        var d = pen.Dash.Length % 2 == 0 ? pen.Dash : [.. pen.Dash, .. pen.Dash];
        return SKPathEffect.CreateDash(d.Select(v => (float)Math.Max(v, 0.001)).ToArray(), (float)pen.DashOffset);
    }
}
