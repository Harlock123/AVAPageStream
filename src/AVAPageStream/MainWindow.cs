using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVAPageStream.Core;
using Page = AVAPageStream.Core.Page;

namespace AVAPageStream;

/// <summary>
/// The program's window: the menus and tool bar, the toolbox, the page view, the side panel and the status bar.
/// Built in code; its controls and commands are public so tests can drive them.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly string _settingsPath;
    private bool _closing;
    private (Document Doc, List<string> Ids)? _clipboard;
    private int _pasteCount;

    public AppSettings Settings { get; }
    public Editor Editor { get; private set; }
    public PageView View { get; }
    public SidePanel Panel { get; }

    /// <summary>The file the document was opened from or last saved to, or null for a new one.</summary>
    public string? FilePath { get; private set; }

    public TextBlock Message { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    public TextBlock SelectionInfo { get; } = new() { Foreground = Look.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    public TextBlock CursorInfo { get; } = new() { Foreground = Look.Heading, FontSize = 12, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
    public TextBlock ZoomInfo { get; } = new() { Foreground = Look.Heading, FontSize = 12, MinWidth = 46, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    public TextBlock PageInfo { get; } = new() { Foreground = Look.Text, FontSize = 12, MinWidth = 96, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    public Dictionary<Tool, ToggleButton> ToolButtons { get; } = [];
    private readonly MenuItem _recentMenu = new() { Header = "Open _recent" };
    private readonly MenuItem _snapItem = new() { Header = "_Snap to guides", ToggleType = MenuItemToggleType.CheckBox };
    private readonly MenuItem _guidesItem = new() { Header = "Show _guides", ToggleType = MenuItemToggleType.CheckBox };

    /// <summary>Asks a question (title, message, buttons) and returns the button pressed; tests answer it themselves.</summary>
    public Func<string, string, string[], Task<int>> Ask { get; set; }

    /// <summary>Asks for a file to open (by kind: "document" or "image"); tests answer it themselves.</summary>
    public Func<string, Task<string?>> PickOpen { get; set; }

    /// <summary>Asks where to save (kind "document", "pdf" or "png", and a suggested name); tests answer it themselves.</summary>
    public Func<string, string, Task<string?>> PickSave { get; set; }

    public MainWindow(string settingsPath, string? open = null)
    {
        _settingsPath = settingsPath;
        Settings = AppSettings.Load(settingsPath);
        Editor = new Editor(Document.New(new Setup(Unit: Settings.Unit)));
        Ask = (title, message, buttons) => AskDialog.Show(this, title, message, buttons);
        PickOpen = PickOpenFile;
        PickSave = PickSaveFile;

        Width = 1360;
        Height = 880;
        MinWidth = 900;
        MinHeight = 560;
        Background = Look.Window;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AVAPageStream/Assets/AVAPageStream.png")));

        View = new PageView(Editor) { Snap = Settings.Snap, ShowGuides = Settings.ShowGuides };
        View.PickImage = async () => await PickOpen("image") is { } path ? await File.ReadAllBytesAsync(path) : null;
        View.SelectionChanged += Refresh;
        View.ViewChanged += () => ZoomInfo.Text = $"{Math.Round(View.Zoom * 100)}%";
        View.Status += Say;
        View.PointerAt += p => CursorInfo.Text = p is { } pt
            ? $"{Units.Format(pt.X, Editor.Doc.Setup.Unit, false)}, {Units.Format(pt.Y, Editor.Doc.Setup.Unit)}" : "";
        View.ShowSheet += ShowSheet;
        Editor.Changed += OnChanged;
        Panel = new SidePanel(this);

        var centre = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,330") };
        centre.Children.Add(new Border { Child = View, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 1, 1, 0) });
        var splitter = new GridSplitter { Background = Look.Window, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        var side = new Border { Child = Panel, Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 1, 0, 0) };
        Grid.SetColumn(side, 2);
        centre.Children.Add(splitter);
        centre.Children.Add(side);

        var dock = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);
        var tools = BuildToolBar();
        DockPanel.SetDock(tools, Dock.Top);
        dock.Children.Add(tools);
        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        var box = BuildToolbox();
        DockPanel.SetDock(box, Dock.Left);
        dock.Children.Add(box);
        dock.Children.Add(centre);
        Content = dock;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => View.Focus();
        _snapItem.IsChecked = View.Snap;
        _guidesItem.IsChecked = View.ShowGuides;

        if (open is not null) OpenFile(open);
        else Say("A new document. Draw a text frame with the Text tool (T) and start typing.");
        Refresh();
    }

    // ---- Building the window ----------------------------------------------------------------------------------------

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action action, string? keys = null)
        {
            var item = new MenuItem { Header = header };
            if (keys is not null) item.InputGesture = KeyGesture.Parse(keys);
            item.Click += (_, _) => action();
            return item;
        }

        _snapItem.Click += (_, _) => { View.Snap = Settings.Snap = !View.Snap; _snapItem.IsChecked = View.Snap; SaveSettings(); };
        _guidesItem.Click += (_, _) =>
        {
            View.ShowGuides = Settings.ShowGuides = !View.ShowGuides;
            _guidesItem.IsChecked = View.ShowGuides;
            View.InvalidateVisual();
            SaveSettings();
        };
        _recentMenu.SubmenuOpened += (_, _) => FillRecent();
        FillRecent();

        return new Menu
        {
            Background = Look.Bar,
            Items =
            {
                new MenuItem
                {
                    Header = "_File",
                    Items =
                    {
                        Item("_New", () => _ = New(), "Ctrl+N"),
                        Item("_Open…", () => _ = OpenWithPicker(), "Ctrl+O"),
                        _recentMenu,
                        new Separator(),
                        Item("_Save", () => _ = Save(), "Ctrl+S"),
                        Item("Save _as…", () => _ = SaveAs(), "Ctrl+Shift+S"),
                        new Separator(),
                        Item("Export _PDF…", () => _ = ExportPdf(), "Ctrl+E"),
                        Item("Export this page as P_NG…", () => _ = ExportPng(all: false)),
                        Item("Export every page as PN_G…", () => _ = ExportPng(all: true)),
                        new Separator(),
                        Item("_Quit", Close, "Ctrl+Q"),
                    },
                },
                new MenuItem
                {
                    Header = "_Edit",
                    Items =
                    {
                        Item("_Undo", Undo, "Ctrl+Z"),
                        Item("_Redo", Redo, "Ctrl+Y"),
                        new Separator(),
                        Item("Cu_t", () => _ = Cut(), "Ctrl+X"),
                        Item("_Copy", () => _ = Copy(), "Ctrl+C"),
                        Item("_Paste", () => _ = Paste(), "Ctrl+V"),
                        Item("_Duplicate", Duplicate, "Ctrl+D"),
                        Item("De_lete", View.DeleteSelection, "Delete"),
                        new Separator(),
                        Item("Select _all", View.SelectAll, "Ctrl+A"),
                    },
                },
                new MenuItem
                {
                    Header = "_View",
                    Items =
                    {
                        Item("_Fit page", View.FitPage, "Ctrl+0"),
                        Item("_Actual size", View.ActualSize, "Ctrl+1"),
                        Item("Zoom _in", () => View.SetZoom(View.Zoom * 1.25), "Ctrl+OemPlus"),
                        Item("Zoom _out", () => View.SetZoom(View.Zoom / 1.25), "Ctrl+OemMinus"),
                        new Separator(),
                        _snapItem,
                        _guidesItem,
                    },
                },
                new MenuItem
                {
                    Header = "_Object",
                    Items =
                    {
                        Item("Bring to _front", () => Arrange(Editor.Order.Front), "Ctrl+Shift+OemCloseBrackets"),
                        Item("Bring _forward", () => Arrange(Editor.Order.Forward), "Ctrl+OemCloseBrackets"),
                        Item("Send _backward", () => Arrange(Editor.Order.Backward), "Ctrl+OemOpenBrackets"),
                        Item("Send to bac_k", () => Arrange(Editor.Order.Back), "Ctrl+Shift+OemOpenBrackets"),
                        new Separator(),
                        Item("_Lock", () => Lock(true)),
                        Item("_Unlock", () => Lock(false)),
                        new Separator(),
                        Item("_Place picture…", PlacePicture),
                        Item("Unlink text _frame", UnlinkSelected),
                    },
                },
                new MenuItem
                {
                    Header = "_Text",
                    Items =
                    {
                        Item("_Bold", () => View.ToggleChar(f => f.Bold, (f, v) => f with { Bold = v }, "Bold"), "Ctrl+B"),
                        Item("_Italic", () => View.ToggleChar(f => f.Italic, (f, v) => f with { Italic = v }, "Italic"), "Ctrl+I"),
                        Item("_Underline", () => View.ToggleChar(f => f.Underline, (f, v) => f with { Underline = v }, "Underline"), "Ctrl+U"),
                        new Separator(),
                        Item("Align _left", () => View.ApplyPara(p => p with { Align = Align.Left }, "Alignment")),
                        Item("Align _centre", () => View.ApplyPara(p => p with { Align = Align.Centre }, "Alignment")),
                        Item("Align _right", () => View.ApplyPara(p => p with { Align = Align.Right }, "Alignment")),
                        Item("_Justify", () => View.ApplyPara(p => p with { Align = Align.Justify }, "Alignment")),
                    },
                },
                new MenuItem
                {
                    Header = "_Page",
                    Items =
                    {
                        Item("Add page _before", () => AddPage(after: false)),
                        Item("Add page _after", () => AddPage(after: true)),
                        Item("_Delete page", DeletePage),
                        new Separator(),
                        Item("_Previous page", () => StepPage(-1), "PageUp"),
                        Item("_Next page", () => StepPage(1), "PageDown"),
                        Item("Edit this page's _master", EditMaster),
                    },
                },
                new MenuItem
                {
                    Header = "_Help",
                    Items = { Item("_Keys and tools", () => _ = ShowHelp()) },
                },
            },
        };
    }

    private Control BuildToolBar()
    {
        Button B(string text, Action click, string tip)
        {
            var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(10, 4), Background = Brushes.Transparent };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        Control Gap() => new Border { Width = 1, Height = 20, Background = Look.Border, Margin = new Thickness(8, 0) };
        return new Border
        {
            Background = Look.Bar,
            BorderBrush = Look.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 3),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    B("New", () => _ = New(), "New document (Ctrl+N)"),
                    B("Open", () => _ = OpenWithPicker(), "Open a document (Ctrl+O)"),
                    B("Save", () => _ = Save(), "Save (Ctrl+S)"),
                    B("PDF", () => _ = ExportPdf(), "Export PDF (Ctrl+E)"),
                    Gap(),
                    B("Undo", Undo, "Undo (Ctrl+Z)"),
                    B("Redo", Redo, "Redo (Ctrl+Y)"),
                    Gap(),
                    B("−", () => View.SetZoom(View.Zoom / 1.25), "Zoom out (Ctrl+-)"),
                    ZoomInfo,
                    B("+", () => View.SetZoom(View.Zoom * 1.25), "Zoom in (Ctrl+=)"),
                    B("Fit", View.FitPage, "Fit the page (Ctrl+0)"),
                    B("100%", View.ActualSize, "Actual size (Ctrl+1)"),
                    Gap(),
                    B("◀", () => StepPage(-1), "Previous page (Page Up)"),
                    PageInfo,
                    B("▶", () => StepPage(1), "Next page (Page Down)"),
                    B("+ Page", () => AddPage(after: true), "Add a page after this one"),
                },
            },
        };
    }

    /// <summary>The tools, drawn as small icons so they look the same on every computer.</summary>
    private static readonly (Tool Tool, string Name, string Key, string Icon)[] Tools =
    [
        (Tool.Select, "Select", "V", "M4,2 L4,17 L8,13.5 L11,19.5 L13.4,18.4 L10.5,12.5 L15.5,12.5 Z"),
        (Tool.Text, "Text", "T", "M3,3 H17 V7.5 H15.2 V5 H11 V16.5 H13.5 V18.5 H6.5 V16.5 H9 V5 H4.8 V7.5 H3 Z"),
        (Tool.Rectangle, "Rectangle", "R", "F0 M2.5,5 H17.5 V15 H2.5 Z M4.3,6.8 V13.2 H15.7 V6.8 Z"),
        (Tool.Ellipse, "Ellipse", "E", "F0 M10,4 A7.5,6 0 1 1 9.99,4 Z M10,5.8 A5.7,4.2 0 1 0 10.01,5.8 Z"),
        (Tool.Line, "Line", "L", "M3,15.8 L15.8,3 L17,4.2 L4.2,17 Z"),
        (Tool.Picture, "Picture", "P", "F0 M2,4 H18 V16 H2 Z M3.8,5.8 V14.2 H16.2 V5.8 Z M5,13 L8.2,8.8 L10.4,11.2 L12.4,8.4 L15,13 Z"),
        (Tool.Link, "Link frames", "K", "F0 M1.5,10 A4,4 0 0 1 5.5,6 H9 V8 H5.5 A2,2 0 0 0 5.5,12 H9 V14 H5.5 A4,4 0 0 1 1.5,10 Z M11,6 H14.5 A4,4 0 0 1 14.5,14 H11 V12 H14.5 A2,2 0 0 0 14.5,8 H11 Z M6.5,9 H13.5 V11 H6.5 Z"),
        (Tool.Hand, "Hand", "H", "M6,18.5 L2.8,11.6 L4.6,10.6 L7,13.2 V4.2 H9 V10 H10 V3 H12 V10 H13 V4.2 H15 V10.6 H15.6 V7 H17.6 V13 L15,18.5 Z"),
    ];

    private Control BuildToolbox()
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(4, 6) };
        foreach (var (tool, name, key, icon) in Tools)
        {
            var b = new ToggleButton
            {
                Width = 36,
                Height = 34,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new PathIcon { Data = Geometry.Parse(icon), Width = 18, Height = 18 },
            };
            ToolTip.SetTip(b, $"{name} ({key})");
            b.Click += (_, _) => { View.Tool = tool; View.Focus(); };
            ToolButtons[tool] = b;
            stack.Children.Add(b);
        }
        return new Border { Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 1, 0), Child = stack };
    }

    private Control BuildStatusBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(10, 3) };
        Grid.SetColumn(Message, 1);
        Grid.SetColumn(CursorInfo, 2);
        Message.Margin = new Thickness(16, 0);
        grid.Children.Add(SelectionInfo);
        grid.Children.Add(Message);
        grid.Children.Add(CursorInfo);
        return new Border { Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = grid };
    }

    // ---- Keeping the window up to date --------------------------------------------------------------------------------

    public void Say(string message) => Message.Text = message;

    public void SaveSettings() => Settings.Save(_settingsPath);

    private void OnChanged()
    {
        UpdateTitle();
        // The view refreshes the panel after its own bookkeeping; drags preview without a refresh each move.
    }

    private void UpdateTitle()
    {
        var name = FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);
        Title = $"{name}{(Editor.Dirty ? " •" : "")} — AVAPageStream";
    }

    /// <summary>Brings every part of the window up to date with the document and the selection.</summary>
    public void Refresh()
    {
        UpdateTitle();
        foreach (var (tool, b) in ToolButtons) b.IsChecked = View.Tool == tool;
        var doc = Editor.Doc;
        PageInfo.Text = View.Sheet.IsMaster
            ? doc.Masters[View.Sheet.Index].Name
            : $"Page {View.Sheet.Index + 1} of {doc.Pages.Count}";
        ZoomInfo.Text = $"{Math.Round(View.Zoom * 100)}%";
        SelectionInfo.Text = Describe();
        Panel.Refresh();
    }

    private string Describe()
    {
        var unit = Editor.Doc.Setup.Unit;
        if (View.Editing is { } t && Editor.Doc.Stories.TryGetValue(t.StoryId, out var story))
        {
            var layout = View.LayoutOf(t.StoryId);
            var over = layout is { Overset: true } ? $" · {story.Length - layout.OversetFrom} characters overset" : "";
            var words = story.Text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            return t.Length > 0 ? $"Text · {t.Length} selected of {story.Length}{over}" : $"Text · {words} words, {story.ParagraphCount} paragraphs{over}";
        }
        var selected = View.Selected;
        if (selected.Count == 0) return View.Sheet.IsMaster ? "Editing a master page" : "";
        if (selected.Count > 1) return $"{selected.Count} objects";
        var o = selected[0];
        var where = $"X {Units.Format(o.X, unit, false)}  Y {Units.Format(o.Y, unit, false)}  W {Units.Format(o.W, unit, false)}  H {Units.Format(o.H, unit)}";
        if (o is TextFrame f && Editor.Doc.StoryOf(f) is { } s)
            return $"Text frame {s.Frames.IndexOf(f.Id) + 1} of {s.Frames.Count} · {where}";
        return $"{o.Kind} · {where}";
    }

    /// <summary>Shows a page or master page.</summary>
    public void ShowSheet(SheetRef sheet)
    {
        var doc = Editor.Doc;
        if (sheet.IsMaster ? sheet.Index < 0 || sheet.Index >= doc.Masters.Count : sheet.Index < 0 || sheet.Index >= doc.Pages.Count) return;
        View.Sheet = sheet;
        Refresh();
    }

    public void StepPage(int delta)
    {
        var count = View.Sheet.IsMaster ? Editor.Doc.Masters.Count : Editor.Doc.Pages.Count;
        ShowSheet(View.Sheet with { Index = Math.Clamp(View.Sheet.Index + delta, 0, count - 1) });
    }

    public void AddPage(bool after)
    {
        var at = View.Sheet.IsMaster ? Editor.Doc.Pages.Count : View.Sheet.Index + (after ? 1 : 0);
        ShowSheet(SheetRef.PageAt(Editor.AddPage(at)));
        Say($"Added page {at + 1}.");
    }

    public void DeletePage()
    {
        if (View.Sheet.IsMaster) { Say("Pick a page to delete: a master page is shown."); return; }
        var index = View.Sheet.Index;
        if (Editor.DeletePage(index) is { } why) { Say(why); return; }
        ShowSheet(SheetRef.PageAt(Math.Min(index, Editor.Doc.Pages.Count - 1)));
        Say($"Deleted page {index + 1}.");
    }

    private void EditMaster()
    {
        if (View.Sheet.IsMaster) { ShowSheet(SheetRef.PageAt(0)); return; }
        var name = Editor.Doc.Pages[View.Sheet.Index].Master;
        var index = Editor.Doc.Masters.FindIndex(m => m.Name == name);
        if (index < 0) { Say("This page has no master page."); return; }
        ShowSheet(SheetRef.MasterAt(index));
        Say($"Editing {name}: what is drawn here appears under every page that uses it.");
    }

    // ---- Commands -------------------------------------------------------------------------------------------------

    public void Undo()
    {
        if (!Editor.CanUndo) { Say("Nothing to undo."); return; }
        var label = Editor.UndoLabel;
        Editor.Undo();
        Say($"Undid {label?.ToLowerInvariant()}.");
    }

    public void Redo()
    {
        if (!Editor.CanRedo) { Say("Nothing to redo."); return; }
        var label = Editor.RedoLabel;
        Editor.Redo();
        Say($"Redid {label?.ToLowerInvariant()}.");
    }

    private void Arrange(Editor.Order order)
    {
        if (View.SelectedIds.Count > 0) Editor.Arrange(View.SelectedIds.ToList(), order);
    }

    private void Lock(bool locked)
    {
        var ids = View.SelectedIds.ToList();
        if (ids.Count == 0) return;
        Editor.Change(locked ? "Lock" : "Unlock", d => { foreach (var id in ids) if (d.Find(id) is { } o) o.Locked = locked; });
    }

    public void Duplicate()
    {
        var ids = View.Selected.Select(o => o.Id).ToList();
        if (ids.Count == 0) return;
        View.Select(Editor.Duplicate(ids));
    }

    public void UnlinkSelected()
    {
        if (View.Selected is not [TextFrame frame]) { Say("Select one text frame to unlink."); return; }
        Say(Editor.TryChange("Unlink frame", d => Chains.Unlink(d, (TextFrame)d.Find(frame.Id)!)) ?? "Unlinked: the text flows on without that frame.");
    }

    private void PlacePicture()
    {
        if (View.Selected is [PictureObject p]) { _ = View.ReplaceImage(p.Id); return; }
        View.Tool = Tool.Picture;
        Say("Drag out a frame for the picture.");
    }

    public async Task Copy()
    {
        if (View.Editing is not null)
        {
            if (View.SelectedText() is { } text && Clipboard is { } c) await c.SetTextAsync(text.Replace("\n", Environment.NewLine));
            return;
        }
        var ids = View.Selected.Select(o => o.Id).ToList();
        if (ids.Count == 0) return;
        _clipboard = (Editor.Doc.Clone(), ids);
        _pasteCount = 0;
        Say(ids.Count == 1 ? "Copied." : $"Copied {ids.Count} objects.");
    }

    public async Task Cut()
    {
        await Copy();
        if (View.Editing is { Length: > 0 }) View.Type("");
        else if (View.Editing is null) View.DeleteSelection();
    }

    public async Task Paste()
    {
        if (View.Editing is not null)
        {
            if (Clipboard is { } c && await c.TryGetTextAsync() is { Length: > 0 } text) View.Type(text);
            return;
        }
        if (_clipboard is not { } clip) return;
        _pasteCount++;
        View.Select(Editor.Paste(View.Sheet, clip.Doc, clip.Ids, 12 * _pasteCount));
    }

    // ---- Files ----------------------------------------------------------------------------------------------------

    /// <summary>Asks whether to save changes; false if the person cancelled.</summary>
    private async Task<bool> SaveChanges()
    {
        if (!Editor.Dirty) return true;
        var name = FilePath is null ? "this new document" : Path.GetFileName(FilePath);
        var answer = await Ask("Save changes?", $"Save the changes to {name}?", ["Save", "Don't save", "Cancel"]);
        return answer switch
        {
            0 => await Save(),
            1 => true,
            _ => false,
        };
    }

    private void Use(Editor editor, string? path)
    {
        Editor.Changed -= OnChanged;
        Editor = editor;
        Editor.Changed += OnChanged;
        FilePath = path;
        _pasteCount = 0;
        View.Editor = editor;
        Refresh();
    }

    public async Task New()
    {
        if (!await SaveChanges()) return;
        Use(new Editor(Document.New(new Setup(Unit: Settings.Unit))), null);
        Say("A new document.");
    }

    private async Task OpenWithPicker()
    {
        if (!await SaveChanges()) return;
        if (await PickOpen("document") is { } path) OpenFile(path);
    }

    /// <summary>Opens a document, replacing the one shown (changes to it are not asked about: callers ask first).</summary>
    public bool OpenFile(string path)
    {
        try
        {
            var doc = DocumentFile.Load(path);
            Use(new Editor(doc), path);
            Settings.Opened(path);
            SaveSettings();
            Say($"Opened {Path.GetFileName(path)}: {doc.Pages.Count} page{(doc.Pages.Count == 1 ? "" : "s")}.");
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Say(e is FileNotFoundException or DirectoryNotFoundException ? $"{Path.GetFileName(path)} isn't there any more." : $"Couldn't open {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    public async Task<bool> Save() => FilePath is null ? await SaveAs() : SaveTo(FilePath);

    public async Task<bool> SaveAs()
    {
        var suggested = FilePath is null ? "Untitled" + DocumentFile.Extension : Path.GetFileName(FilePath);
        return await PickSave("document", suggested) is { } path && SaveTo(path);
    }

    public bool SaveTo(string path)
    {
        if (!path.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase)) path += DocumentFile.Extension;
        try
        {
            DocumentFile.Save(Editor.Doc, path);
            FilePath = path;
            Editor.MarkSaved();
            Settings.Opened(path);
            SaveSettings();
            UpdateTitle();
            Say($"Saved {Path.GetFileName(path)}.");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    private string BaseName => FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);

    public async Task ExportPdf()
    {
        if (await PickSave("pdf", BaseName + ".pdf") is not { } path) return;
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";
        try
        {
            await Export.Pdf(Editor.Doc, path, title: BaseName);
            Say($"Exported {Editor.Doc.Pages.Count} page{(Editor.Doc.Pages.Count == 1 ? "" : "s")} to {Path.GetFileName(path)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't export {Path.GetFileName(path)}: {e.Message}");
        }
    }

    public async Task ExportPng(bool all)
    {
        if (View.Sheet.IsMaster && !all) { Say("Show a page to export it."); return; }
        if (await PickSave("png", BaseName + ".png") is not { } path) return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
        var doc = Editor.Doc;
        var pages = all ? Enumerable.Range(0, doc.Pages.Count).ToList() : [View.Sheet.Index];
        try
        {
            foreach (var i in pages) Export.Png(doc, doc.Pages[i], Export.Numbered(path, i, all ? pages.Count : 1), Settings.ExportDpi);
            Say(pages.Count == 1 ? $"Exported {Path.GetFileName(path)} at {Settings.ExportDpi} dpi." : $"Exported {pages.Count} pages as PNG at {Settings.ExportDpi} dpi.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't export: {e.Message}");
        }
    }

    private void FillRecent()
    {
        _recentMenu.Items.Clear();
        foreach (var path in Settings.Recent)
        {
            var item = new MenuItem { Header = path.Replace("_", "__") };
            item.Click += async (_, _) => { if (await SaveChanges()) OpenFile(path); };
            _recentMenu.Items.Add(item);
        }
        if (_recentMenu.Items.Count == 0) _recentMenu.Items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false });
    }

    private async Task<string?> PickOpenFile(string kind)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = kind == "image" ? "Place a picture" : "Open a document",
            AllowMultiple = false,
            FileTypeFilter = kind == "image"
                ? [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] }]
                : [new FilePickerFileType("AVAPageStream documents") { Patterns = ["*" + DocumentFile.Extension] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveFile(string kind, string suggested)
    {
        var (name, pattern, ext) = kind switch
        {
            "pdf" => ("PDF", "*.pdf", "pdf"),
            "png" => ("PNG picture", "*.png", "png"),
            _ => ("AVAPageStream document", "*" + DocumentFile.Extension, DocumentFile.Extension.TrimStart('.')),
        };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = kind == "document" ? "Save the document" : "Export",
            SuggestedFileName = suggested,
            DefaultExtension = ext,
            FileTypeChoices = [new FilePickerFileType(name) { Patterns = [pattern] }],
        });
        return file?.TryGetLocalPath();
    }

    private async Task ShowHelp() => await Ask("Keys and tools",
        "Tools: V select, T text, R rectangle, E ellipse, L line, P picture, K link frames, H hand (or hold Space).\n\n" +
        "Draw a text frame with T and type. Click a frame's red overset mark, or use K, to link it to another frame: the " +
        "text flows on into it. Double-click a frame with Select to edit its text; Escape leaves the text.\n\n" +
        "Drag from a ruler to make a guide; drag it back to remove it. Arrows nudge (Shift: 10 pt). Shift keeps shapes " +
        "square and lines at 45°. Ctrl+wheel zooms; Ctrl+0 fits the page.\n\n" +
        "Measures can be typed in any unit: 54, 0.75in, 4p6, 20mm.", ["OK"]);

    // ---- Keys ---------------------------------------------------------------------------------------------------------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (!command) return;
        // A box in the side panel keeps its own editing keys.
        var inBox = FocusManager?.GetFocusedElement() is TextBox;
        switch (e.Key)
        {
            case Key.N: _ = New(); break;
            case Key.O: _ = OpenWithPicker(); break;
            case Key.S when shift: _ = SaveAs(); break;
            case Key.S: _ = Save(); break;
            case Key.E: _ = ExportPdf(); break;
            case Key.Q: Close(); break;
            case Key.Z when inBox: return;
            case Key.Z when shift: Redo(); break;
            case Key.Z: Undo(); break;
            case Key.Y when inBox: return;
            case Key.Y: Redo(); break;
            case Key.X or Key.C or Key.V or Key.A when inBox: return;
            case Key.X: _ = Cut(); break;
            case Key.C: _ = Copy(); break;
            case Key.V: _ = Paste(); break;
            case Key.A: View.SelectAll(); break;
            case Key.D: Duplicate(); break;
            case Key.D0 or Key.NumPad0: View.FitPage(); break;
            case Key.D1 or Key.NumPad1: View.ActualSize(); break;
            case Key.OemPlus or Key.Add: View.SetZoom(View.Zoom * 1.25); break;
            case Key.OemMinus or Key.Subtract: View.SetZoom(View.Zoom / 1.25); break;
            case Key.OemCloseBrackets: Arrange(shift ? Editor.Order.Front : Editor.Order.Forward); break;
            case Key.OemOpenBrackets: Arrange(shift ? Editor.Order.Back : Editor.Order.Backward); break;
            case Key.B or Key.I or Key.U when View.Editing is null && !inBox:
                if (e.Key == Key.B) View.ToggleChar(f => f.Bold, (f, v) => f with { Bold = v }, "Bold");
                else if (e.Key == Key.I) View.ToggleChar(f => f.Italic, (f, v) => f with { Italic = v }, "Italic");
                else View.ToggleChar(f => f.Underline, (f, v) => f with { Underline = v }, "Underline");
                break;
            default: return;
        }
        e.Handled = true;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closing || !Editor.Dirty) return;
        e.Cancel = true;
        if (!await SaveChanges()) return;
        _closing = true;
        Close();
    }
}
