using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVAPageStream.Core;

namespace AVAPageStream;

/// <summary>
/// A story as plain text in a window of its own, for writing and correcting without the layout in the way. Typing
/// here changes the story at once (keeping the formats of the text round the change), and changes made on the page
/// show here. The paragraph style of the caret's paragraph is shown, and can be set.
/// </summary>
public sealed class StoryEditor : Window
{
    private readonly MainWindow _window;
    private bool _syncing;

    public string StoryId { get; }

    public TextBox Text { get; } = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = FontSupport.Family(Fonts.Serif),
        FontSize = 15,
        Padding = new Thickness(14, 10),
    };

    public ComboBox StyleChoice { get; } = new() { MinWidth = 160, FontSize = 12 };
    public TextBlock Info { get; } = new() { Foreground = Look.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    public StoryEditor(MainWindow window, string storyId)
    {
        _window = window;
        StoryId = storyId;
        Width = 620;
        Height = 640;
        Background = Look.Window;
        Title = "Story";

        var bar = new DockPanel { Margin = new Thickness(10, 6) };
        var label = new TextBlock { Text = "Paragraph style", Foreground = Look.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(StyleChoice, Dock.Left);
        Info.Margin = new Thickness(14, 0, 0, 0);
        bar.Children.AddRange([label, StyleChoice, Info]);
        var dock = new DockPanel();
        var top = new Border { Child = bar, Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(Text);
        Content = dock;

        Text.TextChanged += (_, _) => ToStory();
        Text.PropertyChanged += (_, e) => { if (e.Property == TextBox.CaretIndexProperty) ShowStyle(); };
        StyleChoice.SelectionChanged += (_, _) =>
        {
            if (_syncing || StyleChoice.SelectedItem is not string name) return;
            _window.Editor.ApplyStyle(StoryId, Math.Min(Text.CaretIndex, Story?.Length ?? 0), 0, name);
        };
        window.Editor.Changed += FromStory;
        Closed += (_, _) => _window.Editor.Changed -= FromStory;
        FromStory();
    }

    private Story? Story => _window.Editor.Doc.Stories.GetValueOrDefault(StoryId);

    /// <summary>Puts what was typed here into the story: only the stretch that changed, so formats round it stay.</summary>
    private void ToStory()
    {
        if (_syncing || Story is not { } story) return;
        var now = (Text.Text ?? "").Replace("\r\n", "\n");
        var was = story.Text;
        if (now == was) return;
        var start = 0;
        while (start < now.Length && start < was.Length && now[start] == was[start]) start++;
        var end = 0;
        while (end < now.Length - start && end < was.Length - start && now[^(end + 1)] == was[^(end + 1)]) end++;
        _syncing = true;
        try
        {
            _window.Editor.ReplaceText(StoryId, start, was.Length - start - end, now[start..(now.Length - end)], merge: "story editor:" + StoryId);
        }
        finally
        {
            _syncing = false;
        }
        ShowInfo();
    }

    /// <summary>Shows the story as it now is (after an edit on the page, or an undo).</summary>
    private void FromStory()
    {
        if (_syncing) return;
        if (Story is not { } story)
        {
            Close(); // the story went: its frames were deleted
            return;
        }
        Title = $"Story: {Preview(story.Text)}";
        _syncing = true;
        try
        {
            if ((Text.Text ?? "").Replace("\r\n", "\n") != story.Text)
            {
                var caret = Text.CaretIndex;
                Text.Text = story.Text;
                Text.CaretIndex = Math.Min(caret, story.Length);
            }
            StyleChoice.ItemsSource = _window.Editor.Doc.Styles.Select(s => s.Name).ToList();
        }
        finally
        {
            _syncing = false;
        }
        ShowStyle();
        ShowInfo();
    }

    private void ShowStyle()
    {
        if (Story is not { } story) return;
        _syncing = true;
        try
        {
            StyleChoice.SelectedItem = story.Paras[story.ParagraphAt(Math.Min(Text.CaretIndex, story.Length))].Style;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ShowInfo()
    {
        if (Story is not { } story) return;
        var words = story.Text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (story.NoteOf is not null)
        {
            Info.Text = $"{words} words · a footnote";
            return;
        }
        if (story.OnPath is { } pathId && _window.Editor.Doc.Find(pathId) is PathObject path)
        {
            var (_, from) = PathText.Place(story, PathText.Points(path), _window.View.Layouts.Measurer, path.TextOffset);
            Info.Text = $"{words} words · on a path" + (from < story.Length ? $" · {story.Length - from} characters past its end" : "");
            return;
        }
        var layout = _window.View.LayoutOf(StoryId);
        var over = layout is { Overset: true } ? $" · {story.Length - layout.OversetFrom} characters overset" : "";
        Info.Text = $"{words} words · {story.ParagraphCount} paragraphs · {story.Frames.Count} frame{(story.Frames.Count == 1 ? "" : "s")}{over}";
    }

    private static string Preview(string text)
    {
        var first = text.Split('\n')[0].Trim();
        return first.Length == 0 ? "(empty)" : first.Length > 40 ? first[..40] + "…" : first;
    }
}
