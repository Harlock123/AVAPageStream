using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAPageStream.Core;

/// <summary>
/// The pictures a document uses, keyed by the SHA-256 of their bytes. Images are never changed, only added, so every
/// undo state of a document shares one store.
/// </summary>
public sealed class ImageStore
{
    private readonly Dictionary<string, byte[]> _images = [];

    public string Add(byte[] bytes)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _images.TryAdd(key, bytes);
        return key;
    }

    public void Put(string key, byte[] bytes) => _images[key] = bytes;

    public byte[]? Get(string key) => _images.GetValueOrDefault(key);

    public IEnumerable<string> Keys => _images.Keys;

    /// <summary>The file extension an image's bytes say it is.</summary>
    public static string Extension(byte[] b)
    {
        if (IsSvg(b)) return "svg";
        if (IsEps(b)) return "eps";
        return Raster(b);
    }

    /// <summary>Whether the bytes are an SVG drawing: XML whose first element is &lt;svg&gt;.</summary>
    public static bool IsSvg(byte[] b)
    {
        var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 2048)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return (head.StartsWith("<?xml", StringComparison.Ordinal) || head.StartsWith("<svg", StringComparison.Ordinal) || head.StartsWith("<!--", StringComparison.Ordinal))
               && head.Contains("<svg", StringComparison.Ordinal);
    }

    /// <summary>Whether the bytes are Encapsulated PostScript: text starting %!PS-Adobe … EPSF, or the DOS binary form with a preview.</summary>
    public static bool IsEps(byte[] b) =>
        b is [0xC5, 0xD0, 0xD3, 0xC6, ..] ||
        (b is [(byte)'%', (byte)'!', ..] && System.Text.Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 80)).Split('\n')[0].Contains("EPSF"));

    private static string Raster(byte[] b) => b switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "png",
        [0xFF, 0xD8, ..] => "jpg",
        [0x47, 0x49, 0x46, ..] => "gif",
        [0x42, 0x4D, ..] => "bmp",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "webp",
        _ => "bin",
    };

    public static bool IsImage(byte[] b) => Extension(b) != "bin";
}

/// <summary>
/// A publication: its setup, styles and colours, its stories, master pages and pages. Positions are in points from
/// the top-left of the page an object is on.
/// </summary>
public sealed class Document
{
    public const int FormatVersion = 1;

    public int Version { get; set; } = FormatVersion;
    public Setup Setup { get; set; } = new();
    public List<ParagraphStyle> Styles { get; set; } = [];
    public List<Swatch> Swatches { get; set; } = [];
    public Dictionary<string, Story> Stories { get; set; } = [];
    public List<MasterPage> Masters { get; set; } = [];
    public List<Page> Pages { get; set; } = [];

    /// <summary>The language spelling is checked in ("en-US", "en-GB").</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>Words the spelling checker passes in this document (Ignore all).</summary>
    public List<string> IgnoredWords { get; set; } = [];

    /// <summary>The number the next new id is made from.</summary>
    public int NextId { get; set; } = 1;

    [JsonIgnore]
    public ImageStore Images { get; set; } = new();

    public static List<ParagraphStyle> DefaultStyles() =>
    [
        new("Body", new CharFormat(), new ParaFormat(SpaceAfter: 6)),
        new("Heading", new CharFormat(Fonts.Sans, 24, Bold: true), new ParaFormat(SpaceBefore: 12, SpaceAfter: 6)),
        new("Subheading", new CharFormat(Fonts.Sans, 16, Bold: true), new ParaFormat(SpaceBefore: 9, SpaceAfter: 4)),
        new("Caption", new CharFormat(Fonts.Sans, 9, Italic: true), new ParaFormat()),
    ];

    /// <summary>A new document: one page on the master "A-Master", with the default styles and colours.</summary>
    public static Document New(Setup? setup = null) => new()
    {
        Setup = setup ?? new Setup(),
        Styles = DefaultStyles(),
        Swatches = Colours.DefaultSwatches(),
        Masters = [new MasterPage()],
        Pages = [new Page()],
    };

    public string NewId(string prefix) => prefix + NextId++;

    public ParagraphStyle Style(string name) =>
        Styles.FirstOrDefault(s => s.Name == name) ?? Styles.FirstOrDefault() ?? DefaultStyles()[0];

    public MasterPage? Master(string? name) => name is null ? null : Masters.FirstOrDefault(m => m.Name == name);

    /// <summary>The master sheet drawn under a page (in a facing document, a left page's master's left side).</summary>
    public Sheet? MasterFor(Page page) =>
        Master(page.Master) is { } m ? (IsLeft(page) && m.Left is { } side ? side : m) : null;

    /// <summary>Whether a page is a left-hand page: in a facing document, every even page (page 1 is on the right).</summary>
    public bool IsLeft(Page page) => Setup.Facing && (Pages.IndexOf(page) + 1) % 2 == 0;

    /// <summary>Whether a sheet is on the left of its spread: an even page, or a master's left side.</summary>
    public bool IsLeft(Sheet sheet) => Setup.Facing && sheet switch
    {
        Page p => IsLeft(p),
        MasterSide => true,
        _ => false,
    };

    /// <summary>The master a sheet takes its margins and columns from: a page's master, or the master itself (either side).</summary>
    public MasterPage? MasterOf(Sheet sheet) => sheet switch
    {
        Page p => Master(p.Master),
        MasterPage m => m,
        MasterSide side => Masters.FirstOrDefault(m => m.Left == side),
        _ => null,
    };

    /// <summary>
    /// A sheet's margins and column guides: its master's own, or the document's. In a facing document the left and
    /// right margins are the inside and outside ones, so a left-hand page has them the other way round.
    /// </summary>
    public (Box Live, IReadOnlyList<Box> Columns) GuidesFor(Sheet sheet)
    {
        var master = MasterOf(sheet);
        var m = master?.Margins ?? Setup.Margin;
        var columns = Math.Max(1, master?.Columns ?? Setup.Columns);
        var gutter = master?.Gutter ?? Setup.Gutter;
        var (left, right) = IsLeft(sheet) ? (m.Right, m.Left) : (m.Left, m.Right);
        var live = new Box(left, m.Top, Setup.PageWidth - left - right, Setup.PageHeight - m.Top - m.Bottom);
        var w = (live.W - gutter * (columns - 1)) / columns;
        return (live, Enumerable.Range(0, columns).Select(i => new Box(live.X + i * (w + gutter), live.Y, w, live.H)).ToList());
    }

    /// <summary>Gives every master a left side, as a facing document needs.</summary>
    public void EnsureSides()
    {
        foreach (var m in Masters) m.Left ??= new MasterSide();
    }

    /// <summary>Every sheet: the masters (with their left sides) and the pages.</summary>
    public IEnumerable<Sheet> Sheets => Masters.SelectMany(m => m.Left is { } side ? new Sheet[] { side, m } : [m]).Concat(Pages);

    /// <summary>Every object, those inside groups too.</summary>
    public IEnumerable<PageObject> AllObjects => Sheets.SelectMany(s => s.All);

    public PageObject? Find(string id) => AllObjects.FirstOrDefault(o => o.Id == id);

    /// <summary>The page or master an object is on.</summary>
    public Sheet? SheetOf(string id) => Sheets.FirstOrDefault(s => s.All.Any(o => o.Id == id));

    /// <summary>The groups an object is inside, innermost first, and refits each to what it now holds.</summary>
    public void Refit(string id)
    {
        if (SheetOf(id) is not { } sheet) return;
        foreach (var g in Shapes.GroupsAround(sheet.Objects, id)) g.Fit();
    }

    public Story? StoryOf(TextFrame frame) => Stories.GetValueOrDefault(frame.StoryId);

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions Compact = new(Json) { WriteIndented = false };

    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this, indented ? Json : Compact);

    /// <summary>Reads a document's JSON; its images are given separately.</summary>
    public static Document FromJson(string json, ImageStore images)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, Json) ?? throw new InvalidDataException("The document is empty.");
        doc.Images = images;
        return doc;
    }

    /// <summary>A copy that shares nothing but the image store.</summary>
    public Document Clone() => FromJson(JsonSerializer.Serialize(this, Compact), Images);
}
