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
    public static string Extension(byte[] b) => b switch
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

    public IEnumerable<Sheet> Sheets => Masters.Cast<Sheet>().Concat(Pages);

    public IEnumerable<PageObject> AllObjects => Sheets.SelectMany(s => s.Objects);

    public PageObject? Find(string id) => AllObjects.FirstOrDefault(o => o.Id == id);

    /// <summary>The page or master an object is on.</summary>
    public Sheet? SheetOf(string id) => Sheets.FirstOrDefault(s => s.Objects.Any(o => o.Id == id));

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
