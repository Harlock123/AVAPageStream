using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAPageStream.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AVAPageStream.Mcp;

/// <summary>
/// The MCP tools: what an agent can do with AVAPageStream. Each handler reaches the document only through
/// <see cref="IPageHost.RunAsync{T}"/>, so the same code serves headless and live. Every change is one undo step
/// (<see cref="Editor.Group{T}"/>), a call that fails leaves the document as it was, and failures come back as MCP tool
/// errors (<see cref="McpException"/>) with a message that says what to do instead.
/// </summary>
/// <remarks>The parameter names are the JSON names agents see, hence snake_case.</remarks>
public sealed partial class PageTools(IPageHost host)
{
    private readonly Workspace _files = new(host.Root);

    private const string MeasureHelp = "In points (72 to the inch), or as text with a unit: \"1in\", \"6p\", \"12mm\", \"2.5cm\".";
    private const string PageHelp = "The page, numbered from 1, or a master page's name (\"A-Master\") to work on the master. Default: the page the person is looking at, else page 1.";

    // ───────────────────────────── the document ─────────────────────────────

    [McpServerTool(Name = "get_document", ReadOnly = true, Idempotent = true)]
    [Description("The document as a whole: its file, page size, margins, columns and units; its pages (with their masters and how many objects each has); the master pages; the paragraph and character styles; the swatches; the layers; and its stories (which frames each runs through, how long it is, and whether it's overset: longer than its frames hold). Start here.")]
    public Task<string> GetDocument(CancellationToken cancel = default) =>
        Read("read the document", s =>
        {
            var d = s.Editor.Doc;
            var setup = d.Setup;
            return new JsonObject
            {
                ["file"] = s.FilePath is { } f ? Path.GetRelativePath(host.Root, f) : null,
                ["changed"] = s.Editor.Dirty,
                ["title"] = d.Title,
                ["author"] = d.Author,
                ["page_size"] = new JsonObject
                {
                    ["width"] = R(setup.PageWidth), ["height"] = R(setup.PageHeight),
                    ["paper"] = PaperSize.Match(setup.PageWidth, setup.PageHeight)?.Name ?? "custom",
                },
                ["margins"] = Margins(setup.Margin),
                ["columns"] = setup.Columns,
                ["gutter"] = R(setup.Gutter),
                ["facing"] = setup.Facing,
                ["bleed"] = R(setup.Bleed),
                ["unit_shown_to_the_person"] = Units.Name(setup.Unit),
                ["pages"] = new JsonArray(d.Pages.Select((p, i) => (JsonNode)new JsonObject
                {
                    ["page"] = i + 1, ["label"] = d.PageLabel(i), ["master"] = p.Master, ["objects"] = p.All.Count(),
                }).ToArray()),
                ["masters"] = new JsonArray(d.Masters.Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["objects"] = m.All.Count() }).ToArray()),
                ["paragraph_styles"] = new JsonArray(d.Styles.Select(st => (JsonNode)DescribeStyle(st)).ToArray()),
                ["character_styles"] = new JsonArray(d.CharStyles.Select(cs => (JsonNode)new JsonObject { ["name"] = cs.Name, ["sets"] = cs.Set.Describe() }).ToArray()),
                ["swatches"] = new JsonArray(d.Swatches.Select(w => (JsonNode)new JsonObject { ["name"] = w.Name, ["colour"] = w.Colour, ["spot"] = w.Spot }).ToArray()),
                ["layers"] = new JsonArray(d.Layers.Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["visible"] = l.Visible, ["locked"] = l.Locked, ["prints"] = l.Print }).ToArray()),
                ["stories"] = new JsonArray(d.Stories.Values.Where(st => st.Table is null && st.NoteOf is null && st.OnPath is null).Select(st => (JsonNode)DescribeStory(d, st)).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "new_document")]
    [Description("Starts a new document, replacing the open one: a page size (a paper name or a width and height), margins, columns, facing pages and how many pages; or one of the built-in templates. The new document has the Body, Heading, Subheading and Caption styles and a few swatches. Fails if the open document has unsaved changes, unless discard_changes is true.")]
    public Task<string> NewDocument(
        [Description("A paper size: Letter, Legal, Tabloid, A3, A4, A5 or B5. Default Letter, unless width and height are given.")] string? paper = null,
        [Description("Page width. " + MeasureHelp)] JsonElement? width = null,
        [Description("Page height. " + MeasureHelp)] JsonElement? height = null,
        [Description("Landscape: the paper turned sideways. Default false.")] bool landscape = false,
        [Description("Margins on every side, or with top, bottom, left and right to set each. Default 36 pt (half an inch).")] JsonElement? margins = null,
        [Description("Columns in the margins (the master's guides). Default 1.")] int columns = 1,
        [Description("Space between columns. Default 18 pt.")] JsonElement? gutter = null,
        [Description("Facing pages (left and right, as a book). Default false.")] bool facing = false,
        [Description("How many pages. Default 1.")] int pages = 1,
        [Description("Start from a built-in template instead (\"Newsletter\" or \"Booklet\"): its pages, master and frames.")] string? template = null,
        [Description("Throw away unsaved changes to the open document. Default false.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Read("started a new document", s =>
        {
            Discardable(s, discard_changes);
            Document doc;
            if (template is not null)
            {
                var t = Templates.BuiltIn.FirstOrDefault(x => x.Name.Equals(template.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"There's no template \"{template}\". The templates: {string.Join(", ", Templates.BuiltIn.Select(x => x.Name))}.");
                doc = t.Make();
            }
            else
            {
                if (pages is < 1 or > 500) throw new ArgumentException("pages is from 1 to 500.");
                if (columns is < 1 or > 12) throw new ArgumentException("columns is from 1 to 12.");
                var (w, h) = PageSize(paper, width, height, landscape);
                doc = Document.New(new Setup(w, h, MarginsFrom(margins, Core.Margins.Half), columns, Measure(gutter, "gutter") ?? 18, Facing: facing));
                for (var i = 1; i < pages; i++) doc.Pages.Add(new Page { Master = doc.Masters[0].Name });
                if (facing) doc.EnsureSides();
            }
            s.Replace(doc, null);
            return new JsonObject
            {
                ["pages"] = doc.Pages.Count,
                ["page_size"] = $"{R(doc.Setup.PageWidth)} x {R(doc.Setup.PageHeight)} pt",
                ["live_area"] = BoxJson(doc.Setup.Live),
                ["note"] = "Put frames inside the live area (the margins); get_page shows what's on a page.",
            };
        }, cancel);

    [McpServerTool(Name = "open_document")]
    [Description("Opens an .avps document (or an .avpt template, as a new document) from the working folder, replacing the open one. Fails if the open document has unsaved changes, unless discard_changes is true.")]
    public Task<string> OpenDocument(
        [Description("The file, relative to the working folder.")] string path,
        [Description("Throw away unsaved changes to the open document. Default false.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Read("opened a document", s =>
        {
            Discardable(s, discard_changes);
            var full = _files.Resolve(path, "A document", DocumentFile.Extension, Templates.Extension);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no {path} in the working folder.");
            var doc = DocumentFile.Load(full);
            s.Replace(doc, full.EndsWith(Templates.Extension, StringComparison.OrdinalIgnoreCase) ? null : full);
            var missing = Links.Missing(doc, Path.GetDirectoryName(full)).Count;
            return new JsonObject
            {
                ["opened"] = Path.GetRelativePath(host.Root, full),
                ["pages"] = doc.Pages.Count,
                ["stories"] = doc.Stories.Count,
                ["missing_linked_pictures"] = missing,
            };
        }, cancel);

    [McpServerTool(Name = "save_document")]
    [Description("Saves the document as .avps (everything in one file, pictures too, except linked ones). Without a path it saves to the file it came from.")]
    public Task<string> SaveDocument(
        [Description("Where to save, relative to the working folder; .avps is added if missing. Default: the document's own file.")] string? path = null,
        [Description("Write over an existing file. Default false (saving to the document's own file always may).")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Read("saved the document", s =>
        {
            string full;
            if (path is null)
            {
                full = s.FilePath ?? throw new ArgumentException("The document hasn't been saved before: give a path, such as \"newsletter.avps\".");
                _files.Resolve(full);
                overwrite = true;
            }
            else
            {
                var p = path.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase) ? path : path + DocumentFile.Extension;
                full = _files.Resolve(p);
                if (s.FilePath is { } own && string.Equals(Path.GetFullPath(own), full, StringComparison.Ordinal)) overwrite = true;
            }
            _files.CheckWritable(full, overwrite);
            DocumentFile.Save(s.Editor.Doc, full);
            _files.Wrote(full);
            s.Saved(full);
            return new JsonObject { ["saved"] = Path.GetRelativePath(host.Root, full), ["bytes"] = new FileInfo(full).Length };
        }, cancel);

    [McpServerTool(Name = "set_document")]
    [Description("Changes the document's setup: the page size (every page), the margins, columns and gutter of the first master's guides, facing pages, bleed, a baseline grid, and its title and author (which go into the PDF). Objects already placed stay where they are.")]
    public Task<string> SetDocument(
        [Description("A paper size: Letter, Legal, Tabloid, A3, A4, A5 or B5.")] string? paper = null,
        [Description("Page width. " + MeasureHelp)] JsonElement? width = null,
        [Description("Page height. " + MeasureHelp)] JsonElement? height = null,
        [Description("With paper: turned sideways.")] bool? landscape = null,
        [Description("Margins on every side, or with top, bottom, left and right to set each.")] JsonElement? margins = null,
        [Description("Columns in the margins.")] int? columns = null,
        [Description("Space between columns. " + MeasureHelp)] JsonElement? gutter = null,
        [Description("Facing pages (left and right, as a book).")] bool? facing = null,
        [Description("How far artwork may run past the page's edge for printing to the edge. " + MeasureHelp)] JsonElement? bleed = null,
        [Description("A baseline grid's spacing (0: none). " + MeasureHelp)] JsonElement? baseline_grid = null,
        [Description("The document's title.")] string? title = null,
        [Description("Its author.")] string? author = null,
        CancellationToken cancel = default) =>
        Edit("changed the document's setup", s =>
        {
            var e = s.Editor;
            var setup = e.Doc.Setup;
            if (paper is not null || width is not null || height is not null)
            {
                var (w, h) = PageSize(paper, width ?? (paper is null ? Num(setup.PageWidth) : null), height ?? (paper is null ? Num(setup.PageHeight) : null), landscape ?? false);
                setup = setup with { PageWidth = w, PageHeight = h };
            }
            if (margins is not null) setup = setup with { Margins = MarginsFrom(margins, setup.Margin) };
            if (columns is { } c)
            {
                if (c is < 1 or > 12) throw new ArgumentException("columns is from 1 to 12.");
                setup = setup with { Columns = c };
            }
            if (Measure(gutter, "gutter") is { } g) setup = setup with { Gutter = g };
            if (Measure(bleed, "bleed") is { } b) setup = setup with { Bleed = Math.Clamp(b, 0, 72) };
            if (Measure(baseline_grid, "baseline_grid") is { } bg) setup = setup with { BaselineGrid = Math.Max(0, bg) };
            var set = setup;
            e.Change("Document setup", d =>
            {
                d.Setup = set with { Facing = d.Setup.Facing };
                if (title is not null) d.Title = title;
                if (author is not null) d.Author = author;
                if (margins is not null || columns is not null || gutter is not null)
                    foreach (var m in d.Masters.Take(1)) { m.Margins = null; m.Columns = null; m.Gutter = null; }
            });
            if (facing is { } f && f != e.Doc.Setup.Facing) e.SetFacing(f);
            var now = e.Doc.Setup;
            return new JsonObject
            {
                ["page_size"] = $"{R(now.PageWidth)} x {R(now.PageHeight)} pt",
                ["margins"] = Margins(now.Margin),
                ["columns"] = now.Columns,
                ["facing"] = now.Facing,
                ["live_area"] = BoxJson(now.Live),
            };
        }, cancel);

    [McpServerTool(Name = "list_fonts", ReadOnly = true, Idempotent = true)]
    [Description("The font families text can be set in. Liberation Serif and Liberation Sans come with the program, so they're always there (and embed in PDFs); others are this computer's.")]
    public Task<string> ListFonts(
        [Description("Only families whose name has this in it. Optional.")] string? search = null,
        CancellationToken cancel = default) =>
        Read("listed the fonts", _ =>
        {
            var fonts = host.Library.Fonts.Where(f => search is null || f.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            return new JsonObject { ["count"] = fonts.Count, ["fonts"] = new JsonArray(fonts.Select(f => (JsonNode)f).ToArray()) };
        }, cancel);

    // ───────────────────────────── pages ─────────────────────────────

    [McpServerTool(Name = "get_page", ReadOnly = true, Idempotent = true)]
    [Description("Everything on a page (or a master page), in drawing order, bottom first: each object's id, kind, box (x, y, w, h in points from the page's top-left), rotation, colours and layer; a text frame's story, the start of its text and whether the story is overset; a picture's file and fit; a table's rows and columns; a group's members. Also the page's master and its live area (inside the margins).")]
    public Task<string> GetPage(
        [Description(PageHelp)] string? page = null,
        CancellationToken cancel = default) =>
        Read("read a page", s =>
        {
            var d = s.Editor.Doc;
            var at = SheetOf(s, page);
            var sheet = at.Resolve(d);
            var o = new JsonObject
            {
                ["page"] = SheetName(d, at),
                ["live_area"] = BoxJson(d.GuidesFor(sheet).Live),
                ["column_guides"] = new JsonArray(d.GuidesFor(sheet).Columns.Select(b => (JsonNode)BoxJson(b)).ToArray()),
            };
            if (sheet is Page p) o["master"] = p.Master;
            o["objects"] = new JsonArray(d.Ordered(sheet.Objects).Select(x => (JsonNode)Describe(d, x)).ToArray());
            return o;
        }, cancel);

    [McpServerTool(Name = "add_pages")]
    [Description("Adds pages after a page (or at the end), on a master. New pages are empty but for what their master shows.")]
    public Task<string> AddPages(
        [Description("How many. Default 1.")] int count = 1,
        [Description("Add them after this page (numbered from 1); 0 puts them first. Default: at the end.")] int? after = null,
        [Description("The master page they're on. Default: the master of the page before them.")] string? master = null,
        CancellationToken cancel = default) =>
        Edit("added pages", s =>
        {
            var e = s.Editor;
            if (count is < 1 or > 500) throw new ArgumentException("count is from 1 to 500.");
            var at = after ?? e.Doc.Pages.Count;
            if (at < 0 || at > e.Doc.Pages.Count) throw new ArgumentException($"after is from 0 to {e.Doc.Pages.Count} (the document has {e.Doc.Pages.Count} pages).");
            if (master is not null && e.Doc.Master(master) is null) throw new ArgumentException($"There's no master page \"{master}\". The masters: {string.Join(", ", e.Doc.Masters.Select(m => m.Name))}.");
            for (var i = 0; i < count; i++)
            {
                var index = e.AddPage(at + i);
                if (master is not null) e.Change("Master", d => d.Pages[index].Master = master);
            }
            s.Show(SheetRef.PageAt(at));
            return new JsonObject { ["added"] = count, ["first_new_page"] = at + 1, ["pages"] = e.Doc.Pages.Count };
        }, cancel);

    [McpServerTool(Name = "delete_page", Destructive = true)]
    [Description("Deletes a page and everything on it (a story running through it flows on through its other frames). The last page can't be deleted.")]
    public Task<string> DeletePage(
        [Description("The page, numbered from 1.")] int page,
        CancellationToken cancel = default) =>
        Edit("deleted a page", s =>
        {
            if (s.Editor.DeletePage(page - 1) is { } why) throw new ArgumentException(why);
            return new JsonObject { ["deleted"] = page, ["pages"] = s.Editor.Doc.Pages.Count };
        }, cancel);

    [McpServerTool(Name = "move_page")]
    [Description("Moves a page to another place in the document, renumbering the rest.")]
    public Task<string> MovePage(
        [Description("The page, numbered from 1.")] int page,
        [Description("Where it goes, numbered from 1 as the pages are now.")] int to,
        CancellationToken cancel = default) =>
        Edit("moved a page", s =>
        {
            var n = s.Editor.Doc.Pages.Count;
            if (page < 1 || page > n || to < 1 || to > n) throw new ArgumentException($"page and to are from 1 to {n}.");
            s.Editor.MovePage(page - 1, to - 1);
            return new JsonObject { ["moved"] = page, ["now"] = to };
        }, cancel);

    [McpServerTool(Name = "set_page_master")]
    [Description("Puts pages on a master page (whose headers, footers, page numbers and guides they then show). With a master name that doesn't exist, a new empty master is made with that name first.")]
    public Task<string> SetPageMaster(
        [Description("The master's name, such as \"A-Master\" or \"B-Master\".")] string master,
        [Description("The pages, numbered from 1, as a list or text such as \"2-5, 8\". Default: none (just make the master).")] JsonElement? pages = null,
        CancellationToken cancel = default) =>
        Edit("set pages' master", s =>
        {
            var e = s.Editor;
            var name = master.Trim();
            if (name.Length == 0) throw new ArgumentException("master needs a name.");
            var made = false;
            if (e.Doc.Master(name) is null)
            {
                var given = e.AddMaster();
                if (given != name && e.RenameMaster(given, name) is { } why) throw new ArgumentException(why);
                made = true;
            }
            var list = PageList(e.Doc, pages);
            e.Change("Master " + name, d => { foreach (var i in list) d.Pages[i].Master = name; });
            return new JsonObject { ["master"] = name, ["made"] = made, ["pages"] = new JsonArray(list.Select(i => (JsonNode)(i + 1)).ToArray()) };
        }, cancel);

    // ───────────────────────────── objects ─────────────────────────────

    [McpServerTool(Name = "add_text_frame")]
    [Description("Draws a text frame on a page (or master) and, optionally, puts text in it. A new frame has a story of its own, unless follows names a frame whose story it continues (the text flows on from that frame into this one). Returns the frame's id, its story's id and whether the text fits. A frame on a master with the text \"{page}\" in it shows each page's number.")]
    public Task<string> AddTextFrame(
        [Description("Left edge. " + MeasureHelp)] JsonElement x,
        [Description("Top edge. " + MeasureHelp)] JsonElement y,
        [Description("Width. " + MeasureHelp)] JsonElement w,
        [Description("Height. " + MeasureHelp)] JsonElement h,
        [Description(PageHelp)] string? page = null,
        [Description("The text: paragraphs separated by newlines. \"{page}\" puts in the page number and \"{pages}\" the page count. Optional.")] string? text = null,
        [Description("Read the text as Markdown: # Heading, ## Subheading, **bold**, *italic*, lists. Default false.")] bool markdown = false,
        [Description("The paragraph style for the text (with markdown, for the plain paragraphs). Default Body.")] string? style = null,
        [Description("Columns inside the frame. Default 1.")] int columns = 1,
        [Description("Space between the frame's columns. Default 12 pt.")] JsonElement? gutter = null,
        [Description("Space between the frame's edge and its text. Default 0.")] JsonElement? inset = null,
        [Description("A frame (by id) this one follows in its chain: its story flows on into this frame.")] string? follows = null,
        [Description("A name for the frame (shown in the Object tab). Optional.")] string? name = null,
        CancellationToken cancel = default) =>
        Edit("added a text frame", s =>
        {
            var e = s.Editor;
            var at = SheetOf(s, page);
            if (columns is < 1 or > 12) throw new ArgumentException("columns is from 1 to 12.");
            var frame = new TextFrame { Columns = columns, Gutter = Measure(gutter, "gutter") ?? 12, Inset = Measure(inset, "inset") ?? 0, Name = name ?? "" };
            frame.Bounds = BoxFrom(x, y, w, h);
            TextFrame? before = null;
            if (follows is not null)
            {
                before = e.Doc.Find(follows) as TextFrame ?? throw new ArgumentException($"There's no text frame \"{follows}\". get_page lists a page's objects.");
                if (text is not null) throw new ArgumentException("A frame that follows another shows that story's text: leave text out, and use set_text on the story to change it.");
            }
            var made = e.Add(at, frame);
            if (before is not null && e.TryChange("Link frames", d => Chains.Link(d, (TextFrame)d.Find(before.Id)!, (TextFrame)d.Find(made.Id)!)) is { } why)
                throw new ArgumentException(why);
            var storyId = ((TextFrame)e.Doc.Find(made.Id)!).StoryId;
            if (text is not null) PutText(e, storyId, 0, 0, text, markdown, style);
            else if (style is not null && before is null) e.ApplyStyle(storyId, 0, 0, StyleName(e.Doc, style));
            s.Show(at);
            var result = new JsonObject { ["id"] = made.Id, ["story"] = storyId };
            AddFit(result, e.Doc, storyId);
            return result;
        }, cancel);

    [McpServerTool(Name = "add_shape")]
    [Description("Draws a rectangle, an ellipse or a line on a page (or master). A line runs from (x, y) to (x + w, y + h): give a negative h for one that rises. Rectangles and ellipses are filled with fill and outlined with stroke; for a rule or a coloured block give stroke \"none\" or fill \"none\".")]
    public Task<string> AddShape(
        [Description("rectangle, ellipse or line.")] string kind,
        [Description("Left edge (a line's start). " + MeasureHelp)] JsonElement x,
        [Description("Top edge (a line's start). " + MeasureHelp)] JsonElement y,
        [Description("Width (a line: how far right it goes). " + MeasureHelp)] JsonElement w,
        [Description("Height (a line: how far down it goes; negative goes up). " + MeasureHelp)] JsonElement h,
        [Description(PageHelp)] string? page = null,
        [Description("The fill colour: \"#RRGGBB\", a swatch's name (\"Blue\", \"Blue 40%\") or \"none\". Default none.")] string? fill = null,
        [Description("The outline's colour. Default black.")] string? stroke = null,
        [Description("The outline's width in points. Default 1.")] double? stroke_width = null,
        [Description("A rectangle's corner radius. " + MeasureHelp)] JsonElement? corner_radius = null,
        [Description("A line: an arrowhead at its start.")] bool arrow_start = false,
        [Description("A line: an arrowhead at its end.")] bool arrow_end = false,
        [Description("A name for it. Optional.")] string? name = null,
        CancellationToken cancel = default) =>
        Edit("added a shape", s =>
        {
            var e = s.Editor;
            var at = SheetOf(s, page);
            PageObject o;
            switch (kind.Trim().ToLowerInvariant())
            {
                case "rectangle" or "rect" or "box":
                    o = new RectangleObject { CornerRadius = Measure(corner_radius, "corner_radius") ?? 0 };
                    o.Bounds = BoxFrom(x, y, w, h);
                    break;
                case "ellipse" or "circle" or "oval":
                    o = new EllipseObject();
                    o.Bounds = BoxFrom(x, y, w, h);
                    break;
                case "line" or "rule":
                    var (x0, y0) = (Need(x, "x"), Need(y, "y"));
                    var (dx, dy) = (Need(w, "w", allowNegative: true), Need(h, "h", allowNegative: true));
                    var (left, top) = (Math.Min(x0, x0 + dx), Math.Min(y0, y0 + dy));
                    o = new LineObject { Rising = dx * dy < 0, ArrowStart = dx < 0 || (dx == 0 && dy < 0) ? arrow_end : arrow_start, ArrowEnd = dx < 0 || (dx == 0 && dy < 0) ? arrow_start : arrow_end };
                    o.Bounds = new Box(left, top, Math.Abs(dx), Math.Abs(dy));
                    break;
                default:
                    throw new ArgumentException($"kind is rectangle, ellipse or line, not \"{kind}\". Text goes in add_text_frame, pictures in add_picture.");
            }
            o.Name = name ?? "";
            o.Fill = Colour(e.Doc, fill, "fill") ?? null;
            if (stroke is not null) o.Stroke = Colour(e.Doc, stroke, "stroke");
            if (stroke_width is { } sw) o.StrokeWidth = Width(sw);
            var made = e.Add(at, o);
            s.Show(at);
            return new JsonObject { ["id"] = made.Id, ["kind"] = made.Kind };
        }, cancel);

    [McpServerTool(Name = "add_picture")]
    [Description("Places a picture from a file in the working folder (PNG, JPEG, GIF, BMP, TIFF, WebP, SVG, EPS or a PDF page) in a frame on a page. Without a size the frame is the picture's own size (at 72 dpi for a bitmap), shrunk to fit the live area; with one, fit says how the picture sits in it.")]
    public Task<string> AddPicture(
        [Description("The picture's file, relative to the working folder.")] string path,
        [Description("Left edge. " + MeasureHelp + " Default: the live area's left.")] JsonElement? x = null,
        [Description("Top edge. " + MeasureHelp + " Default: the live area's top.")] JsonElement? y = null,
        [Description("Width. " + MeasureHelp)] JsonElement? w = null,
        [Description("Height. " + MeasureHelp + " With only a width, the height keeps the picture's shape.")] JsonElement? h = null,
        [Description(PageHelp)] string? page = null,
        [Description("How it sits in its frame: fit (all of it shows, in proportion; the default), fill (it covers the frame, cropped), stretch, or actual (its own size).")] string fit = "fit",
        [Description("For a PDF: which of its pages, from 1. Default 1.")] int pdf_page = 1,
        [Description("Keep it linked to its file rather than in the document (it updates when the file changes). Default false.")] bool linked = false,
        [Description("A name for it. Optional.")] string? name = null,
        CancellationToken cancel = default) =>
        Edit("placed a picture", s =>
        {
            var e = s.Editor;
            var at = SheetOf(s, page);
            var full = _files.Resolve(path);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no {path} in the working folder.");
            var bytes = File.ReadAllBytes(full);
            if (!ImageStore.IsImage(bytes)) throw new NotSupportedException($"{path} isn't a picture AVAPageStream can show (PNG, JPEG, GIF, BMP, TIFF, WebP, SVG, EPS or PDF).");
            var live = e.Doc.GuidesFor(at.Resolve(e.Doc)).Live;
            var natural = host.Library.PictureSize(bytes) ?? (144, 144);
            double width, height;
            var (mw, mh) = (Measure(w, "w"), Measure(h, "h"));
            if (mw is { } ww && mh is { } hh) (width, height) = (ww, hh);
            else if (mw is { } ww2) (width, height) = (ww2, ww2 * natural.Height / Math.Max(1, natural.Width));
            else if (mh is { } hh2) (width, height) = (hh2 * natural.Width / Math.Max(1, natural.Height), hh2);
            else
            {
                var scale = Math.Min(1, Math.Min(live.W / Math.Max(1, natural.Width), live.H / Math.Max(1, natural.Height)));
                (width, height) = (natural.Width * scale, natural.Height * scale);
            }
            var picture = new PictureObject
            {
                Image = e.Doc.Images.Add(bytes),
                Source = full,
                Linked = linked,
                Page = Math.Max(1, pdf_page),
                Name = name ?? "",
                Fit = fit.Trim().ToLowerInvariant() switch
                {
                    "fit" => Fit.Fit, "fill" => Fit.Fill, "stretch" => Fit.Stretch, "actual" => Fit.Actual,
                    _ => throw new ArgumentException($"fit is fit, fill, stretch or actual, not \"{fit}\"."),
                },
            };
            picture.Bounds = new Box(Measure(x, "x") ?? live.X, Measure(y, "y") ?? live.Y, Math.Round(width, 2), Math.Round(height, 2));
            var made = e.Add(at, picture);
            s.Show(at);
            return new JsonObject { ["id"] = made.Id, ["box"] = BoxJson(made.Bounds), ["natural_size"] = $"{R(natural.Width)} x {R(natural.Height)} pt" };
        }, cancel);

    [McpServerTool(Name = "add_table")]
    [Description("Draws a table on a page: rows and columns of cells, each holding text of its own, the first rows a header (shaded and repeated if the table runs on). Give cells as a list of rows, each a list of cell texts; the table gets as many rows and columns as they need unless rows and columns are given. Returns the table's id and its cells' story ids (row by row), which get_text, set_text and format_text take.")]
    public Task<string> AddTable(
        [Description("Left edge. " + MeasureHelp)] JsonElement x,
        [Description("Top edge. " + MeasureHelp)] JsonElement y,
        [Description("Width (shared between the columns). " + MeasureHelp)] JsonElement w,
        [Description("Height (shared between the rows; rows grow to fit their text). " + MeasureHelp + " Default: 20 pt a row.")] JsonElement? h = null,
        [Description("The cells' text: a list of rows, each a list of strings. Optional.")] List<List<string>>? cells = null,
        [Description("How many rows. Default: as many as cells has, else 3.")] int? rows = null,
        [Description("How many columns. Default: as many as cells' longest row has, else 3.")] int? columns = null,
        [Description("How many rows at the top are headers. Default 1.")] int header_rows = 1,
        [Description(PageHelp)] string? page = null,
        CancellationToken cancel = default) =>
        Edit("added a table", s =>
        {
            var e = s.Editor;
            var at = SheetOf(s, page);
            var r = rows ?? cells?.Count ?? 3;
            var c = columns ?? (cells is { Count: > 0 } ? cells.Max(row => row.Count) : 3);
            if (r is < 1 or > 500 || c is < 1 or > 50) throw new ArgumentException("A table has 1 to 500 rows and 1 to 50 columns.");
            var box = new Box(Need(x, "x"), Need(y, "y"), Need(w, "w"), Measure(h, "h") ?? 20 * r);
            var table = e.AddTable(at, box, r, c);
            var hr = Math.Clamp(header_rows, 0, r);
            e.ChangeTable(table.Id, "Header rows", (_, t) => t.HeaderRows = hr);
            var t = (TableObject)e.Doc.Find(table.Id)!;
            if (cells is not null)
                for (var i = 0; i < Math.Min(r, cells.Count); i++)
                    for (var j = 0; j < Math.Min(c, cells[i].Count); j++)
                        if (cells[i][j] is { Length: > 0 } text)
                            e.ReplaceText(t.Cells[i * c + j], 0, 0, text.Replace("\r\n", "\n"));
            e.ChangeTable(table.Id, "Fit rows", (d, tt) => Tables.FitRows(d, tt, host.Library.Measurer));
            t = (TableObject)e.Doc.Find(table.Id)!;
            s.Show(at);
            return new JsonObject
            {
                ["id"] = t.Id,
                ["box"] = BoxJson(t.Bounds),
                ["cells"] = new JsonArray(Enumerable.Range(0, r).Select(i => (JsonNode)new JsonArray(t.Cells.Skip(i * c).Take(c).Select(id => (JsonNode)id).ToArray())).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "set_object")]
    [Description("Changes an object: where it is and its size, rotation, colours, outline, opacity, name, layer and whether it's locked or text wraps round it; a text frame's columns, gutter and inset; a picture's fit; a rectangle's corner radius. Give only what changes. Several objects at once: give ids.")]
    public Task<string> SetObject(
        [Description("The object's id. Or give ids for several.")] string? id = null,
        [Description("Several objects' ids, all changed the same way.")] List<string>? ids = null,
        [Description("Left edge. " + MeasureHelp)] JsonElement? x = null,
        [Description("Top edge. " + MeasureHelp)] JsonElement? y = null,
        [Description("Width. " + MeasureHelp)] JsonElement? w = null,
        [Description("Height. " + MeasureHelp)] JsonElement? h = null,
        [Description("Rotation in degrees, anticlockwise about its centre.")] double? rotation = null,
        [Description("The fill colour: \"#RRGGBB\", a swatch's name or \"none\".")] string? fill = null,
        [Description("The outline's colour, or \"none\".")] string? stroke = null,
        [Description("The outline's width in points.")] double? stroke_width = null,
        [Description("Opacity, from 0 (invisible) to 1.")] double? opacity = null,
        [Description("A name for it.")] string? name = null,
        [Description("The layer it's on, by name.")] string? layer = null,
        [Description("Locked: can't be moved or deleted by hand.")] bool? locked = null,
        [Description("Text under it wraps round it.")] bool? wrap = null,
        [Description("How far wrapped text keeps from it. " + MeasureHelp)] JsonElement? wrap_gap = null,
        [Description("A text frame's columns.")] int? columns = null,
        [Description("A text frame's space between columns. " + MeasureHelp)] JsonElement? gutter = null,
        [Description("A text frame's space between edge and text. " + MeasureHelp)] JsonElement? inset = null,
        [Description("A picture's fit: fit, fill, stretch or actual.")] string? fit = null,
        [Description("A rectangle's corner radius. " + MeasureHelp)] JsonElement? corner_radius = null,
        CancellationToken cancel = default) =>
        Edit("changed an object", s =>
        {
            var e = s.Editor;
            var targets = Targets(e.Doc, id, ids);
            Fit? fitting = fit?.Trim().ToLowerInvariant() switch
            {
                null => null, "fit" => Fit.Fit, "fill" => Fit.Fill, "stretch" => Fit.Stretch, "actual" => Fit.Actual,
                _ => throw new ArgumentException($"fit is fit, fill, stretch or actual, not \"{fit}\"."),
            };
            if (layer is not null && !e.Doc.Layers.Any(l => l.Name == layer))
                throw new ArgumentException($"There's no layer \"{layer}\". The layers: {string.Join(", ", e.Doc.Layers.Select(l => l.Name))}.");
            var (mx, my, mw, mh) = (Measure(x, "x"), Measure(y, "y"), Measure(w, "w"), Measure(h, "h"));
            var fillColour = fill is null ? null : Colour(e.Doc, fill, "fill");
            var strokeColour = stroke is null ? null : Colour(e.Doc, stroke, "stroke");
            var (gap, gut, ins, radius) = (Measure(wrap_gap, "wrap_gap"), Measure(gutter, "gutter"), Measure(inset, "inset"), Measure(corner_radius, "corner_radius"));
            if (opacity is { } op && (op < 0 || op > 1)) throw new ArgumentException("opacity is from 0 to 1.");
            if (columns is { } cc && cc is < 1 or > 12) throw new ArgumentException("columns is from 1 to 12.");
            e.Change("Change object", d =>
            {
                foreach (var tid in targets)
                {
                    var o = d.Find(tid)!;
                    if (mx is not null || my is not null || mw is not null || mh is not null)
                    {
                        var b = o.Bounds;
                        o.Bounds = new Box(mx ?? b.X, my ?? b.Y, Math.Max(mw ?? b.W, o is LineObject ? 0 : 1), Math.Max(mh ?? b.H, o is LineObject ? 0 : 1));
                    }
                    if (rotation is { } r) o.Rotation = ((r % 360) + 360) % 360;
                    if (fill is not null) o.Fill = fillColour;
                    if (stroke is not null) o.Stroke = strokeColour;
                    if (stroke_width is { } sw) o.StrokeWidth = Width(sw);
                    if (opacity is { } op2) o.Opacity = op2;
                    if (name is not null) o.Name = name;
                    if (layer is not null) o.Layer = layer;
                    if (locked is { } l) o.Locked = l;
                    if (wrap is { } wr) o.Wrap = wr;
                    if (gap is { } g) o.WrapGap = g;
                    if (o is TextFrame f)
                    {
                        if (columns is { } c) f.Columns = c;
                        if (gut is { } gg) f.Gutter = gg;
                        if (ins is { } ii) f.Inset = ii;
                    }
                    if (o is PictureObject p && fitting is { } ft) p.Fit = ft;
                    if (o is RectangleObject rect && radius is { } rr) rect.CornerRadius = rr;
                    d.Refit(tid);
                }
            });
            return new JsonObject { ["changed"] = new JsonArray(targets.Select(t => (JsonNode)Describe(e.Doc, e.Doc.Find(t)!)).ToArray()) };
        }, cancel);

    [McpServerTool(Name = "delete_objects", Destructive = true)]
    [Description("Deletes objects (locked ones stay). A text frame's story flows on through the rest of its chain; a story left with no frame goes with it.")]
    public Task<string> DeleteObjects(
        [Description("The objects' ids.")] List<string> ids,
        CancellationToken cancel = default) =>
        Edit("deleted objects", s =>
        {
            Targets(s.Editor.Doc, null, ids);
            var n = s.Editor.Delete(ids);
            if (n == 0) throw new InvalidOperationException("Nothing was deleted: those objects are locked. set_object locked false unlocks them.");
            return new JsonObject { ["deleted"] = n };
        }, cancel);

    [McpServerTool(Name = "duplicate_objects")]
    [Description("Copies objects on their page, offset down and to the right (a copied text frame gets a copy of its text).")]
    public Task<string> DuplicateObjects(
        [Description("The objects' ids.")] List<string> ids,
        [Description("How far the copies are offset. " + MeasureHelp + " Default 12 pt.")] JsonElement? offset = null,
        CancellationToken cancel = default) =>
        Edit("duplicated objects", s =>
        {
            Targets(s.Editor.Doc, null, ids);
            var made = s.Editor.Duplicate(ids, Measure(offset, "offset", allowNegative: true) ?? 12);
            return new JsonObject { ["copies"] = new JsonArray(made.Select(m => (JsonNode)m).ToArray()) };
        }, cancel);

    [McpServerTool(Name = "arrange_objects")]
    [Description("Stacks, aligns or spaces objects: order brings them to the front or sends them back; align lines up two or more by their edges or centres; distribute spaces three or more evenly.")]
    public Task<string> ArrangeObjects(
        [Description("The objects' ids.")] List<string> ids,
        [Description("front, forward, backward or back. Optional.")] string? order = null,
        [Description("left, centre, right, top, middle or bottom. Optional.")] string? align = null,
        [Description("across (evenly left to right) or down. Optional.")] string? distribute = null,
        CancellationToken cancel = default) =>
        Edit("arranged objects", s =>
        {
            var e = s.Editor;
            Targets(e.Doc, null, ids);
            if (order is null && align is null && distribute is null) throw new ArgumentException("Give order, align or distribute.");
            if (order is not null)
                e.Arrange(ids, order.Trim().ToLowerInvariant() switch
                {
                    "front" => Editor.Order.Front, "forward" => Editor.Order.Forward, "backward" => Editor.Order.Backward, "back" => Editor.Order.Back,
                    _ => throw new ArgumentException($"order is front, forward, backward or back, not \"{order}\"."),
                });
            if (align is not null)
            {
                if (ids.Count < 2) throw new ArgumentException("align needs two or more objects.");
                e.AlignObjects(ids, align.Trim().ToLowerInvariant() switch
                {
                    "left" => Editor.Alignment.Left, "centre" or "center" => Editor.Alignment.Centre, "right" => Editor.Alignment.Right,
                    "top" => Editor.Alignment.Top, "middle" => Editor.Alignment.Middle, "bottom" => Editor.Alignment.Bottom,
                    _ => throw new ArgumentException($"align is left, centre, right, top, middle or bottom, not \"{align}\"."),
                });
            }
            if (distribute is not null)
            {
                var across = distribute.Trim().ToLowerInvariant() switch
                {
                    "across" or "horizontal" => true, "down" or "vertical" => false,
                    _ => throw new ArgumentException($"distribute is across or down, not \"{distribute}\"."),
                };
                if (e.Distribute(ids, across) is { } why) throw new ArgumentException(why);
            }
            return new JsonObject { ["objects"] = new JsonArray(ids.Select(i => (JsonNode)Describe(e.Doc, e.Doc.Find(i)!)).ToArray()) };
        }, cancel);

    [McpServerTool(Name = "group_objects")]
    [Description("Groups objects on one page into one (they move and resize together), or with ungroup true takes groups apart.")]
    public Task<string> GroupObjects(
        [Description("The objects' ids (two or more to group; groups to ungroup).")] List<string> ids,
        [Description("Take the groups apart instead. Default false.")] bool ungroup = false,
        CancellationToken cancel = default) =>
        Edit(ungroup ? "ungrouped objects" : "grouped objects", s =>
        {
            Targets(s.Editor.Doc, null, ids);
            if (ungroup)
            {
                var freed = s.Editor.Ungroup(ids);
                if (freed.Count == 0) throw new ArgumentException("None of those is a group.");
                return new JsonObject { ["objects"] = new JsonArray(freed.Select(i => (JsonNode)i).ToArray()) };
            }
            var g = s.Editor.Group(ids) ?? throw new ArgumentException("Group two or more objects that are all on the top level of one page (not already in a group).");
            return new JsonObject { ["group"] = g };
        }, cancel);

    [McpServerTool(Name = "link_frames")]
    [Description("Links one text frame to another so its story flows on into it (the second frame should be empty). Or with unlink true, takes a frame out of its chain: it gets an empty story of its own and the text flows on through the rest.")]
    public Task<string> LinkFrames(
        [Description("The frame the text comes from (or, to unlink, the frame to take out).")] string from,
        [Description("The frame it flows on into.")] string? to = null,
        [Description("Take from out of its chain instead. Default false.")] bool unlink = false,
        CancellationToken cancel = default) =>
        Edit(unlink ? "unlinked a frame" : "linked frames", s =>
        {
            var e = s.Editor;
            var a = e.Doc.Find(from) as TextFrame ?? throw new ArgumentException($"There's no text frame \"{from}\".");
            if (unlink)
            {
                if (e.TryChange("Unlink frame", d => Chains.Unlink(d, (TextFrame)d.Find(a.Id)!)) is { } why) throw new ArgumentException(why);
            }
            else
            {
                if (to is null) throw new ArgumentException("Give to: the frame the text flows on into.");
                var b = e.Doc.Find(to) as TextFrame ?? throw new ArgumentException($"There's no text frame \"{to}\".");
                if (e.TryChange("Link frames", d => Chains.Link(d, (TextFrame)d.Find(a.Id)!, (TextFrame)d.Find(b.Id)!)) is { } why) throw new ArgumentException(why);
            }
            var story = ((TextFrame)e.Doc.Find(from)!).StoryId;
            var result = new JsonObject { ["story"] = story, ["frames"] = new JsonArray(e.Doc.Stories[story].Frames.Select(f => (JsonNode)f).ToArray()) };
            AddFit(result, e.Doc, story);
            return result;
        }, cancel);

    [McpServerTool(Name = "undo")]
    [Description("Takes back the last change (an agent's call, or the person's own edit), as Ctrl+Z does.")]
    public Task<string> Undo(CancellationToken cancel = default) =>
        Read("undid", s =>
        {
            var label = s.Editor.UndoLabel ?? throw new InvalidOperationException("There's nothing to undo.");
            s.Editor.Undo();
            return new JsonObject { ["undid"] = label, ["next_undo"] = s.Editor.UndoLabel };
        }, cancel);

    [McpServerTool(Name = "redo")]
    [Description("Puts back the change last undone.")]
    public Task<string> Redo(CancellationToken cancel = default) =>
        Read("redid", s =>
        {
            var label = s.Editor.RedoLabel ?? throw new InvalidOperationException("There's nothing to redo.");
            s.Editor.Redo();
            return new JsonObject { ["redid"] = label };
        }, cancel);

    // ───────────────────────────── helpers ─────────────────────────────

    private static void Discardable(IPageSession s, bool discard)
    {
        if (s.Editor.Dirty && !discard)
            throw new InvalidOperationException("The open document has changes that haven't been saved. Save it first (save_document), or call again with discard_changes true to throw them away.");
    }

    /// <summary>The sheet a call means: a page number, a master's name, or the page being looked at.</summary>
    internal static SheetRef SheetOf(IPageSession s, string? page)
    {
        var d = s.Editor.Doc;
        if (string.IsNullOrWhiteSpace(page)) return SheetRef.PageAt(Math.Clamp(s.CurrentPage ?? 0, 0, d.Pages.Count - 1));
        var text = page.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            if (n < 1 || n > d.Pages.Count) throw new ArgumentException($"There's no page {n}: the document has {d.Pages.Count} page{(d.Pages.Count == 1 ? "" : "s")}. add_pages adds more.");
            return SheetRef.PageAt(n - 1);
        }
        var left = text.EndsWith(" left", StringComparison.OrdinalIgnoreCase);
        var name = left ? text[..^5].Trim() : text;
        var i = d.Masters.FindIndex(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new ArgumentException($"page is a page number (1 to {d.Pages.Count}) or a master's name ({string.Join(", ", d.Masters.Select(m => m.Name))}), not \"{page}\".");
        if (left && d.Masters[i].Left is null) throw new ArgumentException($"{d.Masters[i].Name} has no left side: only facing documents' masters do.");
        return SheetRef.MasterAt(i, left);
    }

    internal static string SheetName(Document d, SheetRef at) =>
        at.IsMaster ? d.Masters[at.Index].Name + (at.Left ? " left" : "") : (at.Index + 1).ToString(CultureInfo.InvariantCulture);

    private static List<int> PageList(Document d, JsonElement? pages)
    {
        if (pages is not { } p || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        if (p.ValueKind == JsonValueKind.Number) return [PageIndex(d, p.GetInt32())];
        if (p.ValueKind == JsonValueKind.Array) return p.EnumerateArray().Select(x => PageIndex(d, x.GetInt32())).Distinct().ToList();
        if (p.ValueKind == JsonValueKind.String)
        {
            if (!PageRanges.TryParse(p.GetString(), d.Pages.Count, out var list, out var error))
                throw new ArgumentException($"pages: {error} Give a list of page numbers or text such as \"2-5, 8\" (from 1 to {d.Pages.Count}).");
            return [.. list];
        }
        throw new ArgumentException("pages is a list of page numbers or text such as \"2-5, 8\".");
    }

    private static int PageIndex(Document d, int page) =>
        page >= 1 && page <= d.Pages.Count ? page - 1 : throw new ArgumentException($"There's no page {page}: the document has {d.Pages.Count}.");

    /// <summary>The ids given (one or several), each checked to be an object in the document.</summary>
    private static List<string> Targets(Document d, string? id, List<string>? ids)
    {
        var list = new List<string>();
        if (id is not null) list.Add(id);
        if (ids is not null) list.AddRange(ids);
        if (list.Count == 0) throw new ArgumentException("Give the object's id (or ids for several). get_page lists a page's objects.");
        foreach (var x in list)
            if (d.Find(x) is null) throw new ArgumentException($"There's no object \"{x}\". get_page lists a page's objects.");
        return list.Distinct().ToList();
    }

    /// <summary>A measure an agent gave: a number of points, or text with a unit. Null when not given.</summary>
    internal static double? Measure(JsonElement? value, string what, bool allowNegative = false)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        double points;
        if (v.ValueKind == JsonValueKind.Number) points = v.GetDouble();
        else if (v.ValueKind == JsonValueKind.String && Units.TryParse(v.GetString(), Unit.Point, out var p)) points = p;
        else if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) points = q;
        else throw new ArgumentException($"{what} is a measure: {MeasureHelp} Not {v.GetRawText()}.");
        if (!double.IsFinite(points) || Math.Abs(points) > 100_000) throw new ArgumentException($"{what} must be an ordinary measure, within 100,000 points.");
        if (points < 0 && !allowNegative) throw new ArgumentException($"{what} can't be negative.");
        return Math.Round(points, 2);
    }

    private static double Need(JsonElement value, string what, bool allowNegative = false) =>
        Measure(value, what, allowNegative) ?? throw new ArgumentException($"{what} is needed. {MeasureHelp}");

    private static Box BoxFrom(JsonElement x, JsonElement y, JsonElement w, JsonElement h)
    {
        var box = new Box(Need(x, "x", true), Need(y, "y", true), Need(w, "w"), Need(h, "h"));
        if (box.W < 1 || box.H < 1) throw new ArgumentException("w and h must each be at least 1 point.");
        return box;
    }

    private static JsonElement Num(double v) => JsonSerializer.SerializeToElement(v);

    private static (double W, double H) PageSize(string? paper, JsonElement? width, JsonElement? height, bool landscape)
    {
        double w, h;
        if (paper is not null)
        {
            var p = PaperSize.All.FirstOrDefault(x => x.Name.Equals(paper.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There's no paper \"{paper}\". The papers: {string.Join(", ", PaperSize.All.Select(x => x.Name))}; or give width and height.");
            (w, h) = (p.Width, p.Height);
        }
        else
        {
            (w, h) = (Measure(width, "width") ?? 612, Measure(height, "height") ?? 792);
        }
        if (landscape && w < h) (w, h) = (h, w);
        if (w < 72 || h < 72 || w > 14400 || h > 14400) throw new ArgumentException("A page is from 1 inch (72 pt) to 200 inches each way.");
        return (w, h);
    }

    private static Margins MarginsFrom(JsonElement? value, Margins basis)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return basis;
        if (v.ValueKind == JsonValueKind.Object)
        {
            double? Side(string name) => v.TryGetProperty(name, out var side) ? Measure(side, "margins." + name) : null;
            return new Margins(Side("top") ?? basis.Top, Side("bottom") ?? basis.Bottom, Side("left") ?? Side("inside") ?? basis.Left, Side("right") ?? Side("outside") ?? basis.Right);
        }
        var all = Measure(v, "margins")!.Value;
        return new Margins(all, all, all, all);
    }

    /// <summary>A colour an agent gave, as the document keeps it (a swatch reference or a hex value), or null for none.</summary>
    internal static string? Colour(Document d, string? text, string what)
    {
        if (text is null) return null;
        if (!Colours.TryRead(text, d.Swatches, out var colour))
            throw new ArgumentException($"{what} is \"#RRGGBB\", a swatch's name ({string.Join(", ", d.Swatches.Select(w => w.Name))}), a swatch with a tint (\"Blue 40%\") or \"none\", not \"{text}\".");
        return colour;
    }

    private static double Width(double w) =>
        double.IsFinite(w) && w is >= 0 and <= 144 ? w : throw new ArgumentException("stroke_width is from 0 to 144 points.");

    internal static double R(double v) => Math.Round(v, 2);

    internal static JsonObject BoxJson(Box b) => new() { ["x"] = R(b.X), ["y"] = R(b.Y), ["w"] = R(b.W), ["h"] = R(b.H) };

    private static JsonObject Margins(Margins m) => new() { ["top"] = R(m.Top), ["bottom"] = R(m.Bottom), ["left"] = R(m.Left), ["right"] = R(m.Right) };

    /// <summary>An object as an agent sees it.</summary>
    internal JsonObject Describe(Document d, PageObject o)
    {
        var j = new JsonObject { ["id"] = o.Id, ["kind"] = o.Kind };
        if (o.Name.Length > 0) j["name"] = o.Name;
        j["box"] = BoxJson(o.Bounds);
        if (o.Rotation != 0) j["rotation"] = R(o.Rotation);
        if (o is not (TextFrame or PictureObject or GroupObject) || o.Fill is not null) j["fill"] = Colours.Describe(o.Fill, d.Swatches);
        if (o is not GroupObject && (o is not (TextFrame or PictureObject) || o.Stroke is not null))
        {
            j["stroke"] = Colours.Describe(o.Stroke, d.Swatches);
            if (o.Stroke is not null) j["stroke_width"] = R(o.StrokeWidth);
        }
        if (o.Opacity < 1) j["opacity"] = R(o.Opacity);
        if (d.Layers.Count > 1) j["layer"] = d.LayerOf(o).Name;
        if (o.Locked) j["locked"] = true;
        if (o.Wrap) j["wrap"] = true;
        switch (o)
        {
            case TextFrame f when d.StoryOf(f) is { } story:
                j["story"] = story.Id;
                if (f.Columns > 1) j["columns"] = f.Columns;
                if (story.Frames.Count > 1) j["chain"] = $"frame {story.Frames.IndexOf(f.Id) + 1} of {story.Frames.Count}";
                var shown = story.Text.Length > 120 ? story.Text[..120] + "…" : story.Text;
                j["text_starts"] = Shown(shown).Replace('\n', '¶');
                if (host.Library.LayoutOf(d, story.Id) is { } layout) j["overset"] = layout.Overset;
                break;
            case PictureObject p:
                j["picture"] = p.Source is { } src ? Path.GetFileName(src) : "(pasted)";
                j["fit"] = p.Fit.ToString().ToLowerInvariant();
                if (p.Linked) j["linked"] = true;
                break;
            case TableObject t:
                j["rows"] = t.Rows;
                j["columns"] = t.Columns;
                j["header_rows"] = t.HeaderRows;
                j["cells"] = new JsonArray(t.Cells.Select(c => (JsonNode)c).ToArray());
                break;
            case GroupObject g:
                j["members"] = new JsonArray(g.Children.Select(c => (JsonNode)Describe(d, c)).ToArray());
                break;
            case LineObject l:
                var (a, b) = l.Ends;
                j["from"] = new JsonArray(R(a.X), R(a.Y));
                j["to"] = new JsonArray(R(b.X), R(b.Y));
                break;
            case RectangleObject r when r.CornerRadius > 0:
                j["corner_radius"] = R(r.CornerRadius);
                break;
        }
        return j;
    }

    private JsonObject DescribeStory(Document d, Story st)
    {
        var j = new JsonObject
        {
            ["id"] = st.Id,
            ["frames"] = new JsonArray(st.Frames.Select(f => (JsonNode)f).ToArray()),
            ["characters"] = st.Length,
            ["paragraphs"] = st.ParagraphCount,
        };
        if (host.Library.LayoutOf(d, st.Id) is { } layout) j["overset"] = layout.Overset;
        return j;
    }

    /// <summary>Says whether a story fits its frames and, if not, how much is left over.</summary>
    private void AddFit(JsonObject o, Document d, string storyId)
    {
        if (host.Library.LayoutOf(d, storyId) is not { } layout) return;
        o["overset"] = layout.Overset;
        if (layout.Overset)
        {
            o["characters_not_shown"] = layout.Story.Length - layout.OversetFrom;
            o["note"] = "The text doesn't all fit: make the frame bigger, link another frame (link_frames, or add_text_frame with follows), or cut the text.";
        }
    }

    /// <summary>Runs a call that changes nothing (or replaces the document wholesale) and answers in JSON.</summary>
    private Task<string> Read(string action, Func<IPageSession, JsonObject> work, CancellationToken cancel) =>
        Run(action, s => work(s).ToJsonString(Json.Options), cancel);

    /// <summary>
    /// Runs a change as one undo step called <paramref name="label"/>. If it fails, the document goes back to how it
    /// was before, and no step is left behind.
    /// </summary>
    private Task<string> Edit(string label, Func<IPageSession, JsonObject> work, CancellationToken cancel) =>
        Run(label, s => s.Editor.Group("AI: " + label, () => work(s)).ToJsonString(Json.Options), cancel);

    private async Task<T> Run<T>(string action, Func<IPageSession, T> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    private async Task<T> RunAsync<T>(string action, Func<IPageSession, Task<T>> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>A failure's message for the agent: the message alone for the expected kinds, else with its type.</summary>
    internal static string Explain(Exception e) => e switch
    {
        ArgumentException or InvalidOperationException or FormatException or NotSupportedException or IOException
            or UnauthorizedAccessException or PageBusyException or JsonException or InvalidDataException or KeyNotFoundException => e.Message,
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}
