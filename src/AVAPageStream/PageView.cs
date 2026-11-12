using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AVAPageStream.Core;
using Page = AVAPageStream.Core.Page;

namespace AVAPageStream;

public enum Tool { Select, Text, Rectangle, Ellipse, Line, Picture, Link, Hand, Pen, Table }

/// <summary>The caret and selection in a story being edited, and a format waiting to be typed.</summary>
public sealed class TextEdit
{
    public required string StoryId { get; init; }
    public int Caret { get; set; }
    public int Anchor { get; set; }
    public CharFormat? Pending { get; set; }
    public int Start => Math.Min(Caret, Anchor);
    public int Length => Math.Abs(Caret - Anchor);
    /// <summary>Where up and down arrows try to keep the caret, so it doesn't drift along short lines.</summary>
    public double? GoalX { get; set; }
}

/// <summary>
/// The pasteboard with one page (or master) on it, rulers along the top and left, and the tools: selecting, moving
/// and resizing objects, drawing new ones, linking text frames, editing text in place and dragging guides.
/// </summary>
public sealed class PageView : Control
{
    public const double RulerSize = 20;
    private const double HandleSize = 7;
    private const double SnapPixels = 5;

    private Editor _editor;
    private readonly Layouts _layouts = new(FontMeasurer.Shared);
    private readonly PageRenderer _renderer;
    private readonly List<string> _selection = [];
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool _caretOn = true;
    private bool _fitted;
    private bool _space;
    private SheetRef _sheet = SheetRef.PageAt(0);
    private Tool _tool;

    // The drag under way.
    private enum Drag { None, Pan, Move, Resize, Marquee, Create, Guide, Text, Crop, Rotate, Pen, Node }
    private Drag _drag;
    private Point _dragStartScreen;
    private Pt _dragStart;
    private Pt _dragNow;
    private int _handle;
    private Dictionary<string, Box> _originals = [];
    private bool _dragMoved;
    private Guide? _guide;          // the guide being moved, if an existing one
    private bool _guideVertical;
    private double _guidePosition;
    private bool _shift;
    private Dictionary<string, Box> _extents = [];
    private List<PathNode>? _pen;        // the path being drawn with the Pen tool
    private Pt? _penPreview;
    private Pt _penFrom;
    private (int Node, int Part)? _nodeHit; // in point editing: the point (0) or handle in (1) or out (2) being dragged
    private const int RotateHandle = 8;
    private (double X, double Y) _cropFrom;

    public PageView(Editor editor)
    {
        _editor = editor;
        _renderer = new PageRenderer(_layouts);
        Focusable = true;
        ClipToBounds = true;
        editor.Changed += OnDocumentChanged;
        _blink.Tick += (_, _) => { _caretOn = !_caretOn; InvalidateVisual(); };
    }

    public Editor Editor
    {
        get => _editor;
        set
        {
            _editor.Changed -= OnDocumentChanged;
            _editor = value;
            _editor.Changed += OnDocumentChanged;
            _selection.Clear();
            StopEditing();
            LinkFrom = null;
            _sheet = SheetRef.PageAt(0);
            _layouts.Clear();
            _fitted = false;
            if (Bounds.Width > 0) FitPage();
            Notify();
        }
    }

    private Document Doc => _editor.Doc;

    /// <summary>Raised when the selection, the text being edited or the tool changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when the zoom or the scroll position changes.</summary>
    public event Action? ViewChanged;

    /// <summary>Raised with a message for the status bar.</summary>
    public event Action<string>? Status;

    /// <summary>Raised as the mouse moves, with its position on the page (null when it leaves).</summary>
    public event Action<Pt?>? PointerAt;

    /// <summary>Raised when the view wants another page or master shown: the caret flowed onto it, or Page Up/Down.</summary>
    public event Action<SheetRef>? ShowSheet;

    /// <summary>Asks for an image file's bytes for a picture frame; tests answer it themselves.</summary>
    /// <summary>Asks for a picture file: its bytes and where it is.</summary>
    public Func<Task<(byte[] Bytes, string Path)?>>? PickImage { get; set; }

    /// <summary>The footnote at a point on the sheet being worked on, if there is one.</summary>
    public string? NoteAt(Pt p)
    {
        foreach (var frame in CurrentSheet.All.OfType<TextFrame>().Where(f => f.Rotation == 0))
            if (LayoutOf(frame.StoryId) is { Notes.Count: > 0 } layout)
                foreach (var n in layout.Notes)
                    if (layout.Areas[n.Area].FrameId == frame.Id && n.Box.Contains(p)) return n.Id;
        return null;
    }

    public double Zoom { get; private set; } = 1;

    /// <summary>Where the page's top-left corner is in the view.</summary>
    public Point Origin { get; private set; } = new(60, 60);

    /// <summary>View pixels per point.</summary>
    public double Scale => Zoom * 96.0 / 72.0;

    public bool Snap { get; set; } = true;

    public bool ShowGuides { get; set; } = true;

    /// <summary>Whether the document grid is drawn, and whether things snap to it.</summary>
    public bool ShowGrid { get; set; }
    public bool SnapGrid { get; set; }

    private bool Snapping => Snap || SnapGrid;

    /// <summary>Whether misspelt words are underlined, and the words to pass besides the dictionary's.</summary>
    public bool CheckSpelling { get; set; } = true;
    public Func<ISet<string>> SpellingWords { get; set; } = () => new HashSet<string>();

    private readonly Dictionary<string, (string Text, string Language, int Words, List<Match> Found)> _misspelt = [];

    /// <summary>A story's misspelt words, worked out again only when its text, the language or the passed words change.</summary>
    public List<Match> MisspeltIn(string storyId)
    {
        if (!Doc.Stories.TryGetValue(storyId, out var story)) return [];
        var also = SpellingWords();
        var key = also.Count + Doc.IgnoredWords.Count * 7919;
        if (_misspelt.TryGetValue(storyId, out var c) && c.Text == story.Text && c.Language == Doc.Language && c.Words == key) return c.Found;
        var found = Speller.For(Doc.Language).Misspelt(story, also).ToList();
        _misspelt[storyId] = (story.Text, Doc.Language, key, found);
        return found;
    }

    /// <summary>Forgets what was found misspelt (after a word is added to the dictionary).</summary>
    public void RecheckSpelling()
    {
        _misspelt.Clear();
        InvalidateVisual();
    }

    public SheetRef Sheet
    {
        get => _sheet;
        set
        {
            if (_sheet == value) return;
            if (_editor.Busy) _editor.Cancel();
            _drag = Drag.None;
            CancelPen();
            EditingPoints = null;
            _sheet = value;
            _selection.Clear();
            if (Editing is { } t && !FramesOnSheet(t.StoryId).Any()) StopEditing();
            Notify();
            InvalidateVisual();
        }
    }

    public Sheet CurrentSheet => _sheet.Resolve(Doc);

    public Tool Tool
    {
        get => _tool;
        set
        {
            if (value != Tool.Pen && _pen is { Count: >= 2 }) FinishPen(closed: false);
            else if (value != Tool.Pen) CancelPen();
            EditingPoints = null;
            _tool = value;
            if (value != Tool.Link) LinkFrom = null;
            if (value is not (Tool.Text or Tool.Select)) StopEditing();
            Cursor = new Cursor(value switch
            {
                Tool.Text => StandardCursorType.Ibeam,
                Tool.Hand => StandardCursorType.Hand,
                Tool.Select or Tool.Link => StandardCursorType.Arrow,
                _ => StandardCursorType.Cross,
            });
            Notify();
            InvalidateVisual();
        }
    }

    /// <summary>The text frame a link starts from, with the Link tool.</summary>
    public string? LinkFrom { get; private set; }

    public TextEdit? Editing { get; private set; }

    /// <summary>The selected objects on the sheet shown, top-most last.</summary>
    public IReadOnlyList<PageObject> Selected => CurrentSheet.All.Where(o => _selection.Contains(o.Id)).ToList();

    /// <summary>The path whose points are being edited (double-click a path with Select), or null.</summary>
    public string? EditingPoints { get; private set; }

    public IReadOnlyList<string> SelectedIds => _selection;

    internal Layouts Layouts => _layouts;
    internal PageRenderer Renderer => _renderer;

    internal TextLayout? LayoutOf(string storyId) => _layouts.Of(Doc, storyId);

    private void Notify() => SelectionChanged?.Invoke();

    private void OnDocumentChanged()
    {
        _selection.RemoveAll(id => Doc.Find(id) is null);
        if (_sheet.IsMaster ? _sheet.Index >= Doc.Masters.Count : _sheet.Index >= Doc.Pages.Count)
            _sheet = _sheet.IsMaster ? SheetRef.MasterAt(Math.Max(0, Doc.Masters.Count - 1)) : SheetRef.PageAt(Doc.Pages.Count - 1);
        if (Editing is { } t)
        {
            if (!Doc.Stories.TryGetValue(t.StoryId, out var story)) StopEditing();
            else
            {
                t.Caret = Math.Clamp(t.Caret, 0, story.Length);
                t.Anchor = Math.Clamp(t.Anchor, 0, story.Length);
            }
        }
        if (LinkFrom is not null && Doc.Find(LinkFrom) is null) LinkFrom = null;
        if (!_editor.Busy) Notify();
        InvalidateVisual();
    }

    // ---- Coordinates and zoom -------------------------------------------------------------------------------

    /// <summary>A point in the view on the page, to a hundredth of a point (finer means nothing on paper).</summary>
    public Pt ToPage(Point p) => new(Math.Round((p.X - Origin.X) / Scale - Offset, 2), Math.Round((p.Y - Origin.Y) / Scale, 2));

    public Point ToView(Pt p) => new(Origin.X + (Offset + p.X) * Scale, Origin.Y + p.Y * Scale);

    // ---- Spreads ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The pages shown together, each with how far across the spread it starts (in points). A facing document shows a
    /// left (even) page and a right (odd) page side by side, page 1 alone on the right; a master shows its left and
    /// right sides. Otherwise it is the one page.
    /// </summary>
    public IReadOnlyList<(SheetRef Ref, double X)> Spread()
    {
        var w = Doc.Setup.PageWidth;
        if (!Doc.Setup.Facing) return [(_sheet, 0)];
        if (_sheet.IsMaster) return [(_sheet with { Left = true }, 0), (_sheet with { Left = false }, w)];
        var i = _sheet.Index;
        if ((i + 1) % 2 == 0)
            return i + 1 < Doc.Pages.Count ? [(SheetRef.PageAt(i), 0), (SheetRef.PageAt(i + 1), w)] : [(SheetRef.PageAt(i), 0)];
        return i > 0 ? [(SheetRef.PageAt(i - 1), 0), (SheetRef.PageAt(i), w)] : [(SheetRef.PageAt(i), w)];
    }

    /// <summary>How wide a spread is: two pages in a facing document.</summary>
    public double SpreadWidth => Doc.Setup.Facing ? 2 * Doc.Setup.PageWidth : Doc.Setup.PageWidth;

    private double? _drawOffset;

    /// <summary>How far across the spread the page being drawn (or worked on) starts, in points.</summary>
    private double Offset => _drawOffset ?? Spread().FirstOrDefault(s => s.Ref == _sheet).X;

    /// <summary>The page of the spread under a point in the view, if it isn't the one being worked on.</summary>
    private SheetRef? OtherPageAt(Point screen)
    {
        if (!Doc.Setup.Facing) return null;
        var x = (screen.X - Origin.X) / Scale;
        foreach (var (r, at) in Spread())
            if (x >= at && x < at + Doc.Setup.PageWidth && r != _sheet) return r;
        return null;
    }

    private Rect ToView(Box b) => new(ToView(new Pt(b.X, b.Y)), ToView(new Pt(b.Right, b.Bottom)));

    /// <summary>Zooms (10% to 1600%) keeping the page point under <paramref name="anchor"/> where it is.</summary>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        zoom = Math.Clamp(zoom, 0.1, 16);
        var a = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var under = ToPage(a);
        Zoom = zoom;
        Origin = new Point(a.X - under.X * Scale, a.Y - under.Y * Scale);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Zooms so the whole page fits, centred.</summary>
    public void FitPage()
    {
        var w = Math.Max(100, Bounds.Width - RulerSize - 48);
        var h = Math.Max(100, Bounds.Height - RulerSize - 48);
        Zoom = Math.Clamp(Math.Min(w / SpreadWidth, h / Doc.Setup.PageHeight) * 72 / 96, 0.1, 16);
        CentrePage();
        _fitted = true;
    }

    public void ActualSize()
    {
        Zoom = 1;
        CentrePage();
    }

    private void CentrePage()
    {
        var pw = SpreadWidth * Scale;
        var ph = Doc.Setup.PageHeight * Scale;
        Origin = new Point(RulerSize + Math.Max(24, (Bounds.Width - RulerSize - pw) / 2), RulerSize + Math.Max(24, (Bounds.Height - RulerSize - ph) / 2));
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Scrolls so the page's top-left corner is at <paramref name="origin"/> in the view.</summary>
    public void ScrollTo(Point origin)
    {
        if (origin == Origin) return;
        Origin = origin;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void ScrollBy(double dx, double dy)
    {
        Origin = new Point(Origin.X + dx, Origin.Y + dy);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_fitted && e.NewSize.Width > 0) FitPage();
    }

    // ---- Selection --------------------------------------------------------------------------------------------

    public void Select(IEnumerable<string> ids)
    {
        _selection.Clear();
        _selection.AddRange(ids.Where(id => CurrentSheet.All.Any(o => o.Id == id)).Distinct());
        if (EditingPoints is not null && !_selection.Contains(EditingPoints)) EditingPoints = null;
        Notify();
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (Editing is { } t && Doc.Stories.TryGetValue(t.StoryId, out var story))
        {
            t.Anchor = 0;
            t.Caret = story.Length;
            CaretMoved();
            return;
        }
        Select(CurrentSheet.Objects.Where(Pickable).Select(o => o.Id));
    }

    /// <summary>The objects on the sheet showing a story: its frames (a footnote's, its story's), or its path.</summary>
    private IEnumerable<PageObject> FramesOnSheet(string storyId)
    {
        var shown = Doc.Stories.GetValueOrDefault(storyId)?.NoteOf ?? storyId;
        return CurrentSheet.All.Where(o => o is TextFrame f && f.StoryId == shown || o is PathObject p && p.StoryId == shown);
    }

    /// <summary>
    /// The letter of text on a path under a point (the index a caret there would take), or with <paramref name="near"/>
    /// the nearest wherever the point is; null for none.
    /// </summary>
    public int? PathIndexAt(PathObject path, Pt p, bool near = false)
    {
        if (path.StoryId is not { } id || !Doc.Stories.TryGetValue(id, out var story)) return null;
        var (glyphs, _) = PathText.Place(story, PathText.Points(path), _layouts.Measurer, path.TextOffset);
        if (glyphs.Count == 0) return near ? 0 : null;
        var q = Shapes.ToLocal(path, p);
        int? best = null;
        var bestDistance = double.MaxValue;
        foreach (var g in glyphs)
        {
            // The point in the letter's own frame: along its baseline, and up from it.
            var r = -g.Angle * Math.PI / 180;
            var (dx, dy) = (q.X - g.At.X, q.Y - g.At.Y);
            var along = dx * Math.Cos(r) - dy * Math.Sin(r);
            var up = -(dx * Math.Sin(r) + dy * Math.Cos(r));
            var size = g.Format.Size;
            var index = g.Index + (along > g.Width / 2 ? 1 : 0);
            if (along >= -1 && along <= g.Width + 1 && up >= -size * 0.3 && up <= size * 1.1) return index;
            var mx = Math.Clamp(along, 0, g.Width) - along;
            var my = Math.Clamp(up, 0, size) - up;
            var d = mx * mx + my * my;
            if (d < bestDistance) (bestDistance, best) = (d, index);
        }
        return near ? best : null;
    }

    /// <summary>Where in its story a point in a frame, a cell, a footnote or along a path is.</summary>
    private int? IndexAt(TextLayout layout, TextSpot spot, Pt p) =>
        spot.Object is PathObject path ? PathIndexAt(path, p, near: true) : layout.IndexAt(spot.Area, Shapes.ToLocal(spot.Object, p));

    /// <summary>Puts the caret in a text frame's story, at a point in it (or at its start).</summary>
    /// <summary>Text under a point: a text frame's story, or a table cell's; and the area of the story it is in.</summary>
    public sealed record TextSpot(PageObject Object, string Area, string StoryId);

    /// <summary>The text frame or table cell under a point (inside groups too), of one story if given.</summary>
    public TextSpot? TextAt(Pt p, string? storyId = null)
    {
        switch (HitObject(p, o => o is TextFrame or TableObject or PathObject { StoryId: not null }))
        {
            case TextFrame f when NoteAt(p) is { } note && Doc.Stories.GetValueOrDefault(note)?.NoteOf == f.StoryId:
                // A footnote at the foot of one of the frame's columns.
                return storyId is null || storyId == note ? new TextSpot(f, f.Id, note) : null;
            case TextFrame f when storyId is null || f.StoryId == storyId:
                return new TextSpot(f, f.Id, f.StoryId);
            case PathObject { StoryId: { } onPath } path when (storyId is null || storyId == onPath) && (storyId is not null || PathIndexAt(path, p) is not null):
                return new TextSpot(path, path.Id, onPath);
            case TableObject { Next: null, Continues: null } t when t.CellAt(Shapes.ToLocal(t, p)) is var cell and >= 0 && (storyId is null || t.Cells[cell] == storyId):
                return new TextSpot(t, t.CellId(cell), t.Cells[cell]);
            case TableObject t when Tables.Place(Doc, t) is { } placed && placed.CellAt(t, p) is var (cell, repeated) && (storyId is null || placed.Head.Cells[cell] == storyId):
                // A header row shown again is edited where it is set, in the table's first frame.
                var shown = repeated ? placed.Head : t;
                return new TextSpot(shown, shown.CellId(cell), placed.Head.Cells[cell]);
        }
        return null;
    }

    /// <summary>Puts the caret in a frame's or a cell's text, at a point in it (or at its start).</summary>
    public void StartEditing(TextSpot spot, Pt? at = null)
    {
        var index = 0;
        if (LayoutOf(spot.StoryId) is { } layout)
            index = at is { } p ? IndexAt(layout, spot, p) ?? FirstIndexIn(layout, spot.Area) : FirstIndexIn(layout, spot.Area);
        Editing = new TextEdit { StoryId = spot.StoryId, Caret = index, Anchor = index };
        _selection.Clear();
        _selection.Add(spot.Object.Id);
        _editor.Break();
        CaretMoved();
    }

    /// <summary>Puts the caret in a table's cell, at its start.</summary>
    public void StartEditing(TableObject table, int cell)
    {
        var shown = Tables.Place(Doc, table)?.FrameOf(cell) ?? table;
        StartEditing(new TextSpot(shown, shown.CellId(cell), table.Cells[cell]));
    }

    public void StartEditing(TextFrame frame, Pt? at = null)
    {
        var index = 0;
        if (at is { } p && LayoutOf(frame.StoryId) is { } layout)
            index = layout.IndexAt(frame.Id, Shapes.ToLocal(frame, p)) ?? FirstIndexIn(layout, frame.Id);
        else if (LayoutOf(frame.StoryId) is { } l) index = FirstIndexIn(l, frame.Id);
        Editing = new TextEdit { StoryId = frame.StoryId, Caret = index, Anchor = index };
        _selection.Clear();
        _selection.Add(frame.Id);
        _editor.Break();
        CaretMoved();
    }

    private static int FirstIndexIn(TextLayout layout, string frameId)
    {
        var line = layout.Lines.FirstOrDefault(l => layout.Areas[l.Area].FrameId == frameId);
        return line?.Start ?? (layout.Areas.FirstOrDefault()?.FrameId == frameId ? 0 : layout.OversetFrom);
    }

    /// <summary>
    /// Shows a stretch of a story selected, on the page with the frame it is set in (the story's last frame, if it is
    /// overset). Returns false if the story has no frame.
    /// </summary>
    public bool Reveal(string storyId, int start, int length)
    {
        if (!Doc.Stories.TryGetValue(storyId, out var story)) return false;
        var layout = LayoutOf(storyId);
        var line = layout?.LineOf(start) ?? -1;
        // A footnote's lines are in its story's frame, and a path's text in its path.
        var frameId = line >= 0 ? Chains.ObjectOf(layout!.Areas[layout.Lines[line].Area].FrameId) : story.Frames.LastOrDefault() ?? story.OnPath;
        if (frameId is null || SheetWith(frameId) is not { } sheet) return false;
        if (sheet != _sheet) ShowSheet?.Invoke(sheet);
        Editing = new TextEdit { StoryId = storyId, Anchor = start, Caret = start + length };
        _selection.Clear();
        _selection.Add(frameId);
        _editor.Break();
        _caretOn = true;
        Notify();
        InvalidateVisual();
        return true;
    }

    /// <summary>Leaves the text, selecting the frame the caret was in.</summary>
    public void StopEditing()
    {
        if (Editing is null) return;
        var frame = CaretFrame();
        Editing = null;
        _blink.Stop();
        _selection.Clear();
        if (frame is not null && CurrentSheet.All.Any(o => o.Id == frame)) _selection.Add(frame);
        _editor.Break();
        Notify();
        InvalidateVisual();
    }

    /// <summary>The frame the caret is in (or the story's first, if the caret is in overset text).</summary>
    public string? CaretFrame()
    {
        if (Editing is not { } t || LayoutOf(t.StoryId) is not { } layout) return null;
        var line = layout.LineOf(t.Caret);
        if (line >= 0) return Chains.ObjectOf(layout.Areas[layout.Lines[line].Area].FrameId);
        return layout.Areas.LastOrDefault() is { } last ? Chains.ObjectOf(last.FrameId) : Doc.Stories.GetValueOrDefault(t.StoryId)?.Frames.FirstOrDefault();
    }

    private void CaretMoved()
    {
        _caretOn = true;
        _blink.Stop();
        _blink.Start();
        // When the caret flows into a frame on another page, show that page.
        if (CaretFrame() is { } frame && !CurrentSheet.All.Any(o => o.Id == frame) && SheetWith(frame) is { } sheet)
        {
            var t = Editing;
            ShowSheet?.Invoke(sheet);
            Editing = t;
            _selection.Clear();
            _selection.Add(frame);
        }
        Notify();
        InvalidateVisual();
    }

    private SheetRef? SheetWith(string objectId)
    {
        for (var i = 0; i < Doc.Pages.Count; i++)
            if (Doc.Pages[i].All.Any(o => o.Id == objectId)) return SheetRef.PageAt(i);
        for (var i = 0; i < Doc.Masters.Count; i++)
        {
            if (Doc.Masters[i].All.Any(o => o.Id == objectId)) return SheetRef.MasterAt(i);
            if (Doc.Masters[i].Left?.All.Any(o => o.Id == objectId) == true) return SheetRef.MasterAt(i, left: true);
        }
        return null;
    }

    // ---- Text editing -------------------------------------------------------------------------------------------

    /// <summary>Types text at the caret, replacing any selection.</summary>
    public void Type(string text)
    {
        if (Editing is not { } t) return;
        t.Caret = _editor.ReplaceText(t.StoryId, t.Start, t.Length, text, t.Pending, merge: "type:" + t.StoryId);
        t.Anchor = t.Caret;
        t.Pending = null;
        t.GoalX = null;
        CaretMoved();
    }

    /// <summary>Puts a page-number or page-count field in at the caret.</summary>
    public void InsertField(char field)
    {
        if (Editing is null)
        {
            Status?.Invoke("Put the caret in a text frame first. On a master page, a page number shows each page's own.");
            return;
        }
        Type(field.ToString());
    }

    /// <summary>Moves to the next cell of a table (or back), and from the last cell on into a new row.</summary>
    private void NextCell(string tableId, string storyId, int delta)
    {
        if (Doc.Find(tableId) is not TableObject t) return;
        var i = t.Cells.IndexOf(storyId) + delta;
        if (i < 0) return;
        if (i >= t.Cells.Count)
        {
            _editor.ChangeTable(tableId, "Add row", (d, table) => Tables.InsertRow(d, table, table.Rows));
            t = (TableObject)Doc.Find(tableId)!;
        }
        StartEditing(t, i);
        Editing!.Caret = Doc.Stories[t.Cells[i]].Length; // the whole cell selected, as in a spreadsheet
        Editing.Anchor = 0;
        Notify();
        InvalidateVisual();
    }

    /// <summary>The table and cell the caret is in, if it is in one.</summary>
    public (TableObject Table, int Cell)? CurrentCell() =>
        Editing is { } t && Doc.Stories.GetValueOrDefault(t.StoryId)?.Table is { } id && Doc.Find(id) is TableObject table
            ? (table, table.Cells.IndexOf(t.StoryId)) : null;

    private void Erase(bool forward, bool word)
    {
        if (Editing is not { } t || !Doc.Stories.TryGetValue(t.StoryId, out var story)) return;
        int start, length;
        if (t.Length > 0) (start, length) = (t.Start, t.Length);
        else if (forward)
        {
            if (t.Caret >= story.Length) return;
            var end = word ? TextNav.WordRight(story.Text, t.Caret) : t.Caret + 1;
            (start, length) = (t.Caret, end - t.Caret);
        }
        else
        {
            if (t.Caret <= 0) return;
            var from = word ? TextNav.WordLeft(story.Text, t.Caret) : t.Caret - 1;
            (start, length) = (from, t.Caret - from);
        }
        t.Caret = t.Anchor = _editor.ReplaceText(t.StoryId, start, length, "", merge: "erase:" + t.StoryId);
        t.GoalX = null;
        CaretMoved();
    }

    private void MoveCaret(int index, bool extend, bool keepGoal = false)
    {
        if (Editing is not { } t || !Doc.Stories.TryGetValue(t.StoryId, out var story)) return;
        t.Caret = Math.Clamp(index, 0, story.Length);
        if (!extend) t.Anchor = t.Caret;
        if (!keepGoal) t.GoalX = null;
        t.Pending = null;
        _editor.Break();
        CaretMoved();
    }

    private void MoveLine(int delta, bool extend)
    {
        if (Editing is not { } t || LayoutOf(t.StoryId) is not { } layout) return;
        var line = layout.LineOf(t.Caret);
        if (line < 0) return;
        t.GoalX ??= layout.CaretX(line, t.Caret);
        // The next line at another height (beside text wrap, several lines share one), then the one of those nearest
        // the column the caret is keeping to.
        var here = layout.Lines[line];
        var target = line;
        do target += delta;
        while (target >= 0 && target < layout.Lines.Count && layout.Lines[target].Area == here.Area && layout.Lines[target].Baseline == here.Baseline);
        if (target < 0) { MoveCaret(0, extend); return; }
        if (target >= layout.Lines.Count) { MoveCaret(layout.Story.Length, extend); return; }
        var row = layout.Lines[target];
        var best = target;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < layout.Lines.Count; i++)
        {
            var l = layout.Lines[i];
            if (l.Area != row.Area || l.Baseline != row.Baseline) continue;
            var x0 = layout.CaretX(i, l.Start);
            var x1 = layout.CaretX(i, l.End);
            var d = t.GoalX.Value < x0 ? x0 - t.GoalX.Value : t.GoalX.Value > x1 ? t.GoalX.Value - x1 : 0;
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        MoveCaret(layout.IndexAtX(best, t.GoalX.Value), extend, keepGoal: true);
    }

    /// <summary>
    /// Moves the caret to the start of the next (or previous) frame in the chain, or to the story's end (or start)
    /// from its last (or first) frame. The page with that frame is shown.
    /// </summary>
    private void MoveFrame(int delta, bool extend)
    {
        if (Editing is not { } t || LayoutOf(t.StoryId) is not { } layout || !Doc.Stories.TryGetValue(t.StoryId, out var story)) return;
        var frames = story.Frames;
        var here = CaretFrame() is { } f ? frames.IndexOf(f) : -1;
        var target = here + delta;
        if (here < 0 || target < 0) { MoveCaret(0, extend); return; }
        if (target >= frames.Count) { MoveCaret(story.Length, extend); return; }
        var first = layout.Lines.FirstOrDefault(l => layout.Areas[l.Area].FrameId == frames[target]);
        MoveCaret(first?.Start ?? (delta > 0 ? story.Length : 0), extend);
    }

    private void LineEdge(bool end, bool extend)
    {
        if (Editing is not { } t || LayoutOf(t.StoryId) is not { } layout) return;
        var i = layout.LineOf(t.Caret);
        if (i < 0) return;
        var line = layout.Lines[i];
        MoveCaret(end ? (line.LastOfParagraph || i == layout.Lines.Count - 1 ? line.End : Math.Max(line.Start, line.End - 1)) : line.Start, extend);
    }

    /// <summary>The character format the Text tab shows: of the selection, the caret, or the first selected frame's text.</summary>
    public CharFormat? CurrentChar()
    {
        if (Editing is { } t && Doc.Stories.TryGetValue(t.StoryId, out var story))
            return t.Pending ?? (t.Length > 0 ? story.FormatAt(t.Start) : story.TypingFormat(t.Caret));
        return SelectedStories().FirstOrDefault() is { } s ? s.FormatAt(0) : null;
    }

    public ParaFormat? CurrentPara()
    {
        if (Editing is { } t && Doc.Stories.TryGetValue(t.StoryId, out var story)) return story.Paras[story.ParagraphAt(t.Start)];
        return SelectedStories().FirstOrDefault()?.Paras[0];
    }

    /// <summary>The stories of the selected text frames, each once.</summary>
    public IEnumerable<Story> SelectedStories() =>
        Selected.OfType<TextFrame>().Select(f => Doc.StoryOf(f))
            .Concat(Selected.OfType<PathObject>().Select(p => p.StoryId is { } id ? Doc.Stories.GetValueOrDefault(id) : null))
            .OfType<Story>().DistinctBy(s => s.Id);

    /// <summary>
    /// Changes the character format of the selected text; with only a caret, of the text typed next; with frames
    /// selected and no text being edited, of all their text.
    /// </summary>
    public void ApplyChar(Func<CharFormat, CharFormat> change, string label)
    {
        if (Editing is { } t && Doc.Stories.TryGetValue(t.StoryId, out var story))
        {
            if (t.Length > 0) _editor.FormatChars(t.StoryId, t.Start, t.Length, change, label);
            else
            {
                t.Pending = change(t.Pending ?? story.TypingFormat(t.Caret));
                // An empty paragraph takes the format at once, so its line shows it.
                var (start, end) = story.ParagraphRange(story.ParagraphAt(t.Caret));
                if (start == end) _editor.FormatChars(t.StoryId, end, 1, change, label);
                Notify();
            }
            return;
        }
        var ids = SelectedStories().Select(s => s.Id).ToList();
        if (ids.Count == 0) return;
        _editor.Change(label, d => { foreach (var id in ids) d.Stories[id].FormatChars(0, d.Stories[id].Length + 1, change); });
    }

    public void ApplyPara(Func<ParaFormat, ParaFormat> change, string label)
    {
        if (Editing is { } t) { _editor.FormatParas(t.StoryId, t.Start, t.Length, change, label); return; }
        var ids = SelectedStories().Select(s => s.Id).ToList();
        if (ids.Count == 0) return;
        _editor.Change(label, d => { foreach (var id in ids) d.Stories[id].FormatParas(0, d.Stories[id].Length, change); });
    }

    public void ApplyStyle(string name)
    {
        if (Editing is { } t) { _editor.ApplyStyle(t.StoryId, t.Start, t.Length, name); t.Pending = null; return; }
        var ids = SelectedStories().Select(s => s.Id).ToList();
        if (ids.Count == 0) return;
        _editor.Change("Apply style " + name, d =>
        {
            foreach (var id in ids) d.Stories[id].ApplyStyle(0, d.Stories[id].Length, d.Style(name));
        });
    }

    public string? SelectedText()
    {
        if (Editing is not { } t || t.Length == 0 || !Doc.Stories.TryGetValue(t.StoryId, out var story)) return null;
        // Fields go out as they read here: "#" for a page number on a master.
        return Fields.Expand(story.Slice(t.Start, t.Length), LayoutOf(t.StoryId)?.Fields);
    }

    // ---- Keys -----------------------------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        if (e.Key == Key.Escape && _drag != Drag.None)
        {
            CancelDrag();
            e.Handled = true;
            return;
        }

        if (Editing is { } t && Doc.Stories.TryGetValue(t.StoryId, out var story))
        {
            var text = story.Text;
            switch (e.Key)
            {
                case Key.Left or Key.Right when (e.KeyModifiers & KeyModifiers.Alt) != 0: Kern(e.Key == Key.Left ? -1 : 1, shift); break;
                case Key.Left when command: MoveCaret(TextNav.WordLeft(text, t.Caret), shift); break;
                case Key.Right when command: MoveCaret(TextNav.WordRight(text, t.Caret), shift); break;
                case Key.Left: MoveCaret(!shift && t.Length > 0 ? t.Start : t.Caret - 1, shift); break;
                case Key.Right: MoveCaret(!shift && t.Length > 0 ? t.Start + t.Length : t.Caret + 1, shift); break;
                case Key.Up: MoveLine(-1, shift); break;
                case Key.Down: MoveLine(1, shift); break;
                case Key.PageUp: MoveFrame(-1, shift); break;
                case Key.PageDown: MoveFrame(1, shift); break;
                case Key.Home when command: MoveCaret(0, shift); break;
                case Key.End when command: MoveCaret(story.Length, shift); break;
                case Key.Home: LineEdge(false, shift); break;
                case Key.End: LineEdge(true, shift); break;
                case Key.Back: Erase(false, command); break;
                case Key.Delete: Erase(true, command); break;
                case Key.Enter: Type("\n"); break;
                case Key.Tab when story.Table is { } tableId: NextCell(tableId, story.Id, shift ? -1 : 1); break;
                case Key.Tab: Type("\t"); break;
                case Key.Escape: StopEditing(); break;
                case Key.B when command: ToggleChar(f => f.Bold, (f, v) => f with { Bold = v }, "Bold"); break;
                case Key.I when command: ToggleChar(f => f.Italic, (f, v) => f with { Italic = v }, "Italic"); break;
                case Key.U when command: ToggleChar(f => f.Underline, (f, v) => f with { Underline = v }, "Underline"); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        if (_pen is not null && e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter) FinishPen(closed: false);
            else CancelPen();
            e.Handled = true;
            return;
        }
        if (EditingPoints is not null && e.Key is Key.Escape or Key.Delete or Key.Back)
        {
            if (e.Key == Key.Escape) { EditingPoints = null; InvalidateVisual(); }
            else DeleteNode();
            e.Handled = true;
            return;
        }
        if (command) return;
        var step = shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, -step); break;
            case Key.Down: Nudge(0, step); break;
            case Key.Delete or Key.Back: DeleteSelection(); break;
            case Key.Escape:
                if (LinkFrom is not null) { LinkFrom = null; Notify(); InvalidateVisual(); }
                else Select([]);
                break;
            case Key.Space: _space = true; Cursor = new Cursor(StandardCursorType.Hand); break;
            case Key.PageUp: Step(-1); break;
            case Key.PageDown: Step(1); break;
            case Key.V: Tool = Tool.Select; break;
            case Key.T: Tool = Tool.Text; break;
            case Key.R: Tool = Tool.Rectangle; break;
            case Key.E: Tool = Tool.Ellipse; break;
            case Key.L: Tool = Tool.Line; break;
            case Key.P: Tool = Tool.Picture; break;
            case Key.K: Tool = Tool.Link; break;
            case Key.H: Tool = Tool.Hand; break;
            case Key.B: Tool = Tool.Pen; break;
            case Key.G: Tool = Tool.Table; break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space && _space)
        {
            _space = false;
            Tool = _tool;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Editing is null || string.IsNullOrEmpty(e.Text)) return;
        var text = new string(e.Text.Where(c => !char.IsControl(c)).ToArray());
        if (text.Length == 0) return;
        Type(text);
        e.Handled = true;
    }

    /// <summary>
    /// Kerning: Alt+Left and Alt+Right tighten and loosen the space after the character before the caret (or after
    /// each selected character) by 10 thousandths of an em, 50 with Shift.
    /// </summary>
    public void Kern(int direction, bool more)
    {
        if (Editing is not { } t) return;
        var step = direction * (more ? 50 : 10);
        var (start, length) = t.Length > 0 ? (t.Start, t.Length) : (t.Caret - 1, 1);
        if (start < 0) return;
        _editor.Change(direction < 0 ? "Kern tighter" : "Kern looser",
            d => d.Stories[t.StoryId].FormatChars(start, length, f => f with { Tracking = Math.Clamp(f.Tracking + step, -500, 2000) }),
            merge: $"kern:{t.StoryId}:{start}:{length}");
        Notify();
    }

    public void ToggleChar(Func<CharFormat, bool> get, Func<CharFormat, bool, CharFormat> set, string label)
    {
        var on = CurrentChar() is { } f && get(f);
        ApplyChar(f => set(f, !on), label);
    }

    private void Step(int delta)
    {
        var count = _sheet.IsMaster ? Doc.Masters.Count : Doc.Pages.Count;
        var next = Math.Clamp(_sheet.Index + delta, 0, count - 1);
        if (next != _sheet.Index) ShowSheet?.Invoke(_sheet with { Index = next });
    }

    public void Nudge(double dx, double dy)
    {
        var ids = Selected.Where(o => !o.Locked).Select(o => o.Id).ToList();
        if (ids.Count == 0) return;
        _editor.Change("Nudge", d =>
        {
            foreach (var id in ids)
                if (d.Find(id) is { } o) { o.X += dx; o.Y += dy; }
        }, merge: "nudge:" + string.Join(",", ids));
    }

    public void DeleteSelection()
    {
        var ids = _selection.ToList();
        if (ids.Count == 0) return;
        var locked = Selected.Count(o => o.Locked);
        var n = _editor.Delete(ids);
        if (locked > 0) Status?.Invoke(locked == 1 ? "A locked object was left. Unlock it to delete it." : $"{locked} locked objects were left. Unlock them to delete them.");
        else if (n > 0) Status?.Invoke(n == 1 ? "Deleted." : $"Deleted {n} objects.");
    }

    // ---- The mouse --------------------------------------------------------------------------------------------------

    private double Tolerance => SnapPixels / Scale;

    private Pt Snapped(Pt p) => Snapping ? Targets().Snap(p, Tolerance) : p;

    private SnapTargets Targets() =>
        SnapTargets.For(Doc, CurrentSheet, guides: Snap, grid: SnapGrid);

    /// <summary>
    /// The top-most object of the sheet under a point. A group is hit where anything in it is; looking for a kind
    /// (<paramref name="which"/>) finds it inside groups.
    /// </summary>
    private PageObject? HitObject(Pt p, Func<PageObject, bool>? which = null) =>
        Hit(Doc.Ordered(CurrentSheet.Objects).Where(Pickable).ToList(), p, which);

    /// <summary>Whether an object on the sheet can be picked: its layer shown and not locked.</summary>
    private bool Pickable(PageObject o) => Doc.LayerOf(o) is { Visible: true, Locked: false };

    private PageObject? Hit(List<PageObject> list, Pt p, Func<PageObject, bool>? which)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var o = list[i];
            if (o is GroupObject g)
            {
                if (Hit(g.Children, p, which) is { } inner) return which is null ? g : inner;
                continue;
            }
            if (which is not null && !which(o)) continue;
            if (Touches(o, p)) return o;
        }
        return null;
    }

    /// <summary>Whether a point is on an object: in its turned frame, near a line, or in or on a path.</summary>
    private bool Touches(PageObject o, Pt p)
    {
        var q = Shapes.ToLocal(o, p);
        switch (o)
        {
            case LineObject line:
                var (a, b) = line.Ends;
                return DistanceToSegment(q, a, b) <= Math.Max(4 / Scale, line.StrokeWidth / 2);
            case PathObject path:
                var g = PageRenderer.PathGeometry(path);
                var at = new Point(q.X, q.Y);
                // Text on it stands up to its size above the path.
                var reach = path.StoryId is { } sid && Doc.Stories.GetValueOrDefault(sid) is { } onPath ? onPath.FormatAt(0).Size * 2 : 0;
                return (path.Closed && path.Fill is not null && g.FillContains(at)) ||
                       g.StrokeContains(new Pen(Brushes.Black, Math.Max(Math.Max(8 / Scale, path.StrokeWidth), reach)), at);
            default:
                return o.Bounds.Contains(q, 3 / Scale);
        }
    }

    private static double DistanceToSegment(Pt p, Pt a, Pt b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        var cx = a.X + t * dx - p.X;
        var cy = a.Y + t * dy - p.Y;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>The handle positions of an object: eight round its turned frame, or the two ends of a line.</summary>
    private static Pt[] Handles(PageObject o)
    {
        if (o is LineObject line)
        {
            var (a, b) = line.Ends;
            return [a, b];
        }
        var r = o.Bounds;
        Pt[] local =
        [
            new(r.X, r.Y), new(r.CentreX, r.Y), new(r.Right, r.Y), new(r.Right, r.CentreY),
            new(r.Right, r.Bottom), new(r.CentreX, r.Bottom), new(r.X, r.Bottom), new(r.X, r.CentreY),
        ];
        return local.Select(h => Shapes.ToPage(o, h)).ToArray();
    }

    /// <summary>Lines and groups turn no other way than they are drawn: everything else has a rotate handle.</summary>
    private static bool CanRotate(PageObject o) => o is not (LineObject or GroupObject or TableObject);

    /// <summary>The rotate handle, a little above the middle of the frame's top edge.</summary>
    private Pt RotateHandleAt(PageObject o) => Shapes.ToPage(o, new Pt(o.X + o.W / 2, o.Y - 24 / Scale));

    private int HitHandle(PageObject o, Point screen)
    {
        var handles = Handles(o);
        for (var i = 0; i < handles.Length; i++)
        {
            var h = ToView(handles[i]);
            if (Math.Abs(h.X - screen.X) <= HandleSize && Math.Abs(h.Y - screen.Y) <= HandleSize) return i;
        }
        if (CanRotate(o))
        {
            var r = ToView(RotateHandleAt(o));
            if (Math.Abs(r.X - screen.X) <= HandleSize && Math.Abs(r.Y - screen.Y) <= HandleSize) return RotateHandle;
        }
        return -1;
    }

    /// <summary>Whether a screen point is on the overset mark of a text frame whose story doesn't fit.</summary>
    private TextFrame? HitOverset(Point screen)
    {
        foreach (var frame in CurrentSheet.All.OfType<TextFrame>())
            if (OversetMark(frame) is { } mark && mark.Inflate(2).Contains(screen)) return frame;
        return null;
    }

    private Rect? OversetMark(TextFrame frame)
    {
        if (LayoutOf(frame.StoryId) is not { Overset: true } || Doc.StoryOf(frame)?.Frames.LastOrDefault() != frame.Id) return null;
        var corner = ToView(Shapes.ToPage(frame, new Pt(frame.X + frame.W, frame.Y + frame.H)));
        return new Rect(corner.X - 6, corner.Y - 6, 12, 12);
    }

    private Guide? HitGuide(Point screen)
    {
        foreach (var g in CurrentSheet.Guides)
        {
            var v = g.Vertical ? ToView(new Pt(g.Position, 0)).X : ToView(new Pt(0, g.Position)).Y;
            if (Math.Abs((g.Vertical ? screen.X : screen.Y) - v) <= 3) return g;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var screen = e.GetPosition(this);
        var p = ToPage(screen);
        _shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        _dragStartScreen = screen;
        _dragStart = _dragNow = p;
        _dragMoved = false;

        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && (_space || _tool == Tool.Hand)))
        {
            Begin(Drag.Pan, e);
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        // The rulers: drag a new guide out.
        if (screen.Y < RulerSize && screen.X > RulerSize) { StartGuide(null, vertical: false, p.Y, e); return; }
        if (screen.X < RulerSize && screen.Y > RulerSize) { StartGuide(null, vertical: true, p.X, e); return; }
        if (screen.X < RulerSize && screen.Y < RulerSize) return;

        // Clicking in the story being edited moves the caret, whatever the tool.
        if (Editing is { } t)
        {
            if (TextAt(p, t.StoryId) is { } inStory && LayoutOf(t.StoryId) is { } layout)
            {
                ClickInText(layout, inStory, p, e.ClickCount, _shift);
                Begin(Drag.Text, e);
                return;
            }
            if (Doc.Stories.GetValueOrDefault(t.StoryId)?.Table is { } tableId && TextAt(p) is { Object: TableObject table } cell && table.Id == tableId)
            {
                StartEditing(cell, p); // another cell of the same table
                Begin(Drag.Text, e);
                return;
            }
            StopEditing();
        }

        if (EditingPoints is { } pathId && Doc.Find(pathId) is PathObject editing)
        {
            if (HitNode(editing, screen) is { } node)
            {
                _nodeHit = node;
                _editor.Begin();
                Begin(Drag.Node, e);
                return;
            }
            EditingPoints = null; // a click anywhere else leaves the points
            InvalidateVisual();
        }

        if (OtherPageAt(screen) is { } other && _pen is null)
        {
            ShowSheet?.Invoke(other);
            p = ToPage(screen);
            _dragStart = _dragNow = p;
        }

        switch (_tool)
        {
            case Tool.Pen:
                PenPress(p, e);
                return;

            case Tool.Select:
                if (HitOverset(screen) is { } overset)
                {
                    Tool = Tool.Link;
                    LinkFrom = overset.Id;
                    Status?.Invoke("Click the frame the text should flow into.");
                    Notify();
                    InvalidateVisual();
                    return;
                }
                if (Selected is [{ Locked: false } only] && HitHandle(only, screen) is var h and >= 0)
                {
                    _handle = h;
                    StartObjects(h == RotateHandle ? Drag.Rotate : Drag.Resize, [only], e);
                    return;
                }
                if (alt && HitObject(p) is PictureObject { Locked: false } cropped)
                {
                    // Alt+drag slides the picture within its frame.
                    Select([cropped.Id]);
                    _cropFrom = (cropped.OffsetX, cropped.OffsetY);
                    _originals = new() { [cropped.Id] = cropped.Bounds };
                    _editor.Begin();
                    Begin(Drag.Crop, e);
                    return;
                }
                if (HitObject(p) is { } hit)
                {
                    if (e.ClickCount >= 2 && TextAt(p) is { } frame)
                    {
                        StartEditing(frame, p); // into a frame inside a group, or a table's cell, too
                        Begin(Drag.Text, e);
                        return;
                    }
                    if (e.ClickCount >= 2 && hit is PathObject points)
                    {
                        if (points.Rotation != 0) { Status?.Invoke("Set the path's rotation to 0 to edit its points."); return; }
                        Select([points.Id]);
                        EditingPoints = points.Id;
                        Status?.Invoke("Drag a point or a handle. Delete takes out the last point dragged; Escape or a click elsewhere finishes.");
                        Notify();
                        InvalidateVisual();
                        return;
                    }
                    if (_shift)
                    {
                        if (!_selection.Remove(hit.Id)) _selection.Add(hit.Id);
                        Notify();
                        InvalidateVisual();
                        return;
                    }
                    if (!_selection.Contains(hit.Id)) Select([hit.Id]);
                    var moving = Selected.Where(o => !o.Locked).ToList();
                    if (moving.Count > 0) StartObjects(Drag.Move, moving, e);
                    else if (hit.Locked) Status?.Invoke("That object is locked.");
                    return;
                }
                if (HitGuide(screen) is { } guide)
                {
                    StartGuide(guide, guide.Vertical, guide.Position, e);
                    return;
                }
                if (!_shift) Select([]);
                Begin(Drag.Marquee, e);
                return;

            case Tool.Text:
                if (TextAt(p) is { } target)
                {
                    StartEditing(target, p);
                    Begin(Drag.Text, e);
                    return;
                }
                _dragStart = _dragNow = Snapped(p);
                Begin(Drag.Create, e);
                return;

            case Tool.Link:
                if (HitObject(p, o => o is TextFrame) is not TextFrame f)
                {
                    Status?.Invoke(LinkFrom is null ? "Click a text frame to link from." : "Click a text frame to link to, or press Escape.");
                    return;
                }
                if (alt)
                {
                    var why = _editor.TryChange("Unlink frame", d => Chains.Unlink(d, (TextFrame)d.Find(f.Id)!));
                    Status?.Invoke(why ?? "Unlinked: the text flows on without that frame.");
                    return;
                }
                if (LinkFrom is null)
                {
                    LinkFrom = f.Id;
                    Status?.Invoke("Now click the frame the text should flow into.");
                }
                else
                {
                    var from = LinkFrom;
                    var why = _editor.TryChange("Link frames", d => Chains.Link(d, (TextFrame)d.Find(from)!, (TextFrame)d.Find(f.Id)!));
                    Status?.Invoke(why ?? "Linked: the text flows on into that frame.");
                    LinkFrom = why is null ? f.Id : LinkFrom;
                }
                Notify();
                InvalidateVisual();
                return;

            case Tool.Picture when e.ClickCount == 1 && HitObject(p, o => o is PictureObject) is PictureObject pic:
                _ = ReplaceImage(pic.Id);
                return;

            default:
                _dragStart = _dragNow = Snapped(p);
                Begin(Drag.Create, e);
                return;
        }
    }

    private void ClickInText(TextLayout layout, TextSpot spot, Pt p, int clicks, bool extend)
    {
        if (Editing is not { } t) return;
        var index = IndexAt(layout, spot, p) ?? t.Caret;
        var text = layout.Story.Text;
        if (clicks == 2)
        {
            var (s, end) = TextNav.WordAt(text, index);
            t.Anchor = s;
            t.Caret = end;
        }
        else if (clicks >= 3)
        {
            var (s, end) = layout.Story.ParagraphRange(layout.Story.ParagraphAt(index));
            t.Anchor = s;
            t.Caret = end;
        }
        else
        {
            t.Caret = index;
            if (!extend) t.Anchor = index;
        }
        t.GoalX = null;
        t.Pending = null;
        _editor.Break();
        CaretMoved();
    }

    // ---- The Pen tool and editing points ------------------------------------------------------------------------

    /// <summary>
    /// A click adds a corner point, a drag a smooth one (its handle where the mouse lets go). Clicking the first point
    /// again closes the path; a double-click or Enter ends it open.
    /// </summary>
    private void PenPress(Pt p, PointerPressedEventArgs e)
    {
        var at = _pen is { Count: > 0 } && _shift ? Constrain(_pen[^1].At, p, true) : Snapped(p);
        if (_pen is { Count: >= 3 } && Close(at, _pen[0].At)) { FinishPen(closed: true); return; }
        if (e.ClickCount >= 2 && _pen is { Count: >= 2 })
        {
            // The double-click's first click added a point here already.
            if (Close(_pen[^1].At, at)) FinishPen(closed: false);
            else { _pen.Add(PathNode.Corner(at)); FinishPen(closed: false); }
            return;
        }
        _pen ??= [];
        _pen.Add(PathNode.Corner(at));
        _penFrom = at;
        Begin(Drag.Pen, e);
        Status?.Invoke(_pen.Count == 1 ? "Click for corners, drag for curves. Click the first point to close the shape; double-click or Enter to finish." : "");
        InvalidateVisual();
    }

    private bool Close(Pt a, Pt b) => Math.Abs(a.X - b.X) * Scale <= 6 && Math.Abs(a.Y - b.Y) * Scale <= 6;

    /// <summary>Makes the path drawn so far an object (two points or more), and leaves the Pen ready for another.</summary>
    public void FinishPen(bool closed)
    {
        var nodes = _pen;
        _pen = null;
        _penPreview = null;
        InvalidateVisual();
        if (nodes is not { Count: >= 2 }) return;
        var path = new PathObject { Nodes = nodes, Closed = closed && nodes.Count >= 3 };
        if (NewStyles.TryGetValue(typeof(PathObject), out var style)) (path.Fill, path.Stroke, path.StrokeWidth) = style;
        path.Fit();
        var made = _editor.Add(_sheet, path, closed ? "New shape" : "New path");
        Select([made.Id]);
        Status?.Invoke($"Made a {made.Kind.ToLowerInvariant()} of {nodes.Count} points. Double-click it with Select to edit its points.");
    }

    public void CancelPen()
    {
        _pen = null;
        _penPreview = null;
        InvalidateVisual();
    }

    /// <summary>The point or handle of a path under a screen point: handles first, as they sit over points.</summary>
    private (int Node, int Part)? HitNode(PathObject path, Point screen)
    {
        bool Near(Pt q) => Math.Abs(ToView(q).X - screen.X) <= HandleSize && Math.Abs(ToView(q).Y - screen.Y) <= HandleSize;
        for (var i = 0; i < path.Nodes.Count; i++)
        {
            if (path.Nodes[i].In is { } a && Near(a)) return (i, 1);
            if (path.Nodes[i].Out is { } b && Near(b)) return (i, 2);
        }
        for (var i = 0; i < path.Nodes.Count; i++)
            if (Near(path.Nodes[i].At)) return (i, 0);
        return null;
    }

    private int? _lastNode;

    private void DragNode(Pt p)
    {
        if (EditingPoints is not { } id || Doc.Find(id) is not PathObject path || _nodeHit is not { } hit) return;
        var n = path.Nodes[hit.Node];
        var at = hit.Part == 0 ? Snapped(p) : p;
        path.Nodes[hit.Node] = hit.Part switch
        {
            0 => n.Moved(q => new Pt(q.X + at.X - n.X, q.Y + at.Y - n.Y)),
            1 => n with { InX = at.X, InY = at.Y },
            _ => n with { OutX = at.X, OutY = at.Y },
        };
        path.Fit();
        _lastNode = hit.Node;
        _editor.Preview();
    }

    /// <summary>Takes out the last point dragged, leaving at least two (three in a closed shape).</summary>
    private void DeleteNode()
    {
        if (EditingPoints is not { } id || Doc.Find(id) is not PathObject path || _lastNode is not { } i) return;
        if (path.Nodes.Count <= (path.Closed ? 3 : 2)) { Status?.Invoke("A path needs at least two points (a closed shape three)."); return; }
        _editor.Change("Delete point", d =>
        {
            var pth = (PathObject)d.Find(id)!;
            var at = Math.Min(i, pth.Nodes.Count - 1);
            var starts = pth.Nodes[at].Start;
            pth.Nodes.RemoveAt(at);
            if (starts && at < pth.Nodes.Count && at > 0) pth.Nodes[at] = pth.Nodes[at] with { Start = true }; // the part's next point starts it
            pth.Fit();
        });
        _lastNode = null;
    }

    private void Begin(Drag drag, PointerPressedEventArgs e)
    {
        _drag = drag;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void StartObjects(Drag drag, IEnumerable<PageObject> objects, PointerPressedEventArgs e)
    {
        var list = objects.ToList();
        _originals = list.ToDictionary(o => o.Id, o => o.Bounds);
        _extents = list.ToDictionary(o => o.Id, Shapes.Extent);
        _editor.Begin();
        Begin(drag, e);
    }

    private void StartGuide(Guide? existing, bool vertical, double position, PointerPressedEventArgs e)
    {
        _guide = existing;
        _guideVertical = vertical;
        _guidePosition = position;
        Begin(Drag.Guide, e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);
        var p = ToPage(screen);
        _mouse = screen;
        PointerAt?.Invoke(p);
        _shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (_drag == Drag.None)
        {
            if (_tool == Tool.Pen && _pen is { Count: > 0 })
            {
                _penPreview = _shift ? Constrain(_pen[^1].At, p, true) : p;
                InvalidateVisual();
            }
            if (_tool == Tool.Select && !_space)
                Cursor = new Cursor(Selected is [{ Locked: false } only] && HitHandle(only, screen) >= 0 ? StandardCursorType.SizeAll
                    : HitOverset(screen) is not null ? StandardCursorType.Hand : StandardCursorType.Arrow);
            InvalidateRulers();
            return;
        }
        if (!_dragMoved && Math.Abs(screen.X - _dragStartScreen.X) + Math.Abs(screen.Y - _dragStartScreen.Y) < 3 && _drag != Drag.Pan) return;
        _dragMoved = true;
        switch (_drag)
        {
            case Drag.Pan:
                ScrollBy(screen.X - _dragStartScreen.X, screen.Y - _dragStartScreen.Y);
                _dragStartScreen = screen;
                return;
            case Drag.Move:
                MoveObjects(p);
                break;
            case Drag.Resize:
                ResizeObject(p);
                break;
            case Drag.Crop:
                if (Doc.Find(_originals.Keys.First()) is PictureObject pic)
                {
                    // Along the picture's own axes, if it is turned.
                    var d = Shapes.Rotate(p, _dragStart, -pic.Rotation);
                    pic.OffsetX = Math.Round(_cropFrom.X + d.X - _dragStart.X, 2);
                    pic.OffsetY = Math.Round(_cropFrom.Y + d.Y - _dragStart.Y, 2);
                    _editor.Preview();
                }
                break;
            case Drag.Rotate:
                if (Doc.Find(_originals.Keys.First()) is { } turning)
                {
                    var c = new Pt(_originals.Values.First().CentreX, _originals.Values.First().CentreY);
                    var angle = Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI + 90;
                    if (_shift) angle = Math.Round(angle / 15) * 15;
                    turning.Rotation = Normalise(Math.Round(angle, 2));
                    _editor.Preview();
                }
                break;
            case Drag.Pen:
                if (_pen is { Count: > 0 })
                {
                    _pen[^1] = PathNode.Smooth(_penFrom, p); // dragging out of a point makes it smooth
                    InvalidateVisual();
                }
                break;
            case Drag.Node:
                DragNode(p);
                break;
            case Drag.Create:
                _dragNow = Snapped(p);
                if (_shift) _dragNow = Constrain(_dragStart, _dragNow, _tool == Tool.Line);
                break;
            case Drag.Marquee:
                _dragNow = p;
                break;
            case Drag.Guide:
                _guidePosition = _guideVertical ? (Snap ? SnapTargets.Snap(p.X, Targets().Xs.Where(x => x != _guide?.Position), Tolerance) : p.X)
                                                : (Snap ? SnapTargets.Snap(p.Y, Targets().Ys.Where(y => y != _guide?.Position), Tolerance) : p.Y);
                break;
            case Drag.Text:
                if (Editing is { } t && LayoutOf(t.StoryId) is { } layout && TextAt(p, t.StoryId) is { } spot &&
                    IndexAt(layout, spot, p) is { } index && index != t.Caret)
                {
                    t.Caret = index;
                    CaretMoved();
                }
                break;
        }
        InvalidateVisual();
    }

    /// <summary>Keeps a drag square (or, for a line, at a multiple of 45°).</summary>
    private static Pt Constrain(Pt a, Pt b, bool line)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (line)
        {
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            var length = Math.Sqrt(dx * dx + dy * dy);
            return new Pt(a.X + Math.Round(Math.Cos(angle) * length, 6), a.Y + Math.Round(Math.Sin(angle) * length, 6));
        }
        var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Pt(a.X + Math.Sign(dx == 0 ? 1 : dx) * size, a.Y + Math.Sign(dy == 0 ? 1 : dy) * size);
    }

    /// <summary>An angle in degrees, from -180 (not included) to 180.</summary>
    public static double Normalise(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        return degrees == 0 ? 0 : degrees;
    }

    private void MoveObjects(Pt p)
    {
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        if (_shift) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
        if (Snapping)
        {
            var moved = Box.Union(_extents.Values.Select(b => b.Offset(dx, dy)));
            var (sx, sy) = Targets().SnapBox(moved, Tolerance);
            dx += sx;
            dy += sy;
        }
        foreach (var (id, box) in _originals)
            if (Doc.Find(id) is { } o) o.Bounds = box.Offset(dx, dy);
        _editor.Preview();
    }

    private void ResizeObject(Pt p)
    {
        var (id, box) = _originals.First();
        if (Doc.Find(id) is not { } o) return;
        var at = Snapped(p);
        if (o is LineObject line)
        {
            var original = new LineObject { Rising = line.Rising };
            original.Bounds = box;
            var (a, b) = original.Ends;
            if (_handle == 0) a = _shift ? Constrain(b, at, true) : at;
            else b = _shift ? Constrain(a, at, true) : at;
            line.Bounds = Box.FromCorners(a, b);
            line.Rising = (b.X - a.X) * (b.Y - a.Y) < 0;
            _editor.Preview();
            return;
        }
        var turn = o.Rotation;
        var c0 = new Pt(box.CentreX, box.CentreY);
        if (turn != 0) at = Shapes.Rotate(p, c0, -turn); // snapping means nothing across a turned edge
        double x0 = box.X, y0 = box.Y, x1 = box.Right, y1 = box.Bottom;
        if (_handle is 0 or 6 or 7) x0 = at.X;
        if (_handle is 2 or 3 or 4) x1 = at.X;
        if (_handle is 0 or 1 or 2) y0 = at.Y;
        if (_handle is 4 or 5 or 6) y1 = at.Y;
        if (_shift && _handle is 0 or 2 or 4 or 6 && box.W > 0 && box.H > 0)
        {
            // Keep the shape: the larger of the two changes sets both.
            var s = Math.Max(Math.Abs(x1 - x0) / box.W, Math.Abs(y1 - y0) / box.H);
            if (_handle is 0 or 6) x0 = x1 - box.W * s; else x1 = x0 + box.W * s;
            if (_handle is 0 or 2) y0 = y1 - box.H * s; else y1 = y0 + box.H * s;
        }
        var r = Box.FromCorners(new Pt(x0, y0), new Pt(x1, y1));
        r = r with { W = Math.Max(1, r.W), H = Math.Max(1, r.H) };
        if (turn != 0)
        {
            var c = Shapes.Rotate(new Pt(r.CentreX, r.CentreY), c0, turn);
            r = new Box(c.X - r.W / 2, c.Y - r.H / 2, r.W, r.H);
        }
        o.Bounds = r;
        _editor.Preview();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var drag = _drag;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        switch (drag)
        {
            case Drag.Move:
                _editor.Commit(_originals.Count == 1 ? "Move" : $"Move {_originals.Count} objects");
                break;
            case Drag.Resize:
                _editor.Commit("Resize");
                break;
            case Drag.Crop:
                _editor.Commit("Crop picture");
                break;
            case Drag.Rotate:
                _editor.Commit("Rotate");
                break;
            case Drag.Node:
                _editor.Commit("Edit points");
                break;
            case Drag.Create:
                FinishCreate();
                break;
            case Drag.Marquee:
                if (_dragMoved)
                {
                    var area = Box.FromCorners(_dragStart, _dragNow);
                    var hits = CurrentSheet.Objects.Where(Pickable).Where(o => Shapes.Extent(o).Inside(area)).Select(o => o.Id);
                    Select(_shift ? _selection.Concat(hits).ToList() : hits);
                }
                break;
            case Drag.Guide:
                FinishGuide(e.GetPosition(this));
                break;
        }
        InvalidateVisual();
    }

    private void CancelDrag()
    {
        if (_drag is Drag.Move or Drag.Resize or Drag.Crop or Drag.Rotate or Drag.Node) _editor.Cancel();
        _drag = Drag.None;
        InvalidateVisual();
    }

    private void FinishGuide(Point screen)
    {
        var onRuler = _guideVertical ? screen.X < RulerSize : screen.Y < RulerSize;
        var existing = _guide;
        var guide = new Guide(_guideVertical, Math.Round(_guidePosition, 2));
        var sheet = _sheet;
        if (existing is null && (onRuler || !_dragMoved)) return;
        _editor.Change(onRuler ? "Remove guide" : existing is null ? "Add guide" : "Move guide", d =>
        {
            var guides = sheet.Resolve(d).Guides;
            if (existing is not null) guides.Remove(existing);
            if (!onRuler) guides.Add(guide);
        });
    }

    /// <summary>The object a create drag makes, from the last style used for its kind.</summary>
    private PageObject? Creation()
    {
        var box = Box.FromCorners(_dragStart, _dragNow);
        PageObject? o = _tool switch
        {
            Tool.Rectangle => new RectangleObject(),
            Tool.Ellipse => new EllipseObject(),
            Tool.Line => new LineObject { Rising = (_dragNow.X - _dragStart.X) * (_dragNow.Y - _dragStart.Y) < 0 },
            Tool.Text => new TextFrame(),
            Tool.Picture => new PictureObject(),
            _ => null,
        };
        if (o is null) return null;
        o.Bounds = box;
        if (NewStyles.TryGetValue(o.GetType(), out var style))
        {
            o.Fill = style.Fill;
            o.Stroke = style.Stroke;
            o.StrokeWidth = style.Width;
        }
        return o;
    }

    /// <summary>The fill and stroke the next new object of each kind gets: the last ones set on that kind.</summary>
    public Dictionary<Type, (string? Fill, string? Stroke, double Width)> NewStyles { get; } = [];

    private void FinishCreate()
    {
        if (_tool == Tool.Table)
        {
            var box = Box.FromCorners(_dragStart, _dragNow);
            if (box.W * Scale < 20 || box.H * Scale < 20) return;
            var table = _editor.AddTable(_sheet, box, 4, 3);
            StartEditing((TableObject)Doc.Find(table.Id)!, 0);
            Status?.Invoke("A table of 4 rows and 3 columns. Tab moves from cell to cell (and adds a row at the end); the Object tab changes its rows and columns.");
            return;
        }
        if (Creation() is not { } o) return;
        var tooSmall = o.W * Scale < 4 && o.H * Scale < 4;
        if (tooSmall) return;
        if (o is not LineObject && (o.W < 1 || o.H < 1)) return;
        var made = _editor.Add(_sheet, o);
        Select([made.Id]);
        switch (made)
        {
            case TextFrame frame:
                StartEditing((TextFrame)Doc.Find(frame.Id)!);
                break;
            case PictureObject picture:
                _ = ReplaceImage(picture.Id);
                break;
        }
    }

    /// <summary>Asks for an image and puts it in a picture frame.</summary>
    public async Task ReplaceImage(string pictureId)
    {
        if (PickImage is null) return;
        if (await PickImage() is not var (bytes, path)) return;
        if (!ImageStore.IsImage(bytes))
        {
            Status?.Invoke("That file isn't a picture AVAPageStream can show (PNG, JPEG, GIF, BMP, WebP, SVG or EPS).");
            return;
        }
        var key = Doc.Images.Add(bytes);
        _editor.Change("Place picture", d => { if (d.Find(pictureId) is PictureObject p) { p.Image = key; p.Source = path; } });
        Select([pictureId]);
        if (_renderer.Describe(Doc, key) is { } what) Status?.Invoke($"Placed {what}.");
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (command) SetZoom(Zoom * Math.Pow(1.15, e.Delta.Y), e.GetPosition(this));
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0) ScrollBy(e.Delta.Y * 60, 0);
        else ScrollBy(e.Delta.X * 60, e.Delta.Y * 60);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerAt?.Invoke(null);
        _mouse = null;
        InvalidateVisual();
    }

    private Point? _mouse;

    private void InvalidateRulers() => InvalidateVisual();

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _space = false;
    }

    // ---- Drawing ----------------------------------------------------------------------------------------------------

    public override void Render(DrawingContext dc)
    {
        var bounds = new Rect(Bounds.Size);
        dc.FillRectangle(Look.Pasteboard, bounds);
        var doc = Doc;

        // Every page of the spread, with its guides and frame edges; the one worked on last, so its guides run on top.
        var spread = Spread().OrderBy(s => s.Ref == _sheet).ToList();
        foreach (var (r, x) in spread)
        {
            _drawOffset = x;
            var sheet = r.Resolve(doc);
            var page = ToView(doc.Setup.Page);
            dc.FillRectangle(Look.PageShadow, page.Translate(new Vector(4, 4)));
            var corner = ToView(new Pt(0, 0));
            using (dc.PushTransform(Matrix.CreateScale(Scale, Scale) * Matrix.CreateTranslation(corner.X, corner.Y)))
                _renderer.Draw(dc, doc, sheet, doc.Setup.Bleed);
            if (ShowGrid) DrawGrid(dc, doc);
            if (ShowGuides) DrawPrintGuides(dc, doc);
            if (ShowGuides) DrawGuides(dc, doc, sheet, r == _sheet ? null : page);
            DrawFrames(dc, sheet);
        }
        _drawOffset = null;
        if (spread.Count > 1)
        {
            // The spine between the two pages.
            var spine = Math.Round(Origin.X + doc.Setup.PageWidth * Scale) + 0.5;
            dc.DrawLine(new Pen(Look.Faint, 1), new Point(spine, ToView(new Pt(0, 0)).Y), new Point(spine, ToView(new Pt(0, doc.Setup.PageHeight)).Y));
        }
        if (CheckSpelling) DrawMisspelt(dc);
        DrawSelection(dc);
        DrawText(dc);
        DrawPen(dc);
        DrawDrag(dc);
        DrawRulers(dc, bounds);
    }

    /// <summary>The baseline grid across the page's live area, and the bleed's edge round the page.</summary>
    private void DrawPrintGuides(DrawingContext dc, Document doc)
    {
        var s = doc.Setup;
        if (s.Baselines is { } grid && grid.Step * Scale >= 4)
        {
            var live = ToView(s.Live);
            for (var y = grid.Snap(s.Live.Y); y <= s.Live.Bottom; y += grid.Step)
            {
                var vy = Math.Round(ToView(new Pt(0, y)).Y) + 0.5;
                dc.DrawLine(Look.BaselineGuide, new Point(live.Left, vy), new Point(live.Right, vy));
            }
        }
        if (s.Bleed > 0) dc.DrawRectangle(null, Look.BleedGuide, ToView(new Box(-s.Bleed, -s.Bleed, s.PageWidth + 2 * s.Bleed, s.PageHeight + 2 * s.Bleed)));
    }

    /// <summary>The document grid across the page, when its lines would be at least 6 pixels apart.</summary>
    private void DrawGrid(DrawingContext dc, Document doc)
    {
        var step = doc.Setup.Grid;
        if (step <= 0 || step * Scale < 6) return;
        var pen = new Pen(Look.GridLine, 1);
        var page = ToView(doc.Setup.Page);
        for (var x = step; x < doc.Setup.PageWidth; x += step)
        {
            var vx = Math.Round(ToView(new Pt(x, 0)).X) + 0.5;
            dc.DrawLine(pen, new Point(vx, page.Top), new Point(vx, page.Bottom));
        }
        for (var y = step; y < doc.Setup.PageHeight; y += step)
        {
            var vy = Math.Round(ToView(new Pt(0, y)).Y) + 0.5;
            dc.DrawLine(pen, new Point(page.Left, vy), new Point(page.Right, vy));
        }
    }

    /// <summary>A sheet's margins, columns and ruler guides (kept to <paramref name="within"/> for a page not being worked on).</summary>
    private void DrawGuides(DrawingContext dc, Document doc, Sheet sheet, Rect? within = null)
    {
        var (liveBox, columns) = doc.GuidesFor(sheet);
        var live = ToView(liveBox);
        dc.DrawRectangle(null, Look.MarginGuide, Crisp(live));
        foreach (var c in columns.Skip(1))
        {
            var x = Math.Round(ToView(new Pt(c.X, 0)).X) + 0.5;
            dc.DrawLine(Look.ColumnGuide, new Point(x, live.Top), new Point(x, live.Bottom));
        }
        foreach (var c in columns.SkipLast(1))
        {
            var x = Math.Round(ToView(new Pt(c.Right, 0)).X) + 0.5;
            dc.DrawLine(Look.ColumnGuide, new Point(x, live.Top), new Point(x, live.Bottom));
        }
        var guides = sheet.Guides.AsEnumerable();
        if (sheet is Page p && doc.MasterFor(p) is { } m) guides = guides.Concat(m.Guides);
        foreach (var g in guides) DrawGuide(dc, g.Vertical, g.Position, Look.RulerGuide, within);
    }

    private void DrawGuide(DrawingContext dc, bool vertical, double position, IPen pen, Rect? within = null)
    {
        var area = within ?? new Rect(RulerSize, RulerSize, Bounds.Width, Bounds.Height);
        if (vertical)
        {
            var x = Math.Round(ToView(new Pt(position, 0)).X) + 0.5;
            dc.DrawLine(pen, new Point(x, area.Top), new Point(x, area.Bottom));
        }
        else
        {
            var y = Math.Round(ToView(new Pt(0, position)).Y) + 0.5;
            dc.DrawLine(pen, new Point(area.Left, y), new Point(area.Right, y));
        }
    }

    private static Rect Crisp(Rect r) => new(Math.Round(r.X) + 0.5, Math.Round(r.Y) + 0.5, Math.Round(r.Width), Math.Round(r.Height));

    /// <summary>An object's frame drawn in the view, turned as it is.</summary>
    private void Outline(DrawingContext dc, PageObject o, IBrush? fill, IPen pen)
    {
        if (o.Rotation == 0) { dc.DrawRectangle(fill, pen, Crisp(ToView(o.Bounds))); return; }
        var corners = Shapes.Corners(o).Select(ToView).ToList();
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(corners[0], true);
            foreach (var q in corners.Skip(1)) c.LineTo(q);
            c.EndFigure(true);
        }
        dc.DrawGeometry(fill, pen, g);
    }

    /// <summary>Turns what is drawn next (in view units) the way an object is turned on the page.</summary>
    private DrawingContext.PushedState? Turned(DrawingContext dc, PageObject? o) =>
        o is { Rotation: not 0 } ? dc.PushTransform(PageRenderer.Turn(o, Scale, (Vector)ToView(new Pt(0, 0)))) : null;

    private void DrawFrames(DrawingContext dc, Sheet sheet)
    {
        foreach (var frame in sheet.All.OfType<TextFrame>())
        {
            Outline(dc, frame, null, Look.FrameEdge);
            if (frame.Columns > 1)
                foreach (var c in frame.ColumnBoxes().Skip(1))
                    dc.DrawLine(Look.FrameEdge, ToView(Shapes.ToPage(frame, new Pt(c.X - frame.Gutter / 2, c.Y))), ToView(Shapes.ToPage(frame, new Pt(c.X - frame.Gutter / 2, c.Bottom))));
            if (OversetMark(frame) is { } mark)
            {
                dc.FillRectangle(Brushes.White, mark);
                dc.DrawRectangle(null, new Pen(Look.Overset, 1.5), mark.Deflate(0.75));
                var pen = new Pen(Look.Overset, 1.5);
                dc.DrawLine(pen, new Point(mark.Center.X, mark.Top + 3), new Point(mark.Center.X, mark.Bottom - 3));
                dc.DrawLine(pen, new Point(mark.Left + 3, mark.Center.Y), new Point(mark.Right - 3, mark.Center.Y));
            }
        }
        foreach (var table in sheet.All.OfType<TableObject>())
        {
            if (Tables.Place(Doc, table) is not { } placed) continue;
            for (var i = 0; i < placed.Head.Cells.Count; i++)
                if (placed.FrameOf(i)?.Id == table.Id && placed.CellBox(i) is { } box && LayoutOf(placed.Head.Cells[i]) is { Overset: true })
                {
                    // A little red mark in the corner of a cell whose text doesn't fit.
                    var c = ToView(new Pt(box.Right, box.Bottom));
                    dc.FillRectangle(Look.Overset, new Rect(c.X - 7, c.Y - 7, 6, 6));
                }
            // Rows with no frame left to go in: a mark at the foot of the table's last frame.
            if (placed.Frames[^1].Id == table.Id && placed.Rows.Any(r => r.Frame < 0))
            {
                var c = ToView(new Pt(table.X + table.W / 2, table.Y + table.H));
                var mark = new Rect(c.X - 6, c.Y - 6, 12, 12);
                dc.FillRectangle(Brushes.White, mark);
                dc.DrawRectangle(null, new Pen(Look.Overset, 1.5), mark.Deflate(0.75));
                var pen = new Pen(Look.Overset, 1.5);
                dc.DrawLine(pen, new Point(mark.Center.X, mark.Top + 3), new Point(mark.Center.X, mark.Bottom - 3));
                dc.DrawLine(pen, new Point(mark.Left + 3, mark.Center.Y), new Point(mark.Right - 3, mark.Center.Y));
            }
        }
        if (LinkFrom is not null && sheet.All.FirstOrDefault(o => o.Id == LinkFrom) is { } from)
            Outline(dc, from, Look.LinkFrom, new Pen(Look.Handle, 2));

        // The chains of the selected text frames, as arrows from each frame to the next.
        var shown = new HashSet<string>();
        var here = sheet.All.ToHashSet();
        foreach (var f in Selected.OfType<TextFrame>().Concat(LinkFrom is not null && Doc.Find(LinkFrom) is TextFrame lf ? [lf] : []))
        {
            if (!shown.Add(f.StoryId) || Doc.StoryOf(f) is not { } story) continue;
            var frames = Chains.FramesOf(Doc, story);
            for (var i = 0; i + 1 < frames.Count; i++)
            {
                var a = frames[i];
                var b = frames[i + 1];
                var onA = here.Contains(a);
                var onB = here.Contains(b);
                if (!onA && !onB) continue;
                var aEnd = ToView(Shapes.ToPage(a, new Pt(a.X + a.W, a.Y + a.H)));
                var bStart = ToView(Shapes.ToPage(b, new Pt(b.X, b.Y)));
                var start = onA ? aEnd : bStart - new Vector(30, 30);
                var end = onB ? bStart : start + new Vector(30, 30);
                DrawArrow(dc, start, end);
            }
        }
    }

    private static void DrawArrow(DrawingContext dc, Point a, Point b)
    {
        dc.DrawLine(Look.Chain, a, b);
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        foreach (var side in new[] { -0.45, 0.45 })
            dc.DrawLine(Look.Chain, b, new Point(b.X - 9 * Math.Cos(angle + side), b.Y - 9 * Math.Sin(angle + side)));
    }

    private void Square(DrawingContext dc, Pt at, bool hollow)
    {
        var c = ToView(at);
        var r = new Rect(Math.Round(c.X - HandleSize / 2), Math.Round(c.Y - HandleSize / 2), HandleSize, HandleSize);
        if (hollow) dc.DrawRectangle(Brushes.White, Look.Selection, r);
        else dc.DrawRectangle(Look.Handle, new Pen(Brushes.White, 1), r);
    }

    private void DrawSelection(DrawingContext dc)
    {
        var selected = Selected;
        foreach (var o in selected)
        {
            if (o is not LineObject) Outline(dc, o, null, o is GroupObject ? Look.Marquee : Look.Selection);
            if (Editing is not null || EditingPoints == o.Id) continue;
            foreach (var h in Handles(o)) Square(dc, h, o.Locked);
            if (selected.Count == 1 && CanRotate(o) && !o.Locked)
            {
                var top = ToView(Shapes.ToPage(o, new Pt(o.X + o.W / 2, o.Y)));
                var knob = ToView(RotateHandleAt(o));
                dc.DrawLine(Look.Selection, top, knob);
                dc.DrawEllipse(Look.Handle, new Pen(Brushes.White, 1), knob, HandleSize / 2 + 0.5, HandleSize / 2 + 0.5);
            }
        }
        if (EditingPoints is { } id && Doc.Find(id) is PathObject path) DrawPoints(dc, path.Nodes, -1);
    }

    /// <summary>A path's points as squares and their handles as dots on lines; <paramref name="first"/> marks the point that closes a path being drawn.</summary>
    private void DrawPoints(DrawingContext dc, IReadOnlyList<PathNode> nodes, int first)
    {
        var line = new Pen(Look.Handle, 1);
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            foreach (var h in new[] { n.In, n.Out })
            {
                if (h is not { } q) continue;
                dc.DrawLine(line, ToView(n.At), ToView(q));
                dc.DrawEllipse(Brushes.White, line, ToView(q), 3.5, 3.5);
            }
            Square(dc, n.At, hollow: i == first);
        }
    }

    /// <summary>The letters of text on a path from one index to another, as turned boxes (corners in page points).</summary>
    private List<Pt[]> PathBoxes(PathObject path, int start, int end)
    {
        var boxes = new List<Pt[]>();
        if (path.StoryId is not { } id || !Doc.Stories.TryGetValue(id, out var story)) return boxes;
        foreach (var g in PathText.Place(story, PathText.Points(path), _layouts.Measurer, path.TextOffset).Glyphs)
            if (g.Index >= start && g.Index < end) boxes.Add(GlyphBox(g, 0.8, 0.25));
        return boxes;
    }

    /// <summary>A letter on a path as a turned box: from its baseline up <paramref name="up"/> and down <paramref name="down"/> of its size.</summary>
    private static Pt[] GlyphBox(PathGlyph g, double up, double down)
    {
        var r = g.Angle * Math.PI / 180;
        var (cx, cy) = (Math.Cos(r), Math.Sin(r));   // along the baseline
        var (nx, ny) = (Math.Sin(r), -Math.Cos(r));  // up from it
        Pt At(double a, double u) => new(g.At.X + cx * a + nx * u, g.At.Y + cy * a + ny * u);
        var (top, bottom) = (g.Format.Size * up, -g.Format.Size * down);
        return [At(0, bottom), At(g.Width, bottom), At(g.Width, top), At(0, top)];
    }

    private void FillPolygon(DrawingContext dc, IBrush brush, Pt[] corners)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(ToView(corners[0]), true);
            foreach (var p in corners.Skip(1)) c.LineTo(ToView(p));
            c.EndFigure(true);
        }
        dc.DrawGeometry(brush, null, g);
    }

    /// <summary>The caret and selection in text on a path, along its letters.</summary>
    private void DrawPathCaret(DrawingContext dc, TextEdit t, PathObject path, Story story)
    {
        using var turned = Turned(dc, path);
        if (t.Length > 0)
            foreach (var box in PathBoxes(path, t.Start, t.Start + t.Length)) FillPolygon(dc, Look.TextSelection, box);
        if (!_caretOn && IsFocused) return;
        var glyphs = PathText.Place(story, PathText.Points(path), _layouts.Measurer, path.TextOffset).Glyphs;
        // At the start of the letter it comes before, or the end of the one after it.
        Pt at;
        double angle, size;
        if (glyphs.FirstOrDefault(g => g.Index >= t.Caret) is { } next)
            (at, angle, size) = (next.At, next.Angle, next.Format.Size);
        else if (glyphs.Count > 0 && glyphs[^1] is var last)
        {
            var r = last.Angle * Math.PI / 180;
            (at, angle, size) = (new Pt(last.At.X + Math.Cos(r) * last.Width, last.At.Y + Math.Sin(r) * last.Width), last.Angle, last.Format.Size);
        }
        else (at, angle, size) = (PathText.At(PathText.Points(path), 0).At, 0, story.FormatAt(0).Size);
        var a = angle * Math.PI / 180;
        var (nx, ny) = (Math.Sin(a), -Math.Cos(a));
        dc.DrawLine(new Pen(Look.Caret, 1.5), ToView(new Pt(at.X - nx * size * 0.25, at.Y - ny * size * 0.25)), ToView(new Pt(at.X + nx * size * 0.85, at.Y + ny * size * 0.85)));
    }

    private void DrawText(DrawingContext dc)
    {
        if (Editing is { } pt && Doc.Stories.GetValueOrDefault(pt.StoryId) is { OnPath: { } pathId } onPath)
        {
            if (CurrentSheet.All.FirstOrDefault(o => o.Id == pathId) is PathObject path) DrawPathCaret(dc, pt, path, onPath);
            return;
        }
        if (Editing is not { } t || LayoutOf(t.StoryId) is not { } layout) return;
        var here = CurrentSheet.All.ToDictionary(o => o.Id);
        PageObject? FrameOf(int area) => here.GetValueOrDefault(Chains.ObjectOf(layout.Areas[area].FrameId));
        if (t.Length > 0)
        {
            foreach (var (area, box) in layout.SelectionBoxes(t.Start, t.Start + t.Length))
                if (FrameOf(area) is { } f)
                    using (Turned(dc, f))
                        dc.FillRectangle(Look.TextSelection, ToView(box));
        }
        if (!_caretOn && IsFocused) return;
        var i = layout.LineOf(t.Caret);
        if (i < 0 || FrameOf(layout.Lines[i].Area) is not { } frame) return;
        var line = layout.Lines[i];
        var x = layout.CaretX(i, t.Caret);
        var top = ToView(new Pt(x, line.Baseline - line.Ascent));
        var bottom = ToView(new Pt(x, line.Baseline + line.Descent));
        var cx = frame.Rotation == 0 ? Math.Round(top.X) + 0.5 : top.X;
        using (Turned(dc, frame))
            dc.DrawLine(new Pen(Look.Caret, 1.5), new Point(cx, top.Y), new Point(cx, bottom.Y));
    }

    /// <summary>A red wavy line under each misspelt word on the page being worked on (on screen only).</summary>
    private void DrawMisspelt(DrawingContext dc)
    {
        var here = CurrentSheet.All.ToDictionary(o => o.Id);
        var pen = new Pen(Look.Overset, 1);
        foreach (var path in here.Values.OfType<PathObject>().Where(p => p.StoryId is not null))
            foreach (var m in MisspeltIn(path.StoryId!))
                using (Turned(dc, path))
                    foreach (var box in PathBoxes(path, m.Start, m.Start + m.Length))
                        dc.DrawLine(pen, ToView(box[0]), ToView(box[1]));
        var shownHere = here.Values.SelectMany(o => o switch { TextFrame f => [f.StoryId], TableObject t => t.Cells, _ => Enumerable.Empty<string>() }).Distinct().ToList();
        // Footnotes are set in their stories' frames.
        shownHere.AddRange(shownHere.SelectMany(id => Doc.Stories.TryGetValue(id, out var s) ? Footnotes.Numbers(s).Keys : Enumerable.Empty<string>()).ToList());
        foreach (var storyId in shownHere)
        {
            if (LayoutOf(storyId) is not { } layout) continue;
            foreach (var m in MisspeltIn(storyId))
                foreach (var (area, box) in layout.SelectionBoxes(m.Start, m.Start + m.Length))
                {
                    if (!here.TryGetValue(Chains.ObjectOf(layout.Areas[area].FrameId), out var frame)) continue;
                    var a = ToView(new Pt(box.X, box.Bottom));
                    var b = ToView(new Pt(box.Right, box.Bottom));
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(a.X, a.Y), false);
                        var up = true;
                        for (var x = a.X + 2; x <= b.X; x += 2, up = !up) c.LineTo(new Point(x, a.Y + (up ? -1.5 : 0.5)));
                        c.EndFigure(false);
                    }
                    using (Turned(dc, frame)) dc.DrawGeometry(null, pen, g);
                }
        }
    }

    /// <summary>The path the Pen is drawing, with a line on to the mouse.</summary>
    private void DrawPen(DrawingContext dc)
    {
        if (_pen is not { Count: > 0 } nodes) return;
        if (nodes.Count > 1)
            using (dc.PushTransform(Matrix.CreateScale(Scale, Scale) * Matrix.CreateTranslation(ToView(new Pt(0, 0)).X, ToView(new Pt(0, 0)).Y)))
                dc.DrawGeometry(null, new Pen(Look.Handle, 1.5 / Scale), PageRenderer.PathGeometry(new PathObject(), nodes, closed: false));
        if (_penPreview is { } q && _drag != Drag.Pen) dc.DrawLine(Look.Marquee, ToView(nodes[^1].At), ToView(q));
        DrawPoints(dc, nodes, nodes.Count >= 3 ? 0 : -1);
    }

    private void DrawDrag(DrawingContext dc)
    {
        if (!_dragMoved) return;
        switch (_drag)
        {
            case Drag.Marquee:
                dc.DrawRectangle(null, Look.Marquee, Crisp(ToView(Box.FromCorners(_dragStart, _dragNow))));
                break;
            case Drag.Create when _tool == Tool.Line:
                dc.DrawLine(Look.Selection, ToView(_dragStart), ToView(_dragNow));
                break;
            case Drag.Create:
                var r = ToView(Box.FromCorners(_dragStart, _dragNow));
                if (_tool == Tool.Ellipse) dc.DrawEllipse(null, Look.Selection, r.Center, r.Width / 2, r.Height / 2);
                dc.DrawRectangle(null, Look.Marquee, Crisp(r));
                break;
            case Drag.Guide:
                DrawGuide(dc, _guideVertical, _guidePosition, new Pen(Look.Handle, 1));
                break;
        }
    }

    // ---- Rulers -----------------------------------------------------------------------------------------------------

    private void DrawRulers(DrawingContext dc, Rect bounds)
    {
        dc.FillRectangle(Look.Ruler, new Rect(0, 0, bounds.Width, RulerSize));
        dc.FillRectangle(Look.Ruler, new Rect(0, 0, RulerSize, bounds.Height));
        var line = new Pen(Look.Border, 1);
        dc.DrawLine(line, new Point(0, RulerSize - 0.5), new Point(bounds.Width, RulerSize - 0.5));
        dc.DrawLine(line, new Point(RulerSize - 0.5, 0), new Point(RulerSize - 0.5, bounds.Height));

        var unit = Doc.Setup.Unit;
        var (major, minors) = TickSteps(unit);
        var tick = new Pen(Look.RulerTick, 1);
        // Across the top.
        var first = Math.Floor(ToPage(new Point(RulerSize, 0)).X / major) * major;
        for (var v = first; ToView(new Pt(v, 0)).X < bounds.Width; v += major)
        {
            var x = Math.Round(ToView(new Pt(v, 0)).X) + 0.5;
            if (x > RulerSize) dc.DrawLine(tick, new Point(x, 2), new Point(x, RulerSize));
            for (var k = 1; k < minors; k++)
            {
                var mx = Math.Round(ToView(new Pt(v + major * k / minors, 0)).X) + 0.5;
                if (mx > RulerSize) dc.DrawLine(tick, new Point(mx, RulerSize - (k * 2 == minors ? 8 : 4)), new Point(mx, RulerSize));
            }
            if (x > RulerSize) dc.DrawText(Label(v, unit), new Point(x + 3, 1));
        }
        // Down the left.
        first = Math.Floor(ToPage(new Point(0, RulerSize)).Y / major) * major;
        for (var v = first; ToView(new Pt(0, v)).Y < bounds.Height; v += major)
        {
            var y = Math.Round(ToView(new Pt(0, v)).Y) + 0.5;
            if (y > RulerSize) dc.DrawLine(tick, new Point(2, y), new Point(RulerSize, y));
            for (var k = 1; k < minors; k++)
            {
                var my = Math.Round(ToView(new Pt(0, v + major * k / minors)).Y) + 0.5;
                if (my > RulerSize) dc.DrawLine(tick, new Point(RulerSize - (k * 2 == minors ? 8 : 4), my), new Point(RulerSize, my));
            }
            if (y > RulerSize)
            {
                var label = Label(v, unit);
                using (dc.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(1, y - 3)))
                    dc.DrawText(label, new Point(0, 0));
            }
        }
        dc.FillRectangle(Look.Ruler, new Rect(0, 0, RulerSize, RulerSize));
        if (_mouse is { } m)
        {
            var mark = new Pen(Look.RulerMark, 1);
            if (m.X > RulerSize) dc.DrawLine(mark, new Point(Math.Round(m.X) + 0.5, 0), new Point(Math.Round(m.X) + 0.5, RulerSize));
            if (m.Y > RulerSize) dc.DrawLine(mark, new Point(0, Math.Round(m.Y) + 0.5), new Point(RulerSize, Math.Round(m.Y) + 0.5));
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _mouse = e.GetPosition(this);
    }

    /// <summary>The labelled step along the rulers (in points), at least 60 pixels apart, and how many parts it splits into.</summary>
    private (double Major, int Minors) TickSteps(Unit unit)
    {
        var u = Units.Points(unit);
        double[] multiples = unit == Unit.Point ? [1, 2, 5, 10, 25, 50, 100, 200, 500, 1000] : [0.125, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 200];
        var major = multiples.Select(k => k * u).FirstOrDefault(s => s * Scale >= 60, multiples[^1] * u);
        int[] parts = unit == Unit.Inch ? [8, 4, 2] : [10, 5, 2];
        var minors = parts.FirstOrDefault(n => major / n * Scale >= 5, 1);
        return (major, minors);
    }

    private static FormattedText Label(double points, Unit unit) =>
        new(Math.Round(Units.FromPoints(points, unit), 3).ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 9, Look.RulerTick);

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is Drag.Move or Drag.Resize or Drag.Crop or Drag.Rotate or Drag.Node && _editor.Busy) _editor.Commit("Move");
        _drag = Drag.None;
    }
}
