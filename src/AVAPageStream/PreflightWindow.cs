using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVAPageStream.Core;

namespace AVAPageStream;

/// <summary>
/// Preflight: a list of what would go wrong if the document went to print as it is. Picking an entry shows the page
/// and selects what it is about (with the caret where the text stops fitting, for overset text).
/// </summary>
public sealed class PreflightWindow : Window
{
    private readonly MainWindow _window;

    public ListBox List { get; } = new() { Height = 340, FontSize = 12 };
    public TextBlock Summary { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    public IReadOnlyList<Issue> Issues { get; private set; } = [];

    public PreflightWindow(MainWindow window)
    {
        _window = window;
        Title = "Preflight";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        Background = Look.Window;
        List.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Issue>((issue, _) => new TextBlock
        {
            Text = issue?.Message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = issue?.Serious == true ? Look.Error : Look.Text,
        });
        List.SelectionChanged += (_, _) => { if (List.SelectedItem is Issue i) _window.ShowIssue(i); };
        var again = new Button { Content = "Check again", FontSize = 12 };
        again.Click += (_, _) => Check();
        var close = new Button { Content = "Close", FontSize = 12 };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                Summary,
                List,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { again, close } },
            },
        };
        Check();
    }

    /// <summary>Checks the document again.</summary>
    public void Check()
    {
        Issues = _window.Preflight();
        List.ItemsSource = Issues;
        var serious = Issues.Count(i => i.Serious);
        Summary.Text = Issues.Count == 0
            ? "Nothing wrong: ready to export or print."
            : $"{Issues.Count} thing{(Issues.Count == 1 ? "" : "s")} to look at" + (serious > 0 ? $", {serious} of them (in red) will show in print." : ".") + " Pick one to go to it.";
    }
}
