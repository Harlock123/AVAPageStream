using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    private (Fragment Fragment, string Plain)? _textClipboard; // text copied here, with its formats
    private readonly List<StoryEditor> _storyEditors = [];
    private int _pasteCount;

    public AppSettings Settings { get; }
    public Editor Editor { get; private set; }
    public PageView View { get; }
    public SidePanel Panel { get; }
    public PageScroller Scroller { get; }

    /// <summary>The file the document was opened from or last saved to, or null for a new one.</summary>
    public string? FilePath { get; private set; }

    public TextBlock Message { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    public TextBlock SelectionInfo { get; } = new() { Foreground = Look.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    public TextBlock CursorInfo { get; } = new() { Foreground = Look.Heading, FontSize = 12, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
    public TextBlock ZoomInfo { get; } = new() { Foreground = Look.Heading, FontSize = 12, MinWidth = 46, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    public TextBlock PageInfo { get; } = new() { Foreground = Look.Text, FontSize = 12, MinWidth = 96, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    public Dictionary<Tool, ToggleButton> ToolButtons { get; } = [];

    // The find bar
    public Border FindBar { get; } = new() { IsVisible = false };
    public TextBox FindBox { get; } = new() { Width = 220, PlaceholderText = "Find", FontSize = 12 };
    public TextBox ReplaceBox { get; } = new() { Width = 200, PlaceholderText = "Replace with", FontSize = 12 };
    public CheckBox MatchCase { get; } = new() { Content = "Match case", FontSize = 12 };
    public CheckBox WholeWord { get; } = new() { Content = "Whole word", FontSize = 12 };
    public CheckBox Pattern { get; } = new() { Content = "Pattern", FontSize = 12 };

    /// <summary>Which text to look in: any, or one paragraph style's or character style's (as "¶ Name" and "a Name").</summary>
    public ComboBox FindStyle { get; } = new() { FontSize = 12, MinWidth = 130 };
    private const string AnyStyle = "In any style";
    public TextBlock FindStatus { get; } = new() { Foreground = Look.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly MenuItem _recentMenu = new() { Header = "Open _recent" };
    private readonly MenuItem _templatesMenu = new() { Header = "New from _template" };
    private readonly MenuItem _snapItem = new() { Header = "_Snap to guides", ToggleType = MenuItemToggleType.CheckBox };
    private readonly MenuItem _guidesItem = new() { Header = "Show _guides", ToggleType = MenuItemToggleType.CheckBox };
    private readonly MenuItem _gridItem = new() { Header = "Show g_rid", ToggleType = MenuItemToggleType.CheckBox };
    private readonly MenuItem _snapGridItem = new() { Header = "Snap to gr_id", ToggleType = MenuItemToggleType.CheckBox };

    /// <summary>Asks a question (title, message, buttons) and returns the button pressed; tests answer it themselves.</summary>
    public Func<string, string, string[], Task<int>> Ask { get; set; }

    /// <summary>Asks which pages to export (kind "pdf" or "png") and at what resolution; tests answer it themselves.</summary>
    public Func<string, Task<ExportChoice?>> AskExport { get; set; }

    /// <summary>Asks for a line of text (title, message, what it starts as); null if cancelled.</summary>
    public Func<string, string, string, Task<string?>> AskText { get; set; }

    /// <summary>Asks for an index entry, starting from the words given.</summary>
    public Func<string, Task<IndexMark?>> AskIndex { get; set; }

    /// <summary>Asks what to refer to, from a list of headings and places, and how.</summary>
    public Func<IReadOnlyList<string>, Task<ReferenceChoice?>> AskReference { get; set; }

    /// <summary>Asks which styles a table of contents lists; tests answer it themselves.</summary>
    public Func<IReadOnlyList<string>, Task<List<string>?>> AskContents { get; set; } = _ => Task.FromResult<List<string>?>(null);

    /// <summary>Sends a PDF to the printer, returning why it couldn't; tests answer it themselves.</summary>
    public Func<string, Task<string?>> SendToPrinter { get; set; } = Printing.Send;

    /// <summary>The user's own dictionary: a word a line.</summary>
    public string UserWordsPath { get; set; } = Path.Combine(AppSettings.Folder, "words.txt");

    private HashSet<string>? _userWords;

    /// <summary>The words spelling passes besides the dictionary's: the user's own and the document's ignored ones.</summary>
    public ISet<string> SpellingWords()
    {
        _userWords ??= File.Exists(UserWordsPath) ? [.. File.ReadAllLines(UserWordsPath).Select(l => l.Trim()).Where(l => l.Length > 0)] : [];
        var all = new HashSet<string>(_userWords);
        all.UnionWith(Editor.Doc.IgnoredWords);
        return all;
    }

    /// <summary>Adds a word to the user's own dictionary.</summary>
    public void AddWord(string word)
    {
        SpellingWords();
        if (!_userWords!.Add(word)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserWordsPath)!);
            File.WriteAllLines(UserWordsPath, _userWords.Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save the dictionary: {e.Message}");
        }
        View.RecheckSpelling();
        Say($"Added \"{word}\" to your dictionary.");
    }

    /// <summary>Opens the spelling window, which goes from the caret through the document.</summary>
    /// <summary>What would go wrong in print, as the document is now.</summary>
    public IReadOnlyList<Issue> Preflight()
    {
        var pictures = new PageRenderer(View.Layouts);
        var doc = Editor.Doc;
        return Core.Preflight.Check(doc, new PreflightOptions(FontMeasurer.Shared, key => pictures.PixelsOf(doc, key), FontSupport.State, DocumentFolder));
    }

    public PreflightWindow OpenPreflight()
    {
        var w = new PreflightWindow(this);
        w.Show(this);
        return w;
    }

    /// <summary>Shows what a preflight entry is about: its page, with the object selected, or the caret where its text is.</summary>
    public void ShowIssue(Issue issue)
    {
        if (issue.StoryId is { } story && View.Reveal(story, Math.Min(issue.At, Editor.Doc.Stories.GetValueOrDefault(story)?.Length ?? 0), 0)) { Say(issue.Message); return; }
        if (issue.Sheet is { } sheet) ShowSheet(sheet);
        View.StopEditing();
        View.Select([issue.ObjectId]);
        Say(issue.Message);
    }

    /// <summary>A word on preflight after exporting or printing, if it found anything that will show.</summary>
    private string PreflightNote()
    {
        var serious = Preflight().Count(i => i.Serious);
        return serious == 0 ? "" : $" Preflight found {serious} problem{(serious == 1 ? "" : "s")} that will show: File › Preflight lists {(serious == 1 ? "it" : "them")}.";
    }

    /// <summary>Asks for a folder to put a package in; tests answer it themselves.</summary>
    public Func<Task<string?>> PickFolder { get; set; }

    /// <summary>Gathers the document, its linked pictures and its fonts into a new folder, with a report.</summary>
    public async Task<PackageResult?> MakePackage()
    {
        if (await PickFolder() is not { } parent) return null;
        var name = FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);
        var folder = Path.Combine(parent, name + " package");
        for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(parent, $"{name} package {n}");
        try
        {
            var result = Package.Make(Editor.Doc, name, DocumentFolder, folder, FontSupport.Files, Preflight());
            Say($"Packaged {name} in {Path.GetFileName(folder)}: {result.Pictures} linked picture{(result.Pictures == 1 ? "" : "s")} and {result.Fonts} font file{(result.Fonts == 1 ? "" : "s")}" +
                (result.Missing.Count > 0 ? $"; {result.Missing.Count} couldn't be found (see Report.txt)." : ", with Report.txt."));
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't make the package: {e.Message}");
            return null;
        }
    }

    private async Task<string?> PickFolderDialog()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where to put the package", AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public SpellingWindow OpenSpelling()
    {
        var w = new SpellingWindow(this);
        w.Show(this);
        return w;
    }

    private readonly MenuItem _spellItem = new() { Header = "Check spelling as you _type", ToggleType = MenuItemToggleType.CheckBox };

    /// <summary>The folder templates are saved in.</summary>
    public string TemplatesFolder { get; set; } = Path.Combine(AppSettings.Folder, "templates");

    /// <summary>Asks for a file to open (by kind: "document" or "image"); tests answer it themselves.</summary>
    public Func<string, Task<string?>> PickOpen { get; set; }

    /// <summary>Asks where to save (kind "document", "pdf" or "png", and a suggested name); tests answer it themselves.</summary>
    public Func<string, string, Task<string?>> PickSave { get; set; }

    /// <summary>Where unsaved work is copied every few minutes, beside the settings.</summary>
    public Recovery Recovery { get; }

    /// <summary>This window's copy in <see cref="Recovery"/>.</summary>
    public string RecoveryId { get; private set; } = Guid.NewGuid().ToString("N");

    private int _autosavedStamp = -1;
    private readonly DispatcherTimer _autosave = new();

    public MainWindow(string settingsPath, string? open = null)
    {
        _settingsPath = settingsPath;
        Recovery = new Recovery(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "Recovery"));
        Settings = AppSettings.Load(settingsPath);
        Editor = new Editor(Document.New(new Setup(Unit: Settings.Unit)));
        Ask = (title, message, buttons) => AskDialog.Show(this, title, message, buttons);
        PickOpen = PickOpenFile;
        PickSave = PickSaveFile;
        PickFolder = PickFolderDialog;

        Width = 1360;
        Height = 880;
        MinWidth = 900;
        MinHeight = 560;
        Background = Look.Window;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AVAPageStream/Assets/AVAPageStream.png")));

        View = new PageView(Editor) { Snap = Settings.Snap, ShowGuides = Settings.ShowGuides, ShowGrid = Settings.ShowGrid, SnapGrid = Settings.SnapGrid, CheckSpelling = Settings.CheckSpelling };
        View.SpellingWords = SpellingWords;
        AskText = (title, message, start) => TextDialog.Show(this, title, message, start);
        AskReference = targets => ReferenceDialog.Show(this, targets);
        AskIndex = topic => IndexDialog.Show(this, topic);
        AskExport = kind => ExportDialog.Show(this, kind, Editor.Doc.Pages.Count, View.Sheet.IsMaster ? -1 : View.Sheet.Index, Settings.ExportDpi);
        AskContents = styles => ContentsDialog.Show(this, styles);
        View.AskPassword = (file, wrong) => PasswordDialog.Show(this, file, wrong);
        View.PickImage = async () => await PickOpen("image") is { } path ? (await File.ReadAllBytesAsync(path), path) : null;
        View.SelectionChanged += Refresh;
        View.ViewChanged += () => ZoomInfo.Text = $"{Math.Round(View.Zoom * 100)}%";
        View.Status += Say;
        View.PointerAt += p => CursorInfo.Text = p is { } pt
            ? $"{Units.Format(pt.X, Editor.Doc.Setup.Unit, false)}, {Units.Format(pt.Y, Editor.Doc.Setup.Unit)}" : "";
        View.ShowSheet += ShowSheet;
        Editor.Changed += OnChanged;
        Panel = new SidePanel(this);

        var centre = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,330") };
        Scroller = new PageScroller(View);
        centre.Children.Add(new Border { Child = Scroller, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 1, 1, 0) });
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
        BuildFindBar();
        DockPanel.SetDock(FindBar, Dock.Bottom);
        dock.Children.Add(FindBar);
        var box = BuildToolbox();
        DockPanel.SetDock(box, Dock.Left);
        dock.Children.Add(box);
        dock.Children.Add(centre);
        Content = dock;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => View.Focus();
        Activated += (_, _) => UpdateLinks(quiet: true);
        if (Settings.AutosaveMinutes > 0)
        {
            _autosave.Interval = TimeSpan.FromMinutes(Settings.AutosaveMinutes);
            _autosave.Tick += (_, _) => Autosave();
            _autosave.Start();
            Closed += (_, _) => _autosave.Stop();
        }
        if (open is null) Opened += async (_, _) => await OfferRecovery();
        _snapItem.IsChecked = View.Snap;
        _guidesItem.IsChecked = View.ShowGuides;
        _gridItem.IsChecked = View.ShowGrid;
        _snapGridItem.IsChecked = View.SnapGrid;

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
        _gridItem.IsChecked = View.ShowGrid;
        _snapGridItem.IsChecked = View.SnapGrid;
            View.InvalidateVisual();
            SaveSettings();
        };
        _gridItem.Click += (_, _) => SetGrid(show: !View.ShowGrid, snap: View.SnapGrid);
        _spellItem.IsChecked = Settings.CheckSpelling;
        _spellItem.Click += (_, _) =>
        {
            View.CheckSpelling = Settings.CheckSpelling = !View.CheckSpelling;
            _spellItem.IsChecked = View.CheckSpelling;
            View.InvalidateVisual();
            SaveSettings();
        };
        _snapGridItem.Click += (_, _) => SetGrid(show: View.ShowGrid || !View.SnapGrid, snap: !View.SnapGrid);
        _recentMenu.SubmenuOpened += (_, _) => FillRecent();
        _templatesMenu.SubmenuOpened += (_, _) => FillTemplates();
        FillTemplates();
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
                        _templatesMenu,
                        Item("_Open…", () => _ = OpenWithPicker(), "Ctrl+O"),
                        _recentMenu,
                        new Separator(),
                        Item("_Save", () => _ = Save(), "Ctrl+S"),
                        Item("Save _as…", () => _ = SaveAs(), "Ctrl+Shift+S"),
                        Item("Save as _template…", () => _ = SaveAsTemplate()),
                        new Separator(),
                        Item("Export _PDF…", () => _ = ExportPdf(), "Ctrl+E"),
                        Item("Export P_NG…", () => _ = ExportPng()),
                        Item("P_rint…", () => _ = Print(), "Ctrl+P"),
                        new Separator(),
                        Item("Pre_flight…", () => OpenPreflight()),
                        Item("Pac_kage…", () => _ = MakePackage()),
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
                        new Separator(),
                        Item("_Find and replace…", ShowFind, "Ctrl+F"),
                        Item("Find _next", () => FindNext(), "F3"),
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
                        _gridItem,
                        _snapGridItem,
                        _spellItem,
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
                        Item("_Group", GroupSelected, "Ctrl+G"),
                        Item("U_ngroup", UngroupSelected, "Ctrl+Shift+G"),
                        new Separator(),
                        Item("_Lock", () => Lock(true)),
                        Item("_Unlock", () => Lock(false)),
                        new Separator(),
                        Item("_Place picture…", PlacePicture),
                        Item("Place _text…", () => _ = PlaceText()),
                        Item("Unlink text _frame", UnlinkSelected),
                        Item("Te_xt on path", TextOnPath),
                        Item("Text to _outlines", TextToOutlines, "Ctrl+Shift+O"),
                        Item("_Anchor to text", AnchorToText),
                        Item("_Release from text", ReleaseFromText),
                        new Separator(),
                        new MenuItem
                        {
                            Header = "T_able",
                            Items =
                            {
                                Item("Insert row _above", () => TableEdit("Insert row", (d, t, r, _) => Tables.InsertRow(d, t, r))),
                                Item("Insert row _below", () => TableEdit("Insert row", (d, t, r, _) => Tables.InsertRow(d, t, r + 1))),
                                Item("Insert column _left", () => TableEdit("Insert column", (d, t, _, c) => Tables.InsertColumn(d, t, c))),
                                Item("Insert column _right", () => TableEdit("Insert column", (d, t, _, c) => Tables.InsertColumn(d, t, c + 1))),
                                new Separator(),
                                Item("_Delete row", () => TableEdit("Delete row", (d, t, r, _) => Tables.DeleteRow(d, t, r))),
                                Item("Delete _column", () => TableEdit("Delete column", (d, t, _, c) => Tables.DeleteColumn(d, t, c))),
                                new Separator(),
                                Item("_Run on to the next page", RunOnTable),
                            },
                        },
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
                        Item("_Story editor…", () => OpenStoryEditor(), "Ctrl+Shift+E"),
                        Item("Table of _contents…", () => _ = MakeContents()),
                        Item("In_dex", MakeIndex),
                        Item("_Update contents and index", () => UpdateLists()),
                        Item("Check _spelling…", () => OpenSpelling(), "F7"),
                        new Separator(),
                        Item("Insert page _number", () => View.InsertField(Fields.PageNumber), "Ctrl+Shift+D3"),
                        Item("Insert page c_ount", () => View.InsertField(Fields.PageCount)),
                        Item("Insert _footnote", () => InsertFootnote(), "Ctrl+Alt+F"),
                        Item("_Name this place…", () => _ = NamePlace()),
                        Item("Insert _cross-reference…", () => _ = InsertCrossReference()),
                        Item("Mark _index entry…", () => _ = MarkIndexEntry(), "Ctrl+Alt+I"),
                        Item("_Edit footnote", () => EditFootnote()),
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
        (Tool.Pen, "Pen: polygons and curves", "B", "M3,17 L4.5,12.5 L13,4 L16,7 L7.5,15.5 Z M14,3 L15.5,1.5 L18.5,4.5 L17,6 Z M3,17 L5.5,16.3 L3.7,14.5 Z"),
        (Tool.Table, "Table", "G", "F0 M2,3 H18 V17 H2 Z M3.6,4.6 V7.6 H9.2 V4.6 Z M10.8,4.6 V7.6 H16.4 V4.6 Z M3.6,9.2 V11.6 H9.2 V9.2 Z M10.8,9.2 V11.6 H16.4 V9.2 Z M3.6,13.2 V15.4 H9.2 V13.2 Z M10.8,13.2 V15.4 H16.4 V13.2 Z"),
        (Tool.Gradient, "Gradient: drag across an object", "D", "F0 M2,3 H18 V17 H2 Z M3.8,4.8 V15.2 H16.2 V4.8 Z M5,6 H7 V14 H5 Z M8.5,6 H10 V14 H8.5 Z M11.3,6 H12.3 V14 H11.3 Z M13.4,6 H13.9 V14 H13.4 Z"),
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

    private void BuildFindBar()
    {
        Button B(string text, Action click, string tip)
        {
            var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(10, 3) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        FindBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { FindNext((e.KeyModifiers & KeyModifiers.Shift) == 0); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        ReplaceBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { ReplaceNext(); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        FindBox.TextChanged += (_, _) => FindStatus.Text = "";
        ToolTip.SetTip(Pattern, "Find a regular expression: . any character, \\d a digit, [a-z] a range, ( ) a group; $1 in the replacement is the first group, \\n a new paragraph");
        ToolTip.SetTip(FindStyle, "Find only in text of a paragraph style (¶) or a character style (a). With nothing to find, it finds each stretch in the style.");
        FindStyle.ItemsSource = new[] { AnyStyle };
        FindStyle.SelectedIndex = 0;
        var lastStyle = AnyStyle;
        FindStyle.SelectionChanged += (_, _) =>
        {
            if (FindStyle.SelectedItem is string s && s != lastStyle) { lastStyle = s; FindStatus.Text = ""; }
        };
        FindBar.Background = Look.Bar;
        FindBar.BorderBrush = Look.Border;
        FindBar.BorderThickness = new Thickness(0, 1, 0, 0);
        FindBar.Padding = new Thickness(10, 5);
        FindBar.Child = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                FindBox,
                B("Previous", () => FindNext(false), "Find the one before (Shift+Enter)"),
                B("Next", () => FindNext(true), "Find the next one (Enter, or F3)"),
                ReplaceBox,
                B("Replace", ReplaceNext, "Replace the one found, and find the next"),
                B("Replace all", ReplaceAll, "Replace every one in the document, as one step to undo"),
                MatchCase,
                WholeWord,
                Pattern,
                FindStyle,
                FindStatus,
                B("✕", HideFind, "Close (Escape)"),
            },
        };
        foreach (var c in ((WrapPanel)FindBar.Child).Children) c.Margin = new Thickness(0, 0, 6, 0);
    }

    private FindOptions FindOptions
    {
        get
        {
            var style = FindStyle.SelectedItem as string;
            return new(MatchCase.IsChecked == true, WholeWord.IsChecked == true, Pattern.IsChecked == true,
                style?.StartsWith("¶ ", StringComparison.Ordinal) == true ? style[2..] : null,
                style?.StartsWith("a ", StringComparison.Ordinal) == true ? style[2..] : null);
        }
    }

    /// <summary>The styles the find bar can look in, from the document's, keeping the one chosen if it is still there.</summary>
    private void RefreshFindStyles()
    {
        var items = new List<string> { AnyStyle };
        items.AddRange(Editor.Doc.Styles.Select(s => "¶ " + s.Name));
        items.AddRange(Editor.Doc.CharStyles.Select(s => "a " + s.Name));
        var chosen = FindStyle.SelectedItem as string;
        if (FindStyle.ItemsSource is IEnumerable<string> old && old.SequenceEqual(items)) return;
        FindStyle.ItemsSource = items;
        FindStyle.SelectedIndex = Math.Max(0, items.IndexOf(chosen ?? AnyStyle));
    }

    /// <summary>Shows the find bar, starting from any text selected.</summary>
    public void ShowFind()
    {
        RefreshFindStyles();
        FindBar.IsVisible = true;
        if (View.SelectedText() is { Length: > 0 and < 200 } selected && !selected.Contains('\n')) FindBox.Text = selected;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    public void HideFind()
    {
        FindBar.IsVisible = false;
        View.Focus();
    }

    /// <summary>Finds the next (or previous) match after the selection, round every story, and shows it.</summary>
    public bool FindNext(bool forward = true)
    {
        var query = FindBox.Text ?? "";
        RefreshFindStyles();
        if (Search.Problem(Editor.Doc, query, FindOptions) is { } problem) { FindStatus.Text = problem; return false; }
        var t = View.Editing;
        var from = t is null ? 0 : forward ? t.Start + t.Length : t.Start;
        var match = Search.Next(Editor.Doc, query, FindOptions, t?.StoryId, from, forward);
        if (match is null)
        {
            FindStatus.Text = "Not found.";
            return false;
        }
        View.Reveal(match.StoryId, match.Start, match.Length);
        var all = Search.FindAll(Editor.Doc, query, FindOptions);
        FindStatus.Text = $"{all.FindIndex(m => m == match) + 1} of {all.Count}";
        Refresh();
        return true;
    }

    /// <summary>Replaces the match that is selected (if one is) and finds the next.</summary>
    public void ReplaceNext()
    {
        var query = FindBox.Text ?? "";
        if (View.Editing is { Length: > 0 } t && new Match(t.StoryId, t.Start, t.Length) is var found &&
            Search.IsFound(Editor.Doc, found, query, FindOptions))
        {
            var after = Editor.ReplaceOne(found, Search.Replacement(Editor.Doc, found, query, ReplaceBox.Text ?? "", FindOptions));
            t.Caret = t.Anchor = after;
        }
        if (!FindNext()) FindStatus.Text = Search.FindAll(Editor.Doc, query, FindOptions).Count == 0 ? "None left." : FindStatus.Text;
    }

    public void ReplaceAll()
    {
        var query = FindBox.Text ?? "";
        RefreshFindStyles();
        if (Search.Problem(Editor.Doc, query, FindOptions) is { } problem) { FindStatus.Text = problem; return; }
        var n = Editor.ReplaceAll(query, ReplaceBox.Text ?? "", FindOptions);
        var what = query.Length > 0 ? $"\"{query}\"" : "text in " + (FindOptions.ParaStyle ?? FindOptions.CharStyle);
        FindStatus.Text = n switch { 0 => "Not found.", 1 => "Replaced 1.", _ => $"Replaced {n}." };
        Say(n == 0 ? $"There is no {what} in the document." : $"Replaced {n} of {what}; one undo puts them all back.");
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

    /// <summary>Shows or hides the document grid, and snaps to it or not (snapping shows it).</summary>
    public void SetGrid(bool show, bool snap)
    {
        View.ShowGrid = Settings.ShowGrid = show;
        View.SnapGrid = Settings.SnapGrid = snap;
        _gridItem.IsChecked = show;
        _snapGridItem.IsChecked = snap;
        View.InvalidateVisual();
        SaveSettings();
    }

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
            ? doc.Masters[View.Sheet.Index].Name + (doc.Setup.Facing ? View.Sheet.Left ? " (left)" : " (right)" : "")
            : doc.PageLabel(View.Sheet.Index) is var label && label != (View.Sheet.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                ? $"Page {View.Sheet.Index + 1} ({label}) of {doc.Pages.Count}"
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

    public void EditMaster()
    {
        if (View.Sheet.IsMaster) { ShowSheet(SheetRef.PageAt(0)); return; }
        var page = Editor.Doc.Pages[View.Sheet.Index];
        var index = Editor.Doc.Masters.FindIndex(m => m.Name == page.Master);
        if (index < 0) { Say("This page has no master page."); return; }
        var name = page.Master;
        ShowSheet(SheetRef.MasterAt(index, left: Editor.Doc.IsLeft(page)));
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

    /// <summary>Puts a footnote at the caret and the caret in the note, at the foot of its column, to write it.</summary>
    /// <summary>Names the place at the caret, for cross-references to point at.</summary>
    public async Task NamePlace()
    {
        if (View.Editing is not { } t) { Say("Put the caret where the place is first."); return; }
        if (await AskText("Name this place", "A name for this place in the text, for cross-references to point at:", "") is not { } name) return;
        if (Editor.NamePlace(t.StoryId, t.Start, name) is { } why) { Say(why); return; }
        t.Caret = t.Anchor = t.Start + 1;
        Say($"Named this place {name.Trim()}: Text › Insert cross-reference can point at it.");
    }

    /// <summary>Puts a cross-reference at the caret, to a heading or a named place.</summary>
    public async Task InsertCrossReference()
    {
        if (View.Editing is not { } t) { Say("Put the caret where the cross-reference goes first."); return; }
        var doc = Editor.Doc;
        // The places named by hand, then the headings (in the styles PDF bookmarks are made from), in reading order.
        var targets = new List<(string Label, string? Place, string? Story, int Paragraph)>();
        foreach (var p in References.Places(doc).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            if (!IsHeadingPlace(p)) targets.Add(($"Place: {p.Name}", p.Name, null, 0));
        var headings = PdfExtras.OutlineStyles(doc).ToHashSet();
        foreach (var id in Search.StoryOrder(doc))
        {
            var story = doc.Stories[id];
            for (var k = 0; k < story.ParagraphCount; k++)
            {
                if (!headings.Contains(story.Paras[k].Style)) continue;
                var (start, end) = story.ParagraphRange(k);
                var text = Fields.Expand(story.Text[start..end], null).Trim();
                if (text.Length > 0) targets.Add((text, null, id, k));
            }
        }
        if (targets.Count == 0) { Say("There are no headings or named places to refer to yet."); return; }
        if (await AskReference(targets.Select(x => x.Label).ToList()) is not { } choice || choice.Target < 0 || choice.Target >= targets.Count) return;
        var target = targets[choice.Target];
        var name = target.Place ?? Editor.PlaceForParagraph(target.Story!, target.Paragraph);
        var at = t.Start;
        // Naming a heading's place earlier in the same story moves the caret on by its marker.
        if (target.Story == t.StoryId && doc.Stories[t.StoryId].ParagraphRange(target.Paragraph).Start <= at && target.Place is null) at++;
        Editor.InsertReference(t.StoryId, at, name, choice.Kind);
        t.Caret = t.Anchor = at + 1;
        View.Reveal(t.StoryId, t.Caret, 0);
        Say("Inserted a cross-reference: it keeps up as pages move, and links to its place in PDFs.");

        bool IsHeadingPlace(NamedPlace p) =>
            doc.Stories.TryGetValue(p.StoryId, out var s) && PdfExtras.OutlineStyles(doc).Contains(s.Paras[s.ParagraphAt(p.Index)].Style)
            && s.Text[s.ParagraphRange(s.ParagraphAt(p.Index)).Start..p.Index].All(Fields.IsField);
    }

    /// <summary>Marks an index entry where the caret is (or the selection starts), its topic the selected words.</summary>
    public async Task MarkIndexEntry()
    {
        if (View.Editing is not { } t) { Say("Put the caret in the text the entry is for, or select its words."); return; }
        var words = t.Length > 0 ? Fields.Expand(Editor.Doc.Stories[t.StoryId].Slice(t.Start, t.Length), null).Trim() : "";
        if (await AskIndex(words) is not { } mark) return;
        if (Editor.MarkIndex(t.StoryId, t.Start, mark) is { } why) { Say(why); return; }
        if (t.Caret >= t.Start) t.Caret++;
        if (t.Anchor >= t.Start) t.Anchor++;
        Say($"Marked an index entry for {mark.Topic.Trim()}{(mark.Sub.Trim().Length > 0 ? ", " + mark.Sub.Trim() : "")}. Text › Index makes the index.");
    }

    /// <summary>Puts an index of the document's entries at the caret, or at the end of the selected frame's story.</summary>
    public void MakeIndex()
    {
        string storyId;
        int at;
        if (View.Editing is { } t) (storyId, at) = (t.StoryId, t.Caret);
        else if (View.Selected is [TextFrame f] && Editor.Doc.StoryOf(f) is { } s) (storyId, at) = (s.Id, s.Length);
        else { Say("Put the caret in a text frame, or select one, for the index to go in."); return; }
        var again = GeneratedLists.At(Editor.Doc, storyId, at) is { Kind: ListKind.Index };
        var caret = Editor.InsertIndex(storyId, at, FontMeasurer.Shared);
        if (caret < 0) { Say("Nothing is marked for the index yet: Text › Mark index entry marks it."); return; }
        View.Reveal(storyId, caret, 0);
        var topics = Indexes.Paragraphs(Editor.Doc, Indexes.Entries(Editor.Doc, FontMeasurer.Shared)).Count(p => p.Style == Indexes.StyleFor(0));
        Say($"{(again ? "Made the index again" : "Made an index")} of {topics} topic{(topics == 1 ? "" : "s")}. It keeps up when you export or print, or choose Text › Update contents and index.");
        Refresh();
    }

    public bool InsertFootnote()
    {
        if (View.Editing is not { } t) { Say("Put the caret where the footnote's number goes first."); return false; }
        if (t.Length > 0) View.Type("");
        var at = View.Editing!.Caret;
        if (Editor.InsertFootnote(t.StoryId, at) is not { } note) { Say("A footnote can't go in a footnote, a table's cell or text on a path."); return false; }
        View.Reveal(note, Editor.Doc.Stories[note].Length, 0);
        Say("Write the footnote: it is set at the foot of the column its number is in. Escape goes back to the text.");
        return true;
    }

    /// <summary>Puts the caret in the footnote whose number the caret is beside.</summary>
    public bool EditFootnote()
    {
        if (View.Editing is not { } t || !Editor.Doc.Stories.TryGetValue(t.StoryId, out var story) || Footnotes.At(story, t.Caret) is not { } note)
        {
            Say("Put the caret beside a footnote's number in the text first, or click into the footnote.");
            return false;
        }
        return View.Reveal(note, Editor.Doc.Stories[note].Length, 0);
    }

    /// <summary>Puts text along the selected path and opens it in the story editor to write.</summary>
    public void TextOnPath()
    {
        if (View.Selected is not [PathObject path]) { Say("Select a path or polyline drawn with the pen, then choose Text on path."); return; }
        if (Editor.PutTextOnPath(path.Id) is { } why) { Say(why); return; }
        // Its words selected, ready to type over.
        var story = ((PathObject)Editor.Doc.Find(path.Id)!).StoryId!;
        View.Reveal(story, 0, Editor.Doc.Stories[story].Length);
        Say("Type the path's text. The Text tab sets its type, and its alignment where it sits on the path.");
    }

    /// <summary>
    /// Turns text into paths: the selected frames' text, replacing it, or the selected characters, outlined where they
    /// are and left in the text as clear characters (so nothing moves).
    /// </summary>
    public void TextToOutlines()
    {
        var doc = Editor.Doc;
        if (View.Editing is { Length: > 0 } t)
        {
            if (!doc.Stories.TryGetValue(t.StoryId, out var story) || Chains.FramesOf(doc, story) is not { Count: > 0 } frames)
            {
                Say("Only a text frame's text can be turned into outlines.");
                return;
            }
            var (start, length) = (t.Start, t.Length);
            var outlines = frames.Select(f => (f.Id, Paths: Outlines.Of(View.Renderer, doc, f, start, start + length))).Where(o => o.Paths.Count > 0).ToList();
            if (outlines.Count == 0) { Say("There is nothing to outline there: select some letters."); return; }
            var made = new List<string>();
            for (var i = 0; i < outlines.Count; i++)
                if (Editor.AddOutlines(outlines[i].Id, outlines[i].Paths, replace: false, i == 0 ? (start, length) : null) is { } id) made.Add(id);
            View.Select(made);
            Say("Outlined: the paths are on top, and the text under them is clear, so it keeps its place. Delete it if it isn't wanted.");
            return;
        }
        var selected = View.Selected.OfType<TextFrame>().Where(f => !f.Locked).ToList();
        if (selected.Count == 0) { Say("Select a text frame, or some of its text, to turn into outlines."); return; }
        foreach (var f in selected)
            if (doc.Stories.TryGetValue(f.StoryId, out var s) && Chains.FramesOf(doc, s).Count > 1)
            {
                Say("One of those frames is linked to others: unlink it first, so its text has nowhere else to go.");
                return;
            }
        var ids = new List<string>();
        foreach (var f in selected)
        {
            var paths = Outlines.Of(View.Renderer, Editor.Doc, f);
            if (Editor.AddOutlines(f.Id, paths, replace: true) is { } id) ids.Add(id);
        }
        if (ids.Count == 0) { Say("Those frames have no text to outline."); return; }
        View.Select(ids);
        Say("Turned into outlines: paths now, filled with the text's colours. Edit points (double-click) to reshape them.");
    }

    /// <summary>Anchors the selected object to the text beside it, placed by hand where it is.</summary>
    public void AnchorToText()
    {
        if (View.Selected is not [{ } o]) { Say("Select one object on the page to anchor it to the text beside it. (To set one in a line, copy it and paste it into the text.)"); return; }
        if (o.Anchoring is not null) { Say("That object is anchored in text already."); return; }
        if (Editor.AnchorBeside(o.Id, id => View.Layouts.Of(Editor.Doc, id)) is { } why) { Say(why); return; }
        View.Select([o.Id]);
        Say("Anchored to the text beside it: it keeps its place against that line, and moves with it.");
    }

    /// <summary>Lets the selected anchored object go from its text, onto the page where it is drawn.</summary>
    public void ReleaseFromText()
    {
        if (View.Selected is not [{ Anchoring: not null } o]) { Say("Select an object anchored in text to release it."); return; }
        SheetRef? onto = null;
        var doc = Editor.Doc;
        if (o.PlacedIn is { } frame)
        {
            for (var i = 0; i < doc.Pages.Count && onto is null; i++) if (doc.Pages[i].All.Any(x => x.Id == frame)) onto = SheetRef.PageAt(i);
            for (var i = 0; i < doc.Masters.Count && onto is null; i++)
            {
                if (doc.Masters[i].All.Any(x => x.Id == frame)) onto = SheetRef.MasterAt(i);
                else if (doc.Masters[i].Left?.All.Any(x => x.Id == frame) == true) onto = SheetRef.MasterAt(i, left: true);
            }
        }
        if (Editor.Release(o.Id, onto) is { } why) { Say(why); return; }
        View.Select([o.Id]);
        Say("Released: on the page where it was, no longer moving with the text.");
    }

    /// <summary>Opens the story being edited, or the selected frame's, in a story editor window (one per story).</summary>
    public StoryEditor? OpenStoryEditor(string? story = null)
    {
        var storyId = story ?? View.Editing?.StoryId ?? View.Selected switch
        {
            [TextFrame f] => f.StoryId,
            [PathObject { StoryId: { } onPath }] => onPath,
            _ => null,
        };
        if (storyId is null) { Say("Select a text frame or a path with text on it, or put the caret in a frame, to open its story."); return null; }
        if (_storyEditors.FirstOrDefault(se => se.StoryId == storyId) is { } open) { open.Activate(); return open; }
        var editor = new StoryEditor(this, storyId);
        _storyEditors.Add(editor);
        editor.Closed += (_, _) => _storyEditors.Remove(editor);
        editor.Show(this);
        return editor;
    }

    /// <summary>Makes the selected table (or the one the caret is in) run on to a new frame on the next page.</summary>
    public void RunOnTable()
    {
        var table = View.Selected is [TableObject t] ? t : View.CurrentCell() is var (ct, _) ? ct : null;
        if (table is null) { Say("Select a table, or put the caret in one, first."); return; }
        if (Editor.RunOnTable(table.Id) is not { } id) { Say("A table on a master page can't run on to another page."); return; }
        var page = Editor.Doc.Pages.FindIndex(p => p.All.Any(o => o.Id == id));
        var held = Tables.Place(Editor.Doc, (TableObject)Editor.Doc.Find(id)!)!;
        var left = held.Rows.Count(r => r.Frame < 0);
        Say($"The table runs on to page {page + 1}, its header rows repeated there." +
            (left > 0 ? $" {left} row{(left == 1 ? "" : "s")} still don't fit: run it on again." : " Resize either frame to move rows between them."));
    }

    /// <summary>Changes the table the caret is in, at the caret's row and column, keeping the caret in a cell.</summary>
    public void TableEdit(string label, Action<Document, TableObject, int, int> change)
    {
        if (View.CurrentCell() is not var (table, cell)) { Say("Put the caret in a table's cell first."); return; }
        var (row, column) = (cell / table.Columns, cell % table.Columns);
        Editor.ChangeTable(table.Id, label, (d, t) => change(d, t, row, column));
        if (Editor.Doc.Find(table.Id) is TableObject now)
            View.StartEditing(now, Math.Clamp(Math.Min(row, now.Rows - 1) * now.Columns + Math.Min(column, now.Columns - 1), 0, now.Cells.Count - 1));
        Refresh();
    }

    public void GroupSelected()
    {
        if (View.Editing is not null) return;
        if (Editor.Group(View.SelectedIds.ToList()) is { } id) { View.Select([id]); Say("Grouped: they move, resize and arrange as one now."); }
        else Say("Select two or more objects (not in a group already) to group them.");
    }

    public void UngroupSelected()
    {
        var freed = Editor.Ungroup(View.SelectedIds.ToList());
        if (freed.Count == 0) { Say("Select a group to take apart."); return; }
        View.Select(freed);
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

    /// <summary>
    /// Reads a text or Markdown file into the story being edited, at the caret, or onto the end of the selected text
    /// frame's story. The text flows on through the frame's chain; what doesn't fit shows as overset.
    /// </summary>
    public async Task PlaceText()
    {
        string storyId;
        int at;
        if (View.Editing is { } t)
        {
            if (t.Length > 0) View.Type("");
            (storyId, at) = (t.StoryId, t.Caret);
        }
        else if (View.Selected is [TextFrame frame] && Editor.Doc.StoryOf(frame) is { } story)
            (storyId, at) = (story.Id, story.Length);
        else
        {
            Say("Select a text frame, or put the caret in one, to place text into.");
            return;
        }
        if (await PickOpen("text") is not { } path) return;
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't read {Path.GetFileName(path)}: {e.Message}");
            return;
        }
        var markdown = Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";
        var paras = markdown ? TextImport.Markdown(text) : TextImport.PlainText(text);
        var caret = Editor.Place(storyId, at, paras);
        View.Reveal(storyId, caret, 0);
        var over = View.LayoutOf(storyId) is { Overset: true } ? " Some doesn't fit: link another frame to the red mark to flow it on." : "";
        Say($"Placed {paras.Count} paragraph{(paras.Count == 1 ? "" : "s")} from {Path.GetFileName(path)}.{over}");
        Refresh();
    }

    private void PlacePicture()
    {
        if (View.Editing is { } t) { _ = PlaceInText(t); return; }
        if (View.Selected is [PictureObject p]) { _ = View.ReplaceImage(p.Id); return; }
        View.Tool = Tool.Picture;
        Say("Drag out a frame for the picture.");
    }

    /// <summary>A picture placed at the caret: anchored in the line, at its own size (no wider than the column).</summary>
    public async Task PlaceInText(TextEdit t)
    {
        if (View.PickImage is null || await View.PickImage() is not var (bytes, path)) return;
        var doc = Editor.Doc;
        if (!ImageStore.IsImage(bytes)) { Say("That file isn't a picture AVAPageStream can show."); return; }
        if (doc.Stories[t.StoryId] is { Table: not null } or { NoteOf: not null } or { OnPath: not null }) { Say("Pictures can be anchored in a frame's text, not in tables, footnotes or text on a path."); return; }
        if (PdfPages.IsPdf(bytes) && PdfPages.NeedsPassword(bytes)) { Say("That PDF needs its password: place it in a frame of its own first, then copy it into the text."); return; }
        var size = Pictures.Load(bytes)?.Size ?? new Avalonia.Size(144, 144);
        var column = View.Layouts.Of(doc, t.StoryId) is { } l && l.Lines.FirstOrDefault(x => x.Start <= t.Start && t.Start <= x.End) is { } line ? l.Areas[line.Area].Box.W : 300;
        var scale = Math.Min(1, column / Math.Max(1, size.Width));
        var key = doc.Images.Add(bytes);
        var picture = new PictureObject { W = Math.Round(size.Width * scale, 2), H = Math.Round(size.Height * scale, 2), Image = key, Source = path, Fit = Fit.Fill };
        if (t.Length > 0) Editor.ReplaceText(t.StoryId, t.Start, t.Length, "");
        Editor.AnchorAt(t.StoryId, t.Start, [picture]);
        t.Caret = t.Anchor = t.Start + 1;
        View.Reveal(t.StoryId, t.Caret, 0);
        Say("Placed in the text: it moves with it. The Object tab places it.");
    }

    public async Task Copy()
    {
        if (View.Editing is { } t)
        {
            if (View.SelectedText() is not { } text || !Editor.Doc.Stories.TryGetValue(t.StoryId, out var story)) return;
            // Kept here with its formats, so pasting it in this program keeps them; other programs get the plain text.
            _textClipboard = (story.Extract(t.Start, t.Length), text);
            _objectsLast = false;
            if (Clipboard is { } c) await c.SetTextAsync(text.Replace("\n", Environment.NewLine));
            return;
        }
        var ids = View.Selected.Select(o => o.Id).ToList();
        if (ids.Count == 0) return;
        _clipboard = (Editor.Doc.Clone(), ids);
        _objectsLast = true;
        _pasteCount = 0;
        Say(ids.Count == 1 ? "Copied." : $"Copied {ids.Count} objects.");
    }

    public async Task Cut()
    {
        await Copy();
        if (View.Editing is { Length: > 0 }) View.Type("");
        else if (View.Editing is null) View.DeleteSelection();
    }

    private bool _objectsLast;

    public async Task Paste()
    {
        if (View.Editing is { } t)
        {
            // Objects copied last, pasted into text: anchored there, in the line.
            if (_objectsLast && _clipboard is { } objects)
            {
                if (t.Length > 0) Editor.ReplaceText(t.StoryId, t.Start, t.Length, "");
                var pasted = objects.Ids.Select(objects.Doc.Find).OfType<PageObject>().ToList();
                if (Editor.Doc.Stories[t.StoryId].Table is not null || Editor.Doc.Stories[t.StoryId].NoteOf is not null)
                {
                    Say("Objects can be anchored in a frame's text, not in a table's cells or footnotes.");
                    return;
                }
                Editor.AnchorAt(t.StoryId, t.Start, pasted);
                t.Caret = t.Anchor = t.Start + pasted.Count;
                View.Reveal(t.StoryId, t.Caret, 0);
                Say(pasted.Count == 1 ? "Anchored in the text: it moves with it. The Object tab places it." : $"Anchored {pasted.Count} objects in the text.");
                return;
            }
            var text = Clipboard is { } c ? await c.TryGetTextAsync() : null;
            if (_textClipboard is { } styled && (text is null || text.Replace("\r\n", "\n") == styled.Plain))
            {
                t.Caret = t.Anchor = Editor.PasteFragment(t.StoryId, t.Start, t.Length, styled.Fragment);
                View.Reveal(t.StoryId, t.Caret, 0);
                return;
            }
            if (text is { Length: > 0 }) View.Type(text);
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

    /// <summary>The folder the document is in, where linked pictures that have moved are looked for.</summary>
    public string? DocumentFolder => FilePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(FilePath));

    /// <summary>Brings linked pictures up to date with their files (when the window comes back to the front, say).</summary>
    public void UpdateLinks(bool quiet = false)
    {
        var n = Editor.UpdateLinks(DocumentFolder);
        var missing = Links.Missing(Editor.Doc, DocumentFolder).Count;
        if (n > 0) Say($"Updated {n} linked picture{(n == 1 ? "" : "s")} from {(n == 1 ? "its file" : "their files")}." + (missing > 0 ? $" {missing} can't be found." : ""));
        else if (!quiet) Say(missing > 0 ? $"{missing} linked picture{(missing == 1 ? "'s file" : "s' files")} can't be found." : "The linked pictures are up to date.");
    }

    /// <summary>Copies the document to the recovery folder if it has changed since it was saved, or last copied.</summary>
    public void Autosave()
    {
        if (!Editor.Dirty || Editor.Stamp == _autosavedStamp) return;
        try
        {
            Recovery.Save(Editor.Doc, FilePath, RecoveryId);
            _autosavedStamp = Editor.Stamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save a recovery copy: {e.Message}");
        }
    }

    /// <summary>
    /// When the program last stopped without saving, offers what was saved automatically: the newest copy first; any
    /// others are offered the next time.
    /// </summary>
    public async Task OfferRecovery()
    {
        if (Recovery.Found(RecoveryId) is not [var r, ..]) return;
        var when = r.Saved.ToLocalTime().ToString("d MMMM 'at' HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        var answer = await Ask("Recover your work?",
            $"AVAPageStream stopped before {r.Name} was saved. Its changes up to {when} were saved automatically. Open them?",
            ["Recover", "Discard", "Not now"]);
        if (answer == 1) { Recovery.Discard(r.Id); Say($"Discarded the recovered copy of {r.Name}."); return; }
        if (answer != 0) return;
        try
        {
            var doc = DocumentFile.Load(r.Copy);
            Use(new Editor(doc), r.Original);
            Editor.MarkUnsaved();
            Recovery.Discard(RecoveryId);
            Recovery.Adopt(r.Id, RecoveryId);
            UpdateTitle();
            Say($"Recovered {r.Name}. Save it to keep it.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Say($"Couldn't recover {r.Name}: {e.Message}");
        }
    }

    private void Use(Editor editor, string? path)
    {
        Recovery.Discard(RecoveryId); // the document it was a copy of is going
        _autosavedStamp = -1;
        foreach (var se in _storyEditors.ToList()) se.Close();
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
            var missing = Links.Missing(doc, DocumentFolder).Count;
            Say($"Opened {Path.GetFileName(path)}: {doc.Pages.Count} page{(doc.Pages.Count == 1 ? "" : "s")}." +
                (missing > 0 ? $" {missing} linked picture{(missing == 1 ? "'s file" : "s' files")} can't be found: put {(missing == 1 ? "it" : "them")} beside the document, or replace {(missing == 1 ? "it" : "them")}." : ""));
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
            Recovery.Discard(RecoveryId);
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

    /// <summary>
    /// Before exporting or printing: if preflight finds problems that will show in print, asks whether to go on, or
    /// opens preflight to see them. True to go on.
    /// </summary>
    private async Task<bool> PreflightFirst()
    {
        if (!Settings.PreflightFirst) return true;
        var serious = Preflight().Where(i => i.Serious).ToList();
        if (serious.Count == 0) return true;
        var list = string.Join("\n", serious.Take(4).Select(i => "• " + i.Message)) + (serious.Count > 4 ? $"\n… and {serious.Count - 4} more." : "");
        var answer = await Ask("Problems that will show", $"Preflight found {serious.Count} problem{(serious.Count == 1 ? "" : "s")} that will show in print:\n\n{list}", ["Go on anyway", "Show them", "Cancel"]);
        if (answer == 1) OpenPreflight();
        return answer == 0;
    }

    /// <summary>
    /// Makes any contents and index that are out of date again, as one undo step; says how many if asked. Gives what
    /// to add to a message about it (empty if there were none).
    /// </summary>
    public string UpdateLists(bool say = true)
    {
        var n = Editor.UpdateLists(FontMeasurer.Shared);
        if (say) Say(n == 0 ? (Editor.Doc.Lists.Count == 0 ? "There is no table of contents or index to update: Text › Table of contents or Index makes one." : "The contents and index are up to date.")
            : $"Brought {n} {(n == 1 ? "list" : "lists")} up to date; one undo puts {(n == 1 ? "it" : "them")} back.");
        if (n > 0) Refresh();
        return n == 0 ? "" : $" Brought {(n == 1 ? "a contents or index" : n + " contents and indexes")} up to date first.";
    }

    public async Task ExportPdf()
    {
        var lists = UpdateLists(say: false);
        if (!await PreflightFirst()) return;
        if (await AskExport("pdf") is not { } choice) return;
        if (await PickSave("pdf", BaseName + ".pdf") is not { } path) return;
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";
        try
        {
            if (choice.Booklet) await Export.Booklet(Editor.Doc, path, choice.Pages, BaseName);
            CmykResult? cmyk = null;
            if (!choice.Booklet) cmyk = await Export.Pdf(Editor.Doc, path, choice.Pages, BaseName, choice.Marks, choice.Cmyk || choice.PdfX, choice.PdfX);
            var inks = cmyk is null ? "" : choice.PdfX ? $" As {PdfX.Version}, in CMYK." : " In CMYK" + (cmyk.PicturesLeft > 0 ? $"; {cmyk.PicturesLeft} JPEG picture{(cmyk.PicturesLeft == 1 ? " is" : "s are")} still RGB, to keep {(cmyk.PicturesLeft == 1 ? "its" : "their")} quality." : ".");
            Say((choice.Booklet
                ? $"Exported {Pages(choice.Pages.Count)} as a booklet of {Imposition.Booklet(choice.Pages.Count).Count / 2} sheets, printed both sides, to {Path.GetFileName(path)}."
                : $"Exported {Pages(choice.Pages.Count)} to {Path.GetFileName(path)}.") + inks + lists + PreflightNote());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't export {Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>Exports pages as PNGs, one file each ("name-3.png" for page 3 when there are several).</summary>
    public async Task ExportPng()
    {
        var lists = UpdateLists(say: false);
        if (!await PreflightFirst()) return;
        if (await AskExport("png") is not { } choice) return;
        var dpi = Math.Clamp(choice.Dpi, 72, 600);
        Settings.ExportDpi = dpi;
        SaveSettings();
        if (await PickSave("png", BaseName + ".png") is not { } path) return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
        var doc = Editor.Doc;
        try
        {
            foreach (var i in choice.Pages)
                Export.Png(doc, doc.Pages[i], choice.Pages.Count == 1 ? path : Export.Numbered(path, i, doc.Pages.Count), dpi);
            Say((choice.Pages.Count == 1 ? $"Exported {Path.GetFileName(path)} at {dpi} dpi." : $"Exported {Pages(choice.Pages.Count)} as PNG at {dpi} dpi.") + lists);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't export: {e.Message}");
        }
    }

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";

    /// <summary>Prints: the pages (or a booklet of them) as a PDF sent to the system's printing.</summary>
    public async Task Print()
    {
        var lists = UpdateLists(say: false);
        if (!await PreflightFirst()) return;
        if (await AskExport("print") is not { } choice) return;
        var pdf = Path.Combine(Path.GetTempPath(), $"AVAPageStream print {DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        try
        {
            if (choice.Booklet) await Export.Booklet(Editor.Doc, pdf, choice.Pages, BaseName);
            else await Export.Pdf(Editor.Doc, pdf, choice.Pages, BaseName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't make the pages to print: {e.Message}");
            return;
        }
        var why = await SendToPrinter(pdf);
        Say(why ?? (choice.Booklet ? "Sent the booklet to the printer: print it on both sides, flipping on the short edge." : $"Sent {Pages(choice.Pages.Count)} to the printer.") + (why is null ? lists + PreflightNote() : ""));
    }

    /// <summary>Puts a table of contents of the chosen styles at the caret, or at the end of the selected frame's story.</summary>
    public async Task MakeContents()
    {
        string storyId;
        int at;
        if (View.Editing is { } t) (storyId, at) = (t.StoryId, t.Caret);
        else if (View.Selected is [TextFrame f] && Editor.Doc.StoryOf(f) is { } s) (storyId, at) = (s.Id, s.Length);
        else { Say("Put the caret in a text frame, or select one, for the table of contents to go in."); return; }
        var styles = Editor.Doc.Styles.Select(s => s.Name)
            .Where(n => !n.StartsWith("Contents ", StringComparison.Ordinal) && !n.StartsWith("Index ", StringComparison.Ordinal)).ToList();
        if (await AskContents(styles) is not { Count: > 0 } chosen) return;
        var again = GeneratedLists.At(Editor.Doc, storyId, at) is { Kind: ListKind.Contents };
        // The page numbers' tab goes at the right of the column the contents go in.
        var frame = Editor.Doc.Stories[storyId].Frames.Select(Editor.Doc.Find).OfType<TextFrame>().FirstOrDefault();
        var width = frame?.ColumnBoxes().First().W ?? 300;
        var caret = Editor.InsertContents(storyId, at, chosen, width, FontMeasurer.Shared);
        if (caret < 0) { Say($"No paragraphs are in {string.Join(" or ", chosen)} yet."); return; }
        View.Reveal(storyId, caret, 0);
        var count = Contents.Find(Editor.Doc, chosen, FontMeasurer.Shared).Count;
        Say($"{(again ? "Made the table of contents again" : "Made a table of contents")}: {count} {(count == 1 ? "entry" : "entries")}. It keeps up when you export or print, or choose Text › Update contents and index.");
        Refresh();
    }

    public async Task<bool> SaveAsTemplate()
    {
        if (await PickSave("template", BaseName + Templates.Extension) is not { } path) return false;
        if (!path.EndsWith(Templates.Extension, StringComparison.OrdinalIgnoreCase)) path += Templates.Extension;
        try
        {
            DocumentFile.Save(Editor.Doc, path);
            Say($"Saved the template {Path.GetFileNameWithoutExtension(path)}. File › New from template starts a document from it.");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save the template: {e.Message}");
            return false;
        }
    }

    /// <summary>Starts a new, untitled document from a template: a built-in one, or a template file.</summary>
    public async Task NewFrom(Template? builtIn, string? file = null)
    {
        if (!await SaveChanges()) return;
        try
        {
            var doc = builtIn is not null ? builtIn.Make() : DocumentFile.Load(file!);
            Use(new Editor(doc), null);
            Say($"A new document from the {builtIn?.Name ?? Path.GetFileNameWithoutExtension(file)} template.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Say($"Couldn't use that template: {e.Message}");
        }
    }

    private void FillTemplates()
    {
        _templatesMenu.Items.Clear();
        foreach (var t in Templates.BuiltIn)
        {
            var item = new MenuItem { Header = t.Name };
            ToolTip.SetTip(item, t.Description);
            item.Click += (_, _) => _ = NewFrom(t);
            _templatesMenu.Items.Add(item);
        }
        var saved = Templates.InFolder(TemplatesFolder).ToList();
        if (saved.Count > 0) _templatesMenu.Items.Add(new Separator());
        foreach (var path in saved)
        {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path).Replace("_", "__") };
            item.Click += (_, _) => _ = NewFrom(null, path);
            _templatesMenu.Items.Add(item);
        }
        _templatesMenu.Items.Add(new Separator());
        var other = new MenuItem { Header = "A template file…" };
        other.Click += async (_, _) => { if (await PickOpen("template") is { } path) await NewFrom(null, path); };
        _templatesMenu.Items.Add(other);
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
            Title = kind switch { "image" => "Place a picture", "text" => "Place text", _ => "Open a document" },
            AllowMultiple = false,
            FileTypeFilter = kind switch
            {
                "image" => [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.svg", "*.eps", "*.pdf"] }],
                "text" => [new FilePickerFileType("Text and Markdown") { Patterns = ["*.txt", "*.md", "*.markdown", "*.text"] }],
                "template" => [new FilePickerFileType("AVAPageStream templates") { Patterns = ["*" + Templates.Extension] }],
                _ => [new FilePickerFileType("AVAPageStream documents") { Patterns = ["*" + DocumentFile.Extension] }],
            },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveFile(string kind, string suggested)
    {
        var (name, pattern, ext) = kind switch
        {
            "pdf" => ("PDF", "*.pdf", "pdf"),
            "png" => ("PNG picture", "*.png", "png"),
            "template" => ("AVAPageStream template", "*" + Templates.Extension, Templates.Extension.TrimStart('.')),
            _ => ("AVAPageStream document", "*" + DocumentFile.Extension, DocumentFile.Extension.TrimStart('.')),
        };
        IStorageFolder? start = null;
        if (kind == "template")
        {
            // Templates saved in the templates folder are offered by File › New from template.
            Directory.CreateDirectory(TemplatesFolder);
            start = await StorageProvider.TryGetFolderFromPathAsync(TemplatesFolder);
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedStartLocation = start,
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
        if (e.Key == Key.F7)
        {
            OpenSpelling();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F3)
        {
            if (!FindBar.IsVisible) ShowFind();
            else FindNext(!shift);
            e.Handled = true;
            return;
        }
        if (!command) return;
        // A box in the side panel keeps its own editing keys.
        var inBox = FocusManager?.GetFocusedElement() is TextBox;
        switch (e.Key)
        {
            case Key.F when (e.KeyModifiers & KeyModifiers.Alt) != 0: InsertFootnote(); break;
            case Key.I when (e.KeyModifiers & KeyModifiers.Alt) != 0: _ = MarkIndexEntry(); break;
            case Key.N: _ = New(); break;
            case Key.O when shift && !inBox: TextToOutlines(); break;
            case Key.O: _ = OpenWithPicker(); break;
            case Key.S when shift: _ = SaveAs(); break;
            case Key.S: _ = Save(); break;
            case Key.E when shift: OpenStoryEditor(); break;
            case Key.E: _ = ExportPdf(); break;
            case Key.Q: Close(); break;
            case Key.P: _ = Print(); break;
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
            case Key.D3 when shift: View.InsertField(Fields.PageNumber); break;
            case Key.F or Key.H: ShowFind(); break;
            case Key.D: Duplicate(); break;
            case Key.G when shift: UngroupSelected(); break;
            case Key.G: GroupSelected(); break;
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
        if (_closing || !Editor.Dirty) { if (!e.Cancel) Recovery.Discard(RecoveryId); return; }
        e.Cancel = true;
        if (!await SaveChanges()) return;
        _closing = true;
        Close();
    }
}
