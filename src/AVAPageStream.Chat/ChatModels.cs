using AVAPageStream.Mcp;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVAPageStream.Chat;

/// <summary>The Claude models the chat offers, by their API ids.</summary>
public static class ChatModels
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-4-5-20251001";

    /// <summary>The model a new chat starts with.</summary>
    public const string Default = Opus;

    /// <summary>The choices, most capable first, with a name to show.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Opus, "Claude Opus 5.5"),
        (Sonnet, "Claude Sonnet 5.5"),
        (Haiku, "Claude Haiku 4.5"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>What the model is told: the MCP server's own instructions, and a few lines about working inside the program.</summary>
public static class ChatPrompt
{
    public const string InApp = """
        You are working inside AVAPageStream itself, in its AI Chat panel, with the person watching. Your tool calls change the document open in the window as you make them, and the person's Ctrl+Z undoes your calls one at a time. Talk to them briefly and plainly: say what you're about to lay out, do it, look at it with render_page, fix what doesn't look right, then say what you made. Keep it to a few sentences; the tool calls show in the panel on their own. If the person is busy in the program (a dialog open, a drag under way), calls wait for them. Files you open, save or export are in the working folder.
        """;

    /// <summary>The system prompt: the same instructions every MCP agent gets, then <see cref="InApp"/>.</summary>
    public static string System => PageServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();

    /// <summary>Added for a model given only <see cref="ChatTools.Core"/>.</summary>
    public const string CoreToolsNote =
        "In this chat you have a core set of the server's tools, not all of them: the instructions above may name tools you don't have, so work with the ones you're given.";

    /// <summary>Added for a model that can't see pictures (its tools' pictures become a line of text).</summary>
    public const string NoPicturesNote =
        "You can't see pictures in this chat: render_page still shows the person what you made, but check your work yourself with get_page, get_text and preflight.";
}

/// <summary>Which of the MCP server's tools the chat offers a model.</summary>
public static class ChatTools
{
    /// <summary>
    /// The core set for small local models, which choose better among fewer tools: the document and pages, frames and
    /// shapes, text and its formats, a look and a check, and files.
    /// </summary>
    public static readonly IReadOnlyList<string> Core =
    [
        "get_document", "new_document", "save_document", "get_page", "add_pages", "add_text_frame", "add_shape", "add_picture",
        "set_object", "delete_objects", "get_text", "set_text", "format_text", "format_paragraphs", "render_page",
        "preflight", "export_pdf", "undo",
    ];

    /// <summary>The core tools among <paramref name="tools"/>, in the server's order.</summary>
    public static IReadOnlyList<McpTool> CoreOf(IReadOnlyList<McpTool> tools) => tools.Where(t => Core.Contains(t.Name)).ToList();
}
