namespace AVAPageStream.Core;

/// <summary>Where a picture goes in its frame.</summary>
public static class Fitting
{
    /// <summary>The box an image of a size (in points) takes in its frame, for a fit (before any offset).</summary>
    public static Box Place(Fit fit, double width, double height, Box frame)
    {
        if (width <= 0 || height <= 0) return frame;
        switch (fit)
        {
            case Fit.Stretch: return frame;
            case Fit.Actual: return new Box(frame.X, frame.Y, width, height);
            default:
                var sx = frame.W / width;
                var sy = frame.H / height;
                var s = fit == Fit.Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
                var (w, h) = (width * s, height * s);
                return new Box(frame.X + (frame.W - w) / 2, frame.Y + (frame.H - h) / 2, w, h);
        }
    }
}

/// <summary>What preflight finds.</summary>
public enum Problem { Overset, RowsOverset, NoPicture, LinkMissing, LinkChanged, LowResolution, ShortOfBleed, NoBleed, MissingFont, NoEmbedding }

/// <summary>A font as the computer has it: in the program, installed, not there, or not to be embedded in a PDF.</summary>
public enum FontState { Bundled, Installed, Missing, NoEmbedding }

/// <summary>A picture's pixels, and its size in points before it is fitted to its frame.</summary>
public readonly record struct PictureSize(int Width, int Height, double Points, double PointsHigh);

/// <summary>Something that would go wrong in print: what, where (the sheet and object, and the place in a story), and why.</summary>
public sealed record Issue(Problem Kind, SheetRef? Sheet, string ObjectId, string Message, string? StoryId = null, int At = 0)
{
    public bool Serious => Kind is Problem.Overset or Problem.RowsOverset or Problem.LinkMissing or Problem.NoPicture or Problem.MissingFont or Problem.NoEmbedding;
}

/// <summary>What preflight needs from the program: measuring, pictures' pixel sizes and the fonts on this computer.</summary>
public sealed record PreflightOptions(
    ITextMeasurer Measurer,
    Func<string, PictureSize?> Pixels,
    Func<string, FontState> Font,
    string? Folder = null,
    double MinDpi = 150);

/// <summary>
/// Checks a document for what would go wrong when it is printed or sent out: text that doesn't fit, table rows with
/// nowhere to go, pictures missing or with too few pixels for their printed size, linked files missing or changed,
/// artwork at the page's edge that doesn't run out to the bleed, and fonts that aren't there or can't be embedded.
/// </summary>
public static class Preflight
{
    public static List<Issue> Check(Document doc, PreflightOptions o)
    {
        var issues = new List<Issue>();
        var sheets = new List<(SheetRef Ref, Sheet Sheet, string Name)>();
        for (var i = 0; i < doc.Pages.Count; i++) sheets.Add((SheetRef.PageAt(i), doc.Pages[i], $"Page {doc.PageLabel(i)}"));
        for (var i = 0; i < doc.Masters.Count; i++) sheets.Add((SheetRef.MasterAt(i), doc.Masters[i], doc.Masters[i].Name));
        SheetRef? SheetOf(string id) => sheets.FirstOrDefault(s => s.Sheet.All.Any(x => x.Id == id)).Ref;
        string Where(string id) => sheets.FirstOrDefault(s => s.Sheet.All.Any(x => x.Id == id)).Name ?? "";

        Text(doc, o, issues, SheetOf, Where);
        foreach (var (sref, sheet, name) in sheets)
            foreach (var obj in Shapes.Flatten(sheet.Objects).Where(x => doc.LayerOf(x) is { Visible: true, Print: true }))
            {
                if (obj is TableObject { Continues: null } head && Tables.Place(doc, head) is { } placed && placed.Rows.Count(r => r.Frame < 0) is var left and > 0)
                {
                    var last = placed.Frames[^1];
                    issues.Add(new Issue(Problem.RowsOverset, SheetOf(last.Id), last.Id, $"{Where(last.Id)}: {left} table row{(left == 1 ? " has" : "s have")} no frame to go in: run the table on, or make its frames taller."));
                }
                if (obj is PictureObject picture) Picture(doc, o, picture, sref, name, issues);
                Bleed(doc, obj, sref, name, issues);
            }
        Fonts(doc, o, issues, SheetOf, Where);
        return issues;
    }

    private static void Text(Document doc, PreflightOptions o, List<Issue> issues, Func<string, SheetRef?> sheetOf, Func<string, string> where)
    {
        foreach (var id in Search.StoryOrder(doc))
        {
            var story = doc.Stories[id];
            if (story.NoteOf is not null || story.Length == 0) continue;
            if (story.OnPath is { } pathId)
            {
                if (doc.Find(pathId) is PathObject path && PathText.Place(story, PathText.Points(path), o.Measurer, path.TextOffset).OversetFrom is var from && from < story.Length)
                    issues.Add(new Issue(Problem.Overset, sheetOf(pathId), pathId, $"{where(pathId)}: {story.Length - from} characters run past the end of the path.", id, from));
                continue;
            }
            var page = Chains.PageOf(doc, story);
            var fields = new FieldContext(page, doc.Pages.Count, page > 0 ? doc.PageLabel(page - 1) : null);
            var layout = TextFlow.Layout(story, Chains.Areas(doc, story), o.Measurer, fields, story.Table is null ? doc.Setup.Baselines : null, Footnotes.Heights(doc, o.Measurer));
            if (!layout.Overset) continue;
            var shownIn = story.Table is { } t ? (layout.Areas.Count > 0 ? Chains.ObjectOf(layout.Areas[^1].FrameId) : t) : story.Frames.LastOrDefault() ?? "";
            var what = story.Table is not null ? "a table's cell has more text than fits it: make its row taller (Fit rows to text)" : "the text doesn't all fit: link the last frame on to another, or make it bigger";
            issues.Add(new Issue(Problem.Overset, sheetOf(shownIn), shownIn, $"{where(shownIn)}: {what} ({story.Length - layout.OversetFrom} characters over).", id, layout.OversetFrom));
        }
    }

    private static void Picture(Document doc, PreflightOptions o, PictureObject p, SheetRef sheet, string name, List<Issue> issues)
    {
        var file = p.Source is null ? "the picture" : Path.GetFileName(p.Source);
        switch (Links.StateOf(doc, p, o.Folder))
        {
            case LinkState.Missing:
                issues.Add(new Issue(Problem.LinkMissing, sheet, p.Id, $"{name}: the linked file {file} can't be found: put it beside the document, or replace the picture."));
                return;
            case LinkState.Changed:
                issues.Add(new Issue(Problem.LinkChanged, sheet, p.Id, $"{name}: {file} has changed since it was placed: Update links."));
                break;
        }
        if (p.Image.Length == 0 || doc.Images.Get(p.Image) is null)
        {
            issues.Add(new Issue(Problem.NoPicture, sheet, p.Id, $"{name}: a picture frame has no picture in it."));
            return;
        }
        if (o.Pixels(p.Shown) is not { Width: > 0, Height: > 0 } px) return; // a drawing: any size
        var drawn = Fitting.Place(p.Fit, px.Points, px.PointsHigh, p.Bounds);
        var dpi = Math.Min(px.Width / (drawn.W / 72), px.Height / (drawn.H / 72));
        if (dpi < o.MinDpi - 0.5)
            issues.Add(new Issue(Problem.LowResolution, sheet, p.Id,
                $"{name}: {file} is {px.Width}×{px.Height} pixels, only {Math.Round(dpi)} dots an inch at the size it is printed (at least {o.MinDpi} is best). Use a bigger picture, or print it smaller."));
    }

    private static void Bleed(Document doc, PageObject obj, SheetRef sheet, string name, List<Issue> issues)
    {
        // Only what shows as a solid patch: filled shapes and frames, and pictures.
        if (obj is GroupObject || obj is LineObject || (obj is not PictureObject && obj.Fill is null)) return;
        var e = Shapes.Extent(obj);
        var s = doc.Setup;
        var bleed = s.Bleed;
        // How far past each edge of the page it reaches (negative: short of the edge).
        double[] past = [-e.X, -e.Y, e.Right - s.PageWidth, e.Bottom - s.PageHeight];
        var atEdge = past.Where(d => d > -0.5).ToList();
        if (atEdge.Count == 0 || e.Right <= 0 || e.Bottom <= 0 || e.X >= s.PageWidth || e.Y >= s.PageHeight) return;
        if (bleed <= 0)
            issues.Add(new Issue(Problem.NoBleed, sheet, obj.Id, $"{name}: a {obj.Kind.ToLowerInvariant()} runs to the edge of the page, but the document has no bleed: set one in the Document tab (9 pt is usual) and let it run out past the edge."));
        else if (atEdge.Any(d => d < bleed - 0.5))
            issues.Add(new Issue(Problem.ShortOfBleed, sheet, obj.Id, $"{name}: a {obj.Kind.ToLowerInvariant()} reaches the edge of the page but not the bleed, so a sliver of white may show when it is cut: run it out {Units.Format(bleed, s.Unit)} past the edge."));
    }

    private static void Fonts(Document doc, PreflightOptions o, List<Issue> issues, Func<string, SheetRef?> sheetOf, Func<string, string> where)
    {
        var seen = new HashSet<string>();
        foreach (var id in Search.StoryOrder(doc))
        {
            var story = doc.Stories[id];
            var at = 0;
            foreach (var run in story.Runs)
            {
                var font = run.Format.Font;
                var start = at;
                at += run.Length;
                if (!seen.Add(font)) continue;
                var state = o.Font(font);
                if (state is FontState.Bundled or FontState.Installed) continue;
                var shown = story.Frames.LastOrDefault() ?? story.OnPath ?? (story.NoteOf is { } n ? doc.Stories.GetValueOrDefault(n)?.Frames.FirstOrDefault() : null)
                    ?? (story.Table is { } t ? t : "");
                issues.Add(state == FontState.Missing
                    ? new Issue(Problem.MissingFont, sheetOf(shown), shown, $"{where(shown)}: the font {font} isn't on this computer, so its text is set in Liberation Serif instead. Install it, or choose another.", id, Math.Min(start, story.Length))
                    : new Issue(Problem.NoEmbedding, sheetOf(shown), shown, $"{where(shown)}: the font {font}'s licence doesn't allow it to be put in a PDF. Choose another for anything you send out.", id, Math.Min(start, story.Length)));
            }
        }
    }
}
