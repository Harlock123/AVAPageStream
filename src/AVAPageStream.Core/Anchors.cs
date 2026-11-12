namespace AVAPageStream.Core;

/// <summary>How an anchored object is placed: in the line, or against its anchor's line.</summary>
public enum AnchorKind { Inline, Custom }

/// <summary>Where a custom-placed object is measured from across: the column's left edge, its right edge, or the anchor.</summary>
public enum AnchorFrom { ColumnLeft, ColumnRight, Anchor }

/// <summary>
/// How an object anchored in text sits. Inline: on the baseline, <see cref="Offset"/> up (or down, negative), taking
/// its width in the line. Custom: its top-left <see cref="X"/> from <see cref="From"/> and <see cref="Y"/> below the
/// top of its anchor's line (or its baseline), taking no room in the line; it wraps text if it wraps.
/// </summary>
public sealed record Anchoring(AnchorKind Kind = AnchorKind.Inline, double Offset = 0, AnchorFrom From = AnchorFrom.Anchor, double X = 0, double Y = 0, bool FromBaseline = false);

/// <summary>Objects anchored in text: placed where their markers are set, made, let go, copied.</summary>
public static class Anchors
{
    /// <summary>The anchored object a marker names, if it is set in the line.</summary>
    public static PageObject? Inline(Story story, string id) =>
        story.Anchored.FirstOrDefault(o => o.Id == id) is { Anchoring.Kind: not AnchorKind.Custom } o ? o : null;

    /// <summary>
    /// Puts a story's anchored objects where its layout sets their markers: in the line, or against it. Each is given
    /// the frame it is drawn in; one whose marker isn't set (overset) is drawn nowhere.
    /// </summary>
    public static void Place(Story story, TextLayout layout)
    {
        if (story.Anchored.Count == 0) return;
        foreach (var o in story.Anchored) o.PlacedIn = null;
        foreach (var line in layout.Lines)
            foreach (var piece in line.Pieces)
            {
                if (piece.Format.Anchor is not { } id || story.Anchored.FirstOrDefault(o => o.Id == id) is not { } o) continue;
                if (piece.Start >= story.Text.Length || story.Text[piece.Start] != Fields.Anchor) continue;
                var a = o.Anchoring ?? new Anchoring();
                var area = layout.Areas[line.Area];
                double x, y;
                if (a.Kind == AnchorKind.Inline)
                {
                    x = piece.X;
                    y = line.Baseline - o.H - a.Offset;
                }
                else
                {
                    x = a.From switch { AnchorFrom.ColumnLeft => area.Box.X, AnchorFrom.ColumnRight => area.Box.Right, _ => piece.X } + a.X;
                    y = (a.FromBaseline ? line.Baseline : line.Baseline - line.Ascent) + a.Y;
                }
                var b = o.Bounds;
                if (Math.Abs(b.X - x) > 1e-9 || Math.Abs(b.Y - y) > 1e-9) o.MoveBy(x - b.X, y - b.Y);
                o.PlacedIn = Chains.ObjectOf(area.FrameId); // a cell's table, a footnote's frame
            }
    }

    /// <summary>What the layout of a story depends on in its anchored objects: their sizes, placing and wrap.</summary>
    public static string Signature(Story story) => story.Anchored.Count == 0 ? "" :
        string.Join("|", story.Anchored.Select(o => $"{o.Id} {o.W} {o.H} {o.Anchoring} {o.Wrap} {o.WrapGap} {o.WrapShape}"));

    /// <summary>A copied fragment's objects with ids of their own in the document, its markers renamed to match.</summary>
    public static Fragment Rekey(Document d, Fragment f)
    {
        if (f.Objects is not { Count: > 0 } objects) return f;
        var map = new Dictionary<string, string>();
        var copies = new List<PageObject>();
        foreach (var o in objects)
        {
            var copy = Editor.Copy(d, o);
            copy.Anchoring = o.Anchoring;
            map[o.Id] = copy.Id;
            copies.Add(copy);
        }
        var runs = f.Runs.Select(r => r.Format.Anchor is { } a && map.TryGetValue(a, out var n) ? r with { Format = r.Format with { Anchor = n } } : r).ToList();
        return f with { Runs = runs, Objects = copies };
    }
}
