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
