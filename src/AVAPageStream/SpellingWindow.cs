using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AVAPageStream.Core;

namespace AVAPageStream;

/// <summary>
/// Checking spelling through the document: each misspelt word in turn, shown selected on its page, with suggestions
/// to change it to; or ignore it (here, or everywhere in the document), or add it to the dictionary.
/// </summary>
public sealed class SpellingWindow : Window
{
    private readonly MainWindow _window;

    public Match? Current { get; private set; }
    public TextBlock Word { get; } = new() { Foreground = Look.Error, FontSize = 18, FontWeight = FontWeight.SemiBold };
    public TextBlock State { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    public ListBox Suggestions { get; } = new() { Height = 150, FontSize = 13 };
    public TextBox ChangeTo { get; } = new() { FontSize = 13 };

    public SpellingWindow(MainWindow window)
    {
        _window = window;
        Title = "Spelling";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        Background = Look.Window;
        Button B(string text, Action click)
        {
            var b = new Button { Content = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => click();
            return b;
        }
        Suggestions.SelectionChanged += (_, _) => { if (Suggestions.SelectedItem is string s) ChangeTo.Text = s; };
        var buttons = new UniformGrid { Columns = 2, Children =
        {
            B("Change", Change), B("Change all", ChangeAll),
            B("Ignore", Ignore), B("Ignore all", IgnoreAll),
            B("Add to dictionary", Add), B("Close", Close),
        } };
        foreach (var c in buttons.Children) c.Margin = new Thickness(0, 0, 6, 6);
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                Word,
                State,
                new TextBlock { Text = "Change to", Foreground = Look.Heading, FontSize = 12 },
                ChangeTo,
                Suggestions,
                buttons,
            },
        };
        Opened += (_, _) => Next();
    }

    private Speller Speller => Speller.For(_window.Editor.Doc.Language);

    /// <summary>Goes on to the next misspelt word after the selection (or <paramref name="from"/>).</summary>
    public void Next(string? storyId = null, int? from = null)
    {
        var t = _window.View.Editing;
        storyId ??= t?.StoryId;
        var at = from ?? (t is null ? 0 : t.Start + t.Length);
        Current = Speller.Next(_window.Editor.Doc, _window.SpellingWords(), storyId, at);
        if (Current is null)
        {
            Word.Text = "No misspelt words";
            State.Text = "Every word is in the dictionary, or passed.";
            Suggestions.ItemsSource = null;
            ChangeTo.Text = "";
            return;
        }
        var word = _window.Editor.Doc.Stories[Current.StoryId].Slice(Current.Start, Current.Length);
        Word.Text = word;
        var suggestions = Speller.Suggest(word);
        Suggestions.ItemsSource = suggestions;
        ChangeTo.Text = suggestions.FirstOrDefault() ?? word;
        State.Text = suggestions.Count == 0 ? "No suggestions: type the word as it should be." : "Pick a suggestion, or type the word as it should be.";
        _window.View.Reveal(Current.StoryId, Current.Start, Current.Length);
    }

    private string? CurrentWord => Current is { } m && _window.Editor.Doc.Stories.TryGetValue(m.StoryId, out var s) && m.Start + m.Length <= s.Length ? s.Slice(m.Start, m.Length) : null;

    public void Change()
    {
        if (Current is not { } m || CurrentWord is null) { Next(); return; }
        var after = _window.Editor.ReplaceOne(m, ChangeTo.Text ?? "");
        Next(m.StoryId, after);
    }

    public void ChangeAll()
    {
        if (Current is not { } m || CurrentWord is not { } word) { Next(); return; }
        var n = _window.Editor.ReplaceAll(word, ChangeTo.Text ?? "", new FindOptions(MatchCase: true, WholeWord: true));
        _window.Say($"Changed {n} of \"{word}\".");
        Next(m.StoryId, m.Start);
    }

    public void Ignore()
    {
        if (Current is not { } m) { Next(); return; }
        Next(m.StoryId, m.Start + m.Length);
    }

    /// <summary>Passes the word everywhere in this document (kept with it).</summary>
    public void IgnoreAll()
    {
        if (Current is not { } m || CurrentWord is not { } word) { Next(); return; }
        _window.Editor.Change("Ignore " + word, d => { if (!d.IgnoredWords.Contains(word)) d.IgnoredWords.Add(word); });
        Next(m.StoryId, m.Start);
    }

    /// <summary>Adds the word to the user's own dictionary, for every document.</summary>
    public void Add()
    {
        if (Current is not { } m || CurrentWord is not { } word) { Next(); return; }
        _window.AddWord(word);
        Next(m.StoryId, m.Start);
    }
}
