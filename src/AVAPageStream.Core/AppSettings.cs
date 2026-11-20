using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVAPageStream.Core;

/// <summary>What the program remembers between runs, kept as JSON in the user's application data folder.</summary>
public sealed class AppSettings
{
    public List<string> Recent { get; set; } = [];
    public bool Snap { get; set; } = true;
    public bool ShowGuides { get; set; } = true;
    public bool ShowGrid { get; set; }
    public bool SnapGrid { get; set; }
    public bool CheckSpelling { get; set; } = true;
    public Unit Unit { get; set; } = Unit.Point;
    public int ExportDpi { get; set; } = 150;
    public int AutosaveMinutes { get; set; } = 2; // 0: never
    public bool PreflightFirst { get; set; } = true; // ask before exporting or printing a document with problems that will show

    // ---- AI agents and the AI Chat ----

    /// <summary>Whether AI agents outside the program may work in it, on this computer (AI › Allow AI Agents). Off until turned on.</summary>
    public bool AllowAgents { get; set; }

    /// <summary>Whether AI agents on other computers may, over the network (AI › Connect an Agent). Off until turned on.</summary>
    public bool AllowNetworkAgents { get; set; }
    public string NetworkAgentsAddress { get; set; } = "0.0.0.0";
    public int NetworkAgentsPort { get; set; } = 8768;
    /// <summary>The network door's working folder (empty: the Documents folder).</summary>
    public string NetworkAgentsFolder { get; set; } = "";
    public string NetworkAgentsCertificate { get; set; } = "";
    public string NetworkAgentsKey { get; set; } = "";

    /// <summary>Whether the AI Chat panel shows.</summary>
    public bool ChatOpen { get; set; }
    public bool ChatAutoContinue { get; set; } = true;
    /// <summary>"Anthropic", or "OpenAICompatible" for a local or self-hosted server.</summary>
    public string ChatProvider { get; set; } = "Anthropic";
    public string ChatServerUrl { get; set; } = "http://localhost:11434/v1";
    public string ChatServerModel { get; set; } = "";
    public bool ChatCoreTools { get; set; } = true;
    public bool ChatModelSeesPictures { get; set; }
    /// <summary>Keep chats on this computer (off: a chat may hold a client's details).</summary>
    public bool ChatKeepHistory { get; set; }
    /// <summary>Prices per million tokens by model, where the person set their own.</summary>
    public Dictionary<string, double[]> ChatPrices { get; set; } = [];
    public double? ChatBudget { get; set; }

    public const int MaxRecent = 10;

    /// <summary>Puts a file at the top of the recent list.</summary>
    public void Opened(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Recent.Insert(0, path);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "AVAPageStream");

    public static string DefaultPath => Path.Combine(Folder, "settings.json");

    /// <summary>The saved settings, or the defaults when there are none or they can't be read.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Saves the settings; a failure (a read-only folder) is ignored: they are only a convenience.</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
