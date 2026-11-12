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
