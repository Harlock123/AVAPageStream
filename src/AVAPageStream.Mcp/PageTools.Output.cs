using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAPageStream.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAPageStream.Mcp;

/// <summary>The tools for looking at and sending out the document: pictures of pages, preflight, PDF and PNG.</summary>
public sealed partial class PageTools
{
    [McpServerTool(Name = "render_page", ReadOnly = true, Idempotent = true)]
    [Description("A picture of a page (or a master) as it prints, with its master's objects under it: look at it to check the layout reads well (nothing overlapping by mistake, text fitting its frames, even margins and spacing, pictures where they belong). Guides and frame edges don't show, as in print.")]
    public async Task<CallToolResult> RenderPage(
        [Description(PageHelp)] string? page = null,
        [Description("Dots per inch, from 24 to 150. Default 60 (a Letter page is then 510 by 660 pixels).")] double dpi = 60,
        CancellationToken cancel = default)
    {
        if (!double.IsFinite(dpi) || dpi is < 24 or > 150) throw new McpException("dpi is from 24 to 150.");
        var (png, about) = await Run("drew a page", s =>
        {
            var d = s.Editor.Doc;
            var at = SheetOf(s, page);
            var sheet = at.Resolve(d);
            var image = host.Library.Picture(d, at, dpi);
            var overset = d.Stories.Values.Where(st => st.Frames.Any(f => sheet.All.Any(o => o.Id == f)) && host.Library.LayoutOf(d, st.Id) is { Overset: true })
                .Select(st => st.Id).ToList();
            var about = $"Page {SheetName(d, at)} ({PageTools.R(d.Setup.PageWidth)} x {PageTools.R(d.Setup.PageHeight)} pt) at {dpi:0} dpi: {sheet.All.Count()} objects."
                + (overset.Count > 0 ? $" Overset (text that doesn't fit): {string.Join(", ", overset)}." : "");
            return (image, about);
        }, cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = about }] };
    }

    [McpServerTool(Name = "preflight", ReadOnly = true, Idempotent = true)]
    [Description("Checks the document for what would go wrong in print: text that doesn't fit its frames (overset), table rows with nowhere to go, pictures missing or with too few pixels for their size (under 150 dpi), linked files missing or changed, artwork at a page's edge that doesn't run out to the bleed, and fonts missing or that can't be embedded. Each problem with its page, object and what to do; serious ones first.")]
    public Task<string> Preflight(CancellationToken cancel = default) =>
        Read("checked the document", s =>
        {
            var d = s.Editor.Doc;
            var issues = host.Library.Preflight(d, s.FilePath is { } f ? Path.GetDirectoryName(f) : host.Root).OrderByDescending(i => i.Serious).ToList();
            return new JsonObject
            {
                ["problems"] = issues.Count,
                ["serious"] = issues.Count(i => i.Serious),
                ["issues"] = new JsonArray(issues.Select(i =>
                {
                    var o = new JsonObject { ["kind"] = i.Kind.ToString(), ["serious"] = i.Serious, ["message"] = i.Message };
                    if (i.Sheet is { } at) o["page"] = SheetName(d, at);
                    if (i.ObjectId.Length > 0) o["object"] = i.ObjectId;
                    if (i.StoryId is { } st) o["story"] = st;
                    return (JsonNode)o;
                }).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "export_pdf")]
    [Description("Exports the document (or some of its pages) as a PDF in the working folder, drawn as the pages print: fonts embedded, pictures at full resolution, a bookmark for each heading, the title and author. Optionally with crop marks and bleed, in CMYK for a printer, or as PDF/X-4. Run preflight first.")]
    public Task<string> ExportPdf(
        [Description("The PDF's file, relative to the working folder; .pdf is added if missing.")] string path,
        [Description("Which pages, numbered from 1, as text such as \"1-3, 5\". Default: all.")] string? pages = null,
        [Description("Crop marks and the bleed round each page. Default false.")] bool marks = false,
        [Description("CMYK colour for a printer (spot swatches as their own inks). Default false: RGB, for the screen.")] bool cmyk = false,
        [Description("PDF/X-4, the printing standard (CMYK, with an output intent). Default false.")] bool pdfx = false,
        [Description("Write over an existing file. Default false.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        RunAsync("exported a PDF", async s =>
        {
            var d = s.Editor.Doc;
            var full = _files.Resolve(path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? path : path + ".pdf");
            List<int>? chosen = null;
            if (pages is not null)
            {
                if (!PageRanges.TryParse(pages, d.Pages.Count, out var list, out var error)) throw new ArgumentException($"pages: {error}");
                chosen = list;
            }
            _files.CheckWritable(full, overwrite);
            await host.Library.ExportPdf(d, full, chosen, marks, cmyk || pdfx, pdfx);
            _files.Wrote(full);
            return new JsonObject
            {
                ["exported"] = Path.GetRelativePath(host.Root, full),
                ["pages"] = chosen?.Count ?? d.Pages.Count,
                ["bytes"] = new FileInfo(full).Length,
            }.ToJsonString(Json.Options);
        }, cancel);

    [McpServerTool(Name = "export_png")]
    [Description("Exports one page as a PNG picture in the working folder, as it prints.")]
    public Task<string> ExportPng(
        [Description("The PNG's file, relative to the working folder; .png is added if missing.")] string path,
        [Description(PageHelp)] string? page = null,
        [Description("Dots per inch, from 24 to 600. Default 150.")] double dpi = 150,
        [Description("Write over an existing file. Default false.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Read("exported a PNG", s =>
        {
            if (!double.IsFinite(dpi) || dpi is < 24 or > 600) throw new ArgumentException("dpi is from 24 to 600.");
            var full = _files.Resolve(path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path : path + ".png");
            _files.CheckWritable(full, overwrite);
            var at = SheetOf(s, page);
            File.WriteAllBytes(full, host.Library.Picture(s.Editor.Doc, at, dpi));
            _files.Wrote(full);
            return new JsonObject { ["exported"] = Path.GetRelativePath(host.Root, full), ["page"] = SheetName(s.Editor.Doc, at), ["bytes"] = new FileInfo(full).Length };
        }, cancel);
}
