namespace AVAPageStream.Core;

/// <summary>Where objects really are once rotated, and walking the objects inside groups.</summary>
public static class Shapes
{
    /// <summary>A point turned clockwise by <paramref name="degrees"/> about <paramref name="c"/> (y grows downwards).</summary>
    public static Pt Rotate(Pt p, Pt c, double degrees)
    {
        if (degrees == 0) return p;
        var a = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        var (dx, dy) = (p.X - c.X, p.Y - c.Y);
        return new Pt(c.X + dx * cos - dy * sin, c.Y + dx * sin + dy * cos);
    }

    public static Pt Centre(PageObject o) => new(o.X + o.W / 2, o.Y + o.H / 2);

    /// <summary>A page point in the object's own, unrotated, frame.</summary>
    public static Pt ToLocal(PageObject o, Pt p) => Rotate(p, Centre(o), -o.Rotation);

    /// <summary>A point of the object's unrotated frame where it is on the page.</summary>
    public static Pt ToPage(PageObject o, Pt p) => Rotate(p, Centre(o), o.Rotation);

    /// <summary>The frame's corners on the page: top-left, top-right, bottom-right, bottom-left.</summary>
    public static Pt[] Corners(PageObject o)
    {
        var b = o.Bounds;
        return [ToPage(o, new Pt(b.X, b.Y)), ToPage(o, new Pt(b.Right, b.Y)), ToPage(o, new Pt(b.Right, b.Bottom)), ToPage(o, new Pt(b.X, b.Bottom))];
    }

    /// <summary>The box an object covers on the page: its frame, or the box round its frame once rotated.</summary>
    public static Box Extent(PageObject o)
    {
        if (o.Rotation == 0) return o.Bounds;
        var c = Corners(o);
        return Box.FromCorners(new Pt(c.Min(p => p.X), c.Min(p => p.Y)), new Pt(c.Max(p => p.X), c.Max(p => p.Y)));
    }

    /// <summary>
    /// What text keeps out of round a wrapping object: its turned box widened by the standoff, and an outline inside
    /// that when it is turned or wraps by shape (an ellipse's curve, a closed path's outline, pushed out by the standoff).
    /// </summary>
    public static Keep KeepOf(PageObject o)
    {
        var gap = o.WrapGap;
        IReadOnlyList<Pt>? outline = null;
        if (o.WrapShape && o is EllipseObject)
        {
            var c = Centre(o);
            var (rx, ry) = (o.W / 2 + gap, o.H / 2 + gap);
            outline = Enumerable.Range(0, 48).Select(i => i * Math.PI * 2 / 48)
                .Select(a => ToPage(o, new Pt(c.X + rx * Math.Cos(a), c.Y + ry * Math.Sin(a)))).ToList();
        }
        else if (o.WrapShape && o is PathObject { Closed: true, Nodes.Count: > 2 } path)
        {
            var pts = Flatten(path);
            var c = new Pt(pts.Average(p => p.X), pts.Average(p => p.Y));
            outline = pts.Select(p =>
            {
                var (dx, dy) = (p.X - c.X, p.Y - c.Y);
                var len = Math.Sqrt(dx * dx + dy * dy);
                return ToPage(o, len == 0 ? p : new Pt(p.X + dx / len * gap, p.Y + dy / len * gap));
            }).ToList();
        }
        else if (o.Rotation != 0)
        {
            var b = o.Bounds.Inflate(gap);
            outline = [ToPage(o, new Pt(b.X, b.Y)), ToPage(o, new Pt(b.Right, b.Y)), ToPage(o, new Pt(b.Right, b.Bottom)), ToPage(o, new Pt(b.X, b.Bottom))];
        }
        if (outline is null) return new Keep(o.WrapBox);
        var bounds = Box.FromCorners(new Pt(outline.Min(p => p.X), outline.Min(p => p.Y)), new Pt(outline.Max(p => p.X), outline.Max(p => p.Y)));
        return new Keep(bounds, outline);
    }

    /// <summary>A path's outline as straight segments, each curve cut into twelve: all its parts, or only its first.</summary>
    public static List<Pt> Flatten(PathObject path, bool firstPart = false)
    {
        var pts = new List<Pt>();
        foreach (var part in path.Parts())
        {
            Flatten(part, path.Closed, pts);
            if (firstPart) break;
        }
        return pts;
    }

    private static void Flatten(List<PathNode> n, bool closed, List<Pt> pts)
    {
        void Segment(PathNode a, PathNode b)
        {
            if (a.Out is null && b.In is null) { pts.Add(b.At); return; }
            var (p0, p1, p2, p3) = (a.At, a.Out ?? a.At, b.In ?? b.At, b.At);
            for (var i = 1; i <= 12; i++)
            {
                var t = i / 12.0;
                var u = 1 - t;
                pts.Add(new Pt(u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                               u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
            }
        }
        if (n.Count == 0) return;
        pts.Add(n[0].At);
        for (var i = 1; i < n.Count; i++) Segment(n[i - 1], n[i]);
        if (closed) Segment(n[^1], n[0]);
    }

    /// <summary>Objects and everything inside their groups, in the order they are drawn (a group before what it holds).</summary>
    public static IEnumerable<PageObject> Flatten(IEnumerable<PageObject> objects)
    {
        foreach (var o in objects)
        {
            yield return o;
            if (o is GroupObject g)
                foreach (var c in Flatten(g.Children)) yield return c;
        }
    }

    /// <summary>The list an object is in: its sheet's objects, or the children of the group holding it.</summary>
    public static List<PageObject>? ContainerOf(List<PageObject> top, string id)
    {
        if (top.Any(o => o.Id == id)) return top;
        foreach (var g in top.OfType<GroupObject>())
            if (ContainerOf(g.Children, id) is { } found) return found;
        return null;
    }

    /// <summary>The groups holding an object, innermost first.</summary>
    public static List<GroupObject> GroupsAround(List<PageObject> top, string id)
    {
        foreach (var g in top.OfType<GroupObject>())
        {
            if (g.Children.Any(c => c.Id == id)) return [g];
            if (GroupsAround(g.Children, id) is { Count: > 0 } inner) return [.. inner, g];
        }
        return [];
    }
}
