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
    Unit Unit = Unit.Point)
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
    public List<Guide> Guides { get; set; } = [];
}

/// <summary>
/// Which page or master is meant, by position, so it can be found again in whichever copy of the document undo has
/// made current.
/// </summary>
public readonly record struct SheetRef(bool IsMaster, int Index)
{
    public static SheetRef PageAt(int index) => new(false, index);
    public static SheetRef MasterAt(int index) => new(true, index);

    public Sheet Resolve(Document d) => IsMaster
        ? d.Masters[Math.Clamp(Index, 0, d.Masters.Count - 1)]
        : d.Pages[Math.Clamp(Index, 0, d.Pages.Count - 1)];
}

/// <summary>A page of objects drawn under every page that uses it.</summary>
public sealed class MasterPage : Sheet
{
    public string Name { get; set; } = "A-Master";
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

    [JsonIgnore]
    public Box Bounds
    {
        get => new(X, Y, W, H);
        set { X = value.X; Y = value.Y; W = value.W; H = value.H; }
    }

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
