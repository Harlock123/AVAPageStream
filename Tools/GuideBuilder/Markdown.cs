using System.Text.RegularExpressions;

namespace AVAPageStream.Guide;

/// <summary>A run of text with the emphasis it was written with.</summary>
public readonly record struct Run(string Text, bool Bold, bool Italic, bool Code);

public abstract record Block;

public sealed record Heading(int Level, IReadOnlyList<Run> Text) : Block;
public sealed record Paragraph(IReadOnlyList<Run> Text) : Block;
public sealed record Bullet(IReadOnlyList<Run> Text, string? Number = null) : Block;
public sealed record Picture(string Path, string Alt) : Block;
public sealed record Table(IReadOnlyList<IReadOnlyList<IReadOnlyList<Run>>> Rows, bool HasHeader) : Block;
public sealed record Code(IReadOnlyList<string> Lines) : Block;

/// <summary>
/// Enough Markdown for a user guide: headings, paragraphs, bullets, tables, pictures and code
/// blocks, with bold, italic and code spans inside them.
///
/// Deliberately not a Markdown implementation. It reads the document in this repository, and
/// anything it does not understand it passes through as text rather than guessing - a guide
/// with a stray asterisk in it is a far smaller problem than one that silently loses a
/// paragraph.
/// </summary>
public static class Markdown
{
    public static List<Block> Read(string text)
    {
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;

            blocks.Add(new Paragraph(Inline(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // Fenced code.
            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();

                var code = new List<string>();

                while (++i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    code.Add(lines[i]);

                blocks.Add(new Code(code));
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                FlushParagraph();

                var level = trimmed.TakeWhile(c => c == '#').Count();
                blocks.Add(new Heading(level, Inline(trimmed[level..].Trim())));
                continue;
            }

            if (Regex.Match(trimmed, @"^!\[(.*?)\]\((.*?)\)$") is { Success: true } picture)
            {
                FlushParagraph();
                blocks.Add(new Picture(picture.Groups[2].Value, picture.Groups[1].Value));
                continue;
            }

            // A table: a run of pipe rows, with the second one possibly being the rule.
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();

                var rows = new List<IReadOnlyList<IReadOnlyList<Run>>>();
                var header = false;

                while (i < lines.Length && lines[i].Trim().StartsWith('|'))
                {
                    var row = lines[i].Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();

                    // The |---|---| rule marks everything above it as the heading row.
                    if (row.All(cell => cell.Length > 0 && cell.All(c => c is '-' or ':')))
                    {
                        header = rows.Count > 0;
                    }
                    else
                    {
                        rows.Add(row.Select(Inline).ToList());
                    }

                    i++;
                }

                i--;

                if (rows.Count > 0)
                    blocks.Add(new Table(rows, header));

                continue;
            }

            // A numbered item ("1. "): a bullet that shows its number.
            var numbered = Regex.Match(trimmed, @"^(\d+)\.\s+");

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || numbered.Success)
            {
                FlushParagraph();

                // A bullet runs on until a blank line or the next bullet.
                var item = numbered.Success ? trimmed[numbered.Length..] : trimmed[2..];

                while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") &&
                       lines[i + 1].Trim().Length > 0 && !lines[i + 1].Trim().StartsWith("- "))
                    item += " " + lines[++i].Trim();

                blocks.Add(new Bullet(Inline(item), numbered.Success ? numbered.Groups[1].Value + "." : null));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>Splits a line into runs, honouring `code`, **bold** and *italic*.</summary>
    public static IReadOnlyList<Run> Inline(string text)
    {
        // Links become their words: a printed page has nowhere to go.
        text = Regex.Replace(text, @"\[(.*?)\]\((.*?)\)", "$1");

        var runs = new List<Run>();
        var plain = new System.Text.StringBuilder();
        var bold = false;
        var italic = false;

        void Flush()
        {
            if (plain.Length == 0)
                return;

            runs.Add(new Run(plain.ToString(), bold, italic, false));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                var close = text.IndexOf('`', i + 1);

                if (close > i)
                {
                    Flush();
                    runs.Add(new Run(text[(i + 1)..close], bold, italic, true));
                    i = close;
                    continue;
                }
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                Flush();
                bold = !bold;
                i++;
                continue;
            }

            if (text[i] == '*')
            {
                Flush();
                italic = !italic;
                continue;
            }

            plain.Append(text[i]);
        }

        Flush();
        return runs;
    }
}
