using Avalonia.Threading;
using AVAPageStream.Core;
using AVAPageStream.Mcp;

namespace AVAPageStream.Agents;

/// <summary>A window's document as an agent sees it: its editor, its file, the page shown, and opening another.</summary>
internal sealed class WindowSession(MainWindow window) : IPageSession
{
    public Editor Editor => window.Editor;

    public string? FilePath => window.FilePath;

    public int? CurrentPage => window.View.Sheet.IsMaster ? null : window.View.Sheet.Index;

    public void Replace(Document doc, string? path) => window.Adopt(doc, path);

    public void Saved(string path) => window.AgentSaved(path);

    public void Show(SheetRef sheet)
    {
        if (!sheet.Equals(window.View.Sheet)) window.ShowSheet(sheet);
    }
}

/// <summary>
/// The window's door for AI agents, and the live host they and the AI Chat work through.
/// <para>
/// While agents are allowed (AI › Allow AI Agents), a <see cref="LiveListener"/> takes connections from
/// <c>AVAPageStream --mcp --attach</c> bridges (and, while agents on other computers are allowed, a
/// <see cref="NetworkListener"/> MCP clients on the network). Every call runs on the UI thread against the window's
/// document, so the page redraws as the agent works and Ctrl+Z undoes its calls. The AI Chat gets a host of its own
/// (<see cref="ChatHost"/>) whether or not outside agents are allowed. Calls wait while a dialog is open or the person
/// is dragging.
/// </para>
/// </summary>
internal sealed class LiveAgents(MainWindow window) : IPageDesk, IUiThread, IAsyncDisposable
{
    private LiveListener? _listener;
    private NetworkListener? _network;
    private readonly WindowSession _session = new(window);

    /// <summary>Text, drawing and export, as the window does them.</summary>
    public AgentLibrary Library { get; } = new();

    public bool Running => _listener != null;

    /// <summary>How long an agent's call waits for the person to finish with a dialog or a drag.</summary>
    public TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Raised (on the UI thread) when an outside agent connects or goes, with how many are connected.</summary>
    public event Action<int>? ConnectionsChanged;

    public IPageSession Session => _session;

    public string? Busy =>
        window.OwnedWindows.Any(w => w.GetType().Name.EndsWith("Dialog", StringComparison.Ordinal)) || ChatLook.ModalsOpen > 0 ? "a dialog box is open in AVAPageStream"
        : window.Editor.Busy ? "the person is dragging on the page"
        : null;

    public void Report(string text) => window.Say(text);

    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);

    /// <summary>The host outside agents work through, in <paramref name="root"/>.</summary>
    public PageHost Host(string root) => new(this, this, Library, root) { BusyWait = BusyWait };

    /// <summary>
    /// The host the AI Chat runs its in-process MCP server on: the same as outside agents get (UI thread, waiting while
    /// the person is busy, each call one undo step), with status lines naming the chat's model.
    /// </summary>
    public PageHost ChatHost(string root, Func<string> who) => new(this, new ChatDesk(this, who), Library, root) { BusyWait = BusyWait };

    private sealed class ChatDesk(LiveAgents agents, Func<string> who) : IPageDesk
    {
        public IPageSession Session => agents.Session;

        public string? Busy => agents.Busy;

        public void Report(string text) =>
            agents.Report(text.StartsWith("AI agent: ", StringComparison.Ordinal) ? who() + ": " + text["AI agent: ".Length..] : text);
    }

    /// <summary>Opens the door on a pipe; returns why it couldn't (another AVAPageStream has it), or null.</summary>
    public string? Start(string pipeName)
    {
        if (_listener != null) return null;
        var listener = new LiveListener(pipeName, Host);
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = listener.DisposeAsync();
            return e.Message;
        }
        _listener = listener;
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener == null) return;
        listener.Stop();
        await listener.DisposeAsync();
    }

    /// <summary>Whether the network door is open.</summary>
    public bool NetworkRunning => _network != null;

    /// <summary>The settings and working folder the network door was opened with (to tell when they've changed).</summary>
    public NetworkSettings? NetworkSettings { get; private set; }

    public string? NetworkRoot { get; private set; }

    /// <summary>Where the network door listens, while it's open.</summary>
    public IReadOnlyList<string> NetworkUrls => _network?.Urls ?? [];

    /// <summary>Opens the network door in <paramref name="root"/>; returns why it couldn't, or null.</summary>
    public async Task<string?> StartNetworkAsync(NetworkSettings settings, string root)
    {
        if (_network != null) return null;
        if (!Directory.Exists(root)) return $"the working folder {root} doesn't exist.";
        var listener = new NetworkListener(settings, PageServer.Name, o => PageServer.Configure(o, Host(root)));
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            await listener.StartAsync();
        }
        catch (IOException e)
        {
            return e.Message;
        }
        _network = listener;
        NetworkSettings = settings;
        NetworkRoot = root;
        return null;
    }

    public async Task StopNetworkAsync()
    {
        var listener = _network;
        _network = null;
        NetworkSettings = null;
        NetworkRoot = null;
        if (listener != null) await listener.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await StopNetworkAsync();
    }
}
