using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAPageStream.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAPageStream.Mcp;

/// <summary>
/// Checkpoints: the document as it was at a moment, kept by name for this session, to compare with (diff), look at
/// (render_checkpoint) or go back to (restore_checkpoint). The AI Chat takes one before each reply's first change, for
/// its Undo this reply button and its timeline.
/// </summary>
public sealed partial class PageTools
{
    private readonly Dictionary<string, (Document Doc, string? Note, DateTime At)> _checkpoints = [];

    [McpServerTool(Name = "checkpoint")]
    [Description("Remembers the document as it is now under a name, for this session: diff compares the document with it, render_checkpoint draws it and restore_checkpoint goes back to it. Take one before a big change you might want to undo as a whole. Without a name, lists the checkpoints.")]
    public Task<string> Checkpoint(
        [Description("The checkpoint's name. A name used before is replaced.")] string? name = null,
        [Description("A note on what it's for. Optional.")] string? note = null,
        [Description("Accepted for other clients; checkpoints last as long as the session.")] bool persist = false,
        CancellationToken cancel = default) =>
        Read("took a checkpoint", s =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return new JsonObject
                {
                    ["checkpoints"] = new JsonArray(_checkpoints.Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["note"] = kv.Value.Note, ["taken"] = kv.Value.At.ToString("HH:mm:ss") }).ToArray()),
                };
            _checkpoints[name.Trim()] = (s.Editor.Doc.Clone(), note, DateTime.Now);
            _ = persist;
            return new JsonObject { ["checkpoint"] = name.Trim(), ["pages"] = s.Editor.Doc.Pages.Count };
        }, cancel);

    [McpServerTool(Name = "diff", ReadOnly = true, Idempotent = true)]
    [Description("What has changed since a checkpoint: objects added, removed and changed (by id), stories whose text or formats changed, pages added or removed, and whether the styles, swatches or setup changed.")]
    public Task<string> Diff(
        [Description("The checkpoint's name.")] string since,
        CancellationToken cancel = default) =>
        Read("compared with a checkpoint", s => Compare(CheckpointNamed(since), s.Editor.Doc), cancel);

    [McpServerTool(Name = "render_checkpoint", ReadOnly = true, Idempotent = true)]
    [Description("A picture of a page as it was at a checkpoint, fitted into width by height pixels.")]
    public async Task<CallToolResult> RenderCheckpoint(
        [Description("The checkpoint's name.")] string name,
        [Description("Width in pixels, from 40 to 1200. Default 320.")] int width = 320,
        [Description("Height in pixels, from 40 to 1200. Default 240.")] int height = 240,
        [Description("The page, from 1. Default: the page being looked at, else 1.")] int? page = null,
        CancellationToken cancel = default)
    {
        if (width is < 40 or > 1200 || height is < 40 or > 1200) throw new McpException("width and height are from 40 to 1200 pixels.");
        var png = await Run("drew a checkpoint", s =>
        {
            var doc = CheckpointNamed(name);
            var index = Math.Clamp((page ?? (s.CurrentPage ?? 0) + 1) - 1, 0, doc.Pages.Count - 1);
            var dpi = Math.Min(width * 72.0 / doc.Setup.PageWidth, height * 72.0 / doc.Setup.PageHeight);
            return host.Library.Picture(doc, SheetRef.PageAt(index), Math.Clamp(dpi, 4, 300));
        }, cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png")] };
    }

    [McpServerTool(Name = "restore_checkpoint", Destructive = true)]
    [Description("Puts the whole document back as it was at a checkpoint, as one undo step (so undo brings back what was there).")]
    public Task<string> RestoreCheckpoint(
        [Description("The checkpoint's name.")] string name,
        CancellationToken cancel = default) =>
        Read("went back to a checkpoint", s =>
        {
            var doc = CheckpointNamed(name);
            var changes = Compare(doc, s.Editor.Doc);
            if (changes["changed"]?.GetValue<bool>() == false) return new JsonObject { ["restored"] = name, ["changed"] = false };
            s.Editor.Revert("AI: back to " + name, doc);
            return new JsonObject { ["restored"] = name, ["changed"] = true, ["undone"] = changes };
        }, cancel);

    private Document CheckpointNamed(string name) =>
        _checkpoints.TryGetValue(name.Trim(), out var c) ? c.Doc
        : throw new ArgumentException($"There's no checkpoint \"{name}\"" + (_checkpoints.Count > 0 ? $". The checkpoints: {string.Join(", ", _checkpoints.Keys)}." : ": checkpoint takes one."));

    /// <summary>How <paramref name="now"/> differs from <paramref name="was"/>, for an agent and for the chat's timeline.</summary>
    internal static JsonObject Compare(Document was, Document now)
    {
        static Dictionary<string, string> Objects(Document d) =>
            d.Sheets.SelectMany(sh => sh.All).Concat(d.AnchoredObjects).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => JsonSerializer.Serialize<PageObject>(g.First()));
        static Dictionary<string, string> Stories(Document d) =>
            d.Stories.ToDictionary(kv => kv.Key, kv => JsonSerializer.Serialize(kv.Value));
        var (a, b) = (Objects(was), Objects(now));
        var added = b.Keys.Where(k => !a.ContainsKey(k)).ToList();
        var removed = a.Keys.Where(k => !b.ContainsKey(k)).ToList();
        var changed = b.Keys.Where(k => a.TryGetValue(k, out var v) && v != b[k]).ToList();
        var (sa, sb) = (Stories(was), Stories(now));
        var text = sb.Keys.Where(k => !sa.TryGetValue(k, out var v) || v != sb[k]).ToList();
        var styles = JsonSerializer.Serialize(was.Styles) != JsonSerializer.Serialize(now.Styles) || JsonSerializer.Serialize(was.CharStyles) != JsonSerializer.Serialize(now.CharStyles);
        var swatches = JsonSerializer.Serialize(was.Swatches) != JsonSerializer.Serialize(now.Swatches);
        var setup = was.Setup != now.Setup || was.Title != now.Title || was.Author != now.Author;
        var anything = was.ToJson(false) != now.ToJson(false);
        return new JsonObject
        {
            ["changed"] = anything,
            ["objects"] = new JsonObject
            {
                ["added"] = new JsonArray(added.Select(x => (JsonNode)x).ToArray()),
                ["removed"] = new JsonArray(removed.Select(x => (JsonNode)x).ToArray()),
                ["changed"] = new JsonArray(changed.Select(x => (JsonNode)x).ToArray()),
            },
            ["text_changed"] = new JsonArray(text.Select(x => (JsonNode)x).ToArray()),
            ["pages"] = new JsonObject { ["was"] = was.Pages.Count, ["now"] = now.Pages.Count },
            ["styles_changed"] = styles,
            ["swatches_changed"] = swatches,
            ["setup_changed"] = setup,
        };
    }
}
