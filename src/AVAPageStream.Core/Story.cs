namespace AVAPageStream.Core;

/// <summary>How characters look. Every character of a story has one complete format.</summary>
public sealed record CharFormat(
    string Font = Fonts.Serif,
    double Size = 12,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    string Colour = Colours.Black,
    double Tracking = 0, // thousandths of an em added after each character
    double Shift = 0,    // baseline shift, in points up
    bool SmallCaps = false,
    Script Script = Script.Normal,
    string? CharStyle = null,  // the character style it is in, by name
    string? Note = null);      // on a footnote reference, the note's story

/// <summary>Superscript and subscript: set smaller, above or below the baseline.</summary>
public enum Script { Normal, Super, Sub }

public enum Align { Left, Centre, Right, Justify }

/// <summary>How a paragraph is set. <see cref="Leading"/> 0 means automatic: 120% of the largest size on the line.</summary>
public sealed record ParaFormat(
    string Style = "Body",
    Align Align = Align.Left,
    double Leading = 0,
    double LeftIndent = 0,
    double RightIndent = 0,
    double FirstIndent = 0,
    double SpaceBefore = 0,
    double SpaceAfter = 0,
    string Tabs = "",        // tab stops, as Core.Tabs keeps them
    int DropLines = 0,       // a drop cap this many lines deep (0: none)
    int DropChars = 1,       // and this many characters wide
    bool Hyphenate = false,  // break words at the end of lines with a hyphen
    bool KeepWithNext = false, // in the same column as the next paragraph's first line (a heading)
    bool KeepTogether = false, // all its lines in one column
    int Orphans = 1,         // at least this many of its lines at the foot of a column it starts in
    int Widows = 1,          // at least this many at the top of a column it runs on into
    bool OnGrid = false);    // its baselines on the document's baseline grid

/// <summary>A named set of character and paragraph settings.</summary>
public sealed record ParagraphStyle(string Name, CharFormat Char, ParaFormat Para);

/// <summary>Character settings a character style sets; those left null come from the paragraph's style.</summary>
public sealed record CharOverrides(
    string? Font = null,
    double? Size = null,
    bool? Bold = null,
    bool? Italic = null,
    bool? Underline = null,
    string? Colour = null,
    double? Tracking = null,
    bool? SmallCaps = null,
    Script? Script = null)
{
    public CharFormat ApplyTo(CharFormat f) => f with
    {
        Font = Font ?? f.Font,
        Size = Size ?? f.Size,
        Bold = Bold ?? f.Bold,
        Italic = Italic ?? f.Italic,
        Underline = Underline ?? f.Underline,
        Colour = Colour ?? f.Colour,
        Tracking = Tracking ?? f.Tracking,
        SmallCaps = SmallCaps ?? f.SmallCaps,
        Script = Script ?? f.Script,
    };

    /// <summary>Takes back what these set, to a base format's values.</summary>
    public CharFormat Revert(CharFormat f, CharFormat basis) => f with
    {
        Font = Font is null ? f.Font : basis.Font,
        Size = Size is null ? f.Size : basis.Size,
        Bold = Bold is null ? f.Bold : basis.Bold,
        Italic = Italic is null ? f.Italic : basis.Italic,
        Underline = Underline is null ? f.Underline : basis.Underline,
        Colour = Colour is null ? f.Colour : basis.Colour,
        Tracking = Tracking is null ? f.Tracking : basis.Tracking,
        SmallCaps = SmallCaps is null ? f.SmallCaps : basis.SmallCaps,
        Script = Script is null ? f.Script : basis.Script,
    };

    /// <summary>The settings in which <paramref name="to"/> differs from <paramref name="from"/>.</summary>
    public static CharOverrides Difference(CharFormat from, CharFormat to) => new(
        to.Font != from.Font ? to.Font : null,
        to.Size != from.Size ? to.Size : null,
        to.Bold != from.Bold ? to.Bold : null,
        to.Italic != from.Italic ? to.Italic : null,
        to.Underline != from.Underline ? to.Underline : null,
        to.Colour != from.Colour ? to.Colour : null,
        to.Tracking != from.Tracking ? to.Tracking : null,
        to.SmallCaps != from.SmallCaps ? to.SmallCaps : null,
        to.Script != from.Script ? to.Script : null);

    /// <summary>What it sets, in a few words: "Italic", "Bold, 14 pt".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Font is { } font) parts.Add(font);
        if (Size is { } size) parts.Add(FormattableString.Invariant($"{size:0.#} pt"));
        if (Bold is { } b) parts.Add(b ? "Bold" : "Not bold");
        if (Italic is { } i) parts.Add(i ? "Italic" : "Not italic");
        if (Underline is { } u) parts.Add(u ? "Underlined" : "Not underlined");
        if (SmallCaps is { } sc) parts.Add(sc ? "Small caps" : "No small caps");
        if (Script is { } sp) parts.Add(sp.ToString() is "Normal" ? "Normal script" : sp + "script");
        if (Colour is { } c) parts.Add(c.TrimStart('@'));
        if (Tracking is { } t) parts.Add(FormattableString.Invariant($"tracking {t:0.#}"));
        return parts.Count == 0 ? "Nothing" : string.Join(", ", parts);
    }
}

/// <summary>A named set of character settings laid over the paragraph style's (Emphasis: italic).</summary>
public sealed record CharacterStyle(string Name, CharOverrides Set);

/// <summary>A stretch of a story's characters in one format.</summary>
public sealed record Run(int Length, CharFormat Format);

/// <summary>
/// A stretch of a story copied with its formats: the text, runs covering exactly that text, and the format of each
/// paragraph it touches (the first for the paragraph it starts in).
/// </summary>
public sealed record Fragment(string Text, IReadOnlyList<Run> Runs, IReadOnlyList<ParaFormat> Paras);

/// <summary>The fonts that ship with the program.</summary>
public static class Fonts
{
    public const string Serif = "Liberation Serif";
    public const string Sans = "Liberation Sans";
    public static readonly string[] Bundled = [Serif, Sans];
}

/// <summary>
/// A body of styled text, flowing through the text frames in <see cref="Frames"/>. Paragraphs end with '\n'.
/// <see cref="Runs"/> cover every character and one more at the end, standing for the end of the story, so an empty
/// story or a last empty paragraph still has a format; each paragraph's own end ('\n' or that last one) gives an empty
/// paragraph its look. Runs are never empty and two neighbours never share a format.
/// </summary>
public sealed class Story
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public List<Run> Runs { get; set; } = [];
    public List<ParaFormat> Paras { get; set; } = [];

    /// <summary>The ids of the text frames the story flows through, in order (for a table cell's story, the table's).</summary>
    public List<string> Frames { get; set; } = [];

    /// <summary>The table whose cell this story is, or null for a story in text frames.</summary>
    public string? Table { get; set; }

    /// <summary>The path this story is set along, or null.</summary>
    public string? OnPath { get; set; }

    /// <summary>For a footnote's story, the story its reference is in.</summary>
    public string? NoteOf { get; set; }

    public static Story Create(string id, ParagraphStyle style) => new()
    {
        Id = id,
        Runs = [new Run(1, style.Char)],
        Paras = [style.Para with { Style = style.Name }],
    };

    public int Length => Text.Length;

    public int ParagraphCount => Paras.Count;

    /// <summary>The format of the character at <paramref name="index"/> (0 to <see cref="Length"/>).</summary>
    public CharFormat FormatAt(int index)
    {
        var at = 0;
        foreach (var run in Runs)
        {
            if (index < at + run.Length) return run.Format;
            at += run.Length;
        }
        return Runs[^1].Format;
    }

    /// <summary>The format new text typed at <paramref name="index"/> takes: the character before's, or at a paragraph's start the one after.</summary>
    public CharFormat TypingFormat(int index) =>
        (index > 0 && Text[index - 1] != '\n' ? FormatAt(index - 1) : FormatAt(index)) is var f && f.Note is not null
            ? f with { Note = null, Script = Script.Normal } // typing after a footnote's reference makes no reference
            : f;

    /// <summary>The paragraph the character at <paramref name="index"/> is in.</summary>
    public int ParagraphAt(int index)
    {
        var count = 0;
        var end = Math.Min(index, Text.Length);
        for (var i = 0; i < end; i++)
            if (Text[i] == '\n') count++;
        return count;
    }

    /// <summary>Where paragraph <paramref name="p"/> starts, and where its end ('\n', or the story's end) is.</summary>
    public (int Start, int End) ParagraphRange(int p)
    {
        var start = 0;
        for (var k = 0; k < p; k++)
        {
            var n = Text.IndexOf('\n', start);
            if (n < 0) return (Text.Length, Text.Length);
            start = n + 1;
        }
        var end = Text.IndexOf('\n', start);
        return (start, end < 0 ? Text.Length : end);
    }

    public string Slice(int start, int length) => Text.Substring(start, length);

    /// <summary>Puts <paramref name="text"/> in at <paramref name="index"/>, in the typing format there unless one is given.</summary>
    public void Insert(int index, string text, CharFormat? format = null)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length == 0) return;
        index = Math.Clamp(index, 0, Text.Length);
        format ??= TypingFormat(index);
        var p = ParagraphAt(index);
        var breaks = text.Count(c => c == '\n');
        for (var k = 0; k < breaks; k++) Paras.Insert(p + 1, Paras[p]);
        var i = Split(index);
        Runs.Insert(i, new Run(text.Length, format));
        Text = Text.Insert(index, text);
        Normalise();
    }

    /// <summary>Takes out <paramref name="length"/> characters from <paramref name="index"/>; joined paragraphs keep the first's format.</summary>
    public void Delete(int index, int length)
    {
        index = Math.Clamp(index, 0, Text.Length);
        length = Math.Clamp(length, 0, Text.Length - index);
        if (length == 0) return;
        var p = ParagraphAt(index);
        var breaks = 0;
        for (var i = index; i < index + length; i++)
            if (Text[i] == '\n') breaks++;
        Paras.RemoveRange(p + 1, breaks);
        var first = Split(index);
        var last = Split(index + length);
        Runs.RemoveRange(first, last - first);
        Text = Text.Remove(index, length);
        Normalise();
    }

    /// <summary>Changes the format of <paramref name="length"/> characters from <paramref name="index"/>.</summary>
    public void FormatChars(int index, int length, Func<CharFormat, CharFormat> change)
    {
        index = Math.Clamp(index, 0, Text.Length + 1);
        length = Math.Clamp(length, 0, Text.Length + 1 - index);
        if (length == 0) return;
        var first = Split(index);
        var last = Split(index + length);
        for (var i = first; i < last; i++) Runs[i] = Runs[i] with { Format = change(Runs[i].Format) };
        Normalise();
    }

    /// <summary>The paragraphs a selection touches (a caret with no selection touches its own).</summary>
    public (int First, int Last) ParagraphsIn(int index, int length) => (ParagraphAt(index), ParagraphAt(index + length));

    /// <summary>Changes the format of every paragraph the selection touches.</summary>
    public void FormatParas(int index, int length, Func<ParaFormat, ParaFormat> change)
    {
        var (first, last) = ParagraphsIn(index, length);
        for (var p = first; p <= last; p++) Paras[p] = change(Paras[p]);
    }

    /// <summary>
    /// Sets every paragraph the selection touches, and all of their characters, to a style; text in a character
    /// style (found by <paramref name="charStyles"/>) keeps it, over the new paragraph style.
    /// </summary>
    public void ApplyStyle(int index, int length, ParagraphStyle style, Func<string, CharacterStyle?>? charStyles = null)
    {
        var (first, last) = ParagraphsIn(index, length);
        for (var p = first; p <= last; p++)
        {
            Paras[p] = style.Para with { Style = style.Name };
            var (start, end) = ParagraphRange(p);
            FormatChars(start, end - start + 1, f => Over(style.Char, f.CharStyle, charStyles));
        }
    }

    /// <summary>A paragraph style's characters with a character style over them (if there is such a style).</summary>
    private static CharFormat Over(CharFormat basis, string? charStyle, Func<string, CharacterStyle?>? charStyles) =>
        charStyle is not null && charStyles?.Invoke(charStyle) is { } cs ? cs.Set.ApplyTo(basis) with { CharStyle = cs.Name } : basis;

    /// <summary>Puts a stretch of text in a character style (or, with null, takes it out of its character style).</summary>
    public void ApplyCharStyle(int index, int length, CharacterStyle? style, Func<string, CharacterStyle?> charStyles, Func<string, ParagraphStyle> paraStyles)
    {
        var (first, last) = ParagraphsIn(index, length);
        for (var p = first; p <= last; p++)
        {
            var (start, end) = ParagraphRange(p);
            var s = Math.Max(start, index);
            var e = Math.Min(end + 1, index + length);
            if (e <= s) continue;
            var basis = paraStyles(Paras[p].Style).Char;
            FormatChars(s, e - s, f =>
            {
                // Out of the old character style first, back to the paragraph style's settings it had changed.
                if (f.CharStyle is not null && charStyles(f.CharStyle) is { } old) f = old.Set.Revert(f, basis);
                return style is null ? f with { CharStyle = null } : style.Set.ApplyTo(f) with { CharStyle = style.Name };
            });
        }
    }

    /// <summary>
    /// Makes every paragraph that uses a style take the style's settings again, keeping bold, italic and underline
    /// set on characters.
    /// </summary>
    public void Restyle(ParagraphStyle style, Func<string, CharacterStyle?>? charStyles = null)
    {
        for (var p = 0; p < Paras.Count; p++)
        {
            if (Paras[p].Style != style.Name) continue;
            Paras[p] = style.Para with { Style = style.Name };
            var (start, end) = ParagraphRange(p);
            FormatChars(start, end - start + 1, f => f.CharStyle is not null && charStyles?.Invoke(f.CharStyle) is not null
                ? Over(style.Char, f.CharStyle, charStyles)
                : style.Char with { Bold = f.Bold, Italic = f.Italic, Underline = f.Underline });
        }
    }

    /// <summary>Brings the text in a character style up to date after it changes (<paramref name="was"/> is how it was).</summary>
    public void RestyleChars(CharacterStyle was, CharacterStyle now, Func<string, ParagraphStyle> paraStyles)
    {
        for (var p = 0; p < Paras.Count; p++)
        {
            var (start, end) = ParagraphRange(p);
            var basis = paraStyles(Paras[p].Style).Char;
            FormatChars(start, end - start + 1, f => f.CharStyle == was.Name ? now.Set.ApplyTo(was.Set.Revert(f, basis)) with { CharStyle = now.Name } : f);
        }
    }

    /// <summary>A stretch of the story with its formats, for styled copying.</summary>
    public Fragment Extract(int index, int length)
    {
        index = Math.Clamp(index, 0, Text.Length);
        length = Math.Clamp(length, 0, Text.Length - index);
        var runs = new List<Run>();
        var at = 0;
        foreach (var run in Runs)
        {
            var s = Math.Max(at, index);
            var e = Math.Min(at + run.Length, index + length);
            if (e > s) runs.Add(new Run(e - s, run.Format));
            at += run.Length;
        }
        var first = ParagraphAt(index);
        var breaks = Text.Substring(index, length).Count(c => c == '\n');
        return new Fragment(Text.Substring(index, length), runs, Paras.GetRange(first, breaks + 1));
    }

    /// <summary>
    /// Puts in a copied stretch with its formats. The paragraph it is pasted into keeps its own format; each new
    /// paragraph it makes takes the one it had.
    /// </summary>
    public void InsertFragment(int index, Fragment fragment)
    {
        if (fragment.Text.Length == 0) return;
        index = Math.Clamp(index, 0, Text.Length);
        var p = ParagraphAt(index);
        Insert(index, fragment.Text, fragment.Runs.Count > 0 ? fragment.Runs[0].Format : null);
        var at = index;
        foreach (var run in fragment.Runs)
        {
            FormatChars(at, run.Length, _ => run.Format);
            at += run.Length;
        }
        for (var k = 1; k < fragment.Paras.Count && p + k < Paras.Count; k++) Paras[p + k] = fragment.Paras[k];
    }

    /// <summary>Splits the runs so one starts at <paramref name="index"/>, and returns its position (or the count, at the end).</summary>
    private int Split(int index)
    {
        var at = 0;
        for (var i = 0; i < Runs.Count; i++)
        {
            var run = Runs[i];
            if (index == at) return i;
            if (index < at + run.Length)
            {
                var before = index - at;
                Runs[i] = run with { Length = before };
                Runs.Insert(i + 1, run with { Length = run.Length - before });
                return i + 1;
            }
            at += run.Length;
        }
        return Runs.Count;
    }

    /// <summary>Drops empty runs and joins neighbours with the same format.</summary>
    private void Normalise()
    {
        for (var i = Runs.Count - 1; i >= 0; i--)
        {
            if (Runs[i].Length <= 0) { Runs.RemoveAt(i); continue; }
            if (i + 1 < Runs.Count && Runs[i + 1].Format == Runs[i].Format)
            {
                Runs[i] = Runs[i] with { Length = Runs[i].Length + Runs[i + 1].Length };
                Runs.RemoveAt(i + 1);
            }
        }
    }

    /// <summary>Whether the runs and paragraphs agree with the text (for tests, and for files read back).</summary>
    public bool IsWellFormed() =>
        Runs.Count > 0 && Runs.Sum(r => r.Length) == Text.Length + 1 && Runs.All(r => r.Length > 0) &&
        Runs.Zip(Runs.Skip(1)).All(p => p.First.Format != p.Second.Format) &&
        Paras.Count == Text.Count(c => c == '\n') + 1;
}
