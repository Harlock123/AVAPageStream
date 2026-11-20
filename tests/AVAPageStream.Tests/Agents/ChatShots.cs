using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AVAPageStream.Chat;
using AVAPageStream.Tests.Agents;
using static AVAPageStream.Tests.Agents.FakeModelServer;

namespace AVAPageStream.Tests;

public partial class WindowTests
{
    /// <summary>The AI Chat laying out a flyer, with a local model's side scripted (the tool calls run for real).</summary>
    [AvaloniaFact]
    public void Guide_the_ai_chat()
    {
        var w = GuideWindow();
        w.ChatKeys = (new ApiKeyStore(null, null), new ApiKeyStore(null, null));
        w.Settings.ChatProvider = "OpenAICompatible";
        w.Settings.ChatServerModel = "qwen3";
        var server = new FakeModelServer()
            .Reply(Say("I'll make a band of colour across the top with the headline in it, then the details below."),
                Call("c1", "add_swatch", new { name = "Accent", colour = "#1F6FB2" }),
                Call("c2", "add_shape", new { kind = "rectangle", x = 0, y = 0, w = 612, h = 170, fill = "Accent", stroke = "none" }),
                Call("c3", "add_text_frame", new { x = 54, y = 50, w = 504, h = 90, text = "Summer Fair", style = "Heading" }))
            .Reply(Call("c4", "format_text", new { id = "frame2", size = 60, colour = "#FFFFFF" }),
                Call("c5", "format_paragraphs", new { id = "frame2", align = "centre" }),
                Call("c6", "add_text_frame", new { x = 54, y = 200, w = 504, h = 400, columns = 2, markdown = true, text = "## When\nSaturday 14 June, 10 till 4.\n\n## Where\nThe village green, by the church.\n\n## What's on\n- Stalls and games\n- Live music from noon\n- Cakes, teas and a barbecue" }))
            .Reply(Call("c7", "render_page", new { page = "1" }))
            .Reply(Say("Done: the headline sits in a blue band across the top, with when, where and what's on in two columns below. Ctrl+Z takes back any step, or Undo this reply all of it."));
        w.ToggleChat();
        w.Chat!.ServerFactory = (_, _) => server.Client();
        w.Chat.LoadSettings();
        w.Chat.Input.Text = "Make a one-page flyer for the summer fair on the village green";
        w.Chat.Send();
        for (var i = 0; i < 3000 && (w.Chat.Session.Working || server.Left > 0); i++) { Pump(); Thread.Sleep(2); }
        Pump();
        w.View.FitPage();
        Guide(w, "ai-chat");
        Assert.Equal(0, server.Left);
        Assert.Contains(w.Editor.Doc.Stories.Values, s => s.Text == "Summer Fair");
        Assert.All(w.Chat.Session.Entries.OfType<ToolEntry>(), t => Assert.False(t.IsError, t.Result));
        Assert.Contains(w.Chat.Session.Entries.OfType<ReplyUndoEntry>(), e => e.CanUndo);
        Done(w);
    }
}
