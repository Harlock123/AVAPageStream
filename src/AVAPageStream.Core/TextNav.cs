namespace AVAPageStream.Core;

/// <summary>Moving through text by words and paragraphs, and picking out a word or a paragraph.</summary>
public static class TextNav
{
    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '_';

    /// <summary>The start of the word before <paramref name="i"/> (Ctrl+Left, Ctrl+Backspace).</summary>
    public static int WordLeft(string text, int i)
    {
        i = Math.Clamp(i, 0, text.Length);
        if (i > 0 && text[i - 1] == '\n') return i - 1;
        while (i > 0 && !IsWord(text[i - 1]) && text[i - 1] != '\n') i--;
        while (i > 0 && IsWord(text[i - 1])) i--;
        return i;
    }

    /// <summary>The start of the next word after <paramref name="i"/> (Ctrl+Right, Ctrl+Delete).</summary>
    public static int WordRight(string text, int i)
    {
        i = Math.Clamp(i, 0, text.Length);
        if (i < text.Length && text[i] == '\n') return i + 1;
        while (i < text.Length && IsWord(text[i])) i++;
        while (i < text.Length && !IsWord(text[i]) && text[i] != '\n') i++;
        return i;
    }

    /// <summary>The word at <paramref name="i"/> (a double-click), or the run of other characters there.</summary>
    public static (int Start, int End) WordAt(string text, int i)
    {
        if (text.Length == 0) return (0, 0);
        i = Math.Clamp(i, 0, text.Length - 1);
        if (text[i] == '\n' && i > 0) i--;
        var word = IsWord(text[i]);
        int s = i, e = i;
        while (s > 0 && IsWord(text[s - 1]) == word && text[s - 1] != '\n') s--;
        while (e < text.Length && IsWord(text[e]) == word && text[e] != '\n') e++;
        return (s, e);
    }
}
