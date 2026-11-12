namespace AVAPageStream.Core.PostScript;

/// <summary>The graphics state: what gsave keeps and grestore puts back.</summary>
public sealed class GState
{
    public PsMatrix Ctm = PsMatrix.Identity;
    public PsPath Path = new();
    public (double X, double Y)? Point;  // in device space
    public (double X, double Y) Start;   // of the current subpath
    public string Space = "DeviceGray";
    public object SpaceArray = new PsName("DeviceGray");
    public double[] Components = [0];
    public PsColour Colour = PsColour.Black;
    public double LineWidth = 1;
    public int Cap, Join;
    public double Miter = 10;
    public double[] Dash = [];
    public double DashOffset;
    public List<PsClip> Clips = [];
    public PsDict? Font;
    public PsDict? Pattern;  // a pattern being painted with
    public PsColour? PatternColour; // for an uncoloured one, its colour
    public bool StrokeAdjust;
    public double Flat = 1;
    internal PdfState? Pdf;  // a PDF's own state, when a PDF page is being drawn

    public GState Clone()
    {
        var g = (GState)MemberwiseClone();
        g.Path = Path.Clone();
        g.Clips = [.. Clips];
        return g;
    }
}

public sealed partial class Interpreter
{
    private readonly List<GState> _gstack = [];

    public GState G { get; private set; } = new();

    /// <summary>The bounding box drawing is kept inside, in its own points (what clippath gives with no clip set).</summary>
    public Box BoundingBox { get; set; } = new(0, 0, 612, 792);

    public void Gsave() => _gstack.Add(G.Clone());

    public void GrestoreOne()
    {
        if (_gstack.Count == 0) return;
        G = _gstack[^1];
        _gstack.RemoveAt(_gstack.Count - 1);
    }

    private PsPen Pen => new(G.LineWidth, G.Cap, G.Join, G.Miter, G.Dash, G.DashOffset);

    // ---- Numbers into matrices and back

    private static PsMatrix ToMatrix(PsArray a)
    {
        if (a.Length != 6) throw new PsError("rangecheck", "matrix");
        return new PsMatrix(ToNum(a[0]), ToNum(a[1]), ToNum(a[2]), ToNum(a[3]), ToNum(a[4]), ToNum(a[5]));
    }

    private static PsArray Fill(PsArray a, PsMatrix m)
    {
        if (a.Length != 6) throw new PsError("rangecheck", "matrix");
        (a[0], a[1], a[2], a[3], a[4], a[5]) = (m.A, m.B, m.C, m.D, m.E, m.F);
        return a;
    }

    private static PsArray NewMatrix(PsMatrix m) => new([m.A, m.B, m.C, m.D, m.E, m.F]);

    private PsMatrix Inverse => G.Ctm.Inverse() ?? throw new PsError("undefinedresult", "the matrix can't be inverted");

    // ---- Paths

    private (double X, double Y) Current => G.Point ?? throw new PsError("nocurrentpoint");

    private void MoveTo(double x, double y)
    {
        if (G.Path.Segs.Count > 0 && G.Path.Segs[^1].Kind == SegKind.Move) G.Path.Segs.RemoveAt(G.Path.Segs.Count - 1);
        G.Path.Segs.Add(new PsSeg(SegKind.Move, X3: x, Y3: y));
        G.Point = G.Start = (x, y);
    }

    private void LineTo(double x, double y)
    {
        _ = Current;
        G.Path.Segs.Add(new PsSeg(SegKind.Line, X3: x, Y3: y));
        G.Point = (x, y);
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        _ = Current;
        G.Path.Segs.Add(new PsSeg(SegKind.Curve, x1, y1, x2, y2, x3, y3));
        G.Point = (x3, y3);
    }

    private void Close()
    {
        if (G.Point is null) return;
        if (G.Path.Segs.Count > 0 && G.Path.Segs[^1].Kind != SegKind.Close) G.Path.Segs.Add(new PsSeg(SegKind.Close));
        G.Point = G.Start;
    }

    /// <summary>An arc in user space, as Bézier curves of at most a quarter turn each, joined to the path by a line.</summary>
    private void Arc(double cx, double cy, double r, double a1, double a2, bool clockwise)
    {
        if (clockwise) { while (a2 > a1) a2 -= 360; }
        else { while (a2 < a1) a2 += 360; }
        var m = G.Ctm;
        (double, double) P(double a) => m.Apply(cx + r * Math.Cos(a * Math.PI / 180), cy + r * Math.Sin(a * Math.PI / 180));
        var (sx, sy) = P(a1);
        if (G.Point is null) MoveTo(sx, sy);
        else if (Math.Abs(G.Point.Value.X - sx) > 1e-9 || Math.Abs(G.Point.Value.Y - sy) > 1e-9) LineTo(sx, sy);
        var sweep = a2 - a1;
        var n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / 90 - 1e-9));
        var step = sweep / n;
        var k = 4.0 / 3 * Math.Tan(step * Math.PI / 720);
        for (var s = 0; s < n; s++)
        {
            var t0 = (a1 + s * step) * Math.PI / 180;
            var t1 = (a1 + (s + 1) * step) * Math.PI / 180;
            var (x0, y0) = (cx + r * Math.Cos(t0), cy + r * Math.Sin(t0));
            var (x3, y3) = (cx + r * Math.Cos(t1), cy + r * Math.Sin(t1));
            var (c1x, c1y) = m.Apply(x0 - k * r * Math.Sin(t0), y0 + k * r * Math.Cos(t0));
            var (c2x, c2y) = m.Apply(x3 + k * r * Math.Sin(t1), y3 - k * r * Math.Cos(t1));
            var (ex, ey) = m.Apply(x3, y3);
            CurveTo(c1x, c1y, c2x, c2y, ex, ey);
        }
    }

    /// <summary>arct and arcto: a line toward (x1, y1) and an arc of radius r turning toward (x2, y2). Returns the tangent points in user space.</summary>
    private (double, double, double, double) ArcTangent(double x1, double y1, double x2, double y2, double r)
    {
        var (x0, y0) = Inverse.Apply(Current.X, Current.Y);
        var (ax, ay, bx, by) = (x0 - x1, y0 - y1, x2 - x1, y2 - y1);
        var (la, lb) = (Math.Sqrt(ax * ax + ay * ay), Math.Sqrt(bx * bx + by * by));
        var cross = ax * by - ay * bx;
        if (la == 0 || lb == 0 || Math.Abs(cross) < 1e-12)
        {
            var (px, py) = G.Ctm.Apply(x1, y1);
            LineTo(px, py);
            return (x1, y1, x1, y1);
        }
        var cos = (ax * bx + ay * by) / (la * lb);
        var half = Math.Acos(Math.Clamp(cos, -1, 1)) / 2;
        var d = r / Math.Tan(half);
        var (t1x, t1y) = (x1 + ax / la * d, y1 + ay / la * d);
        var (t2x, t2y) = (x1 + bx / lb * d, y1 + by / lb * d);
        // The centre is r from both tangent points, on the inside of the corner.
        var (nx, ny) = cross > 0 ? (-ay / la, ax / la) : (ay / la, -ax / la);
        var (ccx, ccy) = (t1x + nx * r, t1y + ny * r);
        var start = Math.Atan2(t1y - ccy, t1x - ccx) * 180 / Math.PI;
        var end = Math.Atan2(t2y - ccy, t2x - ccx) * 180 / Math.PI;
        var (lx, ly) = G.Ctm.Apply(t1x, t1y);
        LineTo(lx, ly);
        Arc(ccx, ccy, r, start, end, clockwise: cross > 0);
        return (t1x, t1y, t2x, t2y);
    }

    private void Rect(double x, double y, double w, double h)
    {
        var m = G.Ctm;
        (double, double) P(double px, double py) => m.Apply(px, py);
        var (ax, ay) = P(x, y);
        MoveTo(ax, ay);
        var (bx, by) = P(x + w, y);
        LineTo(bx, by);
        var (cx, cy) = P(x + w, y + h);
        LineTo(cx, cy);
        var (dx, dy) = P(x, y + h);
        LineTo(dx, dy);
        Close();
    }

    /// <summary>The rectangles rectfill, rectstroke and rectclip take: four numbers, or an array of them in fours.</summary>
    private List<(double X, double Y, double W, double H)> Rects()
    {
        if (Top is PsArray a)
        {
            Pop();
            var list = new List<(double, double, double, double)>();
            for (var k = 0; k + 3 < a.Length; k += 4) list.Add((ToNum(a[k]), ToNum(a[k + 1]), ToNum(a[k + 2]), ToNum(a[k + 3])));
            return list;
        }
        if (Top is PsString) throw new PsError("unsupported", "encoded rectangles");
        var h = Num();
        var w = Num();
        var y = Num();
        var x = Num();
        return [(x, y, w, h)];
    }

    // ---- Painting

    private void FillPath(bool evenOdd)
    {
        if (!G.Path.IsEmpty)
        {
            if (G.Space == "Pattern") FillWithPattern(G.Path.Clone(), evenOdd);
            else Ops.Add(new PsFill(G.Path.Clone(), G.Colour, evenOdd, [.. G.Clips]));
        }
        NewPath();
    }

    private void StrokePath()
    {
        if (!G.Path.IsEmpty && G.Ctm.Inverse() is { } inv) Ops.Add(new PsStroke(G.Path.Transformed(inv), G.Ctm, Pen, Painted(), [.. G.Clips]));
        NewPath();
    }

    private PsColour Painted() => G.Space == "Pattern" ? throw new PsError("unsupported", "patterns") : G.Colour;

    private void NewPath()
    {
        G.Path = new PsPath();
        G.Point = null;
    }

    // ---- Colour

    private void SetSpace(object space)
    {
        var (name, array) = space switch
        {
            PsName n => (n.Value, (object)n),
            PsArray { Length: > 0 } a when a[0] is PsName n => (n.Value, a),
            _ => throw new PsError("typecheck", "setcolorspace"),
        };
        G.SpaceArray = array;
        G.Space = name switch
        {
            "DeviceGray" or "CalGray" or "G" => "DeviceGray",
            "DeviceRGB" or "CalRGB" or "RGB" => "DeviceRGB",
            "DeviceCMYK" or "CMYK" => "DeviceCMYK",
            "Indexed" or "I" => "Indexed",
            "Separation" => "Separation",
            "DeviceN" => "DeviceN",
            "ICCBased" => "ICCBased",
            "Pattern" => "Pattern",
            "CIEBasedABC" or "CIEBasedDEF" => name,
            "CIEBasedA" => name,
            "CIEBasedDEFG" => name,
            "Lab" => "Lab",
            _ => throw new PsError("unsupported", "colour space " + name),
        };
        SetComponents(G.Space switch
        {
            "DeviceRGB" => [0, 0, 0],
            "DeviceCMYK" => [0, 0, 0, 1],
            "Separation" => [1],
            "DeviceN" => Enumerable.Repeat(1.0, ((PsArray)((PsArray)array)[1]).Length).ToArray(),
            "ICCBased" => new double[IccComponents((PsArray)array)],
            "CIEBasedABC" or "CIEBasedDEF" or "Lab" => [0, 0, 0],
            "CIEBasedDEFG" => [0, 0, 0, 0],
            _ => [0],
        });
    }

    private static int IccComponents(PsArray space) =>
        space.Length > 1 && space[1] is PsDict d && d.Get("N") is { } n ? (int)ToNum(n) : 3;

    /// <summary>Sets the colour from its components in the current space.</summary>
    private void SetComponents(double[] c)
    {
        G.Components = c;
        G.Colour = ToRgb(G.Space, G.SpaceArray, c);
    }

    private PsColour ToRgb(string space, object array, double[] c)
    {
        double At(int k) => k < c.Length ? Math.Clamp(c[k], 0, 1) : 0;
        switch (space)
        {
            case "DeviceGray": return new PsColour(At(0), At(0), At(0));
            case "DeviceRGB": return new PsColour(At(0), At(1), At(2));
            case "DeviceCMYK":
                return new PsColour(1 - Math.Min(1, At(0) + At(3)), 1 - Math.Min(1, At(1) + At(3)), 1 - Math.Min(1, At(2) + At(3)));
            case "ICCBased":
                var n = IccComponents((PsArray)array);
                return ToRgb(n switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" }, new PsName("x"), c);
            case "Indexed":
            {
                var a = (PsArray)array;
                var (baseName, baseArray) = a[1] switch { PsName bn => (bn.Value, (object)bn), PsArray ba when ba[0] is PsName bn2 => (bn2.Value, ba), _ => throw new PsError("typecheck", "Indexed") };
                baseName = baseName switch { "CalRGB" or "RGB" => "DeviceRGB", "CalGray" or "G" => "DeviceGray", "CMYK" => "DeviceCMYK", _ => baseName };
                var count = baseName switch { "DeviceGray" => 1, "DeviceCMYK" => 4, "ICCBased" => IccComponents((PsArray)baseArray), _ => 3 };
                var hival = (int)ToNum(a[2]);
                var index = Math.Clamp((int)Math.Round(c.Length > 0 ? c[0] : 0), 0, hival);
                var comps = new double[count];
                if (a[3] is PsString table)
                {
                    for (var k = 0; k < count && index * count + k < table.Length; k++) comps[k] = table[index * count + k] / 255.0;
                }
                else if (a[3] is PsArray { Exec: true } proc)
                {
                    Push(index);
                    RunProc(proc);
                    for (var k = count - 1; k >= 0; k--) comps[k] = Num();
                }
                return ToRgb(baseName, baseArray, comps);
            }
            case "CIEBasedA" or "CIEBasedABC" or "CIEBasedDEF" or "CIEBasedDEFG":
                return Cie(space, (PsArray)array, c);
            case "Lab":
                return Lab(array, c);
            case "Separation" or "DeviceN":
            {
                // The colour its alternative space gives: the tint run through the tint transform.
                var a = (PsArray)array;
                var alt = a[2];
                var altName = alt switch { PsName an => an.Value, PsArray aa when aa[0] is PsName an2 => an2.Value, _ => "DeviceGray" };
                altName = altName switch { "CalRGB" => "DeviceRGB", "CalGray" => "DeviceGray", _ => altName };
                if (space == "Separation" && a[1] is PsName { Value: "None" }) return new PsColour(1, 1, 1);
                var count = altName switch { "DeviceGray" => 1, "DeviceCMYK" => 4, "ICCBased" => IccComponents((PsArray)alt), _ => 3 };
                foreach (var v in c) Push(v);
                RunProc((PsArray)a[3]);
                var comps = new double[count];
                for (var k = count - 1; k >= 0; k--) comps[k] = Num();
                return ToRgb(altName, alt, comps);
            }
            default:
                return PsColour.Black;
        }
    }

    /// <summary>
    /// A PDF Lab colour (L* from 0 to 100, a* and b* within its Range) as sRGB: through XYZ, at its white point
    /// (D50 if it gives none), adapted to D65 by Bradford.
    /// </summary>
    private static PsColour Lab(object array, double[] c)
    {
        var d = array is PsArray a && a.Length > 1 ? a[1] as PsDict : null;
        var white = (d?.Get("WhitePoint") as PsArray)?.All().Select(ToNum).ToArray() is { Length: 3 } wp ? wp : [0.9642, 1, 0.8249];
        var range = (d?.Get("Range") as PsArray)?.All().Select(ToNum).ToArray() is { Length: 4 } r ? r : [-100, 100, -100, 100];
        var l = Math.Clamp(c.Length > 0 ? c[0] : 0, 0, 100);
        var aa = Math.Clamp(c.Length > 1 ? c[1] : 0, range[0], range[1]);
        var bb = Math.Clamp(c.Length > 2 ? c[2] : 0, range[2], range[3]);
        static double G(double x) => x >= 6.0 / 29 ? x * x * x : 108.0 / 841 * (x - 4.0 / 29);
        var m = (l + 16) / 116;
        var (x, y, z) = (white[0] * G(m + aa / 500), white[1] * G(m), white[2] * G(m - bb / 200));
        // Bradford from the white point to D65, then XYZ to linear sRGB.
        var (x2, y2, z2) = (x * 0.9555766 - y * 0.0230393 + z * 0.0631636, -x * 0.0282895 + y * 1.0099416 + z * 0.0210077, x * 0.0122982 - y * 0.0204830 + z * 1.3299098);
        if (Math.Abs(white[0] - 0.9642) > 0.01 || Math.Abs(white[2] - 0.8249) > 0.01) (x2, y2, z2) = (x * 0.9505 / white[0], y, z * 1.089 / white[2]);
        var rl = 3.2404542 * x2 - 1.5371385 * y2 - 0.4985314 * z2;
        var gl = -0.9692660 * x2 + 1.8760108 * y2 + 0.0415560 * z2;
        var bl = 0.0556434 * x2 - 0.2040259 * y2 + 1.0572252 * z2;
        static double Gamma(double v) => Math.Clamp(v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055, 0, 1);
        return new PsColour(Gamma(rl), Gamma(gl), Gamma(bl));
    }

    private static PsColour Hsb(double h, double s, double b)
    {
        h = (h - Math.Floor(h)) * 6;
        var i = (int)Math.Floor(h) % 6;
        var f = h - Math.Floor(h);
        var (p, q, t) = (b * (1 - s), b * (1 - s * f), b * (1 - s * (1 - f)));
        return i switch { 0 => new(b, t, p), 1 => new(q, b, p), 2 => new(p, b, t), 3 => new(p, q, b), 4 => new(t, p, b), _ => new(b, p, q) };
    }

    private void RegisterGraphics()
    {
        // ---- The graphics state
        Op("gsave", i => i.Gsave());
        Op("grestore", i => i.GrestoreOne());
        Op("grestoreall", i => { while (i._gstack.Count > 0) i.GrestoreOne(); });
        Op("initgraphics", i => { var font = i.G.Font; i.G = new GState { Font = font }; });
        Op("setlinewidth", i => i.G.LineWidth = Math.Abs(i.Num()));
        Op("currentlinewidth", i => i.Push(i.G.LineWidth));
        Op("setlinecap", i => i.G.Cap = Math.Clamp(i.Int(), 0, 2));
        Op("currentlinecap", i => i.Push(i.G.Cap));
        Op("setlinejoin", i => i.G.Join = Math.Clamp(i.Int(), 0, 2));
        Op("currentlinejoin", i => i.Push(i.G.Join));
        Op("setmiterlimit", i => i.G.Miter = Math.Max(1, i.Num()));
        Op("currentmiterlimit", i => i.Push(i.G.Miter));
        Op("setdash", i =>
        {
            var offset = i.Num();
            var a = i.Pop<PsArray>("setdash");
            i.G.Dash = a.All().Select(ToNum).ToArray();
            i.G.DashOffset = offset;
        });
        Op("currentdash", i => { i.Push(new PsArray(i.G.Dash.Select(d => (object)d).ToList())); i.Push(i.G.DashOffset); });
        Op("setflat", i => i.G.Flat = i.Num());
        Op("currentflat", i => i.Push(i.G.Flat));
        Op("setstrokeadjust", i => i.G.StrokeAdjust = i.Bool());
        Op("currentstrokeadjust", i => i.Push(i.G.StrokeAdjust));
        Op("setoverprint", i => i.Pop());
        Op("currentoverprint", i => i.Push(false));
        Op("setsmoothness", i => i.Pop());
        Op("currentsmoothness", i => i.Push(0.02));

        // ---- The current transformation
        Op("matrix", i => i.Push(NewMatrix(PsMatrix.Identity)));
        Op("identmatrix", i => i.Push(Fill(i.Pop<PsArray>("identmatrix"), PsMatrix.Identity)));
        Op("currentmatrix", i => i.Push(Fill(i.Pop<PsArray>("currentmatrix"), i.G.Ctm)));
        Op("defaultmatrix", i => i.Push(Fill(i.Pop<PsArray>("defaultmatrix"), PsMatrix.Identity)));
        Op("setmatrix", i => i.G.Ctm = ToMatrix(i.Pop<PsArray>("setmatrix")));
        Op("initmatrix", i => i.G.Ctm = PsMatrix.Identity);
        Op("translate", i =>
        {
            PsArray? m = i.Top is PsArray a ? (PsArray)i.Pop() : null;
            var ty = i.Num();
            var tx = i.Num();
            var t = new PsMatrix(1, 0, 0, 1, tx, ty);
            if (m is not null) i.Push(Fill(m, t)); else i.G.Ctm = t.Then(i.G.Ctm);
        });
        Op("scale", i =>
        {
            PsArray? m = i.Top is PsArray a ? (PsArray)i.Pop() : null;
            var sy = i.Num();
            var sx = i.Num();
            var t = new PsMatrix(sx, 0, 0, sy, 0, 0);
            if (m is not null) i.Push(Fill(m, t)); else i.G.Ctm = t.Then(i.G.Ctm);
        });
        Op("rotate", i =>
        {
            PsArray? m = i.Top is PsArray a ? (PsArray)i.Pop() : null;
            var r = i.Num() * Math.PI / 180;
            var t = new PsMatrix(Math.Cos(r), Math.Sin(r), -Math.Sin(r), Math.Cos(r), 0, 0);
            if (m is not null) i.Push(Fill(m, t)); else i.G.Ctm = t.Then(i.G.Ctm);
        });
        Op("concat", i => i.G.Ctm = ToMatrix(i.Pop<PsArray>("concat")).Then(i.G.Ctm));
        Op("concatmatrix", i =>
        {
            var m3 = i.Pop<PsArray>("concatmatrix");
            var m2 = ToMatrix(i.Pop<PsArray>("concatmatrix"));
            var m1 = ToMatrix(i.Pop<PsArray>("concatmatrix"));
            i.Push(Fill(m3, m1.Then(m2)));
        });
        Op("invertmatrix", i =>
        {
            var m2 = i.Pop<PsArray>("invertmatrix");
            var m1 = ToMatrix(i.Pop<PsArray>("invertmatrix"));
            i.Push(Fill(m2, m1.Inverse() ?? throw new PsError("undefinedresult", "invertmatrix")));
        });
        void Point(string name, Func<PsMatrix, double, double, (double, double)> f, bool inverse) => Op(name, i =>
        {
            var m = i.Top is PsArray a ? ToMatrix((PsArray)i.Pop()) : i.G.Ctm;
            if (inverse) m = m.Inverse() ?? throw new PsError("undefinedresult", name);
            var y = i.Num();
            var x = i.Num();
            var (rx, ry) = f(m, x, y);
            i.Push(rx);
            i.Push(ry);
        });
        Point("transform", (m, x, y) => m.Apply(x, y), false);
        Point("itransform", (m, x, y) => m.Apply(x, y), true);
        Point("dtransform", (m, x, y) => m.Delta(x, y), false);
        Point("idtransform", (m, x, y) => m.Delta(x, y), true);

        // ---- Paths
        Op("newpath", i => i.NewPath());
        Op("moveto", i => { var y = i.Num(); var x = i.Num(); var (dx, dy) = i.G.Ctm.Apply(x, y); i.MoveTo(dx, dy); });
        Op("rmoveto", i => { var y = i.Num(); var x = i.Num(); var (cx, cy) = i.Current; var (dx, dy) = i.G.Ctm.Delta(x, y); i.MoveTo(cx + dx, cy + dy); });
        Op("lineto", i => { var y = i.Num(); var x = i.Num(); var (dx, dy) = i.G.Ctm.Apply(x, y); i.LineTo(dx, dy); });
        Op("rlineto", i => { var y = i.Num(); var x = i.Num(); var (cx, cy) = i.Current; var (dx, dy) = i.G.Ctm.Delta(x, y); i.LineTo(cx + dx, cy + dy); });
        Op("curveto", i =>
        {
            var y3 = i.Num(); var x3 = i.Num(); var y2 = i.Num(); var x2 = i.Num(); var y1 = i.Num(); var x1 = i.Num();
            var m = i.G.Ctm;
            var (a, b) = m.Apply(x1, y1);
            var (c, d) = m.Apply(x2, y2);
            var (e, f) = m.Apply(x3, y3);
            i.CurveTo(a, b, c, d, e, f);
        });
        Op("rcurveto", i =>
        {
            var y3 = i.Num(); var x3 = i.Num(); var y2 = i.Num(); var x2 = i.Num(); var y1 = i.Num(); var x1 = i.Num();
            var (cx, cy) = i.Current;
            var m = i.G.Ctm;
            var (a, b) = m.Delta(x1, y1);
            var (c, d) = m.Delta(x2, y2);
            var (e, f) = m.Delta(x3, y3);
            i.CurveTo(cx + a, cy + b, cx + c, cy + d, cx + e, cy + f);
        });
        Op("arc", i => { var a2 = i.Num(); var a1 = i.Num(); var r = i.Num(); var y = i.Num(); var x = i.Num(); i.Arc(x, y, r, a1, a2, false); });
        Op("arcn", i => { var a2 = i.Num(); var a1 = i.Num(); var r = i.Num(); var y = i.Num(); var x = i.Num(); i.Arc(x, y, r, a1, a2, true); });
        Op("arct", i => { var r = i.Num(); var y2 = i.Num(); var x2 = i.Num(); var y1 = i.Num(); var x1 = i.Num(); i.ArcTangent(x1, y1, x2, y2, r); });
        Op("arcto", i =>
        {
            var r = i.Num(); var y2 = i.Num(); var x2 = i.Num(); var y1 = i.Num(); var x1 = i.Num();
            var (a, b, c, d) = i.ArcTangent(x1, y1, x2, y2, r);
            i.Push(a); i.Push(b); i.Push(c); i.Push(d);
        });
        Op("closepath", i => i.Close());
        Op("currentpoint", i => { var (x, y) = i.Inverse.Apply(i.Current.X, i.Current.Y); i.Push(x); i.Push(y); });
        Op("pathbbox", i =>
        {
            if (i.G.Path.Transformed(i.Inverse).Bounds() is not var (x0, y0, x1, y1)) throw new PsError("nocurrentpoint", "pathbbox");
            i.Push(x0); i.Push(y0); i.Push(x1); i.Push(y1);
        });
        Op("flattenpath", _ => { });
        Op("reversepath", _ => { });
        Op("setbbox", i => { i.Num(); i.Num(); i.Num(); i.Num(); });
        Op("strokepath", i =>
        {
            // The outline a stroke would fill, worked out in user space (the pen turns with the matrix) by the program's outliner.
            if (i.G.Path.IsEmpty) return;
            if (i.StrokeOutliner is null) throw new PsError("unsupported", "strokepath");
            var inv = i.Inverse;
            var outline = i.StrokeOutliner(i.G.Path.Transformed(inv), i.Pen) ?? throw new PsError("unsupported", "strokepath");
            i.G.Path = outline.Transformed(i.G.Ctm);
            i.G.Point = null;
        });
        Op("pathforall", i =>
        {
            var close = i.Proc(); var curve = i.Proc(); var line = i.Proc(); var move = i.Proc();
            var inv = i.Inverse;
            try
            {
                foreach (var s in i.G.Path.Segs.ToList())
                {
                    void P(double x, double y) { var (ux, uy) = inv.Apply(x, y); i.Push(ux); i.Push(uy); }
                    switch (s.Kind)
                    {
                        case SegKind.Move: P(s.X3, s.Y3); i.RunProc(move); break;
                        case SegKind.Line: P(s.X3, s.Y3); i.RunProc(line); break;
                        case SegKind.Curve: P(s.X1, s.Y1); P(s.X2, s.Y2); P(s.X3, s.Y3); i.RunProc(curve); break;
                        default: i.RunProc(close); break;
                    }
                }
            }
            catch (PsExit) { }
        });
        Op("clippath", i =>
        {
            if (i.G.Clips.Count > 0) { i.G.Path = i.G.Clips[^1].Path.Clone(); i.G.Point = null; return; }
            var b = i.BoundingBox;
            var keep = i.G.Ctm;
            i.G.Ctm = PsMatrix.Identity;
            i.NewPath();
            i.Rect(b.X, b.Y, b.W, b.H);
            i.G.Ctm = keep;
        });

        // ---- Painting and clipping
        Op("fill", i => i.FillPath(false));
        Op("eofill", i => i.FillPath(true));
        Op("stroke", i => i.StrokePath());
        Op("clip", i => i.G.Clips.Add(new PsClip(i.G.Path.Clone(), false)));
        Op("eoclip", i => i.G.Clips.Add(new PsClip(i.G.Path.Clone(), true)));
        Op("initclip", i => i.G.Clips = []);
        Op("rectfill", i =>
        {
            var keep = (i.G.Path, i.G.Point, i.G.Start);
            i.G.Path = new PsPath();
            foreach (var (x, y, w, h) in i.Rects()) i.Rect(x, y, w, h);
            i.FillPath(false);
            (i.G.Path, i.G.Point, i.G.Start) = keep;
        });
        Op("rectstroke", i =>
        {
            var m = i.Top is PsArray { Length: 6 } a && a.All().All(o => o is int or double) && i._stack.Count >= 5 && i._stack[^2] is int or double ? ToMatrix((PsArray)i.Pop()) : (PsMatrix?)null;
            var keep = (i.G.Path, i.G.Point, i.G.Start, i.G.Ctm);
            i.G.Path = new PsPath();
            var rects = i.Rects();
            if (m is { } mm) i.G.Ctm = mm.Then(i.G.Ctm);
            foreach (var (x, y, w, h) in rects) i.Rect(x, y, w, h);
            i.StrokePath();
            (i.G.Path, i.G.Point, i.G.Start, i.G.Ctm) = keep;
        });
        Op("rectclip", i =>
        {
            i.NewPath();
            foreach (var (x, y, w, h) in i.Rects()) i.Rect(x, y, w, h);
            i.G.Clips.Add(new PsClip(i.G.Path.Clone(), false));
            i.NewPath();
        });

        // ---- Colour
        Op("setgray", i => { i.SetSpace(new PsName("DeviceGray")); i.SetComponents([i.Num()]); });
        Op("setrgbcolor", i => { var b = i.Num(); var g = i.Num(); var r = i.Num(); i.SetSpace(new PsName("DeviceRGB")); i.SetComponents([r, g, b]); });
        Op("setcmykcolor", i => { var k = i.Num(); var y = i.Num(); var m = i.Num(); var c = i.Num(); i.SetSpace(new PsName("DeviceCMYK")); i.SetComponents([c, m, y, k]); });
        Op("sethsbcolor", i =>
        {
            var b = i.Num(); var s = i.Num(); var h = i.Num();
            var rgb = Hsb(h, s, b);
            i.SetSpace(new PsName("DeviceRGB"));
            i.SetComponents([rgb.R, rgb.G, rgb.B]);
        });
        Op("currentgray", i => { var c = i.G.Colour; i.Push(0.3 * c.R + 0.59 * c.G + 0.11 * c.B); });
        Op("currentrgbcolor", i => { var c = i.G.Colour; i.Push(c.R); i.Push(c.G); i.Push(c.B); });
        Op("currenthsbcolor", i =>
        {
            var c = i.G.Colour;
            var max = Math.Max(c.R, Math.Max(c.G, c.B));
            var min = Math.Min(c.R, Math.Min(c.G, c.B));
            var d = max - min;
            var h = d == 0 ? 0 : max == c.R ? ((c.G - c.B) / d % 6) / 6 : max == c.G ? ((c.B - c.R) / d + 2) / 6 : ((c.R - c.G) / d + 4) / 6;
            i.Push(h < 0 ? h + 1 : h); i.Push(max == 0 ? 0 : d / max); i.Push(max);
        });
        Op("currentcmykcolor", i =>
        {
            if (i.G.Space == "DeviceCMYK") { foreach (var v in i.G.Components) i.Push(v); return; }
            var c = i.G.Colour;
            var k = 1 - Math.Max(c.R, Math.Max(c.G, c.B));
            i.Push(1 - c.R - k); i.Push(1 - c.G - k); i.Push(1 - c.B - k); i.Push(k);
        });
        Op("setcolorspace", i => i.SetSpace(i.Pop()));
        Op("currentcolorspace", i => i.Push(i.G.SpaceArray is PsArray a ? a : new PsArray([i.G.SpaceArray])));
        Op("setcolor", i =>
        {
            if (i.G.Space == "Pattern")
            {
                i.G.Pattern = i.Pop<PsDict>("setcolor");
                // An uncoloured pattern's colour, in the space under it: [/Pattern /DeviceRGB].
                if (i.G.SpaceArray is PsArray { Length: > 1 } under)
                {
                    var keep = (i.G.Space, i.G.SpaceArray, i.G.Components, i.G.Colour);
                    i.SetSpace(under[1]);
                    var count = i.G.Components.Length;
                    var cs = new double[count];
                    for (var k = count - 1; k >= 0; k--) cs[k] = i.Num();
                    var colour = i.ToRgb(i.G.Space, i.G.SpaceArray, cs);
                    (i.G.Space, i.G.SpaceArray, i.G.Components, i.G.Colour) = keep;
                    i.G.PatternColour = colour;
                }
                return;
            }
            var n = i.G.Space switch { "DeviceRGB" or "CIEBasedABC" or "CIEBasedDEF" or "Lab" => 3, "DeviceCMYK" or "CIEBasedDEFG" => 4, "DeviceN" => ((PsArray)((PsArray)i.G.SpaceArray)[1]).Length, "ICCBased" => IccComponents((PsArray)i.G.SpaceArray), _ => 1 };
            var c = new double[n];
            for (var k = n - 1; k >= 0; k--) c[k] = i.Num();
            i.SetComponents(c);
        });
        Op("currentcolor", i => { foreach (var v in i.G.Components) i.Push(v); });
        RegisterShading();

        // ---- The device: an EPS draws into a picture, so these do nothing
        Op("showpage", _ => { });
        Op("pdfmark", i => { while (i.Pop() is not PsMark) { } }); // marks for PDF makers: nothing to draw
        Op("copypage", _ => { });
        Op("erasepage", _ => { });
        Op("nulldevice", _ => { });
        Op("setpagedevice", i => i.Pop());
        Op("currentpagedevice", i =>
        {
            var d = new PsDict();
            d.Put("PageSize", new PsArray([i.BoundingBox.Right, i.BoundingBox.Bottom]));
            d.Put("HWResolution", new PsArray([72.0, 72.0]));
            d.Put("Policies", new PsDict());
            i.Push(d);
        });
        Op("setscreen", i => { i.Pop(); i.Pop(); i.Pop(); });
        Op("currentscreen", i => { i.Push(60.0); i.Push(0.0); i.Push(new PsArray([], exec: true)); });
        Op("settransfer", i => i.Pop());
        Op("currenttransfer", i => i.Push(new PsArray([], exec: true)));
        Op("setcolortransfer", i => { i.Pop(); i.Pop(); i.Pop(); i.Pop(); });
        Op("currentcolortransfer", i => { for (var k = 0; k < 4; k++) i.Push(new PsArray([], exec: true)); });
        Op("setblackgeneration", i => i.Pop());
        Op("currentblackgeneration", i => i.Push(new PsArray([], exec: true)));
        Op("setundercolorremoval", i => i.Pop());
        Op("currentundercolorremoval", i => i.Push(new PsArray([], exec: true)));
        Op("sethalftone", i => i.Pop());
        Op("currenthalftone", i => { var d = new PsDict(); d.Put("HalftoneType", 1); i.Push(d); });
        Op("setcolorrendering", i => i.Pop());
        Op("currentcolorrendering", i => i.Push(new PsDict()));
        Op("findcolorrendering", i => { i.Pop(); i.Push(new PsName("Default")); i.Push(false); });
        Op("setcolorscreen", i => { for (var k = 0; k < 12; k++) i.Pop(); });
        Op("setdevparams", i => { i.Pop(); i.Pop(); });
        Op("setuserparams", i => i.Pop());
        Op("currentuserparams", i => i.Push(new PsDict()));
        Op("setsystemparams", i => i.Pop());
        Op("currentsystemparams", i => i.Push(new PsDict()));

        Op("setcachelimit", i => i.Pop());
        Op("setucacheparams", i => { while (i.Pop() is not PsMark) { } });
        Op("resourcestatus", i => { i.Pop(); i.Pop(); i.Push(false); });
        Op("findresource", i =>
        {
            var category = i.Pop<PsName>("findresource").Value;
            var key = i.Pop();
            if (category == "ProcSet" && Show(key) == "CIDInit") { i.Push(i.CidInit()); return; }
            if (category == "ProcSet" && Show(key) == "FontSetInit") { i.Push(i.FontSetInit()); return; }
            if (category == "ProcSet" && i.Lookup(Show(key)) is PsDict known) { i.Push(known); return; }
            if (category == "CMap")
            {
                if (i.Resources("CMap").Get(key) is PsDict cm) { i.Push(cm); return; }
                var made = new PsDict();
                made.Put("CMapName", new PsName(Show(key)));
                made.Put(CMapKey, i.CMapOf(key));
                i.Push(made);
                return;
            }
            if (category == "CIDFont") { i.Push(i.Resources("CIDFont").Get(key) ?? throw new PsError("undefinedresource", Show(key))); return; }
            if (category is "ColorSpace" or "Category" or "ProcSet" or "Encoding")
            {
                i.Push(i.Resources(category).Get(key) ?? throw new PsError("undefinedresource", Show(key)));
                return;
            }
            if (category == "Font") { i.Push(i.FindFont(key)); return; }
            throw new PsError("undefinedresource", Show(key));
        });
        Op("defineresource", i =>
        {
            var category = i.Pop<PsName>("defineresource").Value;
            var instance = i.Pop();
            var key = i.Pop();
            if (instance is PsDict fontLike && category is "CIDFont" or "Font" && fontLike.Get("FID") is null) fontLike.Put("FID", new PsFontId(Show(key)));
            if (category == "Font") i.FontDirectory.Put(key, instance);
            i.Resources(category).Put(key, instance);
            i.Push(instance);
        });
        Op("undefineresource", i => { var category = i.Pop<PsName>("undefineresource").Value; i.Resources(category).Remove(i.Pop()); });

        RegisterFonts();

        RegisterImages();
    }

    private readonly Dictionary<string, PsDict> _resources = [];

    private PsDict Resources(string category)
    {
        if (!_resources.TryGetValue(category, out var d)) _resources[category] = d = new PsDict();
        return d;
    }
}
