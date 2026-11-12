using System.Text.Json.Serialization;

namespace AVAPageStream.Core;

/// <summary>A page's margins, in points.</summary>
public sealed record Margins(double Top, double Bottom, double Left, double Right)
{
    public static readonly Margins Half = new(36, 36, 36, 36);
}

/// <summary>A standard page size, portrait, in points.</summary>
public sealed record PaperSize(string Name, double Width, double Height)
{
    public static readonly PaperSize[] All =
    [
        new("Letter", 612, 792),
        new("Legal", 612, 1008),
        new("Tabloid", 792, 1224),
        new("A3", 841.89, 1190.55),
        new("A4", 595.28, 841.89),
        new("A5", 419.53, 595.28),
        new("B5", 498.9, 708.66),
    ];

    /// <summary>The standard size a page is, either way round, or null for a custom size.</summary>
    public static PaperSize? Match(double width, double height) => All.FirstOrDefault(p =>
        (Near(p.Width, width) && Near(p.Height, height)) || (Near(p.Width, height) && Near(p.Height, width)));

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
}

/// <summary>The document's page size, margins and columns, and the unit measures are shown in.</summary>
public sealed record Setup(
    double PageWidth = 612,
    double PageHeight = 792,
    Margins? Margins = null,
    int Columns = 1,
    double Gutter = 18,
    Unit Unit = Unit.Point,
    double Grid = 18,      // the document grid's spacing, in points
    bool Facing = false)   // pages in spreads: odd pages on the right, margins inside and outside
{
    public Margins Margin => Margins ?? Margins.Half;

    public Box Page => new(0, 0, PageWidth, PageHeight);

    /// <summary>The page inside its margins.</summary>
    public Box Live => new(Margin.Left, Margin.Top, PageWidth - Margin.Left - Margin.Right, PageHeight - Margin.Top - Margin.Bottom);

    /// <summary>The column guides' boxes inside the margins.</summary>
    public IReadOnlyList<Box> ColumnBoxes()
    {
        var live = Live;
        var n = Math.Max(1, Columns);
        var w = (live.W - Gutter * (n - 1)) / n;
        return Enumerable.Range(0, n).Select(i => new Box(live.X + i * (w + Gutter), live.Y, w, live.H)).ToList();
    }
}

/// <summary>A ruler guide on a page: a vertical one at an X, or a horizontal one at a Y.</summary>
public sealed record Guide(bool Vertical, double Position);

/// <summary>A page or a master: objects drawn in order (the last on top) and ruler guides.</summary>
public abstract class Sheet
{
    public List<PageObject> Objects { get; set; } = [];

    /// <summary>The objects and everything inside their groups, in drawing order.</summary>
    [JsonIgnore]
    public IEnumerable<PageObject> All => Shapes.Flatten(Objects);
    public List<Guide> Guides { get; set; } = [];
}

/// <summary>
/// Which page or master is meant, by position, so it can be found again in whichever copy of the document undo has
/// made current.
/// </summary>
public readonly record struct SheetRef(bool IsMaster, int Index, bool Left = false)
{
    public static SheetRef PageAt(int index) => new(false, index);
    public static SheetRef MasterAt(int index, bool left = false) => new(true, index, left);

    /// <summary>The page, or the master (its left side, for a left-hand master that has one).</summary>
    public Sheet Resolve(Document d)
    {
        if (!IsMaster) return d.Pages[Math.Clamp(Index, 0, d.Pages.Count - 1)];
        var m = d.Masters[Math.Clamp(Index, 0, d.Masters.Count - 1)];
        return Left && m.Left is { } side ? side : m;
    }
}

/// <summary>
/// A page of objects drawn under every page that uses it. In a facing document it has a left side too, drawn under
/// the left-hand (even) pages; it may set its own margins and columns in place of the document's.
/// </summary>
public sealed class MasterPage : Sheet
{
    public string Name { get; set; } = "A-Master";

    /// <summary>The master's left-hand side, for facing documents.</summary>
    public MasterSide? Left { get; set; }

    /// <summary>Margins, columns and gutter of its own for the pages using it, or null for the document's.</summary>
    public Margins? Margins { get; set; }
    public int? Columns { get; set; }
    public double? Gutter { get; set; }
}

/// <summary>The left-hand side of a master page.</summary>
public sealed class MasterSide : Sheet
{
}

public sealed class Page : Sheet
{
    /// <summary>The master page under this page, by name, or null for none.</summary>
    public string? Master { get; set; } = "A-Master";
}

public enum Fit { Stretch, Fit, Fill, Actual }

/// <summary>Something on a page in a frame: its top-left corner and size, fill and stroke.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RectangleObject), "rectangle")]
[JsonDerivedType(typeof(EllipseObject), "ellipse")]
[JsonDerivedType(typeof(LineObject), "line")]
[JsonDerivedType(typeof(TextFrame), "text")]
[JsonDerivedType(typeof(PictureObject), "picture")]
[JsonDerivedType(typeof(PathObject), "path")]
[JsonDerivedType(typeof(GroupObject), "group")]
[JsonDerivedType(typeof(TableObject), "table")]
public abstract class PageObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string? Fill { get; set; }
    public string? Stroke { get; set; } = Colours.Black;
    public double StrokeWidth { get; set; } = 1;
    public bool Locked { get; set; }

    /// <summary>Whether text in other frames on the page flows round this object, keeping <see cref="WrapGap"/> away.</summary>
    public bool Wrap { get; set; }
    public double WrapGap { get; set; } = 6;

    /// <summary>Whether text follows the object's shape (an ellipse's curve, a path's outline) rather than its frame.</summary>
    public bool WrapShape { get; set; }

    /// <summary>The box text keeps out of when this object wraps.</summary>
    [JsonIgnore]
    public Box WrapBox => Shapes.Extent(this).Inflate(WrapGap);

    /// <summary>Turned clockwise about the frame's centre, in degrees.</summary>
    public double Rotation { get; set; }

    /// <summary>
    /// The frame, unrotated. Setting it moves and resizes the object (a group moves and scales what is in it, a path
    /// its points), so every move and resize goes through here.
    /// </summary>
    [JsonIgnore]
    public Box Bounds
    {
        get => new(X, Y, W, H);
        set => SetBounds(value);
    }

    protected virtual void SetBounds(Box b)
    {
        X = b.X;
        Y = b.Y;
        W = b.W;
        H = b.H;
    }

    public void MoveBy(double dx, double dy) => Bounds = Bounds.Offset(dx, dy);

    /// <summary>A point's place when a box is mapped onto another (for scaling what is inside a group or path).</summary>
    protected static Pt Map(Pt p, Box from, Box to) => new(
        to.X + (from.W == 0 ? p.X - from.X : (p.X - from.X) * to.W / from.W),
        to.Y + (from.H == 0 ? p.Y - from.Y : (p.Y - from.Y) * to.H / from.H));

    /// <summary>What this kind of object is called in the window.</summary>
    [JsonIgnore]
    public abstract string Kind { get; }
}

public sealed class RectangleObject : PageObject
{
    public double CornerRadius { get; set; }
    public override string Kind => "Rectangle";
}

public sealed class EllipseObject : PageObject
{
    public override string Kind => "Ellipse";
}

/// <summary>A line from corner to corner of its frame: top-left to bottom-right, or bottom-left to top-right if it rises.</summary>
public sealed class LineObject : PageObject
{
    public bool Rising { get; set; }

    /// <summary>Arrowheads at the line's start and end.</summary>
    public bool ArrowStart { get; set; }
    public bool ArrowEnd { get; set; }
    public override string Kind => "Line";

    public (Pt From, Pt To) Ends => Rising ? (new Pt(X, Y + H), new Pt(X + W, Y)) : (new Pt(X, Y), new Pt(X + W, Y + H));
}

/// <summary>A frame its story's text flows through, in one or more columns.</summary>
public sealed class TextFrame : PageObject
{
    public TextFrame()
    {
        Stroke = null;
    }

    public string StoryId { get; set; } = "";
    public int Columns { get; set; } = 1;
    public double Gutter { get; set; } = 12;
    public double Inset { get; set; }
    public override string Kind => "Text frame";

    /// <summary>The boxes text fills, column by column.</summary>
    public IEnumerable<Box> ColumnBoxes()
    {
        var n = Math.Max(1, Columns);
        var inner = new Box(X + Inset, Y + Inset, Math.Max(0, W - 2 * Inset), Math.Max(0, H - 2 * Inset));
        var w = Math.Max(0, (inner.W - Gutter * (n - 1)) / n);
        for (var i = 0; i < n; i++) yield return new Box(inner.X + i * (w + Gutter), inner.Y, w, inner.H);
    }
}

/// <summary>A picture from the document's images, fitted to its frame.</summary>
public sealed class PictureObject : PageObject
{
    public PictureObject()
    {
        Stroke = null;
    }

    /// <summary>The image's key in <see cref="Document.Images"/>, or "" for none yet.</summary>
    public string Image { get; set; } = "";
    public Fit Fit { get; set; } = Fit.Fit;

    /// <summary>How far the image is moved from where its fit puts it, in points: cropping a different part into view.</summary>
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    public override string Kind => "Picture";
}

/// <summary>A point of a path, with the Bézier handles that shape the curve into it and out of it (none: a corner).</summary>
public sealed record PathNode(double X, double Y, double? InX = null, double? InY = null, double? OutX = null, double? OutY = null)
{
    public Pt At => new(X, Y);
    public Pt? In => InX is { } x && InY is { } y ? new Pt(x, y) : null;
    public Pt? Out => OutX is { } x && OutY is { } y ? new Pt(x, y) : null;

    public static PathNode Corner(Pt p) => new(p.X, p.Y);

    /// <summary>A smooth point: its handle out where it is dragged to, and the handle in mirrored.</summary>
    public static PathNode Smooth(Pt p, Pt handle) => new(p.X, p.Y, 2 * p.X - handle.X, 2 * p.Y - handle.Y, handle.X, handle.Y);

    public PathNode Moved(Func<Pt, Pt> f)
    {
        var at = f(At);
        var i = In is { } a ? f(a) : (Pt?)null;
        var o = Out is { } b ? f(b) : (Pt?)null;
        return new(at.X, at.Y, i?.X, i?.Y, o?.X, o?.Y);
    }
}

/// <summary>A polygon or Bézier path: its points in page points, its frame the box round them and their handles.</summary>
public sealed class PathObject : PageObject
{
    public List<PathNode> Nodes { get; set; } = [];

    /// <summary>Whether the last point joins back to the first (only a closed path is filled).</summary>
    public bool Closed { get; set; }

    public override string Kind => Nodes.All(n => n.In is null && n.Out is null) ? (Closed ? "Polygon" : "Polyline") : "Path";

    /// <summary>Sets the frame to the box round the points and handles, without moving them.</summary>
    public void Fit()
    {
        var pts = Nodes.SelectMany(n => new[] { n.At, n.In ?? n.At, n.Out ?? n.At }).ToList();
        if (pts.Count == 0) return;
        var b = Box.FromCorners(new Pt(pts.Min(p => p.X), pts.Min(p => p.Y)), new Pt(pts.Max(p => p.X), pts.Max(p => p.Y)));
        (X, Y, W, H) = (b.X, b.Y, b.W, b.H);
    }

    protected override void SetBounds(Box b)
    {
        var from = Bounds;
        Nodes = Nodes.Select(n => n.Moved(p => Map(p, from, b))).ToList();
        base.SetBounds(b);
    }
}

/// <summary>Objects that move, resize and arrange as one. Its frame is the box round them.</summary>
public sealed class GroupObject : PageObject
{
    public GroupObject()
    {
        Stroke = null;
    }

    public List<PageObject> Children { get; set; } = [];

    public override string Kind => "Group";

    /// <summary>Sets the frame to the box round what is in the group, without moving it.</summary>
    public void Fit()
    {
        if (Children.Count == 0) return;
        var b = Box.Union(Children.Select(Shapes.Extent));
        (X, Y, W, H) = (b.X, b.Y, b.W, b.H);
    }

    protected override void SetBounds(Box b)
    {
        var from = Bounds;
        foreach (var c in Children)
        {
            var cb = c.Bounds;
            var tl = Map(new Pt(cb.X, cb.Y), from, b);
            var br = Map(new Pt(cb.Right, cb.Bottom), from, b);
            c.Bounds = new Box(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
        }
        base.SetBounds(b);
    }
}

/// <summary>
/// A table: rows and columns of cells, each holding a story of its own (set in the cell, less its inset), with rules
/// between them in the stroke's colour and width, and header rows shaded. Row heights are kept as set: a cell whose
/// text doesn't fit shows it is overset until its row is made taller (or fitted to its text).
/// </summary>
public sealed class TableObject : PageObject
{
    public List<double> ColumnWidths { get; set; } = [];
    public List<double> RowHeights { get; set; } = [];

    /// <summary>The cells' story ids, row by row.</summary>
    public List<string> Cells { get; set; } = [];

    public int HeaderRows { get; set; } = 1;
    public string? HeaderFill { get; set; } = "@Light grey";
    public double CellInset { get; set; } = 4;

    [JsonIgnore] public int Rows => RowHeights.Count;
    [JsonIgnore] public int Columns => ColumnWidths.Count;

    public override string Kind => "Table";

    public TableObject()
    {
        Stroke = Colours.Black;
        StrokeWidth = 0.5;
    }

    /// <summary>The id of a cell as an area of text: the table's id, '#', the cell's number counting row by row.</summary>
    public string CellId(int index) => $"{Id}#{index}";

    /// <summary>A cell's box on the page.</summary>
    public Box CellBox(int row, int column)
    {
        var x = X + ColumnWidths.Take(column).Sum();
        var y = Y + RowHeights.Take(row).Sum();
        return new Box(x, y, ColumnWidths[column], RowHeights[row]);
    }

    public Box CellBox(int index) => CellBox(index / Math.Max(1, Columns), index % Math.Max(1, Columns));

    /// <summary>The cell under a point on the page, or -1.</summary>
    public int CellAt(Pt p)
    {
        if (!Bounds.Contains(p)) return -1;
        var c = 0;
        for (var x = X + ColumnWidths[0]; c < Columns - 1 && p.X > x; x += ColumnWidths[++c]) { }
        var r = 0;
        for (var y = Y + RowHeights[0]; r < Rows - 1 && p.Y > y; y += RowHeights[++r]) { }
        return r * Columns + c;
    }

    /// <summary>Makes the frame match the columns and rows.</summary>
    public void Fit()
    {
        W = ColumnWidths.Sum();
        H = RowHeights.Sum();
    }

    protected override void SetBounds(Box b)
    {
        // Resizing scales the columns and rows.
        if (W > 0) ColumnWidths = ColumnWidths.Select(w => w * b.W / W).ToList();
        if (H > 0) RowHeights = RowHeights.Select(h => h * b.H / H).ToList();
        base.SetBounds(b);
    }
}
