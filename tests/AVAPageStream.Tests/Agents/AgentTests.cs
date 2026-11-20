using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AVAPageStream.Agents;
using AVAPageStream.Chat;
using AVAPageStream.Core;
using AVAPageStream.Mcp;
using ModelContextProtocol.Server;
using static AVAPageStream.Tests.Agents.FakeModelServer;

namespace AVAPageStream.Tests.Agents;

/// <summary>
/// The MCP server and the AI Chat, driven as agents drive them: through an in-process MCP client over the real server,
/// on the program's own text measuring and drawing (Avalonia's headless platform with Skia). The chat's model is a
/// scripted fake OpenAI-compatible server; no real model is ever called.
/// </summary>
public class AgentTests
{
    /// <summary>Every tool the server offers. A new tool goes here, with descriptions on it and its arguments.</summary>
    private static readonly string[] ToolNames =
    [
        "get_document", "new_document", "open_document", "save_document", "set_document", "list_fonts", "get_page", "add_pages",
        "delete_page", "move_page", "set_page_master", "add_text_frame", "add_shape", "add_picture", "add_table", "set_object",
        "delete_objects", "duplicate_objects", "arrange_objects", "group_objects", "link_frames", "undo", "redo",
        "checkpoint", "diff", "render_checkpoint", "restore_checkpoint", "render_page", "preflight", "export_pdf", "export_png",
        "get_text", "set_text", "format_text", "format_paragraphs", "define_paragraph_style", "define_character_style",
        "add_swatch", "find_text", "replace_text",
    ];

    /// <summary>A server on a document of its own, in a temporary working folder, and a client connected to it.</summary>
    private sealed class Rig : IDisposable
    {
        public HeadlessDesk Desk { get; } = new();
        public string Root { get; } = Directory.CreateTempSubdirectory("avaps-agent-").FullName;
        public McpToolBridge Bridge { get; }
        public Editor Editor => Desk.Session.Editor;

        public Rig() => Bridge = Wait(McpToolBridge.StartAsync(new PageHost(new AvaloniaThread(), Desk, new AgentLibrary(), Root)));

        public ToolOutcome Call(string tool, object? args = null)
        {
            var input = JsonSerializer.SerializeToElement(args ?? new { }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            return Wait(Bridge.CallAsync(tool, input));
        }

        /// <summary>A call that must work, its answer as JSON.</summary>
        public JsonElement Ok(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.False(o.IsError, $"{tool} failed: {o.Text}");
            return o.Text.StartsWith('{') ? JsonDocument.Parse(o.Text).RootElement.Clone() : default;
        }

        public void Dispose() => Wait(Bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
    }

    internal static T Wait<T>(Task<T> task)
    {
        for (var i = 0; i < 6000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Assert.True(task.IsCompleted, "It didn't finish in time.");
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void The_tools_are_the_ones_listed_and_each_says_what_it_and_its_arguments_are()
    {
        using var rig = new Rig();
        var tools = Wait(rig.Bridge.ListToolsAsync());
        Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} has no description.");
            foreach (var p in t.InputSchema.GetProperty("properties").EnumerateObject())
                Assert.True(p.Value.TryGetProperty("description", out var d) && d.GetString()!.Length > 0, $"{t.Name}'s {p.Name} has no description.");
        }
    }

    [AvaloniaFact]
    public void Every_recipe_runs_call_by_call()
    {
        var recipes = typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null).ToList();
        Assert.Equal(3, recipes.Count);
        foreach (var recipe in recipes)
        {
            using var rig = new Rig();
            var text = (string)recipe.Invoke(null, recipe.GetParameters().Select(_ => (object?)null).ToArray())!;
            var calls = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(Recipes.CallMarker, StringComparison.Ordinal)).ToList();
            Assert.True(calls.Count >= 8, $"{recipe.Name} has few steps.");
            foreach (var call in calls)
            {
                var rest = call[Recipes.CallMarker.Length..];
                var space = rest.IndexOf(' ');
                var (tool, args) = (rest[..space], JsonDocument.Parse(rest[(space + 1)..]).RootElement);
                var o = rig.Call(tool, args);
                Assert.False(o.IsError, $"{recipe.Name}: {tool} failed: {o.Text}");
                if (tool == "preflight")
                    Assert.Equal(0, JsonDocument.Parse(o.Text).RootElement.GetProperty("serious").GetInt32());
            }
            Assert.True(rig.Editor.Doc.AllObjects.Count() >= 3, $"{recipe.Name} made little.");
        }
    }

    [AvaloniaFact]
    public void A_call_is_one_undo_step_and_one_that_fails_changes_nothing()
    {
        using var rig = new Rig();
        var frame = rig.Ok("add_text_frame", new { x = "1in", y = "1in", w = "4in", h = "2in", text = "# Title\n\nSome **body** text.", markdown = true });
        var id = frame.GetProperty("id").GetString()!;
        // A frame, its text and its styles: three changes inside, one step outside.
        Assert.Equal("AI: added a text frame", rig.Editor.UndoLabel);
        var story = rig.Editor.Doc.Stories[frame.GetProperty("story").GetString()!];
        Assert.Equal("Title\nSome body text.", story.Text);
        Assert.Equal("Heading", story.Paras[0].Style);
        Assert.True(story.FormatAt(story.Text.IndexOf("body", StringComparison.Ordinal)).Bold);

        var before = rig.Editor.Doc.ToJson(false);
        var bad = rig.Call("set_text", new { id, text = "x", start = 999 });
        Assert.True(bad.IsError);
        Assert.Contains("start is from 0 to", bad.Text);
        Assert.Equal(before, rig.Editor.Doc.ToJson(false));
        Assert.Equal("AI: added a text frame", rig.Editor.UndoLabel);

        rig.Ok("undo");
        Assert.Empty(rig.Editor.Doc.AllObjects);
        rig.Ok("redo");
        Assert.Single(rig.Editor.Doc.AllObjects);
    }

    [AvaloniaFact]
    public void Text_that_doesnt_fit_says_so_and_flows_on_into_a_linked_frame()
    {
        using var rig = new Rig();
        var text = string.Join("\n", Enumerable.Repeat("A paragraph long enough to need a few lines in a narrow frame like this one.", 12));
        var first = rig.Ok("add_text_frame", new { x = 72, y = 72, w = 200, h = 100, text });
        Assert.True(first.GetProperty("overset").GetBoolean());
        Assert.True(first.GetProperty("characters_not_shown").GetInt32() > 0);
        var second = rig.Ok("add_text_frame", new { x = 300, y = 72, w = 200, h = 600, follows = first.GetProperty("id").GetString() });
        Assert.False(second.GetProperty("overset").GetBoolean());
        Assert.Equal(first.GetProperty("story").GetString(), second.GetProperty("story").GetString());
        var page = rig.Ok("get_page", new { page = "1" });
        Assert.Equal("frame 2 of 2", page.GetProperty("objects")[1].GetProperty("chain").GetString());
    }

    [AvaloniaFact]
    public void Measures_take_units_and_colours_take_swatches()
    {
        using var rig = new Rig();
        rig.Ok("add_swatch", new { name = "Brand", colour = "#336699" });
        var shape = rig.Ok("add_shape", new { kind = "rectangle", x = "1in", y = "2cm", w = "6p", h = "10mm", fill = "Brand 50%", stroke = "none" });
        var o = rig.Editor.Doc.Find(shape.GetProperty("id").GetString()!)!;
        Assert.Equal(new Box(72, 56.69, 72, 28.35), o.Bounds);
        Assert.Equal("@Brand 50%", o.Fill);
        Assert.Null(o.Stroke);
        var bad = rig.Call("add_shape", new { kind = "rectangle", x = 0, y = 0, w = 10, h = 10, fill = "Mauve" });
        Assert.True(bad.IsError);
        Assert.Contains("swatch", bad.Text);
    }

    [AvaloniaFact]
    public void A_page_is_drawn_exported_and_saved_inside_the_working_folder()
    {
        using var rig = new Rig();
        rig.Ok("add_text_frame", new { x = 72, y = 72, w = 300, h = 100, text = "Hello" });
        var picture = rig.Call("render_page", new { page = "1", dpi = 30 });
        Assert.False(picture.IsError, picture.Text);
        var png = Assert.Single(picture.Images);
        Assert.Equal("image/png", png.MediaType);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png.Data[..4]);

        rig.Ok("export_pdf", new { path = "out/hello" });
        Assert.StartsWith("%PDF", File.ReadAllText(Path.Combine(rig.Root, "out", "hello.pdf"))[..4]);
        Assert.True(rig.Call("export_pdf", new { path = "out/hello.pdf" }).IsError is false, "Writing again over its own file is allowed.");
        rig.Ok("save_document", new { path = "hello" });
        Assert.True(File.Exists(Path.Combine(rig.Root, "hello.avps")));
        Assert.False(rig.Editor.Dirty);

        var outside = rig.Call("save_document", new { path = "../escape.avps" });
        Assert.True(outside.IsError);
        Assert.Contains("outside the working folder", outside.Text);

        rig.Ok("new_document", new { paper = "A5" });
        var opened = rig.Ok("open_document", new { path = "hello.avps" });
        Assert.Equal(1, opened.GetProperty("pages").GetInt32());
        Assert.Equal("Hello", rig.Editor.Doc.Stories.Values.Single().Text);
    }

    [AvaloniaFact]
    public void A_checkpoint_is_compared_with_and_gone_back_to_as_one_undo_step()
    {
        using var rig = new Rig();
        rig.Ok("add_shape", new { kind = "ellipse", x = 10, y = 10, w = 50, h = 50 });
        rig.Ok("checkpoint", new { name = "before" });
        rig.Ok("add_text_frame", new { x = 72, y = 72, w = 300, h = 100, text = "New" });
        rig.Ok("add_pages", new { count = 2 });
        var diff = rig.Ok("diff", new { since = "before" });
        Assert.True(diff.GetProperty("changed").GetBoolean());
        Assert.Equal(1, diff.GetProperty("objects").GetProperty("added").GetArrayLength());
        Assert.Equal(3, diff.GetProperty("pages").GetProperty("now").GetInt32());
        Assert.Equal("+1 object, text in 1 story, 2 pages added", ChatSession.Summarise(System.Text.Json.Nodes.JsonNode.Parse(diff.GetRawText())!.AsObject()));
        Assert.Single(rig.Call("render_checkpoint", new { name = "before" }).Images);

        rig.Ok("restore_checkpoint", new { name = "before" });
        Assert.Single(rig.Editor.Doc.AllObjects);
        Assert.Single(rig.Editor.Doc.Pages);
        rig.Ok("undo");
        Assert.Equal(3, rig.Editor.Doc.Pages.Count);
    }

    [AvaloniaFact]
    public void Styles_find_and_replace_work_through_the_stories()
    {
        using var rig = new Rig();
        var f = rig.Ok("add_text_frame", new { x = 72, y = 72, w = 400, h = 300, text = "Walk 1\nUp the hill.\nWalk 2\nDown the dale." });
        rig.Ok("define_paragraph_style", new { name = "Walk", based_on = "Subheading", italic = true, keep_with_next = true });
        var replaced = rig.Ok("replace_text", new { query = "^Walk \\d$", replacement = "", pattern = true, to_paragraph_style = "Walk" });
        Assert.Equal(2, replaced.GetProperty("replaced").GetInt32());
        var text = rig.Ok("get_text", new { id = f.GetProperty("id").GetString() });
        Assert.Equal(["Walk", "Body", "Walk", "Body"], text.GetProperty("paragraphs").EnumerateArray().Select(p => p.GetProperty("style").GetString()));
        var found = rig.Ok("find_text", new { query = "", paragraph_style = "Walk" });
        Assert.Equal(2, found.GetProperty("count").GetInt32());
    }

    [AvaloniaFact]
    public void A_chat_on_a_local_model_lays_out_the_page_and_its_reply_can_be_undone()
    {
        using var rig = new Rig();
        var server = new FakeModelServer()
            .Reply(Say("I'll add a heading."), Call("c1", "add_text_frame", new { x = 72, y = 72, w = 400, h = 60, text = "Bake Sale", style = "Heading" }))
            .Reply(Say("Done: a heading at the top of page 1."));
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.OpenAICompatible, Server = server.Client(), ServerModel = "qwen3" };
        Wait(chat.SendAsync("make a heading saying Bake Sale").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

        Assert.Equal("Bake Sale", rig.Editor.Doc.Stories.Values.Single().Text);
        var tool = Assert.Single(chat.Entries.OfType<ToolEntry>());
        Assert.Equal("add_text_frame", tool.Name);
        Assert.False(tool.IsError, tool.Result);
        Assert.Contains("AVAPageStream", (string?)server.Requests[0]["messages"]![0]!["content"]);
        Assert.Equal("Done: a heading at the top of page 1.", chat.Entries.OfType<AssistantEntry>().Last().Text);

        var undo = Assert.Single(chat.Entries.OfType<ReplyUndoEntry>());
        Assert.True(undo.CanUndo);
        Assert.True(Wait(chat.UndoReplyAsync(undo)));
        Assert.Empty(rig.Editor.Doc.AllObjects);
    }

    [AvaloniaFact]
    public void Core_tools_for_small_models_are_all_real_tools()
    {
        foreach (var name in ChatTools.Core) Assert.Contains(name, ToolNames);
        Assert.Contains("render_page", ChatPrompt.NoPicturesNote);
    }
}
