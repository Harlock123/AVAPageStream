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
    public TextBox FillBox { get; } = Ui.Box("none, a name or #RRGGBB");
    public TextBox StrokeBox { get; } = Ui.Box("none, a name or #RRGGBB");
    public TextBox StrokeWidthBox { get; } = Ui.Box();
    public TextBox CornerBox { get; } = Ui.Box();
    public NumericUpDown ColumnsBox { get; } = new() { Minimum = 1, Maximum = 12, Increment = 1, FormatString = "0", FontSize = 12 };
    public TextBox GutterBox { get; } = Ui.Box();
    public TextBox InsetBox { get; } = Ui.Box();
    public ComboBox FitChoice { get; } = new() { ItemsSource = Enum.GetNames<Fit>(), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    public CheckBox LockedBox { get; } = new() { Content = "Locked", FontSize = 12 };
    private readonly StackPanel _objectBody = new() { Spacing = 0 };
    private readonly StackPanel _cornerRow = new();
    private readonly StackPanel _frameRows = new();
    private readonly StackPanel _pictureRows = new();
    private readonly Border _fillSwatch = Swatch();
    private readonly Border _strokeSwatch = Swatch();

    // Text
    public ComboBox StyleChoice { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
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
    public ComboBox UnitChoice { get; } = new() { ItemsSource = Units.All.Select(Units.Name).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };

    // Drawn with the tab control's own template: themes look a control's template up by its exact type.
    protected override Type StyleKeyOverride => typeof(TabControl);

    public SidePanel(MainWindow window)
    {
        _window = window;
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
        var swatchMenu = new WrapPanel { Margin = new Thickness(74, 2, 0, 4) };
        foreach (var s in Colours.DefaultSwatches().Prepend(new Swatch("None", "")))
        {
            var name = s.Name;
            var b = new Button
            {
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 3, 3),
                Background = s.Colour.Length > 0 ? new SolidColorBrush(Color.Parse(s.Colour)) : Brushes.Transparent,
                BorderBrush = Look.Border,
                BorderThickness = new Thickness(1),
                Content = s.Colour.Length == 0 ? new TextBlock { Text = "∅", FontSize = 11, Foreground = Look.Heading, HorizontalAlignment = HorizontalAlignment.Center } : null,
            };
            ToolTip.SetTip(b, name + " (click: fill, Shift+click: stroke)");
            b.Click += (_, _) => { };
            b.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                var stroke = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                SetColour(stroke, name);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            swatchMenu.Children.Add(b);
        }

        MeasureBox(XBox, (o, v) => o.X = v, "Move");
        MeasureBox(YBox, (o, v) => o.Y = v, "Move");
        MeasureBox(WBox, (o, v) => o.W = Math.Max(o is LineObject ? 0 : 1, v), "Resize");
        MeasureBox(HBox, (o, v) => o.H = Math.Max(o is LineObject ? 0 : 1, v), "Resize");
        MeasureBox(StrokeWidthBox, (o, v) => o.StrokeWidth = Math.Max(0, v), "Stroke width", remember: true);
        MeasureBox(CornerBox, (o, v) => { if (o is RectangleObject r) r.CornerRadius = Math.Max(0, v); }, "Corner radius");
        MeasureBox(GutterBox, (o, v) => { if (o is TextFrame f) f.Gutter = Math.Max(0, v); }, "Gutter");
        MeasureBox(InsetBox, (o, v) => { if (o is TextFrame f) f.Inset = Math.Max(0, v); }, "Inset");
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
        LockedBox.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            var locked = LockedBox.IsChecked == true;
            ChangeSelected(locked ? "Lock" : "Unlock", o => o.Locked = locked, includeLocked: true);
        };

        _cornerRow.Children.Add(Ui.Row(("Corners", CornerBox)));
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
            Ui.Buttons(Ui.Button("Replace picture…", () => { if (View.Selected is [PictureObject p]) _ = View.ReplaceImage(p.Id); })),
        ]);

        _objectBody.Children.AddRange([
            Ui.Heading("Frame"),
            Ui.Row(("X", XBox), ("Y", YBox)),
            Ui.Row(("W", WBox), ("H", HBox)),
            Ui.Heading("Colour"),
            Ui.Row(("Fill", WithSwatch(FillBox, _fillSwatch))),
            Ui.Row(("Stroke", WithSwatch(StrokeBox, _strokeSwatch))),
            swatchMenu,
            Ui.Row(("Width", StrokeWidthBox)),
            _cornerRow,
            _frameRows,
            _pictureRows,
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

        _textBody.Children.AddRange([
            Ui.Heading("Paragraph style"),
            Ui.Row(("Style", StyleChoice)),
            Ui.Row(("New", StyleNameBox)),
            Ui.Buttons(
                Ui.Button("Add from text", NewStyle, "A new style with the settings of the paragraph the caret is in"),
                Ui.Button("Redefine", RedefineStyle, "Make the style match this paragraph; every paragraph in it changes"),
                Ui.Button("Delete", DeleteStyle)),
            Ui.Heading("Character"),
            Ui.Row(("Font", FontChoice)),
            Ui.Row(("Size", SizeBox)),
            Ui.Row(("Style", new StackPanel { Orientation = Orientation.Horizontal, Children = { BoldToggle, ItalicToggle, UnderlineToggle } })),
            Ui.Row(("Colour", TextColourBox)),
            Ui.Row(("Tracking", TrackingBox)),
            Ui.Heading("Paragraph"),
            Ui.Row(("Align", aligns)),
            Ui.Row(("Leading", LeadingBox)),
            Ui.Row(("Left", LeftIndentBox), ("Right", RightIndentBox)),
            Ui.Row(("First line", FirstIndentBox)),
            Ui.Row(("Before", SpaceBeforeBox), ("After", SpaceAfterBox)),
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

    private void DeleteStyle()
    {
        if (StyleChoice.SelectedItem is not string name) return;
        _window.Say(Editor.DeleteStyle(name) ?? $"Deleted the style {name}. Its paragraphs keep their look.");
    }

    // ---- Pages ------------------------------------------------------------------------------------------------------

    private Control BuildPages()
    {
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
                Ui.Heading("Margins"),
                Ui.Row(("Top", MarginTopBox), ("Bot", MarginBottomBox)),
                Ui.Row(("Left", MarginLeftBox), ("Right", MarginRightBox)),
                Ui.Heading("Columns"),
                Ui.Row(("Columns", PageColumnsBox)),
                Ui.Row(("Gutter", PageGutterBox)),
                Ui.Buttons(Ui.Button("Apply", ApplySetup, "Change the page size, margins and columns")),
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
        var selected = View.Selected;
        _objectBody.IsVisible = selected.Count > 0;
        ObjectTitle.Text = selected.Count switch
        {
            0 => "Nothing selected",
            1 => selected[0].Kind + (selected[0].Locked ? " (locked)" : ""),
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
        if (one is RectangleObject r) Set(CornerBox, Units.Format(r.CornerRadius, Unit));
        _frameRows.IsVisible = one is TextFrame;
        if (one is TextFrame f)
        {
            ColumnsBox.Value = f.Columns;
            Set(GutterBox, Units.Format(f.Gutter, Unit));
            Set(InsetBox, Units.Format(f.Inset, Unit));
        }
        _pictureRows.IsVisible = one is PictureObject;
        if (one is PictureObject p) FitChoice.SelectedIndex = (int)p.Fit;
        LockedBox.IsChecked = selected.All(o => o.Locked);
    }

    private static IBrush Brush(string? colour) =>
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
    }

    private void RefreshPages()
    {
        PageList.ItemsSource = Doc.Pages.Select((pg, i) => $"Page {i + 1}   ·   {pg.Master ?? "no master"}").ToList();
        PageList.SelectedIndex = View.Sheet.IsMaster ? -1 : View.Sheet.Index;
        MasterList.ItemsSource = Doc.Masters.Select(m => m.Name).ToList();
        MasterList.SelectedIndex = View.Sheet.IsMaster ? View.Sheet.Index : -1;
        MasterChoice.ItemsSource = Doc.Masters.Select(m => m.Name).Prepend("(none)").ToList();
        MasterChoice.IsEnabled = !View.Sheet.IsMaster;
        if (!View.Sheet.IsMaster)
        {
            var master = Doc.Pages[View.Sheet.Index].Master;
            MasterChoice.SelectedIndex = master is null ? 0 : Doc.Masters.FindIndex(m => m.Name == master) + 1;
        }
        else Set(MasterNameBox, Doc.Masters[View.Sheet.Index].Name);
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
        UnitChoice.SelectedIndex = Array.IndexOf(Units.All, s.Unit);
    }
}
