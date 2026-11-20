using Avalonia;
using Avalonia.Media.Imaging;
using AVAPageStream.Core;
using AVAPageStream.Mcp;

namespace AVAPageStream.Agents;

/// <summary>
/// What the MCP server needs from the program, done as the window does it: text measured by <see cref="FontMeasurer"/>,
/// pages drawn by <see cref="PageRenderer"/> (as they print), PDFs written by <see cref="Export"/>, and preflight with the
/// pictures' pixels and the fonts' states. Used on Avalonia's UI thread only.
/// </summary>
internal sealed class AgentLibrary : IPageLibrary
{
    private readonly Layouts _layouts = new(FontMeasurer.Shared);
    private IReadOnlyList<string>? _fonts;

    public ITextMeasurer Measurer => FontMeasurer.Shared;

    public IReadOnlyList<string> Fonts => _fonts ??= FontSupport.Available();

    public TextLayout? LayoutOf(Document doc, string storyId) => _layouts.Of(doc, storyId);

    public byte[] Picture(Document doc, SheetRef sheet, double dpi)
    {
        var renderer = new PageRenderer(_layouts) { Screen = false };
        var pixels = new PixelSize((int)Math.Ceiling(doc.Setup.PageWidth * dpi / 72), (int)Math.Ceiling(doc.Setup.PageHeight * dpi / 72));
        var scale = dpi * 96.0 / 72; // one of the bitmap's device-independent units is then one point
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(scale, scale));
        bitmap.Render(new PageVisual(doc, sheet.Resolve(doc), renderer));
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    public (double Width, double Height)? PictureSize(byte[] bytes) =>
        Pictures.Load(bytes)?.Size is { } size ? (size.Width, size.Height) : null;

    public Task ExportPdf(Document doc, string path, IReadOnlyList<int>? pages, bool marks, bool cmyk, bool pdfx) =>
        Export.Pdf(doc, path, pages, Path.GetFileNameWithoutExtension(path), marks, cmyk, pdfx);

    public IReadOnlyList<Issue> Preflight(Document doc, string? folder)
    {
        var pictures = new PageRenderer(_layouts);
        return Core.Preflight.Check(doc, new PreflightOptions(FontMeasurer.Shared, key => pictures.PixelsOf(doc, key), FontSupport.State, folder));
    }
}
