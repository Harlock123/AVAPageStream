using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVAPageStream.Core;
using Page = AVAPageStream.Core.Page;

namespace AVAPageStream;

/// <summary>Small builders for the panels' rows of labels and boxes.</summary>
internal static class Ui
{
    public static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Foreground = Look.Heading,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = 0.6,
        Margin = new Thickness(0, 10, 0, 2),
    };

    public static TextBlock Label(string text, double width = 74) => new()
    {
        Text = text,
        Foreground = Look.Heading,
        FontSize = 12,
        Width = width,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Grid Row(params (string Label, Control Control)[] cells)
    {
        var g = new Grid { Margin = new Thickness(0, 2) };
        for (var i = 0; i < cells.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            // Two to a row: single letters (X, Y, W, H) need little room; words need more.
            var width = cells.Length == 1 ? 74 : cells.All(c => c.Label.Length <= 1) ? 22 : 44;
            var label = Label(cells[i].Label, width);
            if (i > 0) label.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(label, i * 2);
            Grid.SetColumn(cells[i].Control, i * 2 + 1);
            g.Children.Add(label);
            g.Children.Add(cells[i].Control);
        }
        return g;
    }

    public static TextBox Box(string? hint = null) => new()
    {
        PlaceholderText = hint,
        FontSize = 12,
        MinHeight = 26,
        Padding = new Thickness(6, 3),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public static Button Button(string text, Action click, string? tip = null)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3), MinHeight = 26 };
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static WrapPanel Buttons(params Control[] buttons)
    {
        var w = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(0, 0, 4, 4);
            w.Children.Add(b);
        }
        return w;
    }

    /// <summary>Calls <paramref name="commit"/> when Enter is pressed in the box or it loses focus with a changed value.</summary>
    public static void OnCommit(TextBox box, Action<string> commit)
    {
        string? before = null;
        box.GotFocus += (_, _) => before = box.Text;
        // On the way down, before the box itself sees the key.
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            commit(box.Text ?? "");
            before = box.Text; // as tidied by the commit, so leaving the box doesn't commit it again
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) =>
        {
            if (box.Text != before) commit(box.Text ?? "");
            before = null;
        };
    }
}

/// <summary>
/// The side panel: the Object tab (frame, colours, arrangement), the Text tab (styles, character and paragraph
/// settings), the Pages tab (pages and master pages) and the Document tab (page size, margins, columns, units).
/// </summary>
public sealed class SidePanel : TabControl
{
    private readonly MainWindow _window;
    private bool _refreshing;

    // Object
    public TextBlock ObjectTitle { get; } = new() { Foreground = Look.Text, FontSize = 14, FontWeight = FontWeight.SemiBold };
    public TextBox XBox { get; } = Ui.Box();
    public TextBox YBox { get; } = Ui.Box();
    public TextBox WBox { get; } = Ui.Box();
    public TextBox HBox { get; } = Ui.Box();
    public TextBox RotationBox { get; } = Ui.Box("degrees, clockwise");
    public TextBox FillBox { get; } = Ui.Box("none, a name or #RRGGBB");
    public TextBox StrokeBox { get; } = Ui.Box("none, a name or #RRGGBB");
    public TextBox StrokeWidthBox { get; } = Ui.Box();
    public TextBox CornerBox { get; } = Ui.Box();
    public NumericUpDown ColumnsBox { get; } = new() { Minimum = 1, Maximum = 12, Increment = 1, FormatString = "0", FontSize = 12 };
    public TextBox GutterBox { get; } = Ui.Box();
    public TextBox InsetBox { get; } = Ui.Box();
    public TextBox OffsetXBox { get; } = Ui.Box();
    public TextBox OffsetYBox { get; } = Ui.Box();
    public ComboBox FitChoice { get; } = new() { ItemsSource = Enum.GetNames<Fit>(), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public CheckBox LockedBox { get; } = new() { Content = "Locked", FontSize = 12 };
    public CheckBox WrapBox { get; } = new() { Content = "Text flows round this", FontSize = 12 };
    public TextBox WrapGapBox { get; } = Ui.Box();
    private readonly StackPanel _objectBody = new() { Spacing = 0 };
    private readonly StackPanel _cornerRow = new();
    public ComboBox ObjectStyleChoice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public TextBox ObjectStyleNameBox { get; } = Ui.Box("New object style's name");
    public TextBlock ObjectStyleInfo { get; } = new() { Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _gradientRows = new();
    private readonly StackPanel _shadowRows = new();
    public ComboBox FillKindChoice { get; } = new() { ItemsSource = new[] { "Colour", "Linear gradient", "Radial gradient" }, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public TextBox GradientStopsBox { get; } = Ui.Box("such as Navy, Gold at 60, White");
    public TextBox GradientAngleBox { get; } = Ui.Box("0, left to right");
    public TextBox OpacityBox { get; } = Ui.Box("100%");
    public TextBox FillOpacityBox { get; } = Ui.Box("100%");
    public TextBox StrokeOpacityBox { get; } = Ui.Box("100%");
    public ComboBox BlendChoice { get; } = new() { ItemsSource = Enum.GetNames<Core.BlendMode>().Select(n => System.Text.RegularExpressions.Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant() is var s ? char.ToUpperInvariant(s[0]) + s[1..] : n).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public ComboBox ShadowChoice { get; } = new() { ItemsSource = new[] { "None", "Drop shadow", "Glow" }, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public TextBox ShadowColourBox { get; } = Ui.Box("Black");
    public TextBox ShadowOpacityBox { get; } = Ui.Box("50%");
    public TextBox ShadowXBox { get; } = Ui.Box();
    public TextBox ShadowYBox { get; } = Ui.Box();
    public TextBox ShadowBlurBox { get; } = Ui.Box();
    public ComboBox CornerStyleChoice { get; } = new() { ItemsSource = new[] { "Rounded", "Cut" }, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    private readonly StackPanel _rotationRow = new();
    private readonly StackPanel _frameRows = new();
    private readonly StackPanel _pictureRows = new();
    public NumericUpDown PdfPageBox { get; } = new() { Minimum = 1, Maximum = 1, Increment = 1, FormatString = "0", FontSize = 12 };
    private readonly TextBlock _pdfPages = new() { Foreground = Look.Faint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly StackPanel _pdfRow = new();
    private readonly Dictionary<string, int> _pageCounts = [];

    /// <summary>How many pages a placed PDF has (0 if it isn't one), worked out once for each.</summary>
    private int PageCount(string key)
    {
        if (_pageCounts.TryGetValue(key, out var n)) return n;
        n = Doc.Images.Get(key) is { } bytes && PdfPages.IsPdf(bytes) ? PdfPages.Count(bytes) : 0;
        return _pageCounts[key] = n;
    }
    private readonly StackPanel _pathTextRows = new();
    public TextBox TextOffsetBox { get; } = Ui.Box();
    private readonly WrapPanel _swatchButtons = new() { Margin = new Thickness(74, 2, 0, 4) };
    private List<Swatch> _swatchesShown = [];
    private readonly Border _fillSwatch = Swatch();
    private readonly Border _strokeSwatch = Swatch();

    // Text
    public ComboBox StyleChoice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public Button RenameStyleButton { get; }
    public TextBox StyleNameBox { get; } = Ui.Box("New style's name");
    public ComboBox FontChoice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12, MaxDropDownHeight = 420 };
    public TextBox SizeBox { get; } = Ui.Box();
    public ToggleButton BoldToggle { get; } = Toggle("B", FontWeight.Bold);
    public ToggleButton ItalicToggle { get; } = Toggle("I", FontWeight.Normal, FontStyle.Italic);
    public ToggleButton UnderlineToggle { get; } = Toggle("U", FontWeight.Normal);
    public ToggleButton SmallCapsToggle { get; } = Toggle("Sc", FontWeight.Normal);
    public ToggleButton SuperToggle { get; } = Toggle("x²", FontWeight.Normal);
    public ToggleButton SubToggle { get; } = Toggle("x₂", FontWeight.Normal);
    public TextBox TextColourBox { get; } = Ui.Box("a name or #RRGGBB");
    public TextBox TrackingBox { get; } = Ui.Box("thousandths of an em");
    public Dictionary<Align, ToggleButton> AlignButtons { get; } = [];
    public TextBox LeadingBox { get; } = Ui.Box("auto");
    public TextBox LeftIndentBox { get; } = Ui.Box();
    public TextBox RightIndentBox { get; } = Ui.Box();
    public TextBox FirstIndentBox { get; } = Ui.Box();
    public TextBox SpaceBeforeBox { get; } = Ui.Box();
    public TextBox SpaceAfterBox { get; } = Ui.Box();
    public TextBox TabsBox { get; } = Ui.Box("1in, 4in right .");
    public NumericUpDown DropLinesBox { get; } = new() { Minimum = 0, Maximum = 10, Increment = 1, FormatString = "0", FontSize = 12 };
    public NumericUpDown DropCharsBox { get; } = new() { Minimum = 1, Maximum = 10, Increment = 1, FormatString = "0", FontSize = 12 };
    public CheckBox HyphenateBox { get; } = new() { Content = "Hyphenate", FontSize = 12 };
    public RuleControls RuleAboveControls { get; } = new();
    public RuleControls RuleBelowControls { get; } = new();

    /// <summary>The boxes for one of a paragraph's rules: none or its length, then its weight, colour, offset and width.</summary>
    public sealed class RuleControls
    {
        public ComboBox Choice { get; } = new() { ItemsSource = new[] { "None", "Column", "Text", "Width" }, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        public TextBox Weight { get; } = Ui.Box("1pt");
        public TextBox Colour { get; } = Ui.Box("Black");
        public TextBox Offset { get; } = Ui.Box("3pt");
        public TextBox Width { get; } = Ui.Box("1in");
        public IEnumerable<TextBox> Boxes => [Weight, Colour, Offset, Width];
    }
    public CheckBox KeepWithNextBox { get; } = new() { Content = "Keep with the next paragraph", FontSize = 12 };
    public CheckBox KeepTogetherBox { get; } = new() { Content = "Keep its lines together", FontSize = 12 };
    public NumericUpDown OrphansBox { get; } = new() { Minimum = 1, Maximum = 9, Increment = 1, FormatString = "0", FontSize = 12 };
    public NumericUpDown WidowsBox { get; } = new() { Minimum = 1, Maximum = 9, Increment = 1, FormatString = "0", FontSize = 12 };
    public ComboBox CharStyleChoice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public TextBox CharStyleNameBox { get; } = Ui.Box("New character style's name");
    public TextBlock CharStyleInfo { get; } = new() { Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    // Page numbering and layers (Pages tab)
    public CheckBox SectionBox { get; } = new() { Content = "Start a section of numbering here", FontSize = 12 };
    public NumericUpDown SectionStartBox { get; } = new() { Minimum = 0, Maximum = 99999, Increment = 1, FormatString = "0", FontSize = 12 };
    public ComboBox SectionStyleChoice { get; } = new() { ItemsSource = new[] { "1, 2, 3", "i, ii, iii", "I, II, III", "a, b, c", "A, B, C" }, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public TextBox SectionPrefixBox { get; } = Ui.Box("such as A-");
    private readonly StackPanel _sectionRows = new();
    private readonly StackPanel _sectionBoxes = new();
    public ListBox LayerList { get; } = new() { MaxHeight = 200, FontSize = 12 };
    public TextBox LayerNameBox { get; } = Ui.Box("Layer's name");
    public CheckBox ArrowStartBox { get; } = new() { Content = "At its start", FontSize = 12 };
    public CheckBox ArrowEndBox { get; } = new() { Content = "At its end", FontSize = 12 };
    private readonly StackPanel _arrowRows = new();
    // Tables
    public NumericUpDown TableRowsBox { get; } = new() { Minimum = 1, Maximum = 200, Increment = 1, FormatString = "0", FontSize = 12 };
    public NumericUpDown TableColumnsBox { get; } = new() { Minimum = 1, Maximum = 50, Increment = 1, FormatString = "0", FontSize = 12 };
    public NumericUpDown HeaderRowsBox { get; } = new() { Minimum = 0, Maximum = 20, Increment = 1, FormatString = "0", FontSize = 12 };
    public TextBox HeaderFillBox { get; } = Ui.Box("none, a name or #RRGGBB");
    public TextBox CellInsetBox { get; } = Ui.Box();
    public TextBox ColumnWidthsBox { get; } = Ui.Box("1in, 2in, 1in");
    private readonly StackPanel _tableRows = new();
    public CheckBox WrapShapeBox { get; } = new() { Content = "Follow its shape, not its frame", FontSize = 12 };
    public TextBlock TextTarget { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _textBody = new();

    // Pages
    public ListBox PageList { get; } = new() { MaxHeight = 300, FontSize = 12 };
    public ComboBox MasterChoice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public ListBox MasterList { get; } = new() { MaxHeight = 160, FontSize = 12 };
    public TextBox MasterNameBox { get; } = Ui.Box("Master page's name");

    // Document
    public ComboBox PaperChoice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public ComboBox OrientationChoice { get; } = new() { ItemsSource = new[] { "Portrait", "Landscape" }, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public TextBox PageWidthBox { get; } = Ui.Box();
    public TextBox PageHeightBox { get; } = Ui.Box();
    public TextBox MarginTopBox { get; } = Ui.Box();
    public TextBox MarginBottomBox { get; } = Ui.Box();
    public TextBox MarginLeftBox { get; } = Ui.Box();
    public TextBox MarginRightBox { get; } = Ui.Box();
    public NumericUpDown PageColumnsBox { get; } = new() { Minimum = 1, Maximum = 12, Increment = 1, FormatString = "0", FontSize = 12 };
    public TextBox PageGutterBox { get; } = Ui.Box();
    public TextBox GridBox { get; } = Ui.Box();
    public CheckBox LinkedBox { get; } = new() { Content = "Linked to its file, not kept in the document", FontSize = 12 };
    public TextBlock LinkInfo { get; } = new() { Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    public TextBox BleedBox { get; } = Ui.Box("0, or such as 9pt");
    public TextBox DocTitleBox { get; } = Ui.Box("the file's name");
    public TextBox AuthorBox { get; } = Ui.Box("for the PDF");
    public TextBox BaselineBox { get; } = Ui.Box("none, or such as 14pt");
    public TextBox BaselineFromBox { get; } = Ui.Box("the top margin");
    public CheckBox OnGridBox { get; } = new() { Content = "Align to the baseline grid", FontSize = 12 };
    public CheckBox FacingBox { get; } = new() { Content = "Facing pages: left and right, in spreads", FontSize = 12 };
    public TextBlock MarginLeftLabel { get; } = Ui.Label("Left", 52);
    private readonly TextBlock _marginRightLabel = Ui.Label("Right", 52);

    // A master's own margins and columns
    public CheckBox OwnGuidesBox { get; } = new() { Content = "Its own margins and columns", FontSize = 12 };
    public TextBox MasterTopBox { get; } = Ui.Box();
    public TextBox MasterBottomBox { get; } = Ui.Box();
    public TextBox MasterInsideBox { get; } = Ui.Box();
    public TextBox MasterOutsideBox { get; } = Ui.Box();
    public NumericUpDown MasterColumnsBox { get; } = new() { Minimum = 1, Maximum = 12, Increment = 1, FormatString = "0", FontSize = 12 };
    public TextBox MasterGutterBox { get; } = Ui.Box();
    private readonly StackPanel _masterGuides = new();
    private readonly StackPanel _masterGuideBoxes = new();

    // Swatches
    public ListBox SwatchList { get; } = new() { MaxHeight = 220, FontSize = 12 };
    public TextBox SwatchNameBox { get; } = Ui.Box("Name");
    public TextBox SwatchValueBox { get; } = Ui.Box("#RRGGBB, or CMYK: 0 100 100 0");
    public CheckBox SpotBox { get; } = new() { Content = "Spot ink (a colour of its own on press)", FontSize = 12 };
    public TextBlock InksInfo { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    public ComboBox LanguageChoice { get; } = new() { ItemsSource = Speller.Languages.Select(l => l.Name).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };

    public ComboBox UnitChoice { get; } = new() { ItemsSource = Units.All.Select(Units.Name).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };

    // Drawn with the tab control's own template: themes look a control's template up by its exact type.
    protected override Type StyleKeyOverride => typeof(TabControl);

    public SidePanel(MainWindow window)
    {
        _window = window;
        RenameStyleButton = Ui.Button("Rename", RenameStyle, "Give the style in the list the name typed above; its paragraphs keep it");
        Padding = new Thickness(0);
        Items.Add(Tab("Object", BuildObject()));
        Items.Add(Tab("Text", BuildText()));
        Items.Add(Tab("Pages", BuildPages()));
        Items.Add(Tab("Document", BuildDocument()));
        SelectedIndex = 0;
    }

    private Editor Editor => _window.Editor;
    private PageView View => _window.View;
    private Document Doc => _window.Editor.Doc;
    private Unit Unit => Doc.Setup.Unit;

    private static TabItem Tab(string header, Control body) => new()
    {
        Header = new TextBlock { Text = header, FontSize = 13 },
        Content = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(14, 6, 14, 14), Child = body },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        },
    };

    private static Border Swatch() => new()
    {
        Width = 22,
        Height = 22,
        CornerRadius = new CornerRadius(3),
        BorderBrush = Look.Border,
        BorderThickness = new Thickness(1),
        Margin = new Thickness(6, 0, 0, 0),
    };

    private static ToggleButton Toggle(string text, FontWeight weight, FontStyle style = FontStyle.Normal) => new()
    {
        Content = new TextBlock { Text = text, FontWeight = weight, FontStyle = style, FontSize = 13, TextDecorations = text == "U" ? TextDecorations.Underline : null },
        Width = 34,
        MinHeight = 28,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 0, 4, 0),
    };

    private static Control WithSwatch(TextBox box, Border swatch)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(swatch, 1);
        g.Children.Add(box);
        g.Children.Add(swatch);
        return g;
    }

    // ---- Object -----------------------------------------------------------------------------------------------------

    private Control BuildObject()
    {
        var swatchMenu = _swatchButtons;
        // Through Bounds, so a group or path moves (and scales) what is in it.
        MeasureBox(XBox, (o, v) => o.Bounds = o.Bounds with { X = v }, "Move");
        MeasureBox(YBox, (o, v) => o.Bounds = o.Bounds with { Y = v }, "Move");
        MeasureBox(WBox, (o, v) => o.Bounds = o.Bounds with { W = Math.Max(o is LineObject or PathObject ? 0 : 1, v) }, "Resize");
        MeasureBox(HBox, (o, v) => o.Bounds = o.Bounds with { H = Math.Max(o is LineObject or PathObject ? 0 : 1, v) }, "Resize");
        Commit(RotationBox, text =>
        {
            if (View.Selected.Count == 0) return;
            var clean = text.Replace("°", "").Trim();
            if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
            {
                _window.Say("Type an angle in degrees, such as 15 or -90.");
                return;
            }
            var angle = PageView.Normalise(Math.Round(degrees, 2));
            ChangeSelected("Rotate", o => { if (o is not (LineObject or GroupObject)) o.Rotation = angle; });
        });
        MeasureBox(StrokeWidthBox, (o, v) => o.StrokeWidth = Math.Max(0, v), "Stroke width", remember: true);
        Commit(CornerBox, text =>
        {
            if (_refreshing || View.Selected.Count == 0) return;
            if (!Core.Corners.TryRead(text, Unit, out var sizes)) { _window.Say("Type a size for every corner, such as 6pt, or four: top left, top right, bottom right, bottom left."); Refresh(); return; }
            var style = CornerStyleChoice.SelectedIndex == 1 ? CornerStyle.Cut : CornerStyle.Round;
            ChangeSelected("Corners", o => { if (o is RectangleObject or TextFrame or PictureObject) o.Corners = new Core.Corners(sizes[0], sizes[1], sizes[2], sizes[3], style); });
        });
        CornerStyleChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || CornerStyleChoice.SelectedIndex < 0) return;
            var style = CornerStyleChoice.SelectedIndex == 1 ? CornerStyle.Cut : CornerStyle.Round;
            ChangeSelected("Corner style", o =>
            {
                if (o is not (RectangleObject or TextFrame or PictureObject)) return;
                var c = o.Corners ?? (o is RectangleObject r ? Core.Corners.All(r.CornerRadius) : Core.Corners.All(6));
                o.Corners = c with { Style = style };
            });
        };
        MeasureBox(GutterBox, (o, v) => { if (o is TextFrame f) f.Gutter = Math.Max(0, v); }, "Gutter");
        MeasureBox(InsetBox, (o, v) => { if (o is TextFrame f) f.Inset = Math.Max(0, v); }, "Inset");
        MeasureBox(OffsetXBox, (o, v) => { if (o is PictureObject p) p.OffsetX = v; }, "Crop");
        MeasureBox(TextOffsetBox, (o, v) => { if (o is PathObject p) p.TextOffset = v; }, "Move text along path");
        _pathTextRows.Children.AddRange([
            Ui.Heading("Text on the path"),
            Ui.Row(("Start", TextOffsetBox)),
            new TextBlock
            {
                Text = "How far along the path the text starts, from where its alignment (in the Text tab) puts it.",
                Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            },
            Ui.Buttons(
                Ui.Button("Edit text…", () => _window.OpenStoryEditor(), "Write the text in the story editor"),
                Ui.Button("Remove text", () => { if (View.Selected is [PathObject p]) Editor.TakeTextOffPath(p.Id); })),
        ]);
        MeasureBox(OffsetYBox, (o, v) => { if (o is PictureObject p) p.OffsetY = v; }, "Crop");
        Commit(FillBox, t => SetColour(false, t));
        Commit(StrokeBox, t => SetColour(true, t));
        ColumnsBox.ValueChanged += (_, _) =>
        {
            if (_refreshing || ColumnsBox.Value is not { } v) return;
            ChangeSelected("Columns", o => { if (o is TextFrame f) f.Columns = (int)v; });
        };
        FitChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || FitChoice.SelectedIndex < 0) return;
            var fit = (Fit)FitChoice.SelectedIndex;
            ChangeSelected("Picture fit", o => { if (o is PictureObject p) p.Fit = fit; });
        };
        WrapBox.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            var wrap = WrapBox.IsChecked == true;
            ChangeSelected(wrap ? "Text wrap" : "No text wrap", o => o.Wrap = wrap);
        };
        MeasureBox(WrapGapBox, (o, v) => o.WrapGap = Math.Max(0, v), "Standoff");
        WrapShapeBox.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            var shape = WrapShapeBox.IsChecked == true;
            ChangeSelected("Wrap by shape", o => o.WrapShape = shape);
        };
        LockedBox.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            var locked = LockedBox.IsChecked == true;
            ChangeSelected(locked ? "Lock" : "Unlock", o => o.Locked = locked, includeLocked: true);
        };

        _cornerRow.Children.Add(Ui.Row(("Corners", CornerBox)));
        _cornerRow.Children.Add(Ui.Row(("Style", CornerStyleChoice)));
        BuildEffects();
        _rotationRow.Children.Add(Ui.Row(("Rotation", RotationBox)));
        BuildTableRows();
        _arrowRows.Children.AddRange([Ui.Heading("Arrowheads"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { ArrowStartBox, ArrowEndBox } }]);
        ArrowStartBox.IsCheckedChanged += (_, _) => { if (!_refreshing) ChangeSelected("Arrowhead", o => { if (o is LineObject l) l.ArrowStart = ArrowStartBox.IsChecked == true; }); };
        ArrowEndBox.IsCheckedChanged += (_, _) => { if (!_refreshing) ChangeSelected("Arrowhead", o => { if (o is LineObject l) l.ArrowEnd = ArrowEndBox.IsChecked == true; }); };
        _frameRows.Children.AddRange([
            Ui.Heading("Text frame"),
            Ui.Row(("Columns", ColumnsBox)),
            Ui.Row(("Gutter", GutterBox)),
            Ui.Row(("Inset", InsetBox)),
            Ui.Buttons(
                Ui.Button("Unlink", () => _window.UnlinkSelected(), "Take this frame out of its chain; the text flows on without it"),
                Ui.Button("Link…", () => { View.Tool = Tool.Link; _window.Say("Click a text frame to link from, then the one the text flows into."); })),
        ]);
        PdfPageBox.ValueChanged += (_, _) =>
        {
            if (_refreshing || PdfPageBox.Value is not { } v) return;
            ChangeSelected("PDF page", o => { if (o is PictureObject pic) pic.Page = (int)v; });
        };
        _pdfRow.Children.Add(Ui.Row(("Page", new StackPanel { Orientation = Orientation.Horizontal, Children = { PdfPageBox, _pdfPages } })));
        _pictureRows.Children.AddRange([
            Ui.Heading("Picture"),
            _pdfRow,
            Ui.Row(("Fit", FitChoice)),
            Ui.Row(("Offset X", OffsetXBox)),
            Ui.Row(("Offset Y", OffsetYBox)),
            new TextBlock
            {
                Text = "Move the picture within its frame to crop a different part into view. Alt+drag it with Select, too.",
                Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            },
            Ui.Buttons(Ui.Button("Replace picture…", () => { if (View.Selected is [PictureObject p]) _ = View.ReplaceImage(p.Id); })),
            LinkedBox,
            LinkInfo,
            Ui.Buttons(Ui.Button("Update links", () => _window.UpdateLinks(), "Read every linked picture's file again")),
        ]);
        LinkedBox.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing || View.Selected is not [PictureObject p]) return;
            if (Editor.SetLinked(p.Id, LinkedBox.IsChecked == true) is { } why) { _window.Say(why); Refresh(); }
            else _window.Say(LinkedBox.IsChecked == true ? "Linked: the picture is read from its file, and follows it when it changes." : "Embedded: the document keeps a copy of the picture.");
        };

        ObjectStyleChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || ObjectStyleChoice.SelectedIndex < 0 || View.Selected.Count == 0) return;
            var name = ObjectStyleChoice.SelectedIndex == 0 ? null : (string)ObjectStyleChoice.SelectedItem!;
            if (Editor.ApplyObjectStyle(View.Selected.Where(o => !o.Locked).Select(o => o.Id), name) is { } why) _window.Say(why);
            Refresh();
        };
        _objectBody.Children.AddRange([
            Ui.Heading("Object style"),
            Ui.Row(("Style", ObjectStyleChoice)),
            ObjectStyleInfo,
            Ui.Row(("New", ObjectStyleNameBox)),
            Ui.Buttons(
                Ui.Button("Add from object", NewObjectStyle, "A new style of the selected object's fill, stroke, effects, corners and wrap"),
                Ui.Button("Redefine", RedefineObjectStyle, "Make the style match the selected object; every object in it changes"),
                Ui.Button("Rename", RenameObjectStyle),
                Ui.Button("Delete", DeleteObjectStyle)),
            Ui.Heading("Frame"),
            Ui.Row(("X", XBox), ("Y", YBox)),
            Ui.Row(("W", WBox), ("H", HBox)),
            _rotationRow,
            Ui.Heading("Colour"),
            Ui.Row(("Fill", WithSwatch(FillBox, _fillSwatch))),
            Ui.Row(("Stroke", WithSwatch(StrokeBox, _strokeSwatch))),
            swatchMenu,
            Ui.Row(("Width", StrokeWidthBox)),
            _cornerRow,
            _gradientRows,
            _shadowRows,
            _frameRows,
            _pictureRows,
            _pathTextRows,
            _arrowRows,
            _tableRows,
            Ui.Heading("Text wrap"),
            WrapBox,
            WrapShapeBox,
            Ui.Row(("Standoff", WrapGapBox)),
            Ui.Heading("Arrange"),
            Ui.Buttons(
                Ui.Button("Front", () => Arrange(Editor.Order.Front), "Bring to front (Ctrl+Shift+])"),
                Ui.Button("Forward", () => Arrange(Editor.Order.Forward), "Bring forward (Ctrl+])"),
                Ui.Button("Backward", () => Arrange(Editor.Order.Backward), "Send backward (Ctrl+[)"),
                Ui.Button("Back", () => Arrange(Editor.Order.Back), "Send to back (Ctrl+Shift+[)")),
            Ui.Heading("Align"),
            Ui.Buttons(
                Ui.Button("Left", () => AlignTo(Editor.Alignment.Left)),
                Ui.Button("Centre", () => AlignTo(Editor.Alignment.Centre)),
                Ui.Button("Right", () => AlignTo(Editor.Alignment.Right)),
                Ui.Button("Top", () => AlignTo(Editor.Alignment.Top)),
                Ui.Button("Middle", () => AlignTo(Editor.Alignment.Middle)),
                Ui.Button("Bottom", () => AlignTo(Editor.Alignment.Bottom))),
            Ui.Buttons(
                Ui.Button("Space across", () => Spread(true), "Space three or more objects evenly from left to right"),
                Ui.Button("Space down", () => Spread(false), "Space three or more objects evenly from top to bottom")),
            new TextBlock { Text = "One object aligns to the page; several, to each other.", Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap },
            new Border { Height = 8 },
            LockedBox,
        ]);
        return new StackPanel { Children = { ObjectTitle, _objectBody } };
    }

    /// <summary>The selected table, by its first frame (which holds its rows and cells) when a frame it runs on into is selected.</summary>
    private TableObject? SelectedTable => View.Selected is [TableObject t] ? Tables.Head(Doc, t) : null;

    private void BuildTableRows()
    {
        void Count(NumericUpDown box, bool rows) => box.ValueChanged += (_, _) =>
        {
            if (_refreshing || box.Value is not { } v || SelectedTable is not { } t) return;
            var want = (int)v;
            Editor.ChangeTable(t.Id, rows ? "Rows" : "Columns", (d, table) =>
            {
                // Rows and columns are put in, or taken out, at the end.
                while ((rows ? table.Rows : table.Columns) < want)
                    if (rows) Tables.InsertRow(d, table, table.Rows); else Tables.InsertColumn(d, table, table.Columns);
                while ((rows ? table.Rows : table.Columns) > want)
                    if (rows) Tables.DeleteRow(d, table, table.Rows - 1); else Tables.DeleteColumn(d, table, table.Columns - 1);
            });
        };
        Count(TableRowsBox, rows: true);
        Count(TableColumnsBox, rows: false);
        HeaderRowsBox.ValueChanged += (_, _) =>
        {
            if (_refreshing || HeaderRowsBox.Value is not { } v || SelectedTable is not { } t) return;
            Editor.ChangeTable(t.Id, "Header rows", (_, table) => table.HeaderRows = Math.Min((int)v, table.Rows));
        };
        Commit(HeaderFillBox, text =>
        {
            if (SelectedTable is not { } t) return;
            if (!Colours.TryRead(text, Doc.Swatches, out var c)) { _window.Say("Type a colour's name, a value like #D0D0D0, or none."); return; }
            Editor.ChangeTable(t.Id, "Header fill", (_, table) => table.HeaderFill = c);
        });
        Commit(CellInsetBox, text =>
        {
            if (SelectedTable is not { } t) return;
            if (!Units.TryParse(text, Unit, out var v)) { _window.Say("Type a measure like 4, 0.05in or 1mm."); Refresh(); return; }
            Editor.ChangeTable(t.Id, "Cell inset", (_, table) => table.CellInset = Math.Max(0, v));
        });
        Commit(ColumnWidthsBox, text =>
        {
            if (SelectedTable is not { } t) return;
            var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var widths = new List<double>();
            foreach (var part in parts)
            {
                if (!Units.TryParse(part, Unit, out var w) || w < 4) { _window.Say($"\"{part}\" isn't a column width. Type one for each column: 1in, 2in, 1in."); return; }
                widths.Add(w);
            }
            if (widths.Count != t.Columns) { _window.Say($"The table has {t.Columns} columns: type a width for each."); return; }
            Editor.ChangeTable(t.Id, "Column widths", (_, table) => { table.ColumnWidths = widths; table.Fit(); });
        });
        _tableRows.Children.AddRange([
            Ui.Heading("Table"),
            Ui.Row(("Rows", TableRowsBox), ("Cols", TableColumnsBox)),
            Ui.Row(("Header rows", HeaderRowsBox)),
            Ui.Row(("Header fill", HeaderFillBox)),
            Ui.Row(("Cell inset", CellInsetBox)),
            Ui.Row(("Widths", ColumnWidthsBox)),
            Ui.Buttons(
                Ui.Button("Fit rows to text", () =>
                {
                    if (SelectedTable is { } t) Editor.ChangeTable(t.Id, "Fit rows", (d, table) => Tables.FitRows(d, table, FontMeasurer.Shared));
                }, "Make each row as tall as its tallest cell's text needs"),
                Ui.Button("Even columns", () =>
                {
                    if (SelectedTable is { } t) Editor.ChangeTable(t.Id, "Even columns", (_, table) => table.ColumnWidths = Enumerable.Repeat(table.W / table.Columns, table.Columns).ToList());
                })),
            Ui.Buttons(Ui.Button("Run on to the next page", () => _window.RunOnTable(), "Carry the rows that don't fit on to a new frame on the next page, under the header rows again")),
            new TextBlock
            {
                Text = "Double-click a cell to type in it; Tab goes to the next (and adds a row after the last). Object › Table puts rows and columns in where the caret is.",
                Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            },
        ]);
    }

    private void Arrange(Editor.Order order)
    {
        if (View.SelectedIds.Count > 0) Editor.Arrange(View.SelectedIds.ToList(), order);
    }

    private void Spread(bool across)
    {
        if (Editor.Distribute(View.SelectedIds.ToList(), across) is { } why) _window.Say(why);
    }

    private void AlignTo(Editor.Alignment how)
    {
        if (View.SelectedIds.Count > 0) Editor.AlignObjects(View.SelectedIds.ToList(), how);
    }

    /// <summary>A box for a measure of the selected objects, read in the document's unit.</summary>
    private void MeasureBox(TextBox box, Action<PageObject, double> set, string label, bool remember = false) =>
        Commit(box, text =>
        {
            if (_refreshing || View.Selected.Count == 0) return;
            if (!Units.TryParse(text, Unit, out var v))
            {
                _window.Say("Type a measure like 54, 0.75in, 4p6 or 20mm.");
                Refresh();
                return;
            }
            ChangeSelected(label, o => set(o, v));
            if (remember) Remember();
        });

    /// <summary>A per cent as typed ("60", "60%"), as a fraction; null if it isn't one from 0 to 100.</summary>
    private static double? Percent(string? text) =>
        double.TryParse((text ?? "").Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 100 ? v / 100 : null;

    private static string Percent(double v) => $"{Math.Round(v * 100):0}%";

    /// <summary>The Effects section: gradient fills, opacity and blending, shadows and glows.</summary>
    private void BuildEffects()
    {
        void Gradient()
        {
            if (_refreshing || View.Selected.Count == 0) return;
            if (FillKindChoice.SelectedIndex <= 0) { ChangeSelected("Fill colour", o => o.Gradient = null); Refresh(); return; }
            var text = string.IsNullOrWhiteSpace(GradientStopsBox.Text) ? $"{FillBox.Text ?? "White"}, White" : GradientStopsBox.Text;
            if (Core.Gradient.ReadStops(text, Doc.Swatches, out var stops) is { } why) { _window.Say(why); return; }
            var angle = double.TryParse((GradientAngleBox.Text ?? "0").Trim().TrimEnd('°'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0;
            var radial = FillKindChoice.SelectedIndex == 2;
            ChangeSelected("Gradient", o => o.Gradient = (o.Gradient ?? new Core.Gradient(radial, stops)) with { Radial = radial, Stops = stops, Angle = angle });
            Refresh();
        }
        FillKindChoice.SelectionChanged += (_, _) => Gradient();
        Commit(GradientStopsBox, _ => Gradient());
        Commit(GradientAngleBox, _ => Gradient());
        void Opacity(TextBox box, string label, Action<PageObject, double> set) => Commit(box, text =>
        {
            if (_refreshing || View.Selected.Count == 0) return;
            if (Percent(text) is not { } v) { _window.Say("Type how opaque, from 0% (clear) to 100% (solid)."); Refresh(); return; }
            ChangeSelected(label, o => set(o, v));
        });
        Opacity(OpacityBox, "Opacity", (o, v) => o.Opacity = v);
        Opacity(FillOpacityBox, "Fill opacity", (o, v) => o.FillOpacity = v);
        Opacity(StrokeOpacityBox, "Stroke opacity", (o, v) => o.StrokeOpacity = v);
        BlendChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || BlendChoice.SelectedIndex < 0) return;
            var mode = (Core.BlendMode)BlendChoice.SelectedIndex;
            ChangeSelected("Blend mode", o => o.Blend = mode);
        };
        void ShadowChanged()
        {
            if (_refreshing || View.Selected.Count == 0) return;
            if (ShadowChoice.SelectedIndex <= 0) { ChangeSelected("No shadow", o => o.Shadow = null); Refresh(); return; }
            if (!Colours.TryRead(ShadowColourBox.Text, Doc.Swatches, out var colour) || colour is null) { _window.Say("Type the shadow's colour: a swatch's name or a value."); return; }
            double Measure(TextBox b, double otherwise) => Units.TryParse(b.Text, Unit, out var v) ? v : otherwise;
            var glow = ShadowChoice.SelectedIndex == 2;
            var shadow = new Shadow(colour, Percent(ShadowOpacityBox.Text) ?? 0.5, Measure(ShadowXBox, glow ? 0 : 4), Measure(ShadowYBox, glow ? 0 : 4), Math.Max(0, Measure(ShadowBlurBox, glow ? 8 : 4)), glow);
            ChangeSelected(glow ? "Glow" : "Drop shadow", o => o.Shadow = shadow);
            Refresh();
        }
        ShadowChoice.SelectionChanged += (_, _) => ShadowChanged();
        foreach (var b in new[] { ShadowColourBox, ShadowOpacityBox, ShadowXBox, ShadowYBox, ShadowBlurBox }) Commit(b, _ => ShadowChanged());
        _gradientRows.Children.AddRange([
            Ui.Row(("Fill with", FillKindChoice)),
            Ui.Row(("Colours", GradientStopsBox)),
            Ui.Row(("Angle", GradientAngleBox)),
            Ui.Heading("Effects"),
            Ui.Row(("Opacity", OpacityBox)),
            Ui.Row(("Fill", FillOpacityBox), ("Stroke", StrokeOpacityBox)),
            Ui.Row(("Blend", BlendChoice)),
        ]);
        _shadowRows.Children.AddRange([
            Ui.Row(("Shadow", ShadowChoice)),
            Ui.Row(("Colour", ShadowColourBox), ("Opacity", ShadowOpacityBox)),
            Ui.Row(("Across", ShadowXBox), ("Down", ShadowYBox)),
            Ui.Row(("Blur", ShadowBlurBox)),
        ]);
    }

    private void RefreshEffects(PageObject o)
    {
        FillKindChoice.SelectedIndex = o.Gradient is null ? 0 : o.Gradient.Radial ? 2 : 1;
        Set(GradientStopsBox, o.Gradient?.Describe(Doc.Swatches) ?? "");
        Set(GradientAngleBox, o.Gradient is { } g ? $"{g.Angle:0.#}°" : "");
        GradientStopsBox.IsEnabled = GradientAngleBox.IsEnabled = o.Gradient is not null;
        Set(OpacityBox, Percent(o.Opacity));
        Set(FillOpacityBox, Percent(o.FillOpacity));
        Set(StrokeOpacityBox, Percent(o.StrokeOpacity));
        BlendChoice.SelectedIndex = (int)o.Blend;
        ShadowChoice.SelectedIndex = o.Shadow is null ? 0 : o.Shadow.Glow ? 2 : 1;
        var s = o.Shadow ?? new Shadow();
        Set(ShadowColourBox, Colours.Describe(s.Colour, Doc.Swatches));
        Set(ShadowOpacityBox, Percent(s.Opacity));
        Set(ShadowXBox, Units.Format(s.X, Unit));
        Set(ShadowYBox, Units.Format(s.Y, Unit));
        Set(ShadowBlurBox, Units.Format(s.Blur, Unit));
    }

    private void ChangeSelected(string label, Action<PageObject> change, bool includeLocked = false)
    {
        var ids = View.Selected.Where(o => includeLocked || !o.Locked).Select(o => o.Id).ToList();
        if (ids.Count == 0)
        {
            if (View.Selected.Count > 0) _window.Say("That object is locked. Unlock it to change it.");
            return;
        }
        Editor.Change(label, d => { foreach (var id in ids) if (d.Find(id) is { } o) change(o); });
    }

    private void SetColour(bool stroke, string text)
    {
        if (_refreshing || View.Selected.Count == 0) return;
        if (!Colours.TryRead(text, Doc.Swatches, out var colour))
        {
            _window.Say("Type a colour's name (such as Red), a value like #2050D0, or none.");
            Refresh();
            return;
        }
        ChangeSelected(stroke ? "Stroke colour" : "Fill colour", o => { if (stroke) o.Stroke = colour; else o.Fill = colour; });
        Remember();
    }

    /// <summary>Makes the selected object's fill and stroke the ones the next object of its kind is drawn with.</summary>
    private void Remember()
    {
        if (View.Selected is [var o]) View.NewStyles[o.GetType()] = (o.Fill, o.Stroke, o.StrokeWidth);
    }

    // ---- Text -------------------------------------------------------------------------------------------------------

    private Control BuildText()
    {
        FontChoice.ItemsSource = FontSupport.Available();
        StyleChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || StyleChoice.SelectedItem is not string name) return;
            View.ApplyStyle(name);
        };
        FontChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || FontChoice.SelectedItem is not string font) return;
            View.ApplyChar(f => f with { Font = font }, "Font");
        };
        Commit(SizeBox, t =>
        {
            if (!Units.TryParse(t, Unit.Point, out var size) || size < 1 || size > 1296)
            {
                _window.Say("Type a size in points, from 1 to 1296.");
                Refresh();
                return;
            }
            View.ApplyChar(f => f with { Size = Math.Round(size, 2) }, "Size");
        });
        BoldToggle.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyChar(f => f with { Bold = BoldToggle.IsChecked == true }, "Bold"); };
        ItalicToggle.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyChar(f => f with { Italic = ItalicToggle.IsChecked == true }, "Italic"); };
        UnderlineToggle.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyChar(f => f with { Underline = UnderlineToggle.IsChecked == true }, "Underline"); };
        SmallCapsToggle.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyChar(f => f with { SmallCaps = SmallCapsToggle.IsChecked == true }, "Small caps"); };
        SuperToggle.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyChar(f => f with { Script = SuperToggle.IsChecked == true ? Script.Super : Script.Normal }, "Superscript"); };
        SubToggle.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyChar(f => f with { Script = SubToggle.IsChecked == true ? Script.Sub : Script.Normal }, "Subscript"); };
        ToolTip.SetTip(SmallCapsToggle, "Small caps");
        ToolTip.SetTip(SuperToggle, "Superscript");
        ToolTip.SetTip(SubToggle, "Subscript");
        Commit(TextColourBox, t =>
        {
            if (!Colours.TryRead(t, Doc.Swatches, out var c) || c is null)
            {
                _window.Say("Type a colour's name (such as Blue) or a value like #2050D0.");
                Refresh();
                return;
            }
            View.ApplyChar(f => f with { Colour = c }, "Text colour");
        });
        Commit(TrackingBox, t =>
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < -500 || v > 2000)
            {
                _window.Say("Tracking is in thousandths of an em, from -500 to 2000.");
                Refresh();
                return;
            }
            View.ApplyChar(f => f with { Tracking = v }, "Tracking");
        });

        var aligns = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (align, label) in new[] { (Align.Left, "Left"), (Align.Centre, "Centre"), (Align.Right, "Right"), (Align.Justify, "Justify") })
        {
            var b = new ToggleButton { Content = label, FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 0) };
            b.IsCheckedChanged += (_, _) =>
            {
                if (_refreshing || b.IsChecked != true) { if (!_refreshing && b.IsChecked == false) Refresh(); return; }
                View.ApplyPara(p => p with { Align = align }, "Alignment");
            };
            AlignButtons[align] = b;
            aligns.Children.Add(b);
        }

        ParaBox(LeadingBox, (p, v) => p with { Leading = v }, "Leading", allowAuto: true);
        ParaBox(LeftIndentBox, (p, v) => p with { LeftIndent = v }, "Left indent");
        ParaBox(RightIndentBox, (p, v) => p with { RightIndent = v }, "Right indent");
        ParaBox(FirstIndentBox, (p, v) => p with { FirstIndent = v }, "First-line indent", negative: true);
        ParaBox(SpaceBeforeBox, (p, v) => p with { SpaceBefore = v }, "Space before");
        ParaBox(SpaceAfterBox, (p, v) => p with { SpaceAfter = v }, "Space after");

        Commit(TabsBox, text =>
        {
            if (!Tabs.TryRead(text, Unit, out var stored, out var why)) { _window.Say(why!); return; }
            View.ApplyPara(p => p with { Tabs = stored }, "Tabs");
        });
        DropLinesBox.ValueChanged += (_, _) =>
        {
            if (_refreshing || DropLinesBox.Value is not { } v) return;
            View.ApplyPara(p => p with { DropLines = v < 2 ? 0 : (int)v }, "Drop cap");
        };
        DropCharsBox.ValueChanged += (_, _) =>
        {
            if (_refreshing || DropCharsBox.Value is not { } v) return;
            View.ApplyPara(p => p with { DropChars = (int)v }, "Drop cap");
        };
        CharStyleChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || CharStyleChoice.SelectedIndex < 0 || View.Editing is not { Length: > 0 } t) return;
            var name = CharStyleChoice.SelectedIndex == 0 ? null : (string)CharStyleChoice.SelectedItem!;
            Editor.ApplyCharStyle(t.StoryId, t.Start, t.Length, name);
        };
        KeepWithNextBox.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyPara(p => p with { KeepWithNext = KeepWithNextBox.IsChecked == true }, "Keep with next"); };
        KeepTogetherBox.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyPara(p => p with { KeepTogether = KeepTogetherBox.IsChecked == true }, "Keep lines together"); };
        OrphansBox.ValueChanged += (_, _) => { if (!_refreshing && OrphansBox.Value is { } v) View.ApplyPara(p => p with { Orphans = (int)v }, "Orphan control"); };
        WidowsBox.ValueChanged += (_, _) => { if (!_refreshing && WidowsBox.Value is { } v) View.ApplyPara(p => p with { Widows = (int)v }, "Widow control"); };
        OnGridBox.IsCheckedChanged += (_, _) => { if (!_refreshing) View.ApplyPara(p => p with { OnGrid = OnGridBox.IsChecked == true }, "Baseline grid"); };
        void RuleChanged(RuleControls rc, bool above)
        {
            if (_refreshing) return;
            ParaRule? rule = null;
            if (rc.Choice.SelectedIndex > 0)
            {
                if (!Colours.TryRead(rc.Colour.Text, Doc.Swatches, out var colour) || colour is null) { _window.Say("Type the rule's colour: a swatch's name or a value."); return; }
                double Measure(TextBox b, double otherwise) => Units.TryParse(b.Text, Unit, out var v) ? v : otherwise;
                var length = rc.Choice.SelectedIndex switch { 2 => RuleLength.Text, 3 => RuleLength.Width, _ => RuleLength.Column };
                rule = new ParaRule(Math.Max(0.1, Measure(rc.Weight, 1)), colour, length, Math.Max(1, Measure(rc.Width, 72)), Measure(rc.Offset, 3));
            }
            View.ApplyPara(p => above ? p with { RuleAbove = rule } : p with { RuleBelow = rule }, above ? "Rule above" : "Rule below");
            Refresh();
        }
        foreach (var (rc, above) in new[] { (RuleAboveControls, true), (RuleBelowControls, false) })
        {
            rc.Choice.SelectionChanged += (_, _) => RuleChanged(rc, above);
            foreach (var b in rc.Boxes) Commit(b, _ => RuleChanged(rc, above));
        }
        HyphenateBox.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            View.ApplyPara(p => p with { Hyphenate = HyphenateBox.IsChecked == true }, "Hyphenation");
        };
        _textBody.Children.AddRange([
            Ui.Heading("Paragraph style"),
            Ui.Row(("Style", StyleChoice)),
            Ui.Row(("New", StyleNameBox)),
            Ui.Buttons(
                Ui.Button("Add from text", NewStyle, "A new style with the settings of the paragraph the caret is in"),
                Ui.Button("Redefine", RedefineStyle, "Make the style match this paragraph; every paragraph in it changes"),
                RenameStyleButton,
                Ui.Button("Delete", DeleteStyle)),
            Ui.Heading("Character style"),
            Ui.Row(("Style", CharStyleChoice)),
            CharStyleInfo,
            Ui.Row(("New", CharStyleNameBox)),
            Ui.Buttons(
                Ui.Button("Add from text", NewCharStyle, "A character style of how the selected text differs from its paragraph's style"),
                Ui.Button("Redefine", RedefineCharStyle, "Make the character style match the selected text; all its text changes"),
                Ui.Button("Rename", RenameCharStyle),
                Ui.Button("Delete", DeleteCharStyle)),
            Ui.Heading("Character"),
            Ui.Row(("Font", FontChoice)),
            Ui.Row(("Size", SizeBox)),
            Ui.Row(("Style", new StackPanel { Orientation = Orientation.Horizontal, Children = { BoldToggle, ItalicToggle, UnderlineToggle, SmallCapsToggle, SuperToggle, SubToggle } })),
            Ui.Row(("Colour", TextColourBox)),
            Ui.Row(("Tracking", TrackingBox)),
            Ui.Heading("Insert"),
            Ui.Buttons(
                Ui.Button("Page number", () => View.InsertField(Fields.PageNumber), "The number of the page it is on; on a master page, of each page using it"),
                Ui.Button("Page count", () => View.InsertField(Fields.PageCount), "How many pages the document has")),
            Ui.Heading("Paragraph"),
            Ui.Row(("Align", aligns)),
            Ui.Row(("Leading", LeadingBox)),
            Ui.Row(("Left", LeftIndentBox), ("Right", RightIndentBox)),
            Ui.Row(("First line", FirstIndentBox)),
            Ui.Row(("Before", SpaceBeforeBox), ("After", SpaceAfterBox)),
            Ui.Row(("Tabs", TabsBox)),
            Ui.Row(("Drop cap", DropLinesBox)),
            Ui.Row(("Letters", DropCharsBox)),
            HyphenateBox,
            OnGridBox,
            KeepWithNextBox,
            KeepTogetherBox,
            Ui.Row(("Orphans", OrphansBox)),
            Ui.Row(("Widows", WidowsBox)),
            Ui.Heading("Rules"),
            Ui.Row(("Above", RuleAboveControls.Choice)),
            Ui.Row(("Weight", RuleAboveControls.Weight), ("Colour", RuleAboveControls.Colour)),
            Ui.Row(("Offset", RuleAboveControls.Offset), ("Width", RuleAboveControls.Width)),
            Ui.Row(("Below", RuleBelowControls.Choice)),
            Ui.Row(("Weight", RuleBelowControls.Weight), ("Colour", RuleBelowControls.Colour)),
            Ui.Row(("Offset", RuleBelowControls.Offset), ("Width", RuleBelowControls.Width)),
            new TextBlock
            {
                Text = "Tabs: a measure, then left, right, centre or decimal, then a leader, such as 4in right . — Rules: the offset is from the top of the first line, or the foot of the last; leave room with space before or after. — Kern: Alt+Left and Alt+Right in the text.",
                Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4),
            },
        ]);
        return new StackPanel { Children = { TextTarget, _textBody } };
    }

    private void ParaBox(TextBox box, Func<ParaFormat, double, ParaFormat> set, string label, bool allowAuto = false, bool negative = false) =>
        Commit(box, text =>
        {
            if (_refreshing) return;
            double v;
            if (allowAuto && (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))) v = 0;
            else if (!Units.TryParse(text, Unit, out v) || (!negative && v < 0))
            {
                _window.Say(allowAuto ? "Type a leading like 14pt, or auto." : "Type a measure like 12pt, 0.25in or 1p6.");
                Refresh();
                return;
            }
            View.ApplyPara(p => set(p, v), label);
        });

    private CharFormat? ParagraphBasis()
    {
        if (View.Editing is not { } t || !Doc.Stories.TryGetValue(t.StoryId, out var story)) return null;
        return Doc.Style(story.Paras[story.ParagraphAt(t.Start)].Style).Char;
    }

    private void NewCharStyle()
    {
        var name = CharStyleNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { _window.Say("Type the new character style's name first."); return; }
        if (Doc.CharStyle(name) is not null) { _window.Say($"There is already a character style called {name}. Use Redefine to change it."); return; }
        if (View.Editing is not { Length: > 0 } t || View.CurrentChar() is not { } c || ParagraphBasis() is not { } basis)
        { _window.Say("Select some text formatted as the character style should be."); return; }
        var set = CharOverrides.Difference(basis, c with { CharStyle = null });
        _window.Say(Editor.DefineCharStyle(new CharacterStyle(name, set)) ?? $"Made the character style {name}: {set.Describe()}.");
        Editor.ApplyCharStyle(t.StoryId, t.Start, t.Length, name);
        CharStyleNameBox.Text = "";
    }

    private void RedefineCharStyle()
    {
        if (View.CurrentChar() is not { CharStyle: { } name } c || ParagraphBasis() is not { } basis)
        { _window.Say("Select text in the character style, formatted as the style should be."); return; }
        var set = CharOverrides.Difference(basis, c with { CharStyle = null });
        _window.Say(Editor.DefineCharStyle(new CharacterStyle(name, set)) ?? $"Redefined {name}: {set.Describe()}.");
    }

    private void RenameCharStyle()
    {
        if (CharStyleChoice.SelectedIndex <= 0) { _window.Say("Pick the character style to rename first."); return; }
        var old = (string)CharStyleChoice.SelectedItem!;
        _window.Say(Editor.RenameCharStyle(old, CharStyleNameBox.Text ?? "") ?? $"Renamed the character style {old}.");
    }

    private void DeleteCharStyle()
    {
        if (CharStyleChoice.SelectedIndex <= 0) { _window.Say("Pick the character style to delete first."); return; }
        var name = (string)CharStyleChoice.SelectedItem!;
        _window.Say(Editor.DeleteCharStyle(name) ?? $"Deleted {name}; its text has its paragraph's style again.");
    }

    private void RefreshObjectStyle(IReadOnlyList<PageObject> selected)
    {
        ObjectStyleChoice.ItemsSource = Doc.ObjectStyles.Select(s => s.Name).Prepend("(none)").ToList();
        var names = selected.Select(o => o.ObjectStyle).Distinct().ToList();
        var name = names.Count == 1 ? names[0] : null;
        ObjectStyleChoice.SelectedIndex = names.Count != 1 ? -1 : name is null ? 0 : Math.Max(0, Doc.ObjectStyles.FindIndex(s => s.Name == name) + 1);
        ObjectStyleInfo.Text = name is not null && Doc.ObjectStyle(name) is { } style
            ? $"{name}: {style.Describe(Doc.Swatches)}." + (selected.All(style.Matches) ? "" : " Changed since: pick the style again to put it back, or Redefine to make the style match.")
            : Doc.ObjectStyles.Count == 0 ? "Style an object, then type a name and Add from object to keep its look as a style." : "";
    }

    internal void NewObjectStyle()
    {
        var name = ObjectStyleNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { _window.Say("Type the new object style's name first."); return; }
        if (Doc.ObjectStyle(name) is not null) { _window.Say($"There is already an object style called {name}. Use Redefine to change it."); return; }
        if (View.Selected.FirstOrDefault() is not { } o) { _window.Say("Select an object to take the style from."); return; }
        if (Editor.DefineObjectStyle(ObjectStyle.From(name, o), View.Selected.Where(s => !s.Locked).Select(s => s.Id)) is { } why) { _window.Say(why); return; }
        ObjectStyleNameBox.Text = "";
        _window.Say($"Made the object style {name}.");
        Refresh();
    }

    internal void RedefineObjectStyle()
    {
        if (View.Selected.FirstOrDefault() is not { } o) { _window.Say("Select an object to take the style from."); return; }
        var name = o.ObjectStyle ?? ObjectStyleChoice.SelectedItem as string;
        if (name is null || Doc.ObjectStyle(name) is null) { _window.Say("Select an object in the style to redefine it from."); return; }
        Editor.DefineObjectStyle(ObjectStyle.From(name, o));
        _window.Say($"Redefined {name}: every object in it now matches.");
        Refresh();
    }

    private void RenameObjectStyle()
    {
        if (ObjectStyleChoice.SelectedItem is not string old || ObjectStyleChoice.SelectedIndex == 0) { _window.Say("Pick the object style to rename first."); return; }
        var name = ObjectStyleNameBox.Text?.Trim() ?? "";
        if (Editor.RenameObjectStyle(old, name) is { } why) { _window.Say(why); return; }
        ObjectStyleNameBox.Text = "";
        _window.Say($"Renamed the object style {old} to {name}.");
        Refresh();
    }

    private void DeleteObjectStyle()
    {
        if (ObjectStyleChoice.SelectedItem is not string name || ObjectStyleChoice.SelectedIndex == 0) return;
        _window.Say(Editor.DeleteObjectStyle(name) ?? $"Deleted the object style {name}. Its objects keep their look.");
        Refresh();
    }

    internal void NewStyle()
    {
        var name = StyleNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { _window.Say("Type the new style's name first."); return; }
        if (Doc.Styles.Any(s => s.Name == name)) { _window.Say($"There is already a style called {name}. Use Redefine to change it."); return; }
        if (View.CurrentChar() is not { } c || View.CurrentPara() is not { } p) { _window.Say("Put the caret in some text, or select a text frame, to take a style from."); return; }
        Editor.DefineStyle(new ParagraphStyle(name, c, p with { Style = name }));
        View.ApplyStyle(name);
        StyleNameBox.Text = "";
        _window.Say($"Made the style {name}.");
    }

    private void RedefineStyle()
    {
        if (View.CurrentChar() is not { } c || View.CurrentPara() is not { } p) { _window.Say("Put the caret in a paragraph of the style first."); return; }
        Editor.DefineStyle(new ParagraphStyle(p.Style, c, p));
        _window.Say($"Redefined {p.Style}: every paragraph in it now matches.");
    }

    private void RenameStyle()
    {
        if (StyleChoice.SelectedItem is not string old) { _window.Say("Pick the style to rename first."); return; }
        var name = StyleNameBox.Text?.Trim() ?? "";
        if (Editor.RenameStyle(old, name) is { } why) { _window.Say(why); return; }
        StyleNameBox.Text = "";
        _window.Say($"Renamed the style {old} to {name}.");
    }

    private void DeleteStyle()
    {
        if (StyleChoice.SelectedItem is not string name) return;
        _window.Say(Editor.DeleteStyle(name) ?? $"Deleted the style {name}. Its paragraphs keep their look.");
    }

    // ---- Pages ------------------------------------------------------------------------------------------------------

    private Control BuildPages()
    {
        PageList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed) return;
            _dragPage = PageIndexAt(e);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        PageList.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_dragPage is null) return;
            PageList.Cursor = PageIndexAt(e) is { } over && over != _dragPage ? new Cursor(StandardCursorType.DragMove) : null;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        PageList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            var from = _dragPage;
            _dragPage = null;
            PageList.Cursor = null;
            if (from is { } f && PageIndexAt(e) is { } to && to != f) DropPage(f, to);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        PageList.SelectionChanged += (_, _) =>
        {
            if (_refreshing || PageList.SelectedIndex < 0) return;
            _window.ShowSheet(SheetRef.PageAt(PageList.SelectedIndex));
        };
        MasterList.SelectionChanged += (_, _) =>
        {
            if (_refreshing || MasterList.SelectedIndex < 0) return;
            _window.ShowSheet(SheetRef.MasterAt(MasterList.SelectedIndex));
        };
        BuildMasterGuides();
        BuildSections();
        LayerList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Layer>((layer, _) =>
        {
            if (layer is null) return new TextBlock();
            CheckBox Tick(string label, bool on, Func<Layer, bool, Layer> set)
            {
                var b = new CheckBox { Content = label, IsChecked = on, FontSize = 11, MinWidth = 0, Margin = new Thickness(2, 0, 0, 0) };
                b.IsCheckedChanged += (_, _) => { if (!_refreshing) Editor.SetLayer(layer.Name, l => set(l, b.IsChecked == true)); };
                return b;
            }
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = layer.Name, Foreground = Look.Text, Width = 72, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                    Tick("Shown", layer.Visible, (l, v) => l with { Visible = v }),
                    Tick("Locked", layer.Locked, (l, v) => l with { Locked = v }),
                    Tick("Prints", layer.Print, (l, v) => l with { Print = v }),
                },
            };
        });
        LayerList.SelectionChanged += (_, _) =>
        {
            if (_refreshing || SelectedLayer is not { } l) return;
            Editor.CurrentLayer = l;
            LayerNameBox.Text = l;
            _window.Say($"New objects go on {l}.");
        };
        MasterChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || MasterChoice.SelectedIndex < 0 || View.Sheet.IsMaster) return;
            var index = View.Sheet.Index;
            var master = MasterChoice.SelectedIndex == 0 ? null : Doc.Masters[MasterChoice.SelectedIndex - 1].Name;
            Editor.Change("Page's master", d => d.Pages[index].Master = master);
        };
        return new StackPanel
        {
            Children =
            {
                Ui.Heading("Pages"),
                PageList,
                Ui.Buttons(
                    Ui.Button("Add before", () => _window.AddPage(after: false)),
                    Ui.Button("Add after", () => _window.AddPage(after: true)),
                    Ui.Button("Delete", _window.DeletePage),
                    Ui.Button("Move up", () => MovePage(-1)),
                    Ui.Button("Move down", () => MovePage(1))),
                Ui.Row(("Master", MasterChoice)),
                _sectionRows,
                Ui.Heading("Master pages"),
                MasterList,
                Ui.Row(("Name", MasterNameBox)),
                _masterGuides,
                Ui.Heading("Layers"),
                LayerList,
                Ui.Row(("Name", LayerNameBox)),
                Ui.Buttons(
                    Ui.Button("Add", () => { Editor.AddLayer(LayerNameBox.Text); LayerNameBox.Text = ""; Refresh(); }, "A new layer above the one picked; new objects go on it"),
                    Ui.Button("Rename", () => { if (SelectedLayer is { } l) _window.Say(Editor.RenameLayer(l, LayerNameBox.Text ?? "") ?? "Renamed the layer."); }),
                    Ui.Button("Delete", () => { if (SelectedLayer is { } l) _window.Say(Editor.DeleteLayer(l) ?? $"Deleted {l}; what was on it is on the layer below now."); }),
                    Ui.Button("Up", () => { if (SelectedLayer is { } l) Editor.MoveLayer(l, 1); }, "Draw this layer above the next"),
                    Ui.Button("Down", () => { if (SelectedLayer is { } l) Editor.MoveLayer(l, -1); }),
                    Ui.Button("Move selection here", () =>
                    {
                        if (SelectedLayer is not { } l || View.SelectedIds.Count == 0) { _window.Say("Select objects, and pick the layer to move them to."); return; }
                        Editor.MoveToLayer(View.SelectedIds.ToList(), l);
                        _window.Say($"Moved to {l}.");
                    })),
                Ui.Buttons(
                    Ui.Button("Rename", RenameMaster),
                    Ui.Button("Add master", () => { var name = Editor.AddMaster(); _window.ShowSheet(SheetRef.MasterAt(Doc.Masters.FindIndex(m => m.Name == name))); }),
                    Ui.Button("Back to pages", () => _window.ShowSheet(SheetRef.PageAt(0)))),
                new TextBlock
                {
                    Text = "A master's objects are drawn under every page that uses it. Pick a master above to edit it.",
                    Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4),
                },
            },
        };
    }

    private int? _dragPage;

    /// <summary>The page whose row in the list is under the pointer.</summary>
    private int? PageIndexAt(PointerEventArgs e)
    {
        for (var i = 0; i < Doc.Pages.Count; i++)
            if (PageList.ContainerFromIndex(i) is Control row && new Rect(row.Bounds.Size).Contains(e.GetPosition(row))) return i;
        return null;
    }

    /// <summary>Moves a page dropped on another page's row to that place, and shows it.</summary>
    public void DropPage(int from, int to)
    {
        Editor.MovePage(from, to);
        _window.ShowSheet(SheetRef.PageAt(to));
        _window.Say($"Moved page {from + 1} to page {to + 1}.");
    }

    private string? SelectedLayer => (LayerList.SelectedItem as Layer)?.Name;

    private void BuildSections()
    {
        _sectionBoxes.Children.AddRange([
            Ui.Row(("Starts at", SectionStartBox)),
            Ui.Row(("Numbers", SectionStyleChoice)),
            Ui.Row(("Prefix", SectionPrefixBox)),
        ]);
        void Apply()
        {
            if (_refreshing || View.Sheet.IsMaster) return;
            var page = View.Sheet.Index;
            if (SectionBox.IsChecked != true) { Editor.SetSection(page, null); return; }
            var section = new PageNumbering((int)(SectionStartBox.Value ?? 1), (NumberStyle)Math.Max(0, SectionStyleChoice.SelectedIndex), SectionPrefixBox.Text ?? "");
            if (section != Doc.Pages[page].Section) Editor.SetSection(page, section);
        }
        SectionBox.IsCheckedChanged += (_, _) => Apply();
        SectionStartBox.ValueChanged += (_, _) => Apply();
        SectionStyleChoice.SelectionChanged += (_, _) => Apply();
        Commit(SectionPrefixBox, _ => Apply());
        _sectionRows.Children.AddRange([SectionBox, _sectionBoxes]);
    }

    private void BuildMasterGuides()
    {
        _masterGuideBoxes.Children.AddRange([
            Ui.Row(("Top", MasterTopBox), ("Bot", MasterBottomBox)),
            Ui.Row(("In", MasterInsideBox), ("Out", MasterOutsideBox)),
            Ui.Row(("Columns", MasterColumnsBox)),
            Ui.Row(("Gutter", MasterGutterBox)),
            Ui.Buttons(Ui.Button("Apply", ApplyMasterGuides, "Use these margins and columns on every page with this master")),
        ]);
        OwnGuidesBox.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing || !View.Sheet.IsMaster) return;
            if (OwnGuidesBox.IsChecked == true) ApplyMasterGuides();
            else Editor.SetMasterGuides(View.Sheet.Index, null, null, null);
        };
        _masterGuides.Children.AddRange([Ui.Heading("This master's guides"), OwnGuidesBox, _masterGuideBoxes]);
    }

    public void ApplyMasterGuides()
    {
        if (!View.Sheet.IsMaster) return;
        bool Read(TextBox b, out double v) => Units.TryParse(b.Text, Unit, out v) && v >= 0;
        if (!Read(MasterTopBox, out var top) || !Read(MasterBottomBox, out var bottom) || !Read(MasterInsideBox, out var inside) ||
            !Read(MasterOutsideBox, out var outside) || !Read(MasterGutterBox, out var gutter))
        {
            _window.Say("Each margin and the gutter is a measure, such as 36, 0.5in or 12mm.");
            return;
        }
        if (inside + outside >= Doc.Setup.PageWidth || top + bottom >= Doc.Setup.PageHeight) { _window.Say("Those margins leave no room on the page."); return; }
        Editor.SetMasterGuides(View.Sheet.Index, new Margins(top, bottom, inside, outside), (int)(MasterColumnsBox.Value ?? 1), gutter);
        _window.Say("The pages using this master have its margins and columns now.");
    }

    private void MovePage(int delta)
    {
        if (View.Sheet.IsMaster) return;
        var from = View.Sheet.Index;
        var to = from + delta;
        if (to < 0 || to >= Doc.Pages.Count) return;
        Editor.MovePage(from, to);
        _window.ShowSheet(SheetRef.PageAt(to));
    }

    private void RenameMaster()
    {
        if (!View.Sheet.IsMaster) { _window.Say("Pick a master page to rename first."); return; }
        var old = Doc.Masters[View.Sheet.Index].Name;
        _window.Say(Editor.RenameMaster(old, MasterNameBox.Text ?? "") ?? $"Renamed {old}.");
    }

    // ---- Document ---------------------------------------------------------------------------------------------------

    private Control BuildDocument()
    {
        PaperChoice.ItemsSource = PaperSize.All.Select(p => p.Name).Append("Custom").ToList();
        PaperChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || PaperChoice.SelectedIndex < 0 || PaperChoice.SelectedIndex >= PaperSize.All.Length) return;
            var paper = PaperSize.All[PaperChoice.SelectedIndex];
            var landscape = OrientationChoice.SelectedIndex == 1;
            PageWidthBox.Text = Units.Format(landscape ? paper.Height : paper.Width, Unit);
            PageHeightBox.Text = Units.Format(landscape ? paper.Width : paper.Height, Unit);
        };
        OrientationChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            (PageWidthBox.Text, PageHeightBox.Text) = (PageHeightBox.Text, PageWidthBox.Text);
        };
        Commit(DocTitleBox, text => { if ((text ?? "").Trim() is var v && v != Doc.Title) Editor.Change("Title", d => d.Title = v); });
        Commit(AuthorBox, text => { if ((text ?? "").Trim() is var v && v != Doc.Author) Editor.Change("Author", d => d.Author = v); });
        Commit(GridBox, text =>
        {
            if (!Units.TryParse(text, Unit, out var v) || v < 1 || v > 720)
            {
                _window.Say("The grid's spacing is a measure from 1 pt to 10 in, such as 18pt, 0.25in or 5mm.");
                return;
            }
            if (Math.Abs(v - Doc.Setup.Grid) > 0.001) Editor.Change("Grid spacing", d => d.Setup = d.Setup with { Grid = v });
            View.InvalidateVisual();
        });
        SwatchList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Swatch>((w, _) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), BorderBrush = Look.Border, BorderThickness = new Thickness(1), Background = w is null ? null : Swatchless(w.Colour) },
                new TextBlock { Text = w?.Name, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = w is null ? null : (w.Process?.ToString() ?? w.Colour) + (w.Spot ? "  spot" : ""), Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        SwatchList.SelectionChanged += (_, _) =>
        {
            if (_refreshing || SelectedSwatch is not { } w) return;
            SwatchNameBox.Text = w.Name;
            SwatchValueBox.Text = w.Process is { } ink ? FormattableString.Invariant($"{ink.C:0.#} {ink.M:0.#} {ink.Y:0.#} {ink.K:0.#}") : w.Colour;
            SpotBox.IsChecked = w.Spot;
        };
        LanguageChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || LanguageChoice.SelectedIndex < 0) return;
            var code = Speller.Languages[LanguageChoice.SelectedIndex].Code;
            if (code != Doc.Language) Editor.Change("Spelling language", d => d.Language = code);
            View.InvalidateVisual();
        };
        FacingBox.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            Editor.SetFacing(FacingBox.IsChecked == true);
            View.FitPage();
            _window.Say(FacingBox.IsChecked == true
                ? "Facing pages: even pages on the left, odd on the right. Left and right margins are inside and outside now."
                : "Single pages.");
        };
        UnitChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || UnitChoice.SelectedIndex < 0) return;
            var unit = Units.All[UnitChoice.SelectedIndex];
            Editor.Change("Units", d => d.Setup = d.Setup with { Unit = unit });
            _window.Settings.Unit = unit;
            _window.SaveSettings();
        };
        return new StackPanel
        {
            Children =
            {
                Ui.Heading("Page"),
                Ui.Row(("Size", PaperChoice)),
                Ui.Row(("Orientation", OrientationChoice)),
                Ui.Row(("W", PageWidthBox), ("H", PageHeightBox)),
                FacingBox,
                Ui.Row(("Bleed", BleedBox)),
                Ui.Heading("Margins"),
                Ui.Row(("Top", MarginTopBox), ("Bot", MarginBottomBox)),
                SideRow(MarginLeftLabel, MarginLeftBox, _marginRightLabel, MarginRightBox),
                Ui.Heading("Columns"),
                Ui.Row(("Columns", PageColumnsBox)),
                Ui.Row(("Gutter", PageGutterBox)),
                Ui.Heading("Baseline grid"),
                Ui.Row(("Spacing", BaselineBox)),
                Ui.Row(("First line", BaselineFromBox)),
                Ui.Buttons(Ui.Button("Apply", ApplySetup, "Change the page size, bleed, margins, columns and baseline grid")),
                Ui.Heading("Grid"),
                Ui.Row(("Spacing", GridBox)),
                Ui.Buttons(
                    Ui.Button("Show grid", () => _window.SetGrid(!View.ShowGrid, View.SnapGrid)),
                    Ui.Button("Snap to grid", () => _window.SetGrid(View.ShowGrid || !View.SnapGrid, !View.SnapGrid))),
                Ui.Heading("PDF"),
                Ui.Row(("Title", DocTitleBox)),
                Ui.Row(("Author", AuthorBox)),
                Ui.Heading("Swatches"),
                SwatchList,
                Ui.Row(("Name", SwatchNameBox)),
                Ui.Row(("Colour", SwatchValueBox)),
                SpotBox,
                Ui.Buttons(
                    Ui.Button("Add", () => SwatchSay(Editor.AddSwatch(SwatchNameBox.Text ?? "", SwatchValueBox.Text ?? "", SpotBox.IsChecked == true), "Added the swatch.")),
                    Ui.Button("Change colour", () => { if (SelectedSwatch is { } w) SwatchSay(Editor.ChangeSwatch(w.Name, SwatchValueBox.Text ?? "", SpotBox.IsChecked == true), $"{w.Name} is a new colour: everything using it changed."); },
                        "Give the selected swatch the colour typed: everything using it changes"),
                    Ui.Button("Rename", () => { if (SelectedSwatch is { } w) SwatchSay(Editor.RenameSwatch(w.Name, SwatchNameBox.Text ?? ""), "Renamed the swatch."); }),
                    Ui.Button("Delete", () => { if (SelectedSwatch is { } w) SwatchSay(Editor.DeleteSwatch(w.Name), $"Deleted {w.Name}; what used it keeps the colour."); })),
                InksInfo,
                new TextBlock
                {
                    Text = "Colours picked by a swatch's name follow it when it changes; \"Navy 40%\" picks a tint of it. CMYK colours are shown, and written to PDFs, as RGB.",
                    Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4),
                },
                Ui.Heading("Spelling"),
                Ui.Row(("Language", LanguageChoice)),
                Ui.Heading("Units"),
                Ui.Row(("Measure in", UnitChoice)),
                new TextBlock
                {
                    Text = "Any box takes a measure in another unit too: 3in, 25mm, 12p6, 18pt.",
                    Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4),
                },
            },
        };
    }

    private Swatch? SelectedSwatch => SwatchList.SelectedItem as Swatch;

    private void SwatchSay(string? why, string done) => _window.Say(why ?? done);

    /// <summary>A row of two labelled boxes whose labels change (left/right, or inside/outside).</summary>
    private static Grid SideRow(TextBlock a, Control boxA, TextBlock b, Control boxB)
    {
        var g = new Grid { Margin = new Thickness(0, 2), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*") };
        b.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(boxA, 1);
        Grid.SetColumn(b, 2);
        Grid.SetColumn(boxB, 3);
        g.Children.AddRange([a, boxA, b, boxB]);
        return g;
    }

    internal void ApplySetup()
    {
        double Read(TextBox b, string what)
        {
            if (Units.TryParse(b.Text, Unit, out var v) && v >= 0) return v;
            throw new FormatException(what);
        }
        try
        {
            var w = Read(PageWidthBox, "width");
            var h = Read(PageHeightBox, "height");
            if (w < 36 || h < 36 || w > 14400 || h > 14400) throw new FormatException("size");
            var margins = new Margins(Read(MarginTopBox, "top margin"), Read(MarginBottomBox, "bottom margin"), Read(MarginLeftBox, "left margin"), Read(MarginRightBox, "right margin"));
            if (margins.Left + margins.Right >= w || margins.Top + margins.Bottom >= h) throw new FormatException("margins");
            var columns = (int)(PageColumnsBox.Value ?? 1);
            var gutter = Read(PageGutterBox, "gutter");
            var bleed = string.IsNullOrWhiteSpace(BleedBox.Text) ? 0 : Read(BleedBox, "bleed");
            if (bleed > 72) throw new FormatException("bleed");
            var baseline = string.IsNullOrWhiteSpace(BaselineBox.Text) || BaselineBox.Text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? 0 : Read(BaselineBox, "baseline grid's spacing");
            if (baseline is > 0 and < 2) throw new FormatException("baseline");
            double? from = string.IsNullOrWhiteSpace(BaselineFromBox.Text) ? null : Read(BaselineFromBox, "baseline grid's first line");
            var setup = Doc.Setup with { PageWidth = w, PageHeight = h, Margins = margins, Columns = columns, Gutter = gutter, Bleed = bleed, BaselineGrid = baseline, BaselineFrom = from };
            if (setup == Doc.Setup) return;
            Editor.Change("Document setup", d => d.Setup = setup);
            View.FitPage();
            _window.Say("Changed the document's setup.");
        }
        catch (FormatException e)
        {
            _window.Say(e.Message switch
            {
                "size" => "A page must be between half an inch and 200 inches each way.",
                "margins" => "The margins leave no room on the page.",
                "bleed" => "A bleed is at most an inch; 9 pt (1/8 in) or 3 mm is usual.",
                "baseline" => "The baseline grid's spacing is at least 2 pt; usually the body text's leading, such as 14pt.",
                _ => $"The {e.Message} isn't a measure. Type something like 36, 0.5in or 12mm.",
            });
            Refresh();
        }
    }

    // ---- Showing what is selected -------------------------------------------------------------------------------------

    /// <summary>Fills every tab from the document and the selection.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            RefreshObject();
            RefreshText();
            RefreshPages();
            RefreshDocument();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Shows a value in a box, unless someone is typing in it (but a box just committed shows the value tidied).</summary>
    private void Set(TextBox box, string text)
    {
        if (!box.IsFocused || box == _committed) box.Text = text;
    }

    private TextBox? _committed;

    /// <summary>Commits a box's value, then shows it tidied: "1in" becomes "72 pt", "red" becomes "Red".</summary>
    private void Commit(TextBox box, Action<string> commit) => Ui.OnCommit(box, text =>
    {
        commit(text);
        _committed = box;
        try { Refresh(); }
        finally { _committed = null; }
    });

    private void RefreshObject()
    {
        FillSwatchButtons();
        var selected = View.Selected;
        _objectBody.IsVisible = selected.Count > 0;
        ObjectTitle.Text = selected.Count switch
        {
            0 => "Nothing selected",
            1 => selected[0].Kind + (selected[0] is GroupObject g ? $" of {g.Children.Count}" : "") + (selected[0].Locked ? " (locked)" : ""),
            _ => $"{selected.Count} objects",
        };
        if (selected.Count == 0) return;
        var box = Box.Union(selected.Select(o => o.Bounds));
        var one = selected.Count == 1 ? selected[0] : null;
        Set(XBox, Units.Format(box.X, Unit));
        Set(YBox, Units.Format(box.Y, Unit));
        Set(WBox, one is null ? "" : Units.Format(one.W, Unit));
        Set(HBox, one is null ? "" : Units.Format(one.H, Unit));
        WBox.IsEnabled = HBox.IsEnabled = one is not null;
        var first = selected[0];
        Set(FillBox, Colours.Describe(first.Fill, Doc.Swatches));
        Set(StrokeBox, Colours.Describe(first.Stroke, Doc.Swatches));
        _fillSwatch.Background = Brush(first.Fill);
        _strokeSwatch.Background = Brush(first.Stroke);
        Set(StrokeWidthBox, Units.Format(first.StrokeWidth, Unit));
        _cornerRow.IsVisible = selected.All(o => o is RectangleObject or TextFrame or PictureObject);
        RefreshEffects(first);
        RefreshObjectStyle(selected);
        _rotationRow.IsVisible = selected.All(o => o is not (LineObject or GroupObject or TableObject));
        Set(RotationBox, first.Rotation.ToString("0.##", CultureInfo.InvariantCulture) + "°");
        Set(CornerBox, first.Corners is { } fc ? fc.Describe(Unit) : first is RectangleObject r ? Units.Format(r.CornerRadius, Unit) : Units.Format(0, Unit));
        CornerStyleChoice.SelectedIndex = first.Corners?.Style == CornerStyle.Cut ? 1 : 0;
        _frameRows.IsVisible = one is TextFrame;
        if (one is TextFrame f)
        {
            ColumnsBox.Value = f.Columns;
            Set(GutterBox, Units.Format(f.Gutter, Unit));
            Set(InsetBox, Units.Format(f.Inset, Unit));
        }
        _pictureRows.IsVisible = one is PictureObject;
        _pathTextRows.IsVisible = one is PathObject { StoryId: not null };
        if (one is PathObject { StoryId: not null } onPath) Set(TextOffsetBox, Units.Format(onPath.TextOffset, Unit));
        _arrowRows.IsVisible = selected.All(o => o is LineObject);
        _tableRows.IsVisible = one is TableObject;
        if (one is TableObject frameOfTable && Tables.Head(Doc, frameOfTable) is { } table)
        {
            TableRowsBox.Value = table.Rows;
            TableColumnsBox.Value = table.Columns;
            HeaderRowsBox.Value = table.HeaderRows;
            Set(HeaderFillBox, Colours.Describe(table.HeaderFill, Doc.Swatches));
            Set(CellInsetBox, Units.Format(table.CellInset, Unit));
            Set(ColumnWidthsBox, string.Join(", ", table.ColumnWidths.Select(w => Units.Format(w, Unit))));
        }
        ArrowStartBox.IsChecked = selected.All(o => o is LineObject { ArrowStart: true });
        ArrowEndBox.IsChecked = selected.All(o => o is LineObject { ArrowEnd: true });
        if (one is PictureObject p)
        {
            var pages = PageCount(p.Image);
            _pdfRow.IsVisible = pages > 1;
            PdfPageBox.Maximum = Math.Max(1, pages);
            PdfPageBox.Value = Math.Clamp(p.Page, 1, Math.Max(1, pages));
            _pdfPages.Text = $"of {pages}";
            FitChoice.SelectedIndex = (int)p.Fit;
            Set(OffsetXBox, Units.Format(p.OffsetX, Unit));
            Set(OffsetYBox, Units.Format(p.OffsetY, Unit));
            LinkedBox.IsChecked = p.Linked;
            LinkedBox.IsEnabled = p.Source is not null;
            var file = p.Source is null ? null : Path.GetFileName(p.Source);
            LinkInfo.Text = Links.StateOf(Doc, p, _window.DocumentFolder) switch
            {
                LinkState.UpToDate => $"Linked to {file}.",
                LinkState.Changed => $"Linked to {file}, which has changed: Update links.",
                LinkState.Missing => $"Linked to {file}, which can't be found.",
                _ => file is null ? "Kept in the document." : $"Kept in the document (placed from {file}).",
            };
        }
        LockedBox.IsChecked = selected.All(o => o.Locked);
        WrapBox.IsChecked = selected.All(o => o.Wrap);
        WrapShapeBox.IsChecked = selected.All(o => o.WrapShape);
        WrapShapeBox.IsVisible = selected.All(o => o is EllipseObject or PathObject);
        Set(WrapGapBox, Units.Format(first.WrapGap, Unit));
    }

    /// <summary>The buttons for the document's swatches (and none): click for the fill, Shift+click for the stroke.</summary>
    private void FillSwatchButtons()
    {
        if (_swatchesShown.SequenceEqual(Doc.Swatches)) return;
        _swatchesShown = [.. Doc.Swatches];
        _swatchButtons.Children.Clear();
        foreach (var s in Doc.Swatches.Prepend(new Swatch("None", "")))
        {
            var name = s.Name;
            var b = new Button
            {
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 3, 3),
                Background = s.Colour.Length > 0 ? Brush(s.Colour) : Brushes.Transparent,
                BorderBrush = Look.Border,
                BorderThickness = new Thickness(1),
                Content = s.Colour.Length == 0 ? new TextBlock { Text = "∅", FontSize = 11, Foreground = Look.Heading, HorizontalAlignment = HorizontalAlignment.Center } : null,
            };
            ToolTip.SetTip(b, name + " (click: fill, Shift+click: stroke)");
            b.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                var stroke = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                SetColour(stroke, name);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            _swatchButtons.Children.Add(b);
        }
    }

    private IBrush Brush(string? colour) => Swatchless(Colours.Resolve(colour, Doc.Swatches));

    private static IBrush Swatchless(string? colour) =>
        colour is not null && Colours.TryParse(colour, out var c) ? new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B))
            : new VisualBrush { Visual = new TextBlock { Text = "∅", Foreground = Look.Heading }, Stretch = Stretch.None };

    private void RefreshText()
    {
        var c = View.CurrentChar();
        var p = View.CurrentPara();
        _textBody.IsVisible = c is not null;
        TextTarget.Text = View.Editing is { } t
            ? t.Length > 0 ? $"Changes apply to the {t.Length} selected characters, and paragraph settings to their paragraphs." : "Changes apply to the text typed next, and to the caret's paragraph."
            : c is not null ? "Changes apply to all the text of the selected frames. Double-click a frame to edit its text."
            : "Select a text frame, or click in one with the Text tool, to set its type.";
        StyleChoice.ItemsSource = Doc.Styles.Select(s => s.Name).ToList();
        if (c is null || p is null) return;
        StyleChoice.SelectedItem = p.Style;
        var fonts = (IList<string>)FontChoice.ItemsSource!;
        if (!fonts.Contains(c.Font)) FontChoice.ItemsSource = fonts.Append(c.Font).ToList();
        FontChoice.SelectedItem = c.Font;
        Set(SizeBox, Units.Format(c.Size, Unit.Point));
        BoldToggle.IsChecked = c.Bold;
        ItalicToggle.IsChecked = c.Italic;
        UnderlineToggle.IsChecked = c.Underline;
        SmallCapsToggle.IsChecked = c.SmallCaps;
        SuperToggle.IsChecked = c.Script == Script.Super;
        SubToggle.IsChecked = c.Script == Script.Sub;
        Set(TextColourBox, Colours.Describe(c.Colour, Doc.Swatches));
        Set(TrackingBox, c.Tracking.ToString("0.##", CultureInfo.InvariantCulture));
        foreach (var (align, b) in AlignButtons) b.IsChecked = p.Align == align;
        Set(LeadingBox, p.Leading > 0 ? Units.Format(p.Leading, Unit) : "auto");
        Set(LeftIndentBox, Units.Format(p.LeftIndent, Unit));
        Set(RightIndentBox, Units.Format(p.RightIndent, Unit));
        Set(FirstIndentBox, Units.Format(p.FirstIndent, Unit));
        Set(SpaceBeforeBox, Units.Format(p.SpaceBefore, Unit));
        Set(SpaceAfterBox, Units.Format(p.SpaceAfter, Unit));
        Set(TabsBox, Tabs.Show(p.Tabs, Unit));
        DropLinesBox.Value = p.DropLines;
        DropCharsBox.Value = Math.Max(1, p.DropChars);
        HyphenateBox.IsChecked = p.Hyphenate;
        OnGridBox.IsChecked = p.OnGrid;
        foreach (var (rc, rule) in new[] { (RuleAboveControls, p.RuleAbove), (RuleBelowControls, p.RuleBelow) })
        {
            rc.Choice.SelectedIndex = rule is null ? 0 : rule.Length switch { RuleLength.Text => 2, RuleLength.Width => 3, _ => 1 };
            var r = rule ?? new ParaRule();
            Set(rc.Weight, Units.Format(r.Weight, Unit));
            Set(rc.Colour, Colours.Describe(r.Colour, Doc.Swatches));
            Set(rc.Offset, Units.Format(r.Offset, Unit));
            Set(rc.Width, Units.Format(r.Width, Unit));
            foreach (var b in rc.Boxes) b.IsEnabled = rule is not null;
            rc.Width.IsEnabled = rule?.Length == RuleLength.Width;
        }
        KeepWithNextBox.IsChecked = p.KeepWithNext;
        KeepTogetherBox.IsChecked = p.KeepTogether;
        OrphansBox.Value = Math.Max(1, p.Orphans);
        WidowsBox.Value = Math.Max(1, p.Widows);
        CharStyleChoice.ItemsSource = Doc.CharStyles.Select(cs => cs.Name).Prepend("(none)").ToList();
        CharStyleChoice.SelectedIndex = c.CharStyle is { } csName ? Math.Max(0, Doc.CharStyles.FindIndex(cs => cs.Name == csName) + 1) : 0;
        CharStyleInfo.Text = c.CharStyle is { } shown && Doc.CharStyle(shown) is { } style
            ? $"{shown}: {style.Set.Describe()}." : "Select text, then pick a character style for it.";
    }

    /// <summary>Gives a list new items only when they differ, so its items (and a drag among them) survive a refresh.</summary>
    private static void Fill(ListBox list, List<string> items)
    {
        if (list.ItemsSource is IEnumerable<string> old && old.SequenceEqual(items)) return;
        list.ItemsSource = items;
    }

    private void RefreshPages()
    {
        Fill(PageList, Doc.Pages.Select((pg, i) =>
            $"Page {i + 1}{(Doc.PageLabel(i) is var label && label != (i + 1).ToString(CultureInfo.InvariantCulture) ? $" ({label})" : "")}   ·   {pg.Master ?? "no master"}{(pg.Section is null ? "" : "   · new section")}").ToList());
        PageList.SelectedIndex = View.Sheet.IsMaster ? -1 : View.Sheet.Index;
        Fill(MasterList, Doc.Masters.Select(m => m.Name).ToList());
        MasterList.SelectedIndex = View.Sheet.IsMaster ? View.Sheet.Index : -1;
        MasterChoice.ItemsSource = Doc.Masters.Select(m => m.Name).Prepend("(none)").ToList();
        MasterChoice.IsEnabled = !View.Sheet.IsMaster;
        _sectionRows.IsVisible = !View.Sheet.IsMaster;
        if (!View.Sheet.IsMaster)
        {
            var section = Doc.Pages[View.Sheet.Index].Section;
            SectionBox.IsChecked = section is not null;
            _sectionBoxes.IsEnabled = section is not null;
            SectionStartBox.Value = section?.Start ?? 1;
            SectionStyleChoice.SelectedIndex = (int)(section?.Style ?? NumberStyle.Arabic);
            Set(SectionPrefixBox, section?.Prefix ?? "");
        }
        // The layers, top first, as they are stacked.
        var layers = Enumerable.Reverse(Doc.Layers).ToList();
        if (LayerList.ItemsSource is not IEnumerable<Layer> shownLayers || !shownLayers.SequenceEqual(layers)) LayerList.ItemsSource = layers;
        LayerList.SelectedItem = layers.FirstOrDefault(l => l.Name == (Editor.CurrentLayer ?? Doc.Layers[^1].Name));
        if (!View.Sheet.IsMaster)
        {
            var master = Doc.Pages[View.Sheet.Index].Master;
            MasterChoice.SelectedIndex = master is null ? 0 : Doc.Masters.FindIndex(m => m.Name == master) + 1;
        }
        else Set(MasterNameBox, Doc.Masters[View.Sheet.Index].Name);
        _masterGuides.IsVisible = View.Sheet.IsMaster;
        if (View.Sheet.IsMaster)
        {
            var m = Doc.Masters[View.Sheet.Index];
            OwnGuidesBox.IsChecked = m.Margins is not null;
            _masterGuideBoxes.IsEnabled = m.Margins is not null;
            var margin = m.Margins ?? Doc.Setup.Margin;
            Set(MasterTopBox, Units.Format(margin.Top, Unit));
            Set(MasterBottomBox, Units.Format(margin.Bottom, Unit));
            Set(MasterInsideBox, Units.Format(margin.Left, Unit));
            Set(MasterOutsideBox, Units.Format(margin.Right, Unit));
            MasterColumnsBox.Value = m.Columns ?? Doc.Setup.Columns;
            Set(MasterGutterBox, Units.Format(m.Gutter ?? Doc.Setup.Gutter, Unit));
        }
    }

    private void RefreshDocument()
    {
        var s = Doc.Setup;
        var paper = PaperSize.Match(s.PageWidth, s.PageHeight);
        PaperChoice.SelectedIndex = paper is null ? PaperSize.All.Length : Array.IndexOf(PaperSize.All, paper);
        OrientationChoice.SelectedIndex = s.PageWidth > s.PageHeight ? 1 : 0;
        Set(PageWidthBox, Units.Format(s.PageWidth, Unit));
        Set(PageHeightBox, Units.Format(s.PageHeight, Unit));
        Set(MarginTopBox, Units.Format(s.Margin.Top, Unit));
        Set(MarginBottomBox, Units.Format(s.Margin.Bottom, Unit));
        Set(MarginLeftBox, Units.Format(s.Margin.Left, Unit));
        Set(MarginRightBox, Units.Format(s.Margin.Right, Unit));
        PageColumnsBox.Value = s.Columns;
        Set(PageGutterBox, Units.Format(s.Gutter, Unit));
        Set(GridBox, Units.Format(s.Grid, Unit));
        Set(BleedBox, s.Bleed > 0 ? Units.Format(s.Bleed, Unit) : "");
        Set(DocTitleBox, Doc.Title);
        Set(AuthorBox, Doc.Author);
        Set(BaselineBox, s.BaselineGrid > 0 ? Units.Format(s.BaselineGrid, Unit) : "");
        Set(BaselineFromBox, s.BaselineFrom is { } bf ? Units.Format(bf, Unit) : "");
        UnitChoice.SelectedIndex = Array.IndexOf(Units.All, s.Unit);
        FacingBox.IsChecked = s.Facing;
        LanguageChoice.SelectedIndex = Math.Max(0, Array.FindIndex(Speller.Languages, l => l.Code == Doc.Language));
        var (process, spots) = Colours.Inks(Doc);
        InksInfo.Text = "Inks used: " + (process || spots.Count == 0 ? "process (CMYK)" : "") + (process && spots.Count > 0 ? ", " : "") +
            (spots.Count > 0 ? "spot " + string.Join(", ", spots) : "") + ".";
        if (SwatchList.ItemsSource is not IEnumerable<Swatch> shown || !shown.SequenceEqual(Doc.Swatches))
        {
            var selected = SelectedSwatch?.Name;
            SwatchList.ItemsSource = Doc.Swatches.ToList();
            SwatchList.SelectedItem = Doc.Swatches.FirstOrDefault(w => w.Name == selected);
        }
        MarginLeftLabel.Text = s.Facing ? "Inside" : "Left";
        _marginRightLabel.Text = s.Facing ? "Outside" : "Right";
    }
}
