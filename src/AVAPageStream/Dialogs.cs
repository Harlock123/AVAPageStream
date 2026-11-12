using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVAPageStream.Core;

namespace AVAPageStream;

/// <summary>A small question with buttons, such as whether to save changes before closing.</summary>
internal sealed class AskDialog : Window
{
    private AskDialog(string title, string message, string[] buttons)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) b.Classes.Add("accent");
            b.Click += (_, _) => Close(index);
            row.Children.Add(b);
        }
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 14 },
                row,
            },
        };
    }

    /// <summary>Asks, and returns the index of the button pressed, or the last one (cancel) if the window is closed.</summary>
    public static async Task<int> Show(Window owner, string title, string message, params string[] buttons)
    {
        var result = await new AskDialog(title, message, buttons).ShowDialog<int?>(owner);
        return result ?? buttons.Length - 1;
    }
}

/// <summary>Asks for a short piece of text: a name, say.</summary>
internal sealed class TextDialog : Window
{
    private TextDialog(string title, string message, string start)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;
        var box = new TextBox { Text = start, FontSize = 14 };
        var ok = new Button { Content = "OK", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Close(box.Text ?? "");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(null);
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Close(box.Text ?? ""); };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 14 },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        Opened += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    public static Task<string?> Show(Window owner, string title, string message, string start = "") => new TextDialog(title, message, start).ShowDialog<string?>(owner);
}

/// <summary>What a cross-reference points at (an index into the list it was chosen from) and how it shows it.</summary>
public sealed record ReferenceChoice(int Target, RefKind Kind);

/// <summary>Chooses a heading or named place to refer to, and whether the reference shows its page, its text or both.</summary>
internal sealed class ReferenceDialog : Window
{
    private ReferenceDialog(IReadOnlyList<string> targets)
    {
        Title = "Cross-reference";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;
        var list = new ListBox { ItemsSource = targets, Height = 240, SelectedIndex = targets.Count > 0 ? 0 : -1 };
        var kind = new ComboBox { ItemsSource = new[] { "Its page number", "Its text", "Its text and page" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var ok = new Button { Content = "Insert", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => { if (list.SelectedIndex >= 0) Close(new ReferenceChoice(list.SelectedIndex, (RefKind)kind.SelectedIndex)); };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(null);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Refer to a heading, or a place named with Text › Name this place:", TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 14 },
                list,
                new TextBlock { Text = "Show", Foreground = Look.Text },
                kind,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
    }

    public static Task<ReferenceChoice?> Show(Window owner, IReadOnlyList<string> targets) => new ReferenceDialog(targets).ShowDialog<ReferenceChoice?>(owner);
}

/// <summary>Asks for an index entry: its topic, a sub-topic under it, and another topic to see instead.</summary>
internal sealed class IndexDialog : Window
{
    private IndexDialog(string topic)
    {
        Title = "Index entry";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;
        var t = new TextBox { Text = topic, FontSize = 14 };
        var s = new TextBox { PlaceholderText = "none", FontSize = 14 };
        var see = new TextBox { PlaceholderText = "none: the page this is on", FontSize = 14 };
        var ok = new Button { Content = "Mark", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Close(new IndexMark(t.Text ?? "", s.Text ?? "", see.Text ?? ""));
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(null);
        StackPanel Row(string label, Control c) => new() { Spacing = 4, Children = { new TextBlock { Text = label, Foreground = Look.Text }, c } };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                Row("Topic", t),
                Row("Sub-topic (under the topic)", s),
                Row("See (another topic, instead of a page)", see),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        Opened += (_, _) => { t.Focus(); t.SelectAll(); };
    }

    public static Task<IndexMark?> Show(Window owner, string topic) => new IndexDialog(topic).ShowDialog<IndexMark?>(owner);
}

/// <summary>Asks for a protected PDF's password.</summary>
internal sealed class PasswordDialog : Window
{
    private PasswordDialog(string file, bool wrong)
    {
        Title = "Password";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;
        var box = new TextBox { PasswordChar = '•', PlaceholderText = "Password", FontSize = 14 };
        var ok = new Button { Content = "Open", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Close(box.Text ?? "");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(null);
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Close(box.Text ?? ""); };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = (wrong ? "That isn't its password. " : "") + $"{file} is protected by a password. Type it to place the page; it is kept with the picture, in the document, so the page can be drawn again.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 14,
                },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        Opened += (_, _) => box.Focus();
    }

    /// <summary>The password typed, or null if cancelled.</summary>
    public static Task<string?> Show(Window owner, string file, bool wrong) => new PasswordDialog(file, wrong).ShowDialog<string?>(owner);
}

/// <summary>What to export or print: the pages (counting from 0), for PNG the resolution, and for PDF or print whether as a booklet.</summary>
public sealed record ExportChoice(IReadOnlyList<int> Pages, int Dpi, bool Booklet = false, bool Marks = false, bool Cmyk = false, bool PdfX = false);

/// <summary>Asks which pages to export (all, this one, or a range like "1-3, 5") and, for PNG, at what resolution.</summary>
internal sealed class ExportDialog : Window
{
    private ExportDialog(string kind, int pageCount, int current, int dpi)
    {
        var png = kind == "png";
        Title = kind switch { "png" => "Export PNG", "print" => "Print", _ => "Export PDF" };
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;

        var all = new RadioButton { Content = pageCount == 1 ? "The page" : $"All {pageCount} pages", GroupName = "pages", IsChecked = true };
        var one = new RadioButton { Content = $"This page ({current + 1})", GroupName = "pages", IsVisible = pageCount > 1 && current >= 0 };
        var some = Some = new RadioButton { Content = "Pages", GroupName = "pages", IsVisible = pageCount > 1 };
        var range = Range = new TextBox { Text = $"1-{pageCount}", Width = 150, Margin = new Thickness(8, 0, 0, 0), IsVisible = pageCount > 1 };
        range.GotFocus += (_, _) => some.IsChecked = true;
        string? checkedText = null;
        // TextChanged can arrive after the click that checked the range, so only a real change hides the error.
        range.TextChanged += (_, _) => { if (Error is not null && range.Text != checkedText) Error.IsVisible = false; };
        var resolution = Resolution = new NumericUpDown { Minimum = 72, Maximum = 600, Increment = 25, Value = dpi, FormatString = "0", Width = 130 };
        var booklet = BookletBox = new CheckBox { Content = "As a booklet: pages in pairs on each sheet, in the order that folds", IsVisible = !png };
        var marks = MarksBox = new CheckBox { Content = "Crop marks and bleed: each page on a larger sheet, for printing to the edge", IsVisible = kind == "pdf" };
        booklet.IsCheckedChanged += (_, _) => { if (booklet.IsChecked == true) marks.IsChecked = false; };
        marks.IsCheckedChanged += (_, _) => { if (marks.IsChecked == true) booklet.IsChecked = false; };
        var cmyk = CmykBox = new CheckBox { Content = "In CMYK, for a printing press: each colour as the inks it prints with, spot colours as their own inks", IsVisible = kind == "pdf" };
        var pdfx = PdfXBox = new CheckBox { Content = "PDF/X-4, for handing to a printer: CMYK with its output intent, everything a print shop's checks look for (no links)", IsVisible = kind == "pdf" };
        pdfx.IsCheckedChanged += (_, _) => { if (pdfx.IsChecked == true) { cmyk.IsChecked = true; booklet.IsChecked = false; } };
        cmyk.IsCheckedChanged += (_, _) => { if (cmyk.IsChecked != true) pdfx.IsChecked = false; };
        booklet.IsCheckedChanged += (_, _) => { if (booklet.IsChecked == true) pdfx.IsChecked = false; };
        var error = Error = new TextBlock { Foreground = Look.Error, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        var export = ExportButton = new Button { Content = kind == "print" ? "Print" : "Export", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        export.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(null);
        export.Click += (_, _) =>
        {
            IReadOnlyList<int> pages;
            if (one.IsChecked == true) pages = [current];
            else if (some.IsChecked == true)
            {
                if (!PageRanges.TryParse(range.Text, pageCount, out var chosen, out var why))
                {
                    checkedText = range.Text;
                    error.Text = why;
                    error.IsVisible = true;
                    return;
                }
                pages = chosen;
            }
            else pages = [.. Enumerable.Range(0, pageCount)];
            Close(new ExportChoice(pages, (int)(resolution.Value ?? dpi), booklet.IsChecked == true, marks.IsChecked == true, cmyk.IsChecked == true, pdfx.IsChecked == true));
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Which pages?", Foreground = Look.Text, FontSize = 14, FontWeight = FontWeight.SemiBold },
                all,
                one,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { some, range } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    IsVisible = png,
                    Children =
                    {
                        new TextBlock { Text = "Resolution", Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center },
                        resolution,
                        new TextBlock { Text = "dpi (72 to 600)", Foreground = Look.Heading, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                booklet,
                marks,
                cmyk,
                pdfx,
                error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { export, cancel } },
            },
        };
    }

    internal CheckBox BookletBox { get; }
    internal CheckBox MarksBox { get; }
    internal CheckBox CmykBox { get; }
    internal CheckBox PdfXBox { get; }
    internal RadioButton Some { get; }
    internal TextBox Range { get; }
    internal NumericUpDown Resolution { get; }
    internal TextBlock Error { get; }
    internal Button ExportButton { get; }

    /// <summary>Asks; null if cancelled. <paramref name="current"/> is the page shown, or -1 when a master is.</summary>
    public static Task<ExportChoice?> Show(Window owner, string kind, int pageCount, int current, int dpi) =>
        new ExportDialog(kind, pageCount, current, dpi).ShowDialog<ExportChoice?>(owner);
}

/// <summary>Asks which paragraph styles a table of contents lists, the first chosen as its top level.</summary>
internal sealed class ContentsDialog : Window
{
    private ContentsDialog(IReadOnlyList<string> styles)
    {
        Title = "Table of contents";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;
        var boxes = styles.Select(s => new CheckBox { Content = s, IsChecked = s is "Heading" or "Subheading" }).ToList();
        var make = new Button { Content = "Make it", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        make.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        make.Click += (_, _) => Close(boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content!).ToList());
        cancel.Click += (_, _) => Close(null);
        var list = new StackPanel { Spacing = 4 };
        list.Children.AddRange(boxes);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "List the paragraphs in these styles, with their pages. The first ticked is the top level.", Foreground = Look.Text, TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { Content = list, MaxHeight = 300 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { make, cancel } },
            },
        };
    }

    public static Task<List<string>?> Show(Window owner, IReadOnlyList<string> styles) =>
        new ContentsDialog(styles).ShowDialog<List<string>?>(owner);
}
