namespace AVAPageStream.Core.PostScript;

/// <summary>
/// What a PDF's graphics state has that PostScript's doesn't: a stroking colour of its own, opacity, blending and a
/// soft mask, and the text state. Kept in <see cref="GState.Pdf"/>, so q and Q keep and restore it with the rest.
/// </summary>
internal sealed record PdfState
{
    public string StrokeSpace { get; init; } = "DeviceGray";
    public object StrokeSpaceArray { get; init; } = new PsName("DeviceGray");
    public double[] StrokeComponents { get; init; } = [0];
    public PsColour StrokeColour { get; init; } = PsColour.Black;
    public PsDict? StrokePattern { get; init; }
    public PsColour? StrokePatternColour { get; init; }
    public double FillAlpha { get; init; } = 1;
    public double StrokeAlpha { get; init; } = 1;
    public string Blend { get; init; } = "Normal";
    public PsMask? Mask { get; init; }
    public double CharSpace { get; init; }
    public double WordSpace { get; init; }
    public double Scale { get; init; } = 1;
    public double Leading { get; init; }
    public double Rise { get; init; }
    public int Render { get; init; }
    public PdfFont? Font { get; init; }
    public double FontSize { get; init; }
}

/// <summary>
/// Drawing a PDF page with the interpreter's graphics: its content stream's operators, its forms, pictures, shadings,
/// patterns and text, into <see cref="Ops"/> as an EPS's drawing is. Like a viewer, it passes over an operator that is
/// damaged and carries on; what it can't draw it notes in <see cref="PdfNotes"/>.
/// </summary>
public sealed partial class Interpreter
{
    private PdfReader? _pdf;
    private PsMatrix _tm = PsMatrix.Identity, _tlm = PsMatrix.Identity;
    private PsPath? _textClip;
    private bool? _clipPending;      // W or W*: the path clips once it is painted
    private PsMatrix _patternBase;   // pattern space: the page's (or the form's) own
    private int _pdfDepth;
    private long _pdfOps;
    private int _hidden;             // inside optional content that is turned off
    private readonly Stack<bool> _marked = new();
    private HashSet<object>? _offGroups;
    private bool _uncolouredGlyph;   // in a Type 3 glyph begun with d1: its colour is the text's
    private readonly List<string> _pdfNotes = [];

    /// <summary>What a PDF page uses that wasn't drawn (or was drawn some other way), each said once.</summary>
    public IReadOnlyList<string> PdfNotes => _pdfNotes;

    private void Note(string what)
    {
        if (!_pdfNotes.Contains(what)) _pdfNotes.Add(what);
    }

    private PdfState PState
    {
        get => G.Pdf ??= new PdfState();
        set => G.Pdf = value;
    }

    /// <summary>
    /// Draws a page of a PDF, turned as its Rotate says, into its box (the crop box, else the media box) from (0, 0).
    /// Returns the box.
    /// </summary>
    public Box RunPdfPage(PdfReader reader, int index)
    {
        _pdf = reader;
        var page = reader.Pages[index];
        var box = reader.PageBox(page);
        var rotate = ((int)PdfNum(reader.Deep(page.Get("Rotate"))) % 360 + 360) % 360 / 90 * 90;
        var (w, h) = rotate is 90 or 270 ? (box.H, box.W) : (box.W, box.H);
        var start = rotate switch
        {
            90 => new PsMatrix(0, -1, 1, 0, -box.Y, box.Right),
            180 => new PsMatrix(-1, 0, 0, -1, box.Right, box.Bottom),
            270 => new PsMatrix(0, 1, -1, 0, box.Bottom, -box.X),
            _ => new PsMatrix(1, 0, 0, 1, -box.X, -box.Y),
        };
        BoundingBox = new Box(0, 0, w, h);
        G = new GState { Ctm = start };
        _patternBase = start;
        _offGroups = OffGroups(reader);
        var resources = reader.Deep(page.Get("Resources")) as PsDict ?? new PsDict();
        RunPdf(reader.Contents(page), resources);
        DrawAnnotations(reader, page, start);
        return BoundingBox;
    }

    /// <summary>The optional content groups the document's default view turns off.</summary>
    private static HashSet<object> OffGroups(PdfReader r)
    {
        var off = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (r.Fetch(r.Trailer.Get("Root")) is PsDict catalog && r.Deep(catalog.Get("OCProperties")) is PsDict oc && oc.Get("D") is PsDict d && d.Get("OFF") is PsArray a)
            foreach (var g in a.All().OfType<PsDict>()) off.Add(g);
        return off;
    }

    /// <summary>Whether optional content (a group, or a membership dictionary of groups) is turned off.</summary>
    private bool IsOff(object? oc)
    {
        if (_offGroups is not { Count: > 0 } off || oc is not PsDict d) return false;
        if (PdfName(d.Get("Type")) != "OCMD") return off.Contains(d);
        var groups = d.Get("OCGs") switch { PsDict one => [one], PsArray many => many.All().OfType<PsDict>().ToList(), _ => new List<PsDict>() };
        if (groups.Count == 0) return false;
        return PdfName(d.Get("P")) switch
        {
            "AllOn" => groups.Any(off.Contains),
            "AnyOff" => !groups.Any(off.Contains),
            "AllOff" => !groups.All(off.Contains),
            _ => groups.All(off.Contains), // AnyOn
        };
    }

    /// <summary>
    /// Runs a content stream with its resources. A q left open at its end is closed, and a Q with no q of its own
    /// is passed over, so one stream can't upset the state of the one that called it.
    /// </summary>
    private void RunPdf(byte[] content, PsDict res)
    {
        var lex = new PdfLexer(content);
        var args = new List<object>(8);
        var level = _gstack.Count;
        while (true)
        {
            object? o;
            try { o = lex.ReadObject(); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { break; }
            if (o is null) break;
            if (o is not PdfKeyword k)
            {
                args.Add(o);
                if (args.Count > 256) args.RemoveAt(0);
                continue;
            }
            if (++_pdfOps > 20_000_000) { Note("it is too long to draw all of it"); break; }
            var before = Ops.Count;
            try
            {
                if (k.Value == "BI") InlineImage(lex, res);
                else PdfOperator(k.Value, args, res, level);
            }
            catch (PsError e) when (e.Name == "unsupported") { Note($"it uses {e.Message.Replace("unsupported in ", "")}, which isn't drawn yet"); }
            catch (PdfException e) { Note(e.Message); }
            catch (Exception e) when (e is PsError or InvalidCastException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException or InvalidDataException or OverflowException)
            {
                // A damaged operator: passed over, as viewers do.
                if (Environment.GetEnvironmentVariable("AVAPS_PDF_DEBUG") is not null) Note($"{k.Value}: {e}");
            }
            if (_hidden > 0 && Ops.Count > before) Ops.RemoveRange(before, Ops.Count - before);
            args.Clear();
        }
        while (_gstack.Count > level) GrestoreOne();
    }

    private static double Arg(List<object> args, int k) => k < args.Count ? PdfNum(args[k]) : throw new PsError("stackunderflow");

    private static PsMatrix MatrixOf(List<object> args) =>
        args.Count >= 6 ? new PsMatrix(Arg(args, args.Count - 6), Arg(args, args.Count - 5), Arg(args, args.Count - 4), Arg(args, args.Count - 3), Arg(args, args.Count - 2), Arg(args, args.Count - 1)) : throw new PsError("stackunderflow");

    /// <summary>The numbers at the end of the operands (the last <paramref name="n"/>, or all of them).</summary>
    private static double[] Last(List<object> args, int n = -1)
    {
        var nums = args.Where(a => a is int or double).Select(ToNum).ToList();
        return n < 0 || nums.Count <= n ? [.. nums] : [.. nums.Skip(nums.Count - n)];
    }

    private static PsDict? Resource(PsDict res, string category, object? name) =>
        name is PsName n && res.Get(category) is PsDict d ? d.Get(n.Value) as PsDict : null;

    private void PdfOperator(string op, List<object> a, PsDict res, int level)
    {
        switch (op)
        {
            // ---- State
            case "q": Gsave(); break;
            case "Q": if (_gstack.Count > level) GrestoreOne(); break;
            case "cm": G.Ctm = MatrixOf(a).Then(G.Ctm); break;
            case "w": G.LineWidth = Math.Abs(Arg(a, a.Count - 1)); break;
            case "J": G.Cap = Math.Clamp((int)Arg(a, a.Count - 1), 0, 2); break;
            case "j": G.Join = Math.Clamp((int)Arg(a, a.Count - 1), 0, 2); break;
            case "M": G.Miter = Math.Max(1, Arg(a, a.Count - 1)); break;
            case "d":
                if (a.Count >= 2 && a[^2] is PsArray dash)
                {
                    G.Dash = dash.All().Where(x => x is int or double).Select(ToNum).ToArray();
                    G.DashOffset = PdfNum(a[^1]);
                }
                break;
            case "ri" or "i": break;
            case "gs":
                if (Resource(res, "ExtGState", a.LastOrDefault()) is { } gs) ExtGState(gs, res);
                break;

            // ---- Paths
            case "m": { var (x, y) = G.Ctm.Apply(Arg(a, a.Count - 2), Arg(a, a.Count - 1)); MoveTo(x, y); break; }
            case "l":
            {
                var (x, y) = G.Ctm.Apply(Arg(a, a.Count - 2), Arg(a, a.Count - 1));
                if (G.Point is null) MoveTo(x, y); else LineTo(x, y);
                break;
            }
            case "c" or "v" or "y":
            {
                var n = Last(a, op == "c" ? 6 : 4);
                if (n.Length < (op == "c" ? 6 : 4)) break;
                (double, double) P(int k) => G.Ctm.Apply(n[k], n[k + 1]);
                if (G.Point is null) { var (sx, sy) = P(0); MoveTo(sx, sy); }
                var cur = Current;
                var (p1, p2, p3) = op switch
                {
                    "c" => (P(0), P(2), P(4)),
                    "v" => (cur, P(0), P(2)),
                    _ => (P(0), P(2), P(2)),
                };
                CurveTo(p1.Item1, p1.Item2, p2.Item1, p2.Item2, p3.Item1, p3.Item2);
                break;
            }
            case "h": Close(); break;
            case "re":
            {
                var n = Last(a, 4);
                if (n.Length < 4) break;
                var (x, y, w, h) = (n[0], n[1], n[2], n[3]);
                var c = new[] { G.Ctm.Apply(x, y), G.Ctm.Apply(x + w, y), G.Ctm.Apply(x + w, y + h), G.Ctm.Apply(x, y + h) };
                MoveTo(c[0].X, c[0].Y);
                for (var k = 1; k < 4; k++) LineTo(c[k].X, c[k].Y);
                Close();
                break;
            }

            // ---- Painting and clipping
            case "S": Paint(fill: false, evenOdd: false, stroke: true); break;
            case "s": Close(); Paint(false, false, true); break;
            case "f" or "F": Paint(true, false, false); break;
            case "f*": Paint(true, true, false); break;
            case "B": Paint(true, false, true); break;
            case "B*": Paint(true, true, true); break;
            case "b": Close(); Paint(true, false, true); break;
            case "b*": Close(); Paint(true, true, true); break;
            case "n": Paint(false, false, false); break;
            case "W": _clipPending = false; break;
            case "W*": _clipPending = true; break;

            // ---- Colour
            case "CS": Stroking(() => SetPdfSpace(a.LastOrDefault(), res)); break;
            case "cs": SetPdfSpace(a.LastOrDefault(), res); break;
            case "SC" or "SCN": Stroking(() => SetPdfColour(a, res)); break;
            case "sc" or "scn": SetPdfColour(a, res); break;
            case "G": Stroking(() => SetDevice("DeviceGray", Last(a, 1))); break;
            case "g": SetDevice("DeviceGray", Last(a, 1)); break;
            case "RG": Stroking(() => SetDevice("DeviceRGB", Last(a, 3))); break;
            case "rg": SetDevice("DeviceRGB", Last(a, 3)); break;
            case "K": Stroking(() => SetDevice("DeviceCMYK", Last(a, 4))); break;
            case "k": SetDevice("DeviceCMYK", Last(a, 4)); break;

            // ---- Shadings, pictures and forms
            case "sh":
                if (Resource(res, "Shading", a.LastOrDefault()) is { } sh)
                {
                    var start = Ops.Count;
                    Shade(PdfShading(sh), PsMatrix.Identity.Then(G.Ctm), null, false);
                    Transparency(start, PState.FillAlpha);
                }
                break;
            case "Do":
                if (Resource(res, "XObject", a.LastOrDefault()) is { } xo) XObject(xo, res);
                break;

            // ---- Text
            case "BT": _tm = _tlm = PsMatrix.Identity; break;
            case "ET":
                if (_textClip is { } tc)
                {
                    G.Clips.Add(new PsClip(tc, false));
                    _textClip = null;
                }
                break;
            case "Tc": PState = PState with { CharSpace = Arg(a, a.Count - 1) }; break;
            case "Tw": PState = PState with { WordSpace = Arg(a, a.Count - 1) }; break;
            case "Tz": PState = PState with { Scale = Arg(a, a.Count - 1) / 100 }; break;
            case "TL": PState = PState with { Leading = Arg(a, a.Count - 1) }; break;
            case "Ts": PState = PState with { Rise = Arg(a, a.Count - 1) }; break;
            case "Tr": PState = PState with { Render = Math.Clamp((int)Arg(a, a.Count - 1), 0, 7) }; break;
            case "Tf":
            {
                var size = Arg(a, a.Count - 1);
                var font = a.Count >= 2 ? Resource(res, "Font", a[^2]) : null;
                PState = PState with { FontSize = size, Font = font is null ? PState.Font : PdfFontOf(font) };
                break;
            }
            case "Td": NextLine(Arg(a, a.Count - 2), Arg(a, a.Count - 1)); break;
            case "TD":
                PState = PState with { Leading = -Arg(a, a.Count - 1) };
                NextLine(Arg(a, a.Count - 2), Arg(a, a.Count - 1));
                break;
            case "Tm": _tm = _tlm = MatrixOf(a); break;
            case "T*": NextLine(0, -PState.Leading); break;
            case "Tj": if (a.LastOrDefault() is PsString tj) ShowPdf(tj, res); break;
            case "'":
                NextLine(0, -PState.Leading);
                if (a.LastOrDefault() is PsString q1) ShowPdf(q1, res);
                break;
            case "\"":
                if (a.Count >= 3)
                {
                    PState = PState with { WordSpace = PdfNum(a[^3]), CharSpace = PdfNum(a[^2]) };
                    NextLine(0, -PState.Leading);
                    if (a[^1] is PsString q2) ShowPdf(q2, res);
                }
                break;
            case "TJ":
                if (a.LastOrDefault() is PsArray parts)
                    foreach (var p in parts.All())
                    {
                        if (p is PsString s) ShowPdf(s, res);
                        else if (p is int or double)
                        {
                            var st = PState;
                            var tx = -ToNum(p) / 1000 * st.FontSize * st.Scale;
                            _tm = new PsMatrix(1, 0, 0, 1, tx, 0).Then(_tm);
                        }
                    }
                break;
            case "d0": break;
            case "d1": _uncolouredGlyph = true; break;

            // ---- Marked and optional content
            case "BMC": _marked.Push(false); break;
            case "BDC":
            {
                var off = false;
                if (a.Count >= 2 && a[^2] is PsName { Value: "OC" })
                    off = IsOff(a[^1] is PsDict inline ? inline : Resource(res, "Properties", a[^1]));
                _marked.Push(off);
                if (off) _hidden++;
                break;
            }
            case "EMC":
                if (_marked.Count > 0 && _marked.Pop()) _hidden--;
                break;
            case "MP" or "DP" or "BX" or "EX": break;
        }
    }

    private void NextLine(double tx, double ty)
    {
        _tlm = new PsMatrix(1, 0, 0, 1, tx, ty).Then(_tlm);
        _tm = _tlm;
    }

    // ---- Painting

    /// <summary>Fills and strokes the path as an operator says, then clips by it if W asked, and ends it.</summary>
    private void Paint(bool fill, bool evenOdd, bool stroke)
    {
        var path = G.Path.Clone();
        if (!path.IsEmpty)
        {
            var st = PState;
            if (fill)
            {
                var start = Ops.Count;
                if (G.Space == "Pattern") FillWithPattern(path.Clone(), evenOdd);
                else if (!_uncolouredGlyph || true) Ops.Add(new PsFill(path.Clone(), G.Colour, evenOdd, [.. G.Clips]));
                Transparency(start, st.FillAlpha);
            }
            if (stroke && G.Ctm.Inverse() is { } inv)
            {
                var start = Ops.Count;
                var pen = Pen;
                // A hairline (width 0) is as thin as the device draws: a fine line, not none.
                if (pen.Width == 0) pen = pen with { Width = 0.5 / Math.Max(1e-9, Math.Sqrt(Math.Abs(G.Ctm.Determinant))) };
                Stroking(() =>
                {
                    if (G.Space == "Pattern")
                    {
                        if (G.PatternColour is { } pc) Ops.Add(new PsStroke(path.Transformed(inv), G.Ctm, pen, pc, [.. G.Clips]));
                        else Note("its lines painted with a pattern are drawn in black");
                    }
                    else Ops.Add(new PsStroke(path.Transformed(inv), G.Ctm, pen, G.Colour, [.. G.Clips]));
                });
                Transparency(start, st.StrokeAlpha);
            }
        }
        if (_clipPending is { } eo)
        {
            G.Clips.Add(new PsClip(path, eo));
            _clipPending = null;
        }
        NewPath();
    }

    /// <summary>Puts what was just painted in a group, if the state paints at an opacity, with a blend or a soft mask.</summary>
    private void Transparency(int start, double alpha)
    {
        var st = PState;
        if (Ops.Count <= start || (alpha >= 1 && st.Blend == "Normal" && st.Mask is null)) return;
        var ops = Ops.GetRange(start, Ops.Count - start);
        Ops.RemoveRange(start, Ops.Count - start);
        Ops.Add(new PsGroup(ops, alpha, st.Blend, st.Mask, []));
    }

    /// <summary>Runs something with the stroking colour as the current one (and keeps what it sets as the stroking colour).</summary>
    private void Stroking(Action action)
    {
        var st = PState;
        var fill = (G.Space, G.SpaceArray, G.Components, G.Colour, G.Pattern, G.PatternColour);
        (G.Space, G.SpaceArray, G.Components, G.Colour, G.Pattern, G.PatternColour) =
            (st.StrokeSpace, st.StrokeSpaceArray, st.StrokeComponents, st.StrokeColour, st.StrokePattern, st.StrokePatternColour);
        try { action(); }
        finally
        {
            PState = PState with
            {
                StrokeSpace = G.Space, StrokeSpaceArray = G.SpaceArray, StrokeComponents = G.Components, StrokeColour = G.Colour,
                StrokePattern = G.Pattern, StrokePatternColour = G.PatternColour,
            };
            (G.Space, G.SpaceArray, G.Components, G.Colour, G.Pattern, G.PatternColour) = fill;
        }
    }

    private void ExtGState(PsDict gs, PsDict res)
    {
        foreach (var (key, v) in gs.Entries)
            switch ((key as PsName)?.Value)
            {
                case "LW": G.LineWidth = Math.Abs(PdfNum(v, G.LineWidth)); break;
                case "LC": G.Cap = Math.Clamp((int)PdfNum(v), 0, 2); break;
                case "LJ": G.Join = Math.Clamp((int)PdfNum(v), 0, 2); break;
                case "ML": G.Miter = Math.Max(1, PdfNum(v, 10)); break;
                case "D" when v is PsArray { Length: 2 } d && d[0] is PsArray dash:
                    G.Dash = dash.All().Where(x => x is int or double).Select(ToNum).ToArray();
                    G.DashOffset = PdfNum(d[1]);
                    break;
                case "CA": PState = PState with { StrokeAlpha = Math.Clamp(PdfNum(v, 1), 0, 1) }; break;
                case "ca": PState = PState with { FillAlpha = Math.Clamp(PdfNum(v, 1), 0, 1) }; break;
                case "BM":
                    var mode = v switch { PsName n => n.Value, PsArray arr when arr.Length > 0 && arr[0] is PsName n0 => n0.Value, _ => "Normal" };
                    PState = PState with { Blend = mode == "Compatible" ? "Normal" : mode };
                    break;
                case "SMask":
                    PState = PState with { Mask = v is PsDict sm ? SoftMask(sm, res) : null };
                    break;
                case "Font" when v is PsArray { Length: 2 } f && f[0] is PsDict fd:
                    PState = PState with { Font = PdfFontOf(fd), FontSize = PdfNum(f[1]) };
                    break;
            }
    }

    /// <summary>A soft mask: its group drawn now (in the space the state is set in), as it will mask what is painted.</summary>
    private PsMask? SoftMask(PsDict sm, PsDict res)
    {
        if (sm.Get("G") is not PsDict group) return null;
        var luminosity = PdfName(sm.Get("S")) != "Alpha";
        PsColour? backdrop = null;
        if (luminosity && sm.Get("BC") is PsArray bc && bc.Length > 0)
        {
            var space = (group.Get("Group") as PsDict)?.Get("CS");
            var comps = bc.All().Select(o => PdfNum(o)).ToArray();
            var keep = (G.Space, G.SpaceArray, G.Components, G.Colour);
            try
            {
                SetSpace(space is null ? new PsName(comps.Length switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" }) : PdfSpace(space, res));
                SetComponents(comps);
                backdrop = G.Colour;
            }
            finally { (G.Space, G.SpaceArray, G.Components, G.Colour) = keep; }
        }
        var start = Ops.Count;
        var outerState = G;
        G = G.Clone();
        try
        {
            PState = PState with { Mask = null, FillAlpha = 1, StrokeAlpha = 1, Blend = "Normal" };
            G.Clips = [];
            DrawForm(group, res, asGroup: false);
        }
        finally { G = outerState; } // the very state object, so nothing holding it is left with a stale one
        var ops = Ops.GetRange(start, Ops.Count - start);
        Ops.RemoveRange(start, Ops.Count - start);
        byte[]? transfer = null;
        if (sm.Get("TR") is { } tr and not PsName)
        {
            var f = PdfFunction(tr);
            transfer = new byte[256];
            for (var k = 0; k < 256; k++)
                transfer[k] = (byte)Math.Round(Math.Clamp(Evaluate(f, k / 255.0) is { Length: > 0 } v ? v[0] : k / 255.0, 0, 1) * 255);
        }
        return new PsMask(ops, luminosity, backdrop, transfer);
    }

    // ---- Colour

    private void SetDevice(string space, double[] c)
    {
        if (_uncolouredGlyph) return;
        SetSpace(new PsName(space));
        SetComponents(c);
    }

    private void SetPdfSpace(object? name, PsDict res)
    {
        if (_uncolouredGlyph || name is null) return;
        SetSpace(PdfSpace(name, res));
        G.Pattern = null;
        G.PatternColour = null;
    }

    private void SetPdfColour(List<object> a, PsDict res)
    {
        if (_uncolouredGlyph) return;
        if (G.Space == "Pattern")
        {
            // Made first: making a tiling pattern's cell saves and restores the state, so G is another object after it.
            if (Resource(res, "Pattern", a.LastOrDefault()) is { } p) { var made = PdfPattern(p, res); G.Pattern = made; }
            // An uncoloured pattern's colour, in the space under the pattern's.
            if (G.SpaceArray is PsArray { Length: > 1 } ps && Last(a) is { Length: > 0 } comps)
            {
                var keep = (G.Space, G.SpaceArray, G.Components, G.Colour);
                SetSpace(ps[1]);
                SetComponents(comps);
                var colour = G.Colour;
                (G.Space, G.SpaceArray, G.Components, G.Colour) = keep;
                G.PatternColour = colour;
            }
            return;
        }
        SetComponents(Last(a));
    }

    private readonly Dictionary<object, object> _spaces = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A PDF colour space as the interpreter has them: a device name, or an array whose parts (an ICC profile's,
    /// an index's lookup table, a separation's tint transform) are in the forms PostScript gives them.
    /// </summary>
    private object PdfSpace(object space, PsDict res)
    {
        if (space is PsName n)
        {
            switch (n.Value)
            {
                case "DeviceGray" or "G": return new PsName("DeviceGray");
                case "DeviceRGB" or "RGB": return new PsName("DeviceRGB");
                case "DeviceCMYK" or "CMYK": return new PsName("DeviceCMYK");
                case "Pattern": return new PsName("Pattern");
            }
            if ((res.Get("ColorSpace") as PsDict)?.Get(n.Value) is { } named) return PdfSpace(named, res);
            throw new PdfException($"it uses a colour space it doesn't have ({n.Value})");
        }
        if (space is not PsArray { Length: > 0 } a || a[0] is not PsName kind) return new PsName("DeviceGray");
        if (_spaces.TryGetValue(a, out var made)) return made;
        object result = kind.Value switch
        {
            "Indexed" or "I" when a.Length >= 4 => new PsArray([new PsName("Indexed"), PdfSpace(a[1], res), a[2],
                a[3] is PsDict table && _pdf is { } r ? PsString.Of(r.Decode(table)) : a[3]]),
            "Separation" when a.Length >= 4 => new PsArray([kind, a[1], PdfSpace(a[2], res), FunctionProc(a[3], 1)]),
            "DeviceN" when a.Length >= 4 && a[1] is PsArray names => new PsArray([kind, names, PdfSpace(a[2], res), FunctionProc(a[3], names.Length)]),
            "Pattern" when a.Length >= 2 => new PsArray([kind, PdfSpace(a[1], res)]),
            "Pattern" => new PsName("Pattern"),
            "ICCBased" when a.Length >= 2 && a[1] is PsDict icc && icc.Get("N") is null =>
                new PsArray([kind, Alternate(icc)]),
            "CalRGB" => new PsName("DeviceRGB"),
            "CalGray" => new PsName("DeviceGray"),
            "CalCMYK" => new PsName("DeviceCMYK"),
            _ => a,
        };
        _spaces[a] = result;
        return result;

        static PsDict Alternate(PsDict icc)
        {
            var d = new PsDict(2);
            d.Put("N", 3);
            return d;
        }
    }

    /// <summary>A PDF function as a procedure: its inputs off the stack, its outputs onto it.</summary>
    private PsArray FunctionProc(object fn, int inputs)
    {
        var f = PdfFunction(fn);
        return new PsArray([new PsOperator("function", i =>
        {
            var x = new double[inputs];
            for (var k = inputs - 1; k >= 0; k--) x[k] = i.Num();
            foreach (var v in i.EvaluateN(f, x)) i.Push(v);
        })], exec: true);
    }

    /// <summary>A PDF function in the form the interpreter's shadings take: a sampled one's samples, a calculator's procedure.</summary>
    private object PdfFunction(object fn)
    {
        if (fn is PsArray many) return new PsArray(many.All().Select(PdfFunction).ToList());
        if (fn is not PsDict d) return fn;
        var type = (int)PdfNum(d.Get("FunctionType"), -1);
        if (type is not (0 or 3 or 4)) return d;
        var copy = new PsDict(d.Count + 2);
        foreach (var (k, v) in d.Entries) copy.Put(k, v);
        if (type == 0 && _pdf is { } r) copy.Put("DataSource", PsString.Of(r.Decode(d)));
        if (type == 3 && d.Get("Functions") is PsArray parts) copy.Put("Functions", PdfFunction(parts));
        if (type == 4 && _pdf is { } r4)
        {
            var code = r4.Decode(d);
            var sc = new Scanner(code, 0, code.Length);
            if (sc.Next(Immediate) is PsArray { Exec: true } proc) copy.Put("Function", proc);
        }
        return copy;
    }

    /// <summary>A function of any number of inputs: one input as shadings take it; more, for a calculator or sampled one.</summary>
    private double[] EvaluateN(object f, double[] x)
    {
        if (x.Length == 1) return Evaluate(f, x[0]);
        if (f is PsArray many && !many.Exec) return many.All().SelectMany(g => EvaluateN(g, x)).ToArray();
        if (f is not PsDict d) throw new PsError("typecheck", "function");
        var type = (int)PdfNum(d.Get("FunctionType"), -1);
        double[] Get(string key, double[] otherwise) => (d.Get(key) as PsArray)?.All().Select(ToNum).ToArray() ?? otherwise;
        var domain = Get("Domain", []);
        for (var k = 0; k < x.Length && 2 * k + 1 < domain.Length; k++) x[k] = Math.Clamp(x[k], domain[2 * k], domain[2 * k + 1]);
        var range = Get("Range", []);
        double[] result;
        if (type == 4)
        {
            var proc = d.Get("Function") as PsArray ?? throw new PsError("undefined", "Function");
            var before = _stack.Count;
            foreach (var v in x) Push(v);
            RunProc(proc);
            result = _stack.Skip(before).Select(ToNum).ToArray();
            _stack.RemoveRange(before, _stack.Count - before);
        }
        else if (type == 0)
        {
            // Sampled: the samples at the cell's corners, mixed by where the inputs fall in it.
            var size = Get("Size", []).Select(s => (int)s).ToArray();
            var bps = (int)PdfNum(d.Get("BitsPerSample"), 8);
            var outputs = range.Length / 2;
            var encode = Get("Encode", size.SelectMany(s => new double[] { 0, s - 1 }).ToArray());
            var decode = Get("Decode", range);
            var data = (d.Get("DataSource") as PsString)?.Span.ToArray() ?? [];
            var m = Math.Min(x.Length, size.Length);
            var e = new double[m];
            for (var k = 0; k < m; k++)
            {
                var (d0, d1) = 2 * k + 1 < domain.Length ? (domain[2 * k], domain[2 * k + 1]) : (0.0, 1.0);
                e[k] = Math.Clamp(encode[2 * k] + (x[k] - d0) * (encode[2 * k + 1] - encode[2 * k]) / (d1 == d0 ? 1 : d1 - d0), 0, size[k] - 1);
            }
            var max = (1L << bps) - 1;
            double Sample(int index, int o)
            {
                var bit = ((long)index * outputs + o) * bps;
                long v = 0;
                for (var b = 0; b < bps; b++, bit++)
                {
                    var by = bit / 8 < data.Length ? data[bit / 8] : 0;
                    v = v << 1 | (long)(by >> (7 - (int)(bit % 8)) & 1);
                }
                return v;
            }
            result = new double[outputs];
            for (var corner = 0; corner < 1 << m; corner++)
            {
                double weight = 1;
                var index = 0;
                var stride = 1;
                for (var k = 0; k < m; k++)
                {
                    var lo = (int)Math.Floor(e[k]);
                    var frac = e[k] - lo;
                    var up = (corner >> k & 1) == 1;
                    var at = Math.Min(up ? lo + 1 : lo, size[k] - 1);
                    weight *= up ? frac : 1 - frac;
                    index += at * stride;
                    stride *= size[k];
                }
                if (weight == 0) continue;
                for (var o = 0; o < outputs; o++) result[o] += weight * Sample(index, o);
            }
            for (var o = 0; o < outputs; o++) result[o] = decode[2 * o] + result[o] * (decode[2 * o + 1] - decode[2 * o]) / max;
        }
        else return Evaluate(f, x[0]);
        for (var k = 0; k < result.Length && 2 * k + 1 < range.Length; k++) result[k] = Math.Clamp(result[k], range[2 * k], range[2 * k + 1]);
        return result;
    }

    // ---- Shadings and patterns

    private PsDict PdfShading(PsDict sh)
    {
        var d = new PsDict(sh.Count + 2);
        foreach (var (k, v) in sh.Entries) if (!(k is PsName { Value: PdfReader.StreamKey })) d.Put(k, v);
        if (sh.Get("ColorSpace") is { } cs) d.Put("ColorSpace", PdfSpace(cs, new PsDict()));
        if (sh.Get("Function") is { } f) d.Put("Function", PdfFunction(f));
        if (sh.Get(PdfReader.StreamKey) is not null && _pdf is { } r) d.Put("DataSource", PsString.Of(r.Decode(sh)));
        return d;
    }

    /// <summary>A pattern as the interpreter paints with them: its cell drawn (a tiling one) and its space fixed.</summary>
    private PsDict PdfPattern(PsDict p, PsDict res)
    {
        var made = new PsDict(p.Count + 2);
        foreach (var (k, v) in p.Entries) if (!(k is PsName { Value: PdfReader.StreamKey })) made.Put(k, v);
        var matrix = p.Get("Matrix") is PsArray m ? ToMatrix(m) : PsMatrix.Identity;
        made.Put("Implementation", NewMatrix(matrix.Then(_patternBase)));
        if ((int)PdfNum(p.Get("PatternType"), 1) == 2)
        {
            if (p.Get("Shading") is PsDict sh) made.Put("Shading", PdfShading(sh));
            return made;
        }
        var start = Ops.Count;
        var outerState = G;
        G = G.Clone();
        try
        {
            G.Ctm = PsMatrix.Identity;
            G.Clips = [];
            NewPath();
            var keepBase = _patternBase;
            _patternBase = PsMatrix.Identity;
            PState = PState with { FillAlpha = 1, StrokeAlpha = 1, Blend = "Normal", Mask = null };
            if (_pdfDepth++ < 24 && _pdf is { } r) RunPdf(r.Decode(p), p.Get("Resources") as PsDict ?? res);
            _pdfDepth--;
            _patternBase = keepBase;
        }
        finally { G = outerState; } // the very state object, so nothing holding it is left with a stale one
        var cell = Ops.GetRange(start, Ops.Count - start);
        Ops.RemoveRange(start, Ops.Count - start);
        made.Put(TileKey, cell);
        return made;
    }

    // ---- XObjects

    private void XObject(PsDict x, PsDict res)
    {
        if (IsOff(x.Get("OC"))) return;
        switch (PdfName(x.Get("Subtype")))
        {
            case "Image":
                PdfImage(x, res);
                break;
            case "Form":
                DrawForm(x, res, asGroup: true);
                break;
            case "PS":
                Note("its PostScript pieces are left out, as PDF viewers leave them");
                break;
        }
    }

    /// <summary>
    /// A form: its content in its own space (its matrix, clipped to its box), with its own resources. A transparency
    /// group is drawn as one, at the opacity, blend and mask it is painted with, its own content starting from none.
    /// </summary>
    private void DrawForm(PsDict form, PsDict res, bool asGroup)
    {
        if (_pdf is not { } r || _pdfDepth >= 24) return;
        _pdfDepth++;
        Gsave();
        try
        {
            if (form.Get("Matrix") is PsArray { Length: 6 } m) G.Ctm = ToMatrix(m).Then(G.Ctm);
            if (form.Get("BBox") is PsArray { Length: 4 } bb)
            {
                var v = bb.All().Select(o => PdfNum(o)).ToArray();
                NewPath();
                var c = new[] { G.Ctm.Apply(v[0], v[1]), G.Ctm.Apply(v[2], v[1]), G.Ctm.Apply(v[2], v[3]), G.Ctm.Apply(v[0], v[3]) };
                MoveTo(c[0].X, c[0].Y);
                for (var k = 1; k < 4; k++) LineTo(c[k].X, c[k].Y);
                Close();
                G.Clips.Add(new PsClip(G.Path.Clone(), false));
                NewPath();
            }
            var group = asGroup && form.Get("Group") is PsDict g && PdfName(g.Get("S")) == "Transparency";
            var outer = PState;
            if (group) PState = PState with { FillAlpha = 1, StrokeAlpha = 1, Blend = "Normal", Mask = null };
            var keepBase = _patternBase;
            _patternBase = G.Ctm;
            var start = Ops.Count;
            RunPdf(r.Decode(form), form.Get("Resources") as PsDict ?? res);
            _patternBase = keepBase;
            if (group && Ops.Count > start && (outer.FillAlpha < 1 || outer.Blend != "Normal" || outer.Mask is not null))
            {
                var ops = Ops.GetRange(start, Ops.Count - start);
                Ops.RemoveRange(start, Ops.Count - start);
                Ops.Add(new PsGroup(ops, outer.FillAlpha, outer.Blend, outer.Mask, []));
            }
        }
        finally
        {
            GrestoreOne();
            _pdfDepth--;
        }
    }

    /// <summary>The page's annotations that print (stamps, form fields, notes), drawn from their appearances.</summary>
    private void DrawAnnotations(PdfReader r, PsDict page, PsMatrix pageCtm)
    {
        if (r.Fetch(page.Get("Annots")) is not PsArray annots) return;
        foreach (var item in annots.All())
        {
            if (r.Deep(item) is not PsDict a) continue;
            var flags = (int)PdfNum(a.Get("F"));
            var kind = PdfName(a.Get("Subtype"));
            if ((flags & 2) != 0 || (flags & 4) == 0 || kind is "Popup" or "Link") continue;
            if (IsOff(a.Get("OC"))) continue;
            var normal = (a.Get("AP") as PsDict)?.Get("N");
            if (normal is PsDict states && states.Get(PdfReader.StreamKey) is null)
                normal = a.Get("AS") is PsName st ? states.Get(st.Value) : null;
            if (normal is not PsDict form || a.Get("Rect") is not PsArray { Length: 4 } rect) continue;
            var rv = rect.All().Select(o => PdfNum(o)).ToArray();
            var target = Box.FromCorners(new Pt(Math.Min(rv[0], rv[2]), Math.Min(rv[1], rv[3])), new Pt(Math.Max(rv[0], rv[2]), Math.Max(rv[1], rv[3])));
            // The form's box through its matrix, fitted to the annotation's rectangle.
            var bb = (form.Get("BBox") as PsArray)?.All().Select(o => PdfNum(o)).ToArray() ?? [0, 0, 1, 1];
            var fm = form.Get("Matrix") is PsArray { Length: 6 } mm ? ToMatrix(mm) : PsMatrix.Identity;
            var corners = new[] { fm.Apply(bb[0], bb[1]), fm.Apply(bb[2], bb[1]), fm.Apply(bb[2], bb[3]), fm.Apply(bb[0], bb[3]) };
            var (x0, y0) = (corners.Min(c => c.X), corners.Min(c => c.Y));
            var (x1, y1) = (corners.Max(c => c.X), corners.Max(c => c.Y));
            if (x1 - x0 <= 0 || y1 - y0 <= 0) continue;
            var fit = new PsMatrix(target.W / (x1 - x0), 0, 0, target.H / (y1 - y0), target.X - x0 * target.W / (x1 - x0), target.Y - y0 * target.H / (y1 - y0));
            Gsave();
            try
            {
                G = new GState { Ctm = fit.Then(pageCtm) };
                DrawForm(form, new PsDict(), asGroup: true);
            }
            finally { GrestoreOne(); }
        }
    }

    // ---- Pictures

    private void InlineImage(PdfLexer lex, PsDict res)
    {
        var d = new PsDict();
        while (true)
        {
            var k = lex.ReadObject();
            if (k is null || k is PdfKeyword { Value: "ID" }) break;
            if (k is not PsName key) continue;
            var v = lex.ReadObject();
            if (v is null) break;
            var full = key.Value switch
            {
                "BPC" => "BitsPerComponent", "CS" => "ColorSpace", "D" => "Decode", "DP" => "DecodeParms", "F" => "Filter",
                "H" => "Height", "IM" => "ImageMask", "I" => "Interpolate", "W" => "Width", "L" => "Length", _ => key.Value,
            };
            if (full == "ColorSpace" && v is PsName { Value: "I" }) v = new PsName("Indexed");
            if (full == "ColorSpace" && v is PsArray { Length: > 0 } ia && ia[0] is PsName { Value: "I" }) ia[0] = new PsName("Indexed");
            d.Put(full, v);
        }
        var b = lex.Bytes;
        var start = lex.Pos + 1; // one white space after ID
        var end = -1;
        if (d.Get("Filter") is null)
        {
            var w = (int)PdfNum(d.Get("Width"));
            var h = (int)PdfNum(d.Get("Height"));
            var bpc = d.Get("ImageMask") is true ? 1 : (int)PdfNum(d.Get("BitsPerComponent"), 8);
            var comps = d.Get("ImageMask") is true ? 1 : d.Get("ColorSpace") switch
            {
                PsName { Value: "DeviceRGB" or "RGB" } => 3,
                PsName { Value: "DeviceCMYK" or "CMYK" } => 4,
                PsName n when (res.Get("ColorSpace") as PsDict)?.Get(n.Value) is PsArray { Length: > 0 } named && named[0] is PsName { Value: "ICCBased" } && named[1] is PsDict icc => (int)PdfNum(icc.Get("N"), 3),
                _ => 1,
            };
            var length = h * ((w * comps * bpc + 7) / 8);
            if (start + length <= b.Length) end = start + length;
        }
        // Otherwise (or if that doesn't land on it), to an EI on its own.
        if (end < 0 || PdfLexer.Find(b, "EI", end, Math.Min(b.Length, end + 8)) < 0)
        {
            for (var at = PdfLexer.Find(b, "EI", start); at >= 0; at = PdfLexer.Find(b, "EI", at + 2))
                if (at > 0 && PdfLexer.White(b[at - 1]) && (at + 2 >= b.Length || PdfLexer.White(b[at + 2]) || PdfLexer.Delimiter(b[at + 2])))
                {
                    end = at - 1;
                    break;
                }
        }
        if (end < start) { lex.Pos = b.Length; return; }
        var ei = PdfLexer.Find(b, "EI", end);
        lex.Pos = ei < 0 ? b.Length : ei + 2;
        d.Put(PdfReader.StreamKey, new PdfStreamBytes(b, start, end - start));
        PdfImage(d, res);
    }

    private void PdfImage(PsDict d, PsDict res)
    {
        if (_pdf is not { } r) return;
        var w = (int)PdfNum(d.Get("Width"));
        var h = (int)PdfNum(d.Get("Height"));
        if (w <= 0 || h <= 0 || (long)w * h > 60_000_000) return;
        var isMask = d.Get("ImageMask") is true;
        var data = r.Decode(d, out var left);
        var start = Ops.Count;
        var imageMatrix = NewMatrix(new PsMatrix(w, 0, 0, -h, 0, h));
        if (left is { } l)
        {
            if (l.Filter is not ("DCTDecode" or "DCT")) { Note($"its {PictureFilter(l.Filter)} pictures aren't drawn"); return; }
            if (JpegDecoder?.Invoke(data) is not { } jpeg) { Note("a JPEG picture in it couldn't be read"); return; }
            var rgba = jpeg.Rgba;
            // An Adobe CMYK JPEG written inverted, with a Decode that says so: undo it.
            if (d.Get("Decode") is PsArray { Length: 8 } dec && PdfNum(dec[0]) == 1)
            {
                rgba = (byte[])rgba.Clone();
                for (var k = 0; k < rgba.Length; k += 4) (rgba[k], rgba[k + 1], rgba[k + 2]) = ((byte)(255 - rgba[k]), (byte)(255 - rgba[k + 1]), (byte)(255 - rgba[k + 2]));
            }
            Ops.Add(new PsImage(jpeg.Width, jpeg.Height, rgba, ImagePlacement(jpeg.Width, jpeg.Height, new PsMatrix(jpeg.Width, 0, 0, -jpeg.Height, 0, jpeg.Height)), [.. G.Clips]));
        }
        else
        {
            var image = new PsDict(8);
            image.Put("ImageType", 1);
            image.Put("Width", w);
            image.Put("Height", h);
            image.Put("ImageMatrix", imageMatrix);
            image.Put("DataSource", PsString.Of(data));
            image.Put("BitsPerComponent", isMask ? 1 : (int)PdfNum(d.Get("BitsPerComponent"), 8));
            if (d.Get("Decode") is PsArray decode) image.Put("Decode", decode);
            if (isMask)
            {
                if (G.Space == "Pattern") { Note("its masks painted with a pattern are drawn in black"); G.Colour = G.PatternColour ?? PsColour.Black; }
                DictImage(image, mask: true);
            }
            else
            {
                if (d.Get("Mask") is PsArray key)
                {
                    image.Put("ImageType", 4);
                    image.Put("MaskColor", key);
                }
                var keep = (G.Space, G.SpaceArray, G.Components, G.Colour);
                try
                {
                    SetSpace(PdfSpace(d.Get("ColorSpace") ?? new PsName("DeviceGray"), res));
                    DictImage(image, mask: false);
                }
                finally { (G.Space, G.SpaceArray, G.Components, G.Colour) = keep; }
            }
        }
        if (Ops.Count > start && Ops[^1] is PsImage img)
        {
            if (d.Get("SMask") is PsDict sm) Mask(img, sm, soft: true);
            else if (d.Get("Mask") is PsDict mk) Mask(img, mk, soft: false);
        }
        Transparency(start, PState.FillAlpha);
    }

    private static string PictureFilter(string filter) => filter switch
    {
        "JPXDecode" => "JPEG 2000",
        "JBIG2Decode" => "JBIG2 (scanned)",
        _ => "CCITT fax (scanned)",
    };

    /// <summary>
    /// A picture's mask over it: a soft mask's grey values as its opacity, or a stencil's ones (by its decode)
    /// leaving it out. The mask is scaled to the picture.
    /// </summary>
    private void Mask(PsImage img, PsDict m, bool soft)
    {
        if (_pdf is not { } r) return;
        var (mw, mh) = ((int)PdfNum(m.Get("Width")), (int)PdfNum(m.Get("Height")));
        if (mw <= 0 || mh <= 0) return;
        var data = r.Decode(m, out var left);
        byte[] values; // one byte a sample, 0 to 255: how much of the picture shows
        if (left is { Filter: "DCTDecode" or "DCT" } && JpegDecoder?.Invoke(data) is { } jpeg && jpeg.Width == mw && jpeg.Height == mh)
        {
            values = new byte[mw * mh];
            for (var k = 0; k < values.Length; k++) values[k] = jpeg.Rgba[k * 4];
        }
        else if (left is not null) { Note($"its {PictureFilter(left.Value.Filter)} masks aren't used"); return; }
        else
        {
            var bpc = soft ? (int)PdfNum(m.Get("BitsPerComponent"), 8) : 1;
            var max = (1 << bpc) - 1;
            var row = (mw * bpc + 7) / 8;
            var decode = (m.Get("Decode") as PsArray)?.All().Select(o => PdfNum(o)).ToArray() ?? [0, 1];
            var inverted = decode is [1, 0, ..];
            values = new byte[mw * mh];
            for (var y = 0; y < mh; y++)
                for (var x = 0; x < mw; x++)
                {
                    var bit = y * row * 8 + x * bpc;
                    var v = 0;
                    for (var b = 0; b < bpc; b++, bit++) v = v << 1 | (bit / 8 < data.Length ? data[bit / 8] >> (7 - bit % 8) & 1 : 0);
                    var t = (double)v / max;
                    if (inverted) t = 1 - t;
                    values[y * mw + x] = soft ? (byte)Math.Round(t * 255) : t >= 0.5 ? (byte)0 : (byte)255;
                }
        }
        for (var y = 0; y < img.Height; y++)
        {
            var my = Math.Min(mh - 1, y * mh / img.Height);
            for (var x = 0; x < img.Width; x++)
            {
                var mx = Math.Min(mw - 1, x * mw / img.Width);
                var at = (y * img.Width + x) * 4 + 3;
                img.Rgba[at] = (byte)(img.Rgba[at] * values[my * mw + mx] / 255);
            }
        }
    }

    // ---- Text

    /// <summary>
    /// Sets a string: each code's glyph placed by the text matrix and moved on by its width, the character and word
    /// spacing and the horizontal scaling; then filled, stroked or added to the text clip as the render mode says.
    /// </summary>
    private void ShowPdf(PsString s, PsDict res)
    {
        var st = PState;
        var font = st.Font ?? (PState = PState with { Font = SystemPdfFont(new PsDict(), "Helvetica") }).Font!;
        var size = st.FontSize;
        var outline = new PsPath();
        foreach (var (code, bytes) in font.Codes(s))
        {
            var trm = new PsMatrix(size * st.Scale, 0, 0, size, 0, st.Rise).Then(_tm).Then(G.Ctm);
            double advance;
            if (font.Type3 is { } t3)
            {
                if (st.Render != 3 && st.Render != 7) Type3Glyph(t3, font, code, trm, res);
                advance = font.FontMatrix.Delta(font.Width(code), 0).X;
            }
            else
            {
                var glyph = font.Glyph(code);
                if (!glyph.IsEmpty) outline.Segs.AddRange(glyph.Transformed(trm).Segs);
                advance = font.Width(code) / 1000;
            }
            var tx = (advance * size + st.CharSpace + (bytes == 1 && code == 32 ? st.WordSpace : 0)) * st.Scale;
            _tm = new PsMatrix(1, 0, 0, 1, tx, 0).Then(_tm);
        }
        if (font.Note is { } note) Note(note);
        if (outline.IsEmpty) return;
        var mode = st.Render;
        if (mode is 0 or 2 or 4 or 6)
        {
            var start = Ops.Count;
            if (G.Space == "Pattern") FillWithPattern(outline.Clone(), false);
            else Ops.Add(new PsFill(outline.Clone(), G.Colour, false, [.. G.Clips]));
            Transparency(start, st.FillAlpha);
        }
        if (mode is 1 or 2 or 5 or 6 && G.Ctm.Inverse() is { } inv)
        {
            var start = Ops.Count;
            Stroking(() => Ops.Add(new PsStroke(outline.Transformed(inv), G.Ctm, Pen, G.Space == "Pattern" ? G.PatternColour ?? PsColour.Black : G.Colour, [.. G.Clips])));
            Transparency(start, st.StrokeAlpha);
        }
        if (mode >= 4) (_textClip ??= new PsPath()).Segs.AddRange(outline.Segs);
    }

    /// <summary>A Type 3 glyph: its content stream run in glyph space, at the place the text matrix puts it.</summary>
    private void Type3Glyph(PsDict font, PdfFont pf, int code, PsMatrix trm, PsDict res)
    {
        if (_pdf is not { } r || _pdfDepth >= 24) return;
        pf.Names ??= PdfEncoding(font, null);
        var name = code < pf.Names.Length ? pf.Names[code] : null;
        if (name is null || (font.Get("CharProcs") as PsDict)?.Get(name) is not PsDict proc) return;
        _pdfDepth++;
        Gsave();
        var keepUncoloured = _uncolouredGlyph;
        var (keepTm, keepTlm) = (_tm, _tlm);
        try
        {
            G.Ctm = pf.FontMatrix.Then(trm);
            NewPath();
            _uncolouredGlyph = false;
            RunPdf(r.Decode(proc), font.Get("Resources") as PsDict ?? res);
        }
        finally
        {
            (_tm, _tlm) = (keepTm, keepTlm);
            _uncolouredGlyph = keepUncoloured;
            GrestoreOne();
            _pdfDepth--;
        }
    }
}
