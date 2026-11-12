namespace AVAPageStream.Core;

/// <summary>A footnote set at the foot of an area: its story, its number, and the box its text is set in.</summary>
public sealed record NoteBox(int Area, string Id, int Number, Box Box);

/// <summary>
/// Footnotes. A reference is the <see cref="Fields.Footnote"/> character in a story, in a run of its own whose format
/// names the note's story (<see cref="CharFormat.Note"/>); the note's story names the story it belongs to
/// (<see cref="Story.NoteOf"/>) and starts with <see cref="Fields.NoteNumber"/>. Notes are numbered from 1 through
/// their story in the order of their references, and set at the foot of the column each reference falls in.
/// </summary>
public static class Footnotes
{
    /// <summary>The room above a column's footnotes, with the short rule that separates them from the text.</summary>
    public const double Gap = 10;

    public const string StyleName = "Footnote";

    public static ParagraphStyle Style() => new(StyleName, new CharFormat(Size: 9), new ParaFormat(SpaceAfter: 2));

    /// <summary>The story's footnotes' numbers, by their stories' ids, in the order of their references.</summary>
    public static Dictionary<string, int> Numbers(Story story)
    {
        var numbers = new Dictionary<string, int>();
        if (!story.Text.Contains(Fields.Footnote)) return numbers;
        var at = 0;
        foreach (var run in story.Runs)
        {
            if (run.Format.Note is { } id && at < story.Text.Length && story.Text[at] == Fields.Footnote && !numbers.ContainsKey(id))
                numbers[id] = numbers.Count + 1;
            at += run.Length;
        }
        return numbers;
    }

    /// <summary>Text from a story with its footnotes' references replaced by their numbers.</summary>
    public static string Number(Story story, int start, string text, IReadOnlyDictionary<string, int> numbers)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Fields.Footnote) { sb.Append(text[i]); continue; }
            if (story.FormatAt(start + i).Note is { } id && numbers.TryGetValue(id, out var n)) sb.Append(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>How tall a note's text is when set at a width.</summary>
    public static double Height(Story note, double width, ITextMeasurer m, int number = 0)
    {
        var layout = TextFlow.Layout(note, [new Area(new Box(0, 0, Math.Max(1, width), 100_000), "note", 0)], m, new FieldContext(0, 0, null, number));
        return layout.Lines.Count == 0 ? 0 : layout.Lines[^1].Baseline + layout.Lines[^1].Descent;
    }

    /// <summary>What a story's layout needs to make room for its notes: their heights, measured in the document.</summary>
    public static Func<string, double, double> Heights(Document doc, ITextMeasurer m) =>
        (id, width) => doc.Stories.TryGetValue(id, out var note) ? Height(note, width, m) : 0;

    /// <summary>Whether the document has any footnotes, or references to any (so <see cref="Tidy"/> has work to do).</summary>
    public static bool Any(Document doc) => doc.Stories.Values.Any(s => s.NoteOf is not null || s.Text.Contains(Fields.Footnote));

    /// <summary>
    /// Keeps references and notes matched after any change: a reference copied (pasted text, a copied frame) gets a
    /// copy of its note, one whose note is gone gets an empty one, characters other than references lose a note
    /// they picked up, and a note whose reference was deleted goes.
    /// </summary>
    public static void Tidy(Document doc)
    {
        var referenced = new HashSet<string>();
        foreach (var story in doc.Stories.Values.Where(s => s.NoteOf is null).ToList())
        {
            if (!story.Runs.Any(r => r.Format.Note is not null)) continue;
            var seen = new HashSet<string>();
            var at = 0;
            foreach (var run in story.Runs.ToList())
            {
                var (start, len, f) = (at, run.Length, run.Format);
                at += len;
                if (f.Note is not { } id) continue;
                for (var i = start; i < start + len && i < story.Text.Length; i++)
                {
                    if (story.Text[i] != Fields.Footnote)
                    {
                        story.FormatChars(i, 1, c => c with { Note = null });
                        continue;
                    }
                    var note = doc.Stories.GetValueOrDefault(id);
                    if (note is not null && note.NoteOf == story.Id && seen.Add(id)) { referenced.Add(id); continue; }
                    // A copy of another reference, or one whose note is missing: a note of its own.
                    var made = note is null ? NewNote(doc, story.Id, "") : CopyNote(doc, note, story.Id);
                    story.FormatChars(i, 1, c => c with { Note = made.Id });
                    seen.Add(made.Id);
                    referenced.Add(made.Id);
                }
            }
        }
        foreach (var gone in doc.Stories.Values.Where(s => s.NoteOf is not null && !referenced.Contains(s.Id)).Select(s => s.Id).ToList())
            doc.Stories.Remove(gone);
    }

    /// <summary>A new note for a story, in the Footnote style (added to the document if it hasn't one): its number, then the text.</summary>
    public static Story NewNote(Document doc, string of, string text)
    {
        if (!doc.Styles.Any(s => s.Name == StyleName)) doc.Styles.Add(Style());
        var style = doc.Style(StyleName);
        var note = Story.Create(doc.NewId("story"), style);
        note.NoteOf = of;
        note.Insert(0, Fields.NoteNumber + " " + text);
        note.FormatChars(0, 1, c => c with { Script = Script.Super });
        doc.Stories[note.Id] = note;
        return note;
    }

    private static Story CopyNote(Document doc, Story note, string of)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<Story>(System.Text.Json.JsonSerializer.Serialize(note, Document.Json), Document.Json)!;
        copy.Id = doc.NewId("story");
        copy.NoteOf = of;
        doc.Stories[copy.Id] = copy;
        return copy;
    }

    /// <summary>
    /// Brings a copied story's notes along from the document it was copied from (the clipboard's), each under a new id.
    /// </summary>
    public static void Bring(Document from, Document to, Story copied)
    {
        var at = 0;
        foreach (var run in copied.Runs.ToList())
        {
            var start = at;
            at += run.Length;
            if (run.Format.Note is not { } id || ReferenceEquals(from, to) || from.Stories.GetValueOrDefault(id) is not { } note) continue;
            var copy = CopyNote(to, note, copied.Id);
            copied.FormatChars(start, run.Length, c => c with { Note = copy.Id });
        }
    }

    /// <summary>The note whose reference is just before or at a place in a story, if there is one.</summary>
    public static string? At(Story story, int index)
    {
        foreach (var i in new[] { index - 1, index })
            if (i >= 0 && i < story.Length && story.Text[i] == Fields.Footnote && story.FormatAt(i).Note is { } id) return id;
        return null;
    }
}
