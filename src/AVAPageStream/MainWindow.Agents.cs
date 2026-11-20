using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using AVAPageStream.Agents;
using AVAPageStream.Chat;
using AVAPageStream.Core;
using AVAPageStream.Mcp;

namespace AVAPageStream;

/// <summary>
/// The window's AI side: the AI menu, the AI Chat docked on the right (F12), the door for agents outside the program
/// (AI › Allow AI Agents: <c>AVAPageStream --mcp --attach</c>) and the network door (AI › Connect an Agent).
/// </summary>
public sealed partial class MainWindow
{
    private readonly MenuItem _allowAgentsItem = new() { Header = "_Allow AI Agents (MCP)", ToggleType = MenuItemToggleType.CheckBox };
    private readonly MenuItem _chatItem = new() { Header = "AI _Chat", ToggleType = MenuItemToggleType.CheckBox, InputGesture = Avalonia.Input.KeyGesture.Parse("F12") };
    private LazyToolBridge? _chatTools;
    private string? _networkToken;

    /// <summary>The door for agents and the host they and the chat work through.</summary>
    internal LiveAgents Agents { get; private set; } = null!;

    /// <summary>The AI Chat panel, made the first time it's shown.</summary>
    internal ChatPanel? Chat { get; private set; }

    /// <summary>Where the chat sits, on the right of the window.</summary>
    internal Border ChatHolder { get; } = new() { Width = 440, IsVisible = false, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 1, 0, 0) };

    /// <summary>The pipe outside agents connect on (tests give one of their own).</summary>
    public string AgentPipe { get; set; } = LiveProtocol.PipeName;

    /// <summary>The settings folder (beside the settings file): API keys, saved chats, the network token.</summary>
    private string SettingsFolder => Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!;

    /// <summary>The settings file.</summary>
    internal string SettingsPath => _settingsPath;

    /// <summary>The folder agents' and the chat's file paths start from: the document's, else Documents.</summary>
    internal string AgentRoot => DocumentFolder ?? DocumentsFolder;

    private static string DocumentsFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } d && Directory.Exists(d) ? d
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private void SetUpAgents()
    {
        Agents = new LiveAgents(this);
        Agents.ConnectionsChanged += n => Say(n == 0 ? "No AI agents are connected now." : $"{n} AI agent{(n == 1 ? " is" : "s are")} connected.");
        _allowAgentsItem.Click += (_, _) => Say(SetAllowAgents(!Settings.AllowAgents));
        _chatItem.Click += (_, _) => ToggleChat();
        Opened += async (_, _) =>
        {
            if (Settings.AllowAgents) SetAllowAgents(true);
            if (Settings.AllowNetworkAgents && await SyncNetworkAsync() is { Length: > 0 } line) Say(line);
            if (Settings.ChatOpen) ToggleChat();
        };
        Closed += async (_, _) =>
        {
            SaveSettings();
            if (_chatTools is not null) await _chatTools.DisposeAsync();
            await Agents.DisposeAsync();
        };
    }

    private MenuItem BuildAiMenu() => new()
    {
        Header = "_AI",
        Items =
        {
            _chatItem,
            new Separator(),
            _allowAgentsItem,
            MenuItemFor("_Connect an Agent…", () => _ = ShowConnect()),
        },
    };

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- the documents agents work on ----

    /// <summary>An agent opened or started a document: it replaces the one shown, as File › Open does.</summary>
    internal void Adopt(Document doc, string? path)
    {
        Use(new Editor(doc), path);
        if (path is not null)
        {
            Settings.Opened(path);
            SaveSettings();
        }
        ShowSheet(SheetRef.PageAt(0));
        UpdateTitle();
    }

    /// <summary>An agent saved the document to <paramref name="path"/>.</summary>
    internal void AgentSaved(string path)
    {
        FilePath = path;
        Editor.MarkSaved();
        Recovery.Discard(RecoveryId);
        Settings.Opened(path);
        SaveSettings();
        UpdateTitle();
    }

    // ---- outside agents ----

    /// <summary>Lets agents outside the program in, or shuts them out, and remembers it. Returns a line for the status bar.</summary>
    public string SetAllowAgents(bool allow)
    {
        if (Settings.AllowAgents != allow)
        {
            Settings.AllowAgents = allow;
            SaveSettings();
        }
        _allowAgentsItem.IsChecked = allow;
        if (!allow)
        {
            _ = Agents.StopAsync();
            return "AI agents are shut out.";
        }
        return Agents.Start(AgentPipe) is { } problem
            ? $"AI agents can't connect: {problem}"
            : "AI agents may connect (AVAPageStream --mcp --attach). They work on the document in this window.";
    }

    /// <summary>The network door's access token (made the first time; kept beside the settings, never in them).</summary>
    public string NetworkToken => _networkToken ??= TryToken(t => t.Get());

    private string TryToken(Func<NetworkToken, string> get)
    {
        try
        {
            return get(Mcp.NetworkToken.In(SettingsFolder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return NetworkListener.NewToken();
        }
    }

    /// <summary>Opens, closes or reopens the network door to match the settings; returns a line for the status bar.</summary>
    public async Task<string> SyncNetworkAsync()
    {
        var s = Settings;
        var want = s.AllowNetworkAgents
            ? new NetworkSettings(s.NetworkAgentsAddress, s.NetworkAgentsPort, NetworkToken, Blank(s.NetworkAgentsCertificate), Blank(s.NetworkAgentsKey))
            : null;
        var root = Blank(s.NetworkAgentsFolder) ?? DocumentsFolder;
        if (Agents.NetworkRunning && (want == null || want != Agents.NetworkSettings || root != Agents.NetworkRoot))
        {
            await Agents.StopNetworkAsync();
            if (want == null) return "AI agents on other computers are shut out.";
        }
        if (want == null || Agents.NetworkRunning) return "";
        if (want.Problem() is { } bad) return $"AI agents on other computers can't connect: {bad}";
        return await Agents.StartNetworkAsync(want, root) is { } problem
            ? $"AI agents on other computers can't connect: {problem}"
            : $"AI agents on other computers may connect (MCP): {Agents.NetworkUrls.FirstOrDefault()}, with the access token (AI › Connect an Agent).";
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>How to connect an agent: on this computer through the pipe, or from another over the network.</summary>
    public async Task ShowConnect()
    {
        var exe = Environment.ProcessPath ?? "AVAPageStream";
        var local = new SelectableTextBlock
        {
            Foreground = Look.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = $"On this computer: turn on AI › Allow AI Agents (it's {(Settings.AllowAgents ? "on" : "off")} now), then add the server to the agent. For Claude Code:\n\n" +
                $"    claude mcp add avapagestream -- \"{exe}\" --mcp --attach\n\n" +
                "The agent then works on the document in this window, and Ctrl+Z undoes its steps. Without --attach the server has a document of its own and no window.",
        };
        var allow = new CheckBox { Content = "Let AI agents on other computers connect (MCP over HTTP)", IsChecked = Settings.AllowNetworkAgents, Foreground = Look.Text };
        var port = new TextBox { Text = Settings.NetworkAgentsPort.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 80 };
        var folder = new TextBox { Text = Settings.NetworkAgentsFolder, PlaceholderText = DocumentsFolder, MinWidth = 300 };
        var token = new TextBox { Text = new string('•', 20), IsReadOnly = true, Width = 300 };
        var show = new Button { Content = "Show" };
        show.Click += (_, _) => token.Text = NetworkToken;
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) => { if (Clipboard is { } c) await c.SetTextAsync(NetworkToken); };
        var renew = new Button { Content = "New token" };
        renew.Click += (_, _) => { _networkToken = TryToken(t => t.Renew()); token.Text = NetworkToken; };
        var where = new SelectableTextBlock { Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void ShowWhere() => where.Text = Agents.NetworkRunning
            ? "Listening at " + string.Join(", ", Agents.NetworkUrls) + ". Agents send \"Authorization: Bearer <token>\"."
            : "Not listening.";
        ShowWhere();
        Grid.SetColumn(port, 1);
        var close = new Button { Content = "Close", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "Apply", MinWidth = 90 };
        apply.Classes.Add("accent");
        var dialog = new ConnectDialog
        {
            Title = "Connect an Agent", Width = 620, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Window,
        };
        apply.Click += async (_, _) =>
        {
            if (!int.TryParse(port.Text, out var p) || p is < 1 or > 65535) { where.Text = "The port is a number from 1 to 65535."; return; }
            Settings.AllowNetworkAgents = allow.IsChecked == true;
            Settings.NetworkAgentsPort = p;
            Settings.NetworkAgentsFolder = folder.Text?.Trim() ?? "";
            SaveSettings();
            var line = await SyncNetworkAsync();
            ShowWhere();
            if (line.Length > 0) { Say(line); if (line.Contains("can't")) where.Text = line; }
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                local,
                new Separator(),
                allow,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Label("Port"), port, Label("Working folder"), folder } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Label("Access token"), token, show, copy, renew } },
                where,
                new TextBlock
                {
                    Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Text = "Plain HTTP sends the token and the work unencrypted: use it on a network you trust, or run AVAPageStream --mcp --http with --cert and --key for HTTPS. Anyone with the token can change the open document.",
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { apply, close } },
            },
        };
        await dialog.ShowDialog(this);
    }

    private static Label Label(string text) => new() { Content = text, Foreground = Look.Heading, VerticalAlignment = VerticalAlignment.Center };

    // ---- the AI Chat ----

    /// <summary>Shows or hides the AI Chat (F12), making it the first time.</summary>
    public void ToggleChat()
    {
        if (Chat is null) MakeChat();
        ChatHolder.IsVisible = !ChatHolder.IsVisible;
        _chatItem.IsChecked = ChatHolder.IsVisible;
        if (Settings.ChatOpen != ChatHolder.IsVisible)
        {
            Settings.ChatOpen = ChatHolder.IsVisible;
            SaveSettings();
        }
        if (ChatHolder.IsVisible) Chat!.Input.Focus();
        else View.Focus();
    }

    /// <summary>Tests' own API key stores (none of the person's real keys).</summary>
    internal (ApiKeyStore Keys, ApiKeyStore ServerKeys)? ChatKeys { get; set; }

    private void MakeChat()
    {
        var keys = ChatKeys?.Keys ?? ApiKeyStore.ForApp(SettingsFolder);
        var serverKeys = ChatKeys?.ServerKeys ?? ApiKeyStore.ForServer(SettingsFolder);
        ChatPanel? panel = null;
        _chatTools = new LazyToolBridge(async cancel => await McpToolBridge.StartAsync(Agents.ChatHost(AgentRoot, () => panel?.Session.Provider == ChatProvider.Anthropic ? "Claude" : "AI Chat"), cancel));
        panel = new ChatPanel(_chatTools, keys, Say, Settings, serverKeys);
        panel.CloseRequested += ToggleChat;
        if (ChatKeys is null)
        {
            panel.History = new ChatHistory(Path.Combine(SettingsFolder, "Chats"));
            panel.Ledger = new UsageLedger(Path.Combine(SettingsFolder, "chat-usage.json"));
            panel.RestoreLast();
        }
        Chat = panel;
        ChatHolder.Child = panel;
    }

    private sealed class ConnectDialog : Window
    {
        protected override Type StyleKeyOverride => typeof(Window);
    }
}
