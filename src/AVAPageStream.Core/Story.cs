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
    Script Script = Script.Normal);

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
    bool Hyphenate = false); // break words at the end of lines with a hyphen

/// <summary>A named set of character and paragraph settings.</summary>
public sealed record ParagraphStyle(string Name, CharFormat Char, ParaFormat Para);

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

    /// <summary>The ids of the text frames the story flows through, in order.</summary>
    public List<string> Frames { get; set; } = [];

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
        index > 0 && Text[index - 1] != '\n' ? FormatAt(index - 1) : FormatAt(index);

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

    /// <summary>Sets every paragraph the selection touches, and all of their characters, to a style.</summary>
    public void ApplyStyle(int index, int length, ParagraphStyle style)
    {
        var (first, last) = ParagraphsIn(index, length);
        for (var p = first; p <= last; p++)
        {
            Paras[p] = style.Para with { Style = style.Name };
            var (start, end) = ParagraphRange(p);
            FormatChars(start, end - start + 1, _ => style.Char);
        }
    }

    /// <summary>
    /// Makes every paragraph that uses a style take the style's settings again, keeping bold, italic and underline
    /// set on characters.
    /// </summary>
    public void Restyle(ParagraphStyle style)
    {
        for (var p = 0; p < Paras.Count; p++)
        {
            if (Paras[p].Style != style.Name) continue;
            Paras[p] = style.Para with { Style = style.Name };
            var (start, end) = ParagraphRange(p);
            FormatChars(start, end - start + 1, f => style.Char with { Bold = f.Bold, Italic = f.Italic, Underline = f.Underline });
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
