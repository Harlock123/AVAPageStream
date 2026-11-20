using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAPageStream.Mcp;

/// <summary>
/// The MCP server: <see cref="PageTools"/>' methods as MCP tools (the SDK's attribute model, read by reflection),
/// with instructions that tell an agent AVAPageStream's conventions. The same server runs over stdio headless, over a
/// pipe for agents attached to the open editor, and in-process for the program's own AI Chat.
/// </summary>
public static class PageServer
{
    public const string Name = "AVAPageStream";

    /// <summary>What every agent is told on connecting: the conventions the tools' descriptions build on.</summary>
    public const string Instructions = """
        AVAPageStream is a frame-based desktop publishing program in the spirit of PageStream: pages and master pages, text frames whose stories flow through linked chains of frames and columns, pictures, shapes and tables, paragraph and character styles, swatches, and PDF export drawn by the same renderer as the screen. You lay out documents with the same tools a person uses.

        Conventions (they matter):
        - Measures are points (72 to the inch) unless a tool says otherwise; a measure may also be given as text with its unit, such as "1in", "6p", "12mm" or "2.5cm". Positions are from the top-left corner of the page, x to the right and y downward; an object's x, y, w and h are its box before rotation.
        - Pages are numbered from 1, as the person sees them. Master pages are named ("A-Master"); everything on a page's master shows on that page (headers, footers, page numbers). Tools that take a page also take a master's name instead, to work on the master.
        - Objects are known by their ids ("frame3", "object7"), given when they are made; get_page lists them.
        - Text lives in stories. A text frame shows a story; frames linked into a chain show one story flowing from one to the next (and through each frame's columns). When a story is longer than its frames hold it is "overset": the end doesn't show. Link another frame, enlarge the frame or cut the text; get_text and preflight report it.
        - Text is placed as paragraphs (lines separated by a newline), each in a paragraph style (Body, Heading, Subheading and Caption to start with; define_paragraph_style makes more). With markdown true, # headings become Heading, ## Subheading, and **bold** and *italic* are kept. Character positions in a story count from 0.
        - Colours are "#RRGGBB" (or "#AARRGGBB"), "none", or a swatch's name (with a tint: "Blue 40%"). Prefer swatches for colours used again: change the swatch and everything in it changes.
        - Fonts are by family name (list_fonts); Liberation Serif and Liberation Sans are always there.
        - Every call that changes the document is one undo step (undo takes it back; in the program Ctrl+Z does too), and a call that fails changes nothing.

        A good way to lay out a page: get_document for the page size, margins and styles; add_text_frame inside the margins with the text (markdown for headings); add pictures and shapes; render_page to look at it, and fix what doesn't look right (overlaps, text that doesn't fit, empty space); preflight before export_pdf. For several pages of one story, add_pages, then link_frames from each page's frame to the next.

        The server's prompts are saved recipes (a one-page flyer, a two-column newsletter, a booklet): step-by-step plans with the exact tool calls, to follow or adapt.
        """;

    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(IPageHost host)
    {
        var target = new PageTools(host);
        foreach (var method in typeof(PageTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The saved recipes (<see cref="Recipes"/>) as MCP prompts.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

    /// <summary>The tools' MCP definitions (names, descriptions, input schemas), without a host to run them.</summary>
    public static IList<Tool> Definitions() => [.. _definitions.Value];

    private static readonly Lazy<Tool[]> _definitions = new(() => Tools(new NoHost()).Select(t => t.ProtocolTool).ToArray());

    public static Implementation Info => new()
    {
        Name = Name,
        Title = "AVAPageStream",
        Version = typeof(PageServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0",
    };

    public static McpServerOptions Options(IPageHost host) => Configure(new McpServerOptions(), host);

    /// <summary>Fills a new session's options (ones the network door's HTTP handler made) as <see cref="Options"/> makes them.</summary>
    public static McpServerOptions Configure(McpServerOptions options, IPageHost host)
    {
        options.ServerInfo = Info;
        options.ServerInstructions = Instructions;
        options.ToolCollection = [];
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancel) =>
        {
            var tool = request.Params is { } p && options.ToolCollection.TryGetPrimitive(p.Name, out var t) ? t.ProtocolTool : null;
            if (tool != null && Arguments.Check(tool, request.Params!.Arguments) is { } problem) return Arguments.Error(problem);
            try
            {
                return await next(request, cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not McpException and not OperationCanceledException)
            {
                // The SDK couldn't bind the arguments to the handler (a value of the wrong kind): say which, not just that it failed.
                return Arguments.Error(tool != null ? $"{tool.Name}: {e.Message} Check the arguments against the tool's input schema." : e.Message);
            }
        });
        foreach (var tool in Tools(host)) options.ToolCollection.Add(tool);
        options.PromptCollection = [];
        foreach (var prompt in Prompts()) options.PromptCollection.Add(prompt);
        return options;
    }

    /// <summary>Serves one MCP session over a transport until the client goes away or <paramref name="cancel"/> fires.</summary>
    public static async Task RunAsync(ITransport transport, IPageHost host, CancellationToken cancel = default)
    {
        await using var server = McpServer.Create(transport, Options(host));
        await server.RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks a call's arguments against its tool's input schema before the SDK binds them.</summary>
    internal static class Arguments
    {
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        /// <summary>What's wrong with the arguments (a name the tool doesn't take, a value of the wrong kind), or null.</summary>
        public static string? Check(Tool tool, IDictionary<string, JsonElement>? args)
        {
            if (args == null || !tool.InputSchema.TryGetProperty("properties", out var props)) return null;
            foreach (var (name, value) in args)
            {
                if (!props.TryGetProperty(name, out var schema))
                {
                    var names = props.EnumerateObject().Select(x => x.Name).ToList();
                    return $"{tool.Name} has no argument \"{name}\". It takes: {(names.Count == 0 ? "none" : string.Join(", ", names))}.";
                }
                if (Kinds(schema) is { Count: > 0 } kinds && !kinds.Any(k => Fits(k, value)))
                    return $"{tool.Name}: {name} must be {string.Join(" or ", kinds.Where(k => k != "null").Select(Describe))}, not {Describe(value)}.";
            }
            return null;
        }

        private static List<string> Kinds(JsonElement schema) =>
            schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ? []
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        private static bool Fits(string kind, JsonElement v) => kind switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "number" => v.ValueKind == JsonValueKind.Number,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => v.ValueKind == JsonValueKind.Array,
            "object" => v.ValueKind == JsonValueKind.Object,
            "null" => v.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string Describe(string kind) => kind switch
        {
            "string" => "text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false",
            "array" => "a list", "object" => "an object", _ => kind,
        };

        private static string Describe(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => $"the text \"{v.GetString()}\"",
            JsonValueKind.Number => $"the number {v.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }

    /// <summary>A host for reading the tools' definitions only.</summary>
    private sealed class NoHost : IPageHost
    {
        public string Root => Path.GetTempPath();
        public IPageLibrary Library => throw new InvalidOperationException("Not connected to a document.");
        public Task<T> RunAsync<T>(string action, Func<IPageSession, Task<T>> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected to a document.");
    }
}
