using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AVAPageStream.Mcp;

/// <summary>
/// Saved recipes, served as MCP prompts: short step-by-step plans for documents agents are often asked for, written
/// with the real tool names and arguments. Each step that calls a tool shows the call on a line of its own,
/// "→ tool {arguments}", so an agent can follow it as it is (and the tests run every recipe through the server, call by
/// call, so a recipe that stops working fails the build).
/// </summary>
[McpServerPromptType]
public static class Recipes
{
    /// <summary>The marker that starts a step's tool call in a recipe's text.</summary>
    public const string CallMarker = "→ ";

    [McpServerPrompt(Name = "flyer", Title = "One-page flyer")]
    [Description("A one-page flyer: a coloured band across the top with the headline in it, the details below in two columns, and a footer line; checked by eye and by preflight, then exported as a PDF.")]
    public static string Flyer(
        [Description("The headline (default \"Summer Fair\").")] string? headline = null,
        [Description("The accent colour as #RRGGBB (default #1F6FB2).")] string? colour = null)
    {
        var title = string.IsNullOrWhiteSpace(headline) ? "Summer Fair" : headline.Trim();
        var accent = string.IsNullOrWhiteSpace(colour) ? "#1F6FB2" : colour.Trim();
        if (!AVAPageStream.Core.Colours.TryParse(accent, out _)) throw new McpException($"colour is #RRGGBB, not \"{colour}\".");
        var r = new Recipe($"Make a one-page Letter flyer headed \"{title}\", in an accent colour of {accent}.");
        r.Step("Start a Letter document with 0.75-inch margins. If it refuses because the open one has unsaved changes, save that first or ask the person.", "new_document", new { paper = "Letter", margins = "0.75in" });
        r.Step("Make the accent a swatch, so changing it later changes everything in it.", "add_swatch", new { name = "Accent", colour = accent });
        r.Step("A bleed, so colour can run off the page's edges in print.", "set_document", new { bleed = 9, title = title });
        r.Step("A band of the accent across the top of the page, out past the edges into the bleed.", "add_shape", new { kind = "rectangle", x = -9, y = -9, w = 630, h = 189, fill = "Accent", stroke = "none" });
        r.Step("The headline in the band (the frame's id comes back; use it in the next step).", "add_text_frame", new { x = "0.75in", y = "0.75in", w = "7in", h = "1.25in", text = title, style = "Heading", name = "Headline" });
        r.Step("Make it big and white, centred.", "format_text", new { id = "frame2", size = 54, colour = "#FFFFFF" });
        r.Step(null, "format_paragraphs", new { id = "frame2", align = "centre" });
        r.Step("The details below, in two columns (write the person's own details here).", "add_text_frame", new
        {
            x = "0.75in", y = "3in", w = "7in", h = "6.5in", columns = 2, gutter = 24, markdown = true,
            text = "## When\nSaturday 14 June, 10 till 4.\n\n## Where\nThe village green, by the church.\n\n## What's on\n- Stalls and games\n- Live music from noon\n- Cakes, teas and a barbecue\n\n## Getting there\nParking in the school field. Buses 12 and 14 stop outside.",
        });
        r.Step("A rule and a footer at the bottom.", "add_shape", new { kind = "line", x = "0.75in", y = "9.9in", w = "7in", h = 0, stroke = "Accent", stroke_width = 2 });
        r.Step(null, "add_text_frame", new { x = "0.75in", y = "10in", w = "7in", h = "0.3in", text = "Everyone welcome · Free entry", style = "Caption" });
        r.Step("Look at it: the headline should sit in the band, the details in two even columns.", "render_page", new { page = "1" });
        r.Step("Check it for print: there should be nothing serious (no overset text).", "preflight", new { });
        r.Step("Export it.", "export_pdf", new { path = "flyer.pdf", overwrite = true });
        return r.Text("Tell the person where the PDF is. If they want changes, set_text, format_text and set_object change what's there; save_document keeps it as .avps.");
    }

    [McpServerPrompt(Name = "newsletter", Title = "Two-column newsletter")]
    [Description("A newsletter from the built-in template: its masthead, two linked columns and a footer with the page number on the master; the masthead and story filled in, a second page added with the story flowing on to it.")]
    public static string Newsletter(
        [Description("The newsletter's name (default \"The Parish Pump\").")] string? name = null)
    {
        var title = string.IsNullOrWhiteSpace(name) ? "The Parish Pump" : name.Trim();
        var r = new Recipe($"Make a two-page newsletter called \"{title}\" from the Newsletter template.");
        r.Step("Start from the template. If it refuses because the open one has unsaved changes, save that first or ask the person.", "new_document", new { template = "Newsletter" });
        r.Step("See what's on the page: the masthead frame, the black rule and the two linked columns.", "get_page", new { page = "1" });
        r.Step("Put the name in the masthead.", "set_text", new { id = "frame4", text = title, style = "Heading" });
        r.Step(null, "format_text", new { id = "frame4", size = 40 });
        r.Step("Put the story in the columns as Markdown (the person's own text goes here; it's long on purpose).", "set_text", new
        {
            id = "frame7", markdown = true,
            text = "## A new roof for the hall\n" + string.Join("\n\n", Enumerable.Repeat("The appeal for the village hall roof passed its target this month, thanks to the summer fair, the quiz night and many generous gifts. Work starts in September and should take six weeks; the hall stays open on the days the builders aren't on site.", 6))
                + "\n\n## Dates for your diary\n" + string.Join("\n\n", Enumerable.Repeat("Harvest supper on the 28th, carols on the green on the 20th of December, and the spring clean-up when the daffodils are out.", 5)),
        });
        r.Step("Justify the body text.", "format_paragraphs", new { id = "frame7", style = "Body", align = "justify", hyphenate = true, start = 0 });
        r.Step("A second page, for what doesn't fit.", "add_pages", new { count = 1 });
        r.Step("A two-column frame on page 2 that the story flows on into from the right-hand column.", "add_text_frame", new { page = "2", x = 54, y = 54, w = 504, h = 684, columns = 2, gutter = 18, follows = "frame9" });
        r.Step("Look at both pages.", "render_page", new { page = "1" });
        r.Step(null, "render_page", new { page = "2" });
        r.Step("Check it.", "preflight", new { });
        return r.Text("If the story is still overset, add another page and frame the same way (follows the last frame). Save with save_document and export with export_pdf when the person is happy.");
    }

    [McpServerPrompt(Name = "booklet", Title = "Booklet with page numbers")]
    [Description("An A5 booklet of facing pages: page numbers on the master, one story flowing through a frame on each page, a heading style of its own.")]
    public static string Booklet(
        [Description("The booklet's title (default \"Walks Round the Village\").")] string? title = null)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "Walks Round the Village" : title.Trim();
        var r = new Recipe($"Make a four-page A5 booklet, \"{t}\", with facing pages and a page number at the foot of each.");
        r.Step("Start it. If it refuses because the open one has unsaved changes, save that first or ask the person.", "new_document", new { paper = "A5", facing = true, pages = 4, margins = new { top = 48, bottom = 54, left = 54, right = 40 } });
        r.Step("The page number at the foot of every page, on the master.", "add_text_frame", new { page = "A-Master", x = 54, y = 560, w = 325, h = 16, text = "{page}", style = "Caption" });
        r.Step(null, "format_paragraphs", new { id = "frame1", align = "centre" });
        r.Step("A heading style for the walks.", "define_paragraph_style", new { name = "Walk", based_on = "Subheading", font = "Liberation Serif", size = 15, italic = true, space_before = 14, keep_with_next = true });
        r.Step("A frame on page 1 with the story.", "add_text_frame", new
        {
            page = "1", x = 54, y = 48, w = 325, h = 490, markdown = true, style = "Body",
            text = $"# {t}\n\n" + string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"## Walk {i}\nFrom the church, follow the lane past the pond and over the stile into the meadow. The path climbs gently to the wood, where it turns back along the stream to the mill. About {i + 1} miles; allow {i} hour{(i == 1 ? "" : "s")}.\n\nGood boots in winter; the meadow floods by the stream.")),
        });
        r.Step("Put the walks' headings in their own style.", "replace_text", new { query = "^Walk \\d+$", replacement = "", pattern = true, to_paragraph_style = "Walk" });
        r.Step("Frames on pages 2 to 4, each following the last, so the story flows through the booklet.", "add_text_frame", new { page = "2", x = 40, y = 48, w = 325, h = 490, follows = "frame3" });
        r.Step(null, "add_text_frame", new { page = "3", x = 54, y = 48, w = 325, h = 490, follows = "frame5" });
        r.Step(null, "add_text_frame", new { page = "4", x = 40, y = 48, w = 325, h = 490, follows = "frame7" });
        r.Step("Look at a spread's pages.", "render_page", new { page = "2" });
        r.Step(null, "render_page", new { page = "3" });
        r.Step("Check it.", "preflight", new { });
        return r.Text("On a left-hand page the inside margin is on the right, so its frame sits further left. Save with save_document; export_pdf makes the PDF.");
    }

    private sealed class Recipe(string intro)
    {
        private readonly StringBuilder _text = new(intro + "\n\nFollow these steps in order. A line starting with \"" + CallMarker.Trim() + "\" is one tool call, with its arguments as JSON. If a step fails, read the error, fix the cause and carry on. The ids in these calls are the ones a fresh document gives; if a call returns a different id, use that one.\n");
        private int _number;

        public void Step(string? says, string tool, object args)
        {
            _number++;
            _text.Append('\n').Append(_number).Append('.').Append(says != null ? " " : "");
            if (says != null) _text.Append(says);
            _text.Append("\n   ");
            _text.Append(CallMarker).Append(tool).Append(' ').Append(JsonSerializer.Serialize(args, Json.Options));
        }

        public string Text(string closing) => _text.Append("\n\n").Append(closing).ToString();
    }
}
