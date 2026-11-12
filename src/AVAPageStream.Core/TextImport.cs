using System.Text;
using System.Text.RegularExpressions;

namespace AVAPageStream.Core;

/// <summary>A stretch of placed text, and whether it is bold or italic.</summary>
public sealed record PlacedRun(string Text, bool Bold = false, bool Italic = false);

/// <summary>A placed paragraph: the style it takes, its runs, and a hanging indent for list items.</summary>
public sealed record PlacedPara(string Style, IReadOnlyList<PlacedRun> Runs, double LeftIndent = 0, double FirstIndent = 0)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>Reading text files to place in a story: plain text, and Markdown.</summary>
public static partial class TextImport
{
    /// <summary>Plain text: each line a paragraph in the Body style.</summary>
    public static List<PlacedPara> PlainText(string text) =>
        Lines(text).Select(l => new PlacedPara("Body", [new PlacedRun(l.Replace('\t', ' '))])).ToList();

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*[-*+]\s+(.*)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^\s*(\d+)[.)]\s+(.*)$")]
    private static partial Regex NumberLine();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleLine();

    /// <summary>
    /// Markdown: # headings take the Heading style and ## (and smaller) Subheading; paragraphs (lines joined until a
    /// blank line) take Body; - and 1. list items get a bullet or number and a hanging indent; &gt; quotes are indented
    /// and italic; **bold**, *italic*, `code` and [links](…) keep their text. Fenced code keeps its lines.
    /// </summary>
    public static List<PlacedPara> Markdown(string text)
    {
        var paras = new List<PlacedPara>();
        var pending = new StringBuilder();
        var quote = false;
        var fenced = false;

        void Flush()
        {
            if (pending.Length == 0) return;
            var runs = Inline(pending.ToString().Trim());
            if (quote) runs = runs.Select(r => r with { Italic = true }).ToList();
            paras.Add(new PlacedPara("Body", runs, quote ? 18 : 0));
            pending.Clear();
            quote = false;
        }

        foreach (var raw in Lines(text))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                fenced = !fenced;
                continue;
            }
            if (fenced)
            {
                paras.Add(new PlacedPara("Body", [new PlacedRun(raw.Replace('\t', ' '))], 18));
                continue;
            }
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (RuleLine().IsMatch(line)) { Flush(); continue; }
            if (HeadingLine().Match(line) is { Success: true } h)
            {
                Flush();
                paras.Add(new PlacedPara(h.Groups[1].Length == 1 ? "Heading" : "Subheading", Inline(h.Groups[2].Value)));
                continue;
            }
            if (BulletLine().Match(line) is { Success: true } b)
            {
                Flush();
                paras.Add(new PlacedPara("Body", [new PlacedRun("•\t"), .. Inline(b.Groups[1].Value)], 14, -14));
                continue;
            }
            if (NumberLine().Match(line) is { Success: true } n)
            {
                Flush();
                paras.Add(new PlacedPara("Body", [new PlacedRun(n.Groups[1].Value + ".\t"), .. Inline(n.Groups[2].Value)], 18, -18));
                continue;
            }
            var isQuote = line.TrimStart().StartsWith('>');
            if (isQuote) line = line.TrimStart()[1..].TrimStart();
            if (pending.Length > 0 && isQuote != quote) Flush();
            quote = isQuote;
            if (pending.Length > 0) pending.Append(' ');
            pending.Append(line.Trim());
        }
        Flush();
        return paras;
    }

    /// <summary>The runs of one line of Markdown: bold and italic markers that close later in the line, code and links.</summary>
    internal static List<PlacedRun> Inline(string text)
    {
        var runs = new List<PlacedRun>();
        var current = new StringBuilder();
        bool bold = false, italic = false;

        void Cut()
        {
            if (current.Length > 0) runs.Add(new PlacedRun(current.ToString(), bold, italic));
            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#+-.!>".Contains(text[i + 1]))
            {
                current.Append(text[++i]);
                continue;
            }
            if (c == '`' && text.IndexOf('`', i + 1) is var close and > 0)
            {
                current.Append(text, i + 1, close - i - 1);
                i = close;
                continue;
            }
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' && Link(text, i + 1) is { } image)
            {
                i = image.End; // pictures are placed separately: leave them out
                continue;
            }
            if (c == '[' && Link(text, i) is { } link)
            {
                current.Append(link.Text);
                i = link.End;
                continue;
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                if (bold || text.IndexOf(marker, i + 2, StringComparison.Ordinal) > i + 2)
                {
                    Cut();
                    bold = !bold;
                    i++;
                    continue;
                }
            }
            else if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) || italic)))
            {
                if (italic || (text.IndexOf(c, i + 1) is var end and > 0 && end > i + 1))
                {
                    Cut();
                    italic = !italic;
                    continue;
                }
            }
            current.Append(c);
        }
        Cut();
        return runs.Count > 0 ? runs : [new PlacedRun("")];
    }

    private static (string Text, int End)? Link(string text, int open)
    {
        var close = text.IndexOf(']', open + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return null;
        var end = text.IndexOf(')', close + 2);
        return end < 0 ? null : (text[(open + 1)..close], end);
    }
}
