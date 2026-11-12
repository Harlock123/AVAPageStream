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
    static EpsRender()
    {
        Interpreter.Jpeg = DecodeJpeg;
        Interpreter.SystemGlyphs = SystemGlyph;
        Interpreter.Outliner = StrokeOutline;
    }

    /// <summary>strokepath: the outline a stroke fills, as Skia works it out.</summary>
    private static PsPath? StrokeOutline(PsPath path, PsPen pen)
    {
        using var src = ToSkia(path, false);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(pen.Width, 0.001),
            StrokeCap = pen.Cap switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt },
            StrokeJoin = pen.Join switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter },
            StrokeMiter = (float)pen.Miter,
        };
        using var dash = Dash(pen);
        paint.PathEffect = dash;
        using var outline = new SKPath();
        if (!paint.GetFillPath(src, outline)) return null;
        return FromSkia(outline, flip: false);
    }

    /// <summary>A Skia path as the interpreter's (its y turned over for glyphs, whose Skia outlines are y down).</summary>
    private static PsPath FromSkia(SKPath path, bool flip)
    {
        var f = flip ? -1f : 1f;
        var ps = new PsPath();
        using var iterator = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(pts)) != SKPathVerb.Done)
            switch (verb)
            {
                case SKPathVerb.Move: ps.Segs.Add(new PsSeg(SegKind.Move, X3: pts[0].X, Y3: f * pts[0].Y)); break;
                case SKPathVerb.Line: ps.Segs.Add(new PsSeg(SegKind.Line, X3: pts[1].X, Y3: f * pts[1].Y)); break;
                case SKPathVerb.Quad:
                    ps.Segs.Add(new PsSeg(SegKind.Curve,
                        pts[0].X + 2f / 3 * (pts[1].X - pts[0].X), f * (pts[0].Y + 2f / 3 * (pts[1].Y - pts[0].Y)),
                        pts[2].X + 2f / 3 * (pts[1].X - pts[2].X), f * (pts[2].Y + 2f / 3 * (pts[1].Y - pts[2].Y)),
                        pts[2].X, f * pts[2].Y));
                    break;
                case SKPathVerb.Conic:
                    // A conic (an arc from a stroke's round cap): close enough as a quadratic through its control point.
                    ps.Segs.Add(new PsSeg(SegKind.Curve,
                        pts[0].X + 2f / 3 * (pts[1].X - pts[0].X), f * (pts[0].Y + 2f / 3 * (pts[1].Y - pts[0].Y)),
                        pts[2].X + 2f / 3 * (pts[1].X - pts[2].X), f * (pts[2].Y + 2f / 3 * (pts[1].Y - pts[2].Y)),
                        pts[2].X, f * pts[2].Y));
                    break;
                case SKPathVerb.Cubic: ps.Segs.Add(new PsSeg(SegKind.Curve, pts[1].X, f * pts[1].Y, pts[2].X, f * pts[2].Y, pts[3].X, f * pts[3].Y)); break;
                case SKPathVerb.Close: ps.Segs.Add(new PsSeg(SegKind.Close)); break;
            }
        return ps;
    }

    private static readonly Dictionary<string, SKFont?> Typefaces = [];

    /// <summary>A glyph of a bundled Liberation font, at 1000 to the em, y up, as the interpreter wants it.</summary>
    private static SystemGlyph? SystemGlyph(string family, bool bold, bool italic, int character)
    {
        var file = family.Replace(" ", "") + "-" + (bold ? italic ? "BoldItalic" : "Bold" : italic ? "Italic" : "Regular") + ".ttf";
        SKFont? font;
        lock (Typefaces)
        {
            if (!Typefaces.TryGetValue(file, out font))
            {
                try
                {
                    using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://AVAPageStream/Assets/Fonts/" + file));
                    var typeface = SKTypeface.FromStream(stream);
                    font = typeface is null ? null : new SKFont(typeface, 1000);
                }
                catch (InvalidOperationException)
                {
                    return null; // Avalonia isn't running yet (a test): not remembered, so it is tried again
                }
                catch (Exception e) when (e is IOException or FileNotFoundException)
                {
                    font = null;
                }
                Typefaces[file] = font;
            }
        }
        if (font is null) return null;
        var glyph = font.Typeface.GetGlyph(character);
        if (glyph == 0 && character != 0)
        {
            // A character Liberation hasn't (a dingbat, a rare symbol): from a font on this computer that has it.
            var other = SKFontManager.Default.MatchCharacter(character);
            if (other is null) return null;
            using var fallback = new SKFont(other, 1000);
            var g = other.GetGlyph(character);
            if (g == 0) return null;
            return Outline(fallback, g);
        }
        return Outline(font, glyph);
    }

    private static SystemGlyph Outline(SKFont font, ushort glyph)
    {
        using var path = font.GetGlyphPath(glyph);
        var advance = font.GetGlyphWidths([glyph])[0];
        var ps = new PsPath();
        if (path is not null)
        {
            using var iterator = path.CreateRawIterator();
            var pts = new SKPoint[4];
            SKPathVerb verb;
            while ((verb = iterator.Next(pts)) != SKPathVerb.Done)
                switch (verb)
                {
                    case SKPathVerb.Move: ps.Segs.Add(new PsSeg(SegKind.Move, X3: pts[0].X, Y3: -pts[0].Y)); break;
                    case SKPathVerb.Line: ps.Segs.Add(new PsSeg(SegKind.Line, X3: pts[1].X, Y3: -pts[1].Y)); break;
                    case SKPathVerb.Quad:
                        ps.Segs.Add(new PsSeg(SegKind.Curve,
                            pts[0].X + 2f / 3 * (pts[1].X - pts[0].X), -(pts[0].Y + 2f / 3 * (pts[1].Y - pts[0].Y)),
                            pts[2].X + 2f / 3 * (pts[1].X - pts[2].X), -(pts[2].Y + 2f / 3 * (pts[1].Y - pts[2].Y)),
                            pts[2].X, -pts[2].Y));
                        break;
                    case SKPathVerb.Cubic: ps.Segs.Add(new PsSeg(SegKind.Curve, pts[1].X, -pts[1].Y, pts[2].X, -pts[2].Y, pts[3].X, -pts[3].Y)); break;
                    case SKPathVerb.Close: ps.Segs.Add(new PsSeg(SegKind.Close)); break;
                }
        }
        return new SystemGlyph(ps, advance);
    }

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

    /// <summary>
    /// How many pixels a point the coarsest picture in a drawing has, where it is drawn (its unit square's sides, in
    /// the EPS's points): what preflight checks a placed EPS by. Null if it has no pictures.
    /// </summary>
    public static double? PixelsPerPoint(PsDrawing d)
    {
        double? least = null;
        foreach (var img in d.Ops.OfType<PsImage>())
        {
            var w = Math.Sqrt(img.Matrix.A * img.Matrix.A + img.Matrix.B * img.Matrix.B);
            var h = Math.Sqrt(img.Matrix.C * img.Matrix.C + img.Matrix.D * img.Matrix.D);
            if (w <= 0 || h <= 0) continue;
            var ppp = Math.Min(img.Width / w, img.Height / h);
            least = least is { } l ? Math.Min(l, ppp) : ppp;
        }
        return least;
    }

    public static SKPicture Record(PsDrawing d)
    {
        var b = d.BoundingBox;
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, (float)b.W, (float)b.H));
        // From the EPS's own space (points, y up from its lower-left) to the picture's.
        canvas.Translate(-(float)b.X, (float)(b.Y + b.H));
        canvas.Scale(1, -1);
        canvas.ClipRect(new SKRect((float)b.X, (float)b.Y, (float)b.Right, (float)b.Bottom));
        Draw(canvas, d.Ops, null, 0);
        return recorder.EndRecording();
    }

    /// <summary>Draws a list of what an EPS paints; with <paramref name="only"/>, all of it in that colour (an uncoloured pattern's cell).</summary>
    private static void Draw(SKCanvas canvas, IReadOnlyList<PsOp> ops, PsColour? only, int depth)
    {
        using var paint = new SKPaint { IsAntialias = true };
        var seamless = ShadingRuns(ops);
        for (var index = 0; index < ops.Count; index++)
        {
            var op = ops[index];
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
                    paint.Color = Colour(only ?? f.Colour);
                    paint.IsAntialias = !seamless[index];
                    canvas.DrawPath(path, paint);
                    paint.IsAntialias = true;
                    break;
                }
                case PsStroke st:
                {
                    using var path = ToSkia(st.Path, false);
                    canvas.Concat(Matrix(st.Matrix));
                    paint.Style = SKPaintStyle.Stroke;
                    paint.Color = Colour(only ?? st.Colour);
                    paint.StrokeWidth = (float)st.Pen.Width;
                    paint.StrokeCap = st.Pen.Cap switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
                    paint.StrokeJoin = st.Pen.Join switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
                    paint.StrokeMiter = (float)st.Pen.Miter;
                    using var dash = Dash(st.Pen);
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
                case PsTiling t when depth < 4 && t.XStep != 0 && t.YStep != 0:
                {
                    // The cell recorded once, repeated by the pattern's steps through its matrix.
                    if (t.Area is { } area)
                    {
                        using var ap = ToSkia(area, t.EvenOdd);
                        canvas.ClipPath(ap, antialias: true);
                    }
                    var tile = new SKRect((float)t.BBox.X, (float)t.BBox.Y, (float)(t.BBox.X + Math.Abs(t.XStep)), (float)(t.BBox.Y + Math.Abs(t.YStep)));
                    using var recorder = new SKPictureRecorder();
                    var cellCanvas = recorder.BeginRecording(tile);
                    cellCanvas.ClipRect(new SKRect((float)t.BBox.X, (float)t.BBox.Y, (float)t.BBox.Right, (float)t.BBox.Bottom));
                    Draw(cellCanvas, t.Cell, t.Colour, depth + 1);
                    using var cell = recorder.EndRecording();
                    using var shader = SKShader.CreatePicture(cell, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKFilterMode.Linear, Matrix(t.Matrix), tile);
                    paint.Style = SKPaintStyle.Fill;
                    paint.PathEffect = null;
                    paint.Shader = shader;
                    canvas.DrawPaint(paint);
                    paint.Shader = null;
                    break;
                }
                case PsMesh mesh:
                {
                    if (mesh.Area is { } area)
                    {
                        using var ap = ToSkia(area, mesh.EvenOdd);
                        canvas.ClipPath(ap, antialias: true);
                    }
                    var points = new SKPoint[mesh.Points.Length / 2];
                    for (var k = 0; k < points.Length; k++) points[k] = new SKPoint((float)mesh.Points[2 * k], (float)mesh.Points[2 * k + 1]);
                    var colours = mesh.Colours.Select(c => Colour(only ?? c)).ToArray();
                    using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, points, colours);
                    using var vp = new SKPaint { IsAntialias = true, Color = SKColors.White };
                    canvas.DrawVertices(vertices, SKBlendMode.Dst, vp);
                    break;
                }
                case PsGroup g when depth < 32:
                {
                    using var layer = new SKPaint
                    {
                        Color = new SKColor(255, 255, 255, (byte)Math.Round(Math.Clamp(g.Alpha, 0, 1) * 255)),
                        BlendMode = Blend(g.Blend),
                    };
                    canvas.SaveLayer(layer);
                    Draw(canvas, g.Ops, only, depth + 1);
                    if (g.Mask is { } mask)
                    {
                        // The mask drawn over the group, keeping the group only as much as the mask's luminosity (or alpha) says.
                        using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
                        using var luma = mask.Luminosity ? SKColorFilter.CreateLumaColor() : null;
                        using var table = mask.Transfer is { Length: 256 } tr ? SKColorFilter.CreateTable(tr, Identity, Identity, Identity) : null;
                        using var both = luma is not null && table is not null ? SKColorFilter.CreateCompose(table, luma) : null;
                        keep.ColorFilter = both ?? table ?? luma;
                        canvas.SaveLayer(keep);
                        if (mask.Luminosity && mask.Backdrop is { } bc) canvas.DrawColor(Colour(bc));
                        Draw(canvas, mask.Ops, null, depth + 1);
                        canvas.Restore();
                    }
                    canvas.Restore();
                    break;
                }
                case PsImage img:
                {
                    // The image's unit square, its first row at the top as PostScript's image space has it.
                    canvas.Concat(Matrix(img.Matrix));
                    var rgba = img.Rgba;
                    if (only is { } oc)
                    {
                        // An uncoloured pattern's mask: its colour where the picture is painted.
                        rgba = (byte[])rgba.Clone();
                        for (var k = 0; k < rgba.Length; k += 4) (rgba[k], rgba[k + 1], rgba[k + 2]) = ((byte)Math.Round(oc.R * 255), (byte)Math.Round(oc.G * 255), (byte)Math.Round(oc.B * 255));
                    }
                    using var bitmap = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                    System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
                    using var image = SKImage.FromBitmap(bitmap);
                    var smooth = img.Width * img.Height > 64 * 64;
                    canvas.DrawImage(image, new SKRect(0, 0, 1, 1), new SKSamplingOptions(smooth ? SKFilterMode.Linear : SKFilterMode.Nearest));
                    break;
                }
            }
            canvas.Restore();
        }
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

    private static readonly byte[] Identity = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    /// <summary>A PDF blend mode as Skia's.</summary>
    private static SKBlendMode Blend(string name) => name switch
    {
        "Multiply" => SKBlendMode.Multiply,
        "Screen" => SKBlendMode.Screen,
        "Overlay" => SKBlendMode.Overlay,
        "Darken" => SKBlendMode.Darken,
        "Lighten" => SKBlendMode.Lighten,
        "ColorDodge" => SKBlendMode.ColorDodge,
        "ColorBurn" => SKBlendMode.ColorBurn,
        "HardLight" => SKBlendMode.HardLight,
        "SoftLight" => SKBlendMode.SoftLight,
        "Difference" => SKBlendMode.Difference,
        "Exclusion" => SKBlendMode.Exclusion,
        "Hue" => SKBlendMode.Hue,
        "Saturation" => SKBlendMode.Saturation,
        "Color" => SKBlendMode.Color,
        "Luminosity" => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };

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
