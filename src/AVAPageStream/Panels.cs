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
    private readonly StackPanel _rotationRow = new();
    private readonly StackPanel _frameRows = new();
    private readonly StackPanel _pictureRows = new();
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
    public CheckBox ArrowStartBox { get; } = new() { Content = "At its start", FontSize = 12 };
    public CheckBox ArrowEndBox { get; } = new() { Content = "At its end", FontSize = 12 };
    private readonly StackPanel _arrowRows = new();
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
    public TextBox SwatchValueBox { get; } = Ui.Box("#RRGGBB");

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
        MeasureBox(CornerBox, (o, v) => { if (o is RectangleObject r) r.CornerRadius = Math.Max(0, v); }, "Corner radius");
        MeasureBox(GutterBox, (o, v) => { if (o is TextFrame f) f.Gutter = Math.Max(0, v); }, "Gutter");
        MeasureBox(InsetBox, (o, v) => { if (o is TextFrame f) f.Inset = Math.Max(0, v); }, "Inset");
        MeasureBox(OffsetXBox, (o, v) => { if (o is PictureObject p) p.OffsetX = v; }, "Crop");
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
        _rotationRow.Children.Add(Ui.Row(("Rotation", RotationBox)));
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
        _pictureRows.Children.AddRange([
            Ui.Heading("Picture"),
            Ui.Row(("Fit", FitChoice)),
            Ui.Row(("Offset X", OffsetXBox)),
            Ui.Row(("Offset Y", OffsetYBox)),
            new TextBlock
            {
                Text = "Move the picture within its frame to crop a different part into view. Alt+drag it with Select, too.",
                Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            },
            Ui.Buttons(Ui.Button("Replace picture…", () => { if (View.Selected is [PictureObject p]) _ = View.ReplaceImage(p.Id); })),
        ]);

        _objectBody.Children.AddRange([
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
            _frameRows,
            _pictureRows,
            _arrowRows,
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
            Ui.Heading("Character"),
            Ui.Row(("Font", FontChoice)),
            Ui.Row(("Size", SizeBox)),
            Ui.Row(("Style", new StackPanel { Orientation = Orientation.Horizontal, Children = { BoldToggle, ItalicToggle, UnderlineToggle } })),
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
            Ui.Row(("Drop cap", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
                DropLinesBox, new TextBlock { Text = "lines", Foreground = Look.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                DropCharsBox, new TextBlock { Text = "letters", Foreground = Look.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } })),
            HyphenateBox,
            new TextBlock
            {
                Text = "Tabs: a measure, then left, right, centre or decimal, then a leader, such as 4in right . — Kern: Alt+Left and Alt+Right in the text.",
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

    private void NewStyle()
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
                Ui.Heading("Master pages"),
                MasterList,
                Ui.Row(("Name", MasterNameBox)),
                _masterGuides,
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
                new TextBlock { Text = w?.Colour, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        SwatchList.SelectionChanged += (_, _) =>
        {
            if (_refreshing || SelectedSwatch is not { } w) return;
            SwatchNameBox.Text = w.Name;
            SwatchValueBox.Text = w.Colour;
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
                Ui.Heading("Margins"),
                Ui.Row(("Top", MarginTopBox), ("Bot", MarginBottomBox)),
                SideRow(MarginLeftLabel, MarginLeftBox, _marginRightLabel, MarginRightBox),
                Ui.Heading("Columns"),
                Ui.Row(("Columns", PageColumnsBox)),
                Ui.Row(("Gutter", PageGutterBox)),
                Ui.Buttons(Ui.Button("Apply", ApplySetup, "Change the page size, margins and columns")),
                Ui.Heading("Grid"),
                Ui.Row(("Spacing", GridBox)),
                Ui.Buttons(
                    Ui.Button("Show grid", () => _window.SetGrid(!View.ShowGrid, View.SnapGrid)),
                    Ui.Button("Snap to grid", () => _window.SetGrid(View.ShowGrid || !View.SnapGrid, !View.SnapGrid))),
                Ui.Heading("Swatches"),
                SwatchList,
                Ui.Row(("Name", SwatchNameBox)),
                Ui.Row(("Colour", SwatchValueBox)),
                Ui.Buttons(
                    Ui.Button("Add", () => SwatchSay(Editor.AddSwatch(SwatchNameBox.Text ?? "", SwatchValueBox.Text ?? ""), "Added the swatch.")),
                    Ui.Button("Change colour", () => { if (SelectedSwatch is { } w) SwatchSay(Editor.ChangeSwatch(w.Name, SwatchValueBox.Text ?? ""), $"{w.Name} is a new colour: everything using it changed."); },
                        "Give the selected swatch the colour typed: everything using it changes"),
                    Ui.Button("Rename", () => { if (SelectedSwatch is { } w) SwatchSay(Editor.RenameSwatch(w.Name, SwatchNameBox.Text ?? ""), "Renamed the swatch."); }),
                    Ui.Button("Delete", () => { if (SelectedSwatch is { } w) SwatchSay(Editor.DeleteSwatch(w.Name), $"Deleted {w.Name}; what used it keeps the colour."); })),
                new TextBlock
                {
                    Text = "Colours picked by a swatch's name follow it when it changes.",
                    Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4),
                },
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

    private void ApplySetup()
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
            var setup = Doc.Setup with { PageWidth = w, PageHeight = h, Margins = margins, Columns = columns, Gutter = gutter };
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
        _cornerRow.IsVisible = one is RectangleObject;
        _rotationRow.IsVisible = selected.All(o => o is not (LineObject or GroupObject));
        Set(RotationBox, first.Rotation.ToString("0.##", CultureInfo.InvariantCulture) + "°");
        if (one is RectangleObject r) Set(CornerBox, Units.Format(r.CornerRadius, Unit));
        _frameRows.IsVisible = one is TextFrame;
        if (one is TextFrame f)
        {
            ColumnsBox.Value = f.Columns;
            Set(GutterBox, Units.Format(f.Gutter, Unit));
            Set(InsetBox, Units.Format(f.Inset, Unit));
        }
        _pictureRows.IsVisible = one is PictureObject;
        _arrowRows.IsVisible = selected.All(o => o is LineObject);
        ArrowStartBox.IsChecked = selected.All(o => o is LineObject { ArrowStart: true });
        ArrowEndBox.IsChecked = selected.All(o => o is LineObject { ArrowEnd: true });
        if (one is PictureObject p)
        {
            FitChoice.SelectedIndex = (int)p.Fit;
            Set(OffsetXBox, Units.Format(p.OffsetX, Unit));
            Set(OffsetYBox, Units.Format(p.OffsetY, Unit));
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
    }

    /// <summary>Gives a list new items only when they differ, so its items (and a drag among them) survive a refresh.</summary>
    private static void Fill(ListBox list, List<string> items)
    {
        if (list.ItemsSource is IEnumerable<string> old && old.SequenceEqual(items)) return;
        list.ItemsSource = items;
    }

    private void RefreshPages()
    {
        Fill(PageList, Doc.Pages.Select((pg, i) => $"Page {i + 1}   ·   {pg.Master ?? "no master"}").ToList());
        PageList.SelectedIndex = View.Sheet.IsMaster ? -1 : View.Sheet.Index;
        Fill(MasterList, Doc.Masters.Select(m => m.Name).ToList());
        MasterList.SelectedIndex = View.Sheet.IsMaster ? View.Sheet.Index : -1;
        MasterChoice.ItemsSource = Doc.Masters.Select(m => m.Name).Prepend("(none)").ToList();
        MasterChoice.IsEnabled = !View.Sheet.IsMaster;
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
        UnitChoice.SelectedIndex = Array.IndexOf(Units.All, s.Unit);
        FacingBox.IsChecked = s.Facing;
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
