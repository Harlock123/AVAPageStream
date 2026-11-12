using System.Globalization;

namespace AVAPageStream.Core;

/// <summary>How a cross-reference shows its place: its page number, its text, or both.</summary>
public enum RefKind { Page, Text, Both }

/// <summary>A place cross-references can point at: its name, and where it is.</summary>
public sealed record NamedPlace(string Name, string StoryId, int Index);

/// <summary>
/// Cross-references: named places in the text (a marker each), and references to them that show where they are, worked
/// out from the layouts so they stay right as pages move.
/// </summary>
public static class References
{
    public static string Key(string name, RefKind kind) => name + "|" + kind.ToString().ToLowerInvariant();

    public static (string Name, RefKind Kind) Parse(string key)
    {
        var bar = key.LastIndexOf('|');
        if (bar < 0) return (key, RefKind.Page);
        var kind = key[(bar + 1)..] switch { "text" => RefKind.Text, "both" => RefKind.Both, _ => RefKind.Page };
        return (key[..bar], kind);
    }

    /// <summary>What a reference shows: worked out, or "?" while it isn't (or its place is missing).</summary>
    public static string Text(string key, RefMap? refs) => refs is not null && refs.Shown.TryGetValue(key, out var s) ? s : "?";

    /// <summary>A stretch of text with its references replaced by what they show.</summary>
    public static string Show(Story story, int start, string text, RefMap? refs)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Fields.Reference) { sb.Append(text[i]); continue; }
            if (story.FormatAt(start + i).Ref is { } key) sb.Append(Text(key, refs));
        }
        return sb.ToString();
    }

    /// <summary>Every named place in the document.</summary>
    public static List<NamedPlace> Places(Document doc)
    {
        var list = new List<NamedPlace>();
        foreach (var (id, story) in doc.Stories)
        {
            var at = 0;
            foreach (var run in story.Runs)
            {
                if (run.Format.Mark is { } name)
                    for (var i = at; i < at + run.Length && i < story.Text.Length; i++)
                        if (story.Text[i] == Fields.Place) list.Add(new NamedPlace(name, id, i));
                at += run.Length;
            }
        }
        return list;
    }

    /// <summary>The references in a story, by key.</summary>
    public static HashSet<string> In(Story story) =>
        story.Text.Contains(Fields.Reference) ? story.Runs.Select(r => r.Format.Ref).OfType<string>().ToHashSet() : [];

    /// <summary>
    /// What each of a story's references shows: its place's page (by its label), the text of the paragraph it marks
    /// (from the place on), or both; "??" for a place that isn't there, "?" for one not set (overset).
    /// </summary>
    public static RefMap? Resolve(Document doc, Story story, Func<Story, TextLayout?> layoutOf)
    {
        var keys = In(story);
        if (keys.Count == 0) return null;
        var places = Places(doc).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
        var pageOf = new Dictionary<string, int>();
        for (var i = 0; i < doc.Pages.Count; i++)
            foreach (var o in doc.Pages[i].All) pageOf[o.Id] = i;
        var shown = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            var (name, kind) = Parse(key);
            if (!places.TryGetValue(name, out var place) || !doc.Stories.TryGetValue(place.StoryId, out var target)) { shown[key] = "??"; continue; }
            string page = "?";
            if (layoutOf(target) is { } layout && layout.LineOf(place.Index) is var li and >= 0)
            {
                var frame = Chains.ObjectOf(layout.Areas[layout.Lines[li].Area].FrameId);
                if (pageOf.TryGetValue(frame, out var p)) page = doc.PageLabel(p);
            }
            var (_, end) = target.ParagraphRange(target.ParagraphAt(place.Index));
            var text = Fields.Expand(target.Text[Math.Min(place.Index, end)..end], null).Replace('\t', ' ').Trim();
            shown[key] = kind switch
            {
                RefKind.Text => text,
                RefKind.Both => $"“{text}” on page {page}",
                _ => page,
            };
        }
        return new RefMap(shown);
    }

    /// <summary>A name for a place made for a heading: its words, made unique among the document's places.</summary>
    public static string NameFor(Document doc, string text)
    {
        var words = string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6));
        var name = words.Length == 0 ? "Place" : words;
        var taken = Places(doc).Select(p => p.Name).ToHashSet();
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{name} ({n.ToString(CultureInfo.InvariantCulture)})";
        return candidate;
    }
}
