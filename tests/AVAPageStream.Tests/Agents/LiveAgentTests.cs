using System.IO.Pipelines;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AVAPageStream.Chat;
using AVAPageStream.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAPageStream.Tests.Agents;

/// <summary>Agents in the open window: the live host, the AI menu and the chat panel.</summary>
public class LiveAgentTests
{
    private static MainWindow Open()
    {
        var home = Directory.CreateTempSubdirectory("avaps-live-").FullName;
        var w = new MainWindow(Path.Combine(home, "settings.json"))
        {
            UserWordsPath = Path.Combine(home, "words.txt"),
            TemplatesFolder = Path.Combine(home, "templates"),
            AgentPipe = "avapagestream-test-" + Guid.NewGuid().ToString("N"),
            ChatKeys = (new ApiKeyStore(null, null), new ApiKeyStore(null, null)),
        };
        w.Ask = (_, _, _) => throw new Xunit.Sdk.XunitException("Nothing should have asked.");
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    [AvaloniaFact]
    public void An_agents_calls_change_the_windows_document_and_ctrl_z_undoes_them()
    {
        var w = Open();
        var root = Directory.CreateTempSubdirectory("avaps-root-").FullName;
        var bridge = AgentTests.Wait(McpToolBridge.StartAsync(w.Agents.Host(root)));
        var input = JsonSerializer.SerializeToElement(new { x = 72, y = 72, w = 300, h = 80, text = "From an agent" }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var outcome = AgentTests.Wait(bridge.CallAsync("add_text_frame", input));
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal("From an agent", w.Editor.Doc.Stories.Values.Single().Text);
        Assert.Equal("AI agent: added a text frame.", w.Message.Text);
        Assert.True(w.Editor.Dirty);

        w.View.Focus();
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(w.Editor.Doc.AllObjects);
        AgentTests.Wait(bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
    }

    [AvaloniaFact]
    public void An_agent_waits_while_a_dialog_is_open()
    {
        var w = Open();
        var root = Directory.CreateTempSubdirectory("avaps-root-").FullName;
        var host = new PageHost(w.Agents, w.Agents, w.Agents.Library, root) { BusyWait = TimeSpan.FromMilliseconds(300), BusyPoll = TimeSpan.FromMilliseconds(20) };
        w.Editor.Begin(); // the person is dragging
        var call = host.RunAsync("looked", s => s.Editor.Doc.Pages.Count);
        var e = Assert.Throws<PageBusyException>(() => AgentTests.Wait(call));
        Assert.Contains("dragging", e.Message);
        w.Editor.Cancel();
        Assert.Equal(1, AgentTests.Wait(host.RunAsync("looked", s => s.Editor.Doc.Pages.Count)));
    }

    [AvaloniaFact]
    public void F12_shows_the_ai_chat_and_allowing_agents_opens_the_door()
    {
        var w = Open();
        Assert.False(w.ChatHolder.IsVisible);
        w.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.ChatHolder.IsVisible);
        Assert.NotNull(w.Chat);
        Assert.True(w.Settings.ChatOpen);
        Assert.Contains("lay out a page", w.Chat!.Input.PlaceholderText);

        Assert.StartsWith("AI agents may connect", w.SetAllowAgents(true));
        Assert.True(w.Agents.Running);
        Assert.True(LiveListener.Answers(w.AgentPipe));
        Assert.Equal("AI agents are shut out.", w.SetAllowAgents(false));
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.Agents.Running);
    }

    [AvaloniaFact]
    public void An_outside_agent_attaches_through_the_pipe_and_works_in_the_window()
    {
        var w = Open();
        Assert.StartsWith("AI agents may connect", w.SetAllowAgents(true));
        var root = Directory.CreateTempSubdirectory("avaps-root-").FullName;
        // AVAPageStream --mcp --attach: a server on stdio passing each call on through the pipe; here stdio is a pair of in-memory pipes.
        var bridge = new LiveBridge(w.AgentPipe, root);
        Pipe up = new(), down = new();
        var server = McpServer.Create(new StreamServerTransport(up.Reader.AsStream(), down.Writer.AsStream(), "attach"), bridge.Options());
        var running = Task.Run(() => server.RunAsync());
        var client = AgentTests.Wait(McpClient.CreateAsync(new StreamClientTransport(up.Writer.AsStream(), down.Reader.AsStream())));
        Assert.Contains("live app", client.ServerInstructions);
        var result = AgentTests.Wait(client.CallToolAsync("add_shape", new Dictionary<string, object?> { ["kind"] = "ellipse", ["x"] = 100, ["y"] = 100, ["w"] = 50, ["h"] = 50 }).AsTask());
        Assert.False(result.IsError == true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.Equal("Ellipse", Assert.Single(w.Editor.Doc.AllObjects).Kind);
        AgentTests.Wait(client.DisposeAsync().AsTask().ContinueWith(_ => true));
        w.SetAllowAgents(false);
    }

    [AvaloniaFact]
    public void The_network_door_opens_on_the_settings_and_wants_the_token()
    {
        var w = Open();
        w.Settings.AllowNetworkAgents = true;
        w.Settings.NetworkAgentsAddress = "localhost";
        w.Settings.NetworkAgentsPort = 0;
        w.Settings.NetworkAgentsFolder = Directory.CreateTempSubdirectory("avaps-net-").FullName;
        var line = AgentTests.Wait(w.SyncNetworkAsync());
        Assert.StartsWith("AI agents on other computers may connect", line);
        var url = w.Agents.NetworkUrls.First();
        using var http = new HttpClient();
        var refused = AgentTests.Wait(http.PostAsync(url, new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
        w.SaveSettings();
        Assert.DoesNotContain(w.NetworkToken, File.ReadAllText(w.SettingsPath)); // the token lives beside the settings, never in them
        w.Settings.AllowNetworkAgents = false;
        Assert.Equal("AI agents on other computers are shut out.", AgentTests.Wait(w.SyncNetworkAsync()));
    }
}
