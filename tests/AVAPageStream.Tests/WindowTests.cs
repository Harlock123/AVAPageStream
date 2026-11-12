using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>
/// The window on Avalonia's headless platform, with the real fonts: drawing with each tool, moving and resizing,
/// typing into frames and text flowing on through a link, styling text from the Text tab, pages and masters, guides,
/// undo, saving and opening, and exporting. Set AVAPAGESTREAM_SHOTS to a folder to save pictures of the window there
/// (the README's screenshots come from here).
/// </summary>
public partial class WindowTests
{
    private static string? Shots => Environment.GetEnvironmentVariable("AVAPAGESTREAM_SHOTS") is { Length: > 0 } d ? d : null;

    private static string Folder() => Directory.CreateTempSubdirectory("avapagestream-").FullName;

    // A 2×2 PNG: red, green, blue and white.
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP4z8DAwMDAxMDAwMDAAAANHQEDasKb6QAAAABJRU5ErkJggg==");

    private static MainWindow Open(string? file = null, int width = 1360, int height = 880)
    {
        var w = new MainWindow(Path.Combine(Folder(), "settings.json"), file) { Width = width, Height = height };
        w.Ask = (_, _, _) => throw new Xunit.Sdk.XunitException("Nothing should have asked.");
        w.PickOpen = _ => throw new Xunit.Sdk.XunitException("Nothing should have asked for a file.");
        w.PickSave = (_, _) => throw new Xunit.Sdk.XunitException("Nothing should have asked where to save.");
        w.AskExport = _ => throw new Xunit.Sdk.XunitException("Nothing should have asked what to export.");
        w.Show();
        Pump();
        w.View.ActualSize(); // 100%: one point is 4/3 of a pixel, from the page's corner at View.Origin
        Pump();
        return w;
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void Wait(Task task)
    {
        for (var i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            Thread.Sleep(5);
            Pump();
        }
        Assert.True(task.IsCompleted, "It didn't finish in time.");
        task.GetAwaiter().GetResult();
        Pump();
    }

    private static void Done(MainWindow w)
    {
        w.Ask = (_, _, _) => Task.FromResult(1);
        w.Close();
        Pump();
    }

    /// <summary>Where a point on the page is in the window.</summary>
    private static Point At(MainWindow w, double x, double y) => w.View.TranslatePoint(w.View.ToView(new Pt(x, y)), w)!.Value;

    private static void Drag(MainWindow w, Pt from, Pt to, RawInputModifiers mods = RawInputModifiers.None)
    {
        var a = At(w, from.X, from.Y);
        var b = At(w, to.X, to.Y);
        w.MouseMove(a, mods);
        w.MouseDown(a, MouseButton.Left, mods);
        for (var i = 1; i <= 4; i++)
            w.MouseMove(new Point(a.X + (b.X - a.X) * i / 4, a.Y + (b.Y - a.Y) * i / 4), mods | RawInputModifiers.LeftMouseButton);
        w.MouseUp(b, MouseButton.Left, mods);
        Pump();
    }

    private static void Click(MainWindow w, Pt p, RawInputModifiers mods = RawInputModifiers.None)
    {
        var a = At(w, p.X, p.Y);
        w.MouseMove(a, mods);
        w.MouseDown(a, MouseButton.Left, mods);
        w.MouseUp(a, MouseButton.Left, mods);
        Pump();
    }

    private static void Press(MainWindow w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    private static void Type(MainWindow w, string text)
    {
        foreach (var part in text.Split('\n'))
        {
            if (part.Length > 0) w.KeyTextInput(part);
            if (!ReferenceEquals(part, text.Split('\n')[^1])) Press(w, Key.Enter);
        }
        Pump();
    }

    private static void TypeLines(MainWindow w, params string[] paragraphs)
    {
        for (var i = 0; i < paragraphs.Length; i++)
        {
            if (i > 0) Press(w, Key.Enter);
            w.KeyTextInput(paragraphs[i]);
            Pump();
        }
    }

    private static void Shot(MainWindow w, string name)
    {
        if (Shots is not { } dir) return;
        Directory.CreateDirectory(dir);
        Pump();
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"AVAPageStream-{name}.png"), PngBitmapEncoderOptions.Default);
    }

    private static void Tool(MainWindow w, Tool tool)
    {
        w.View.Tool = tool;
        w.View.Focus();
        Pump();
    }

    private const string Lorem =
        "Frames hold everything on a page, and a story flows through as many of them as you link together, column by " +
        "column and page by page. Type, and the text reflows as you go: resize a frame and the lines break again.";

    [AvaloniaFact]
    public void A_new_window_shows_one_letter_page_with_the_text_tool_ready_to_use()
    {
        var w = Open();
        Assert.Equal("Untitled — AVAPageStream", w.Title);
        Assert.Single(w.Editor.Doc.Pages);
        Assert.Equal("Page 1 of 1", w.PageInfo.Text);
        Assert.Equal("100%", w.ZoomInfo.Text);
        Assert.Contains("Text tool", w.Message.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void Shapes_are_drawn_with_their_tools_and_snap_to_the_margins()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Rectangle);
        Drag(w, new Pt(38, 34), new Pt(200, 150)); // starts within snapping reach of the ½ in margins
        var rect = Assert.IsType<RectangleObject>(Assert.Single(w.Editor.Doc.Pages[0].Objects));
        Assert.Equal(36, rect.X, 1);
        Assert.Equal(36, rect.Y, 1);
        Assert.Equal(164, rect.W, 1);

        Tool(w, AVAPageStream.Tool.Ellipse);
        Drag(w, new Pt(250, 250), new Pt(330, 290), RawInputModifiers.Shift); // Shift: a circle
        var ellipse = Assert.IsType<EllipseObject>(w.Editor.Doc.Pages[0].Objects[1]);
        Assert.Equal(ellipse.W, ellipse.H, 1);

        Tool(w, AVAPageStream.Tool.Line);
        Drag(w, new Pt(100, 400), new Pt(300, 300));
        var line = Assert.IsType<LineObject>(w.Editor.Doc.Pages[0].Objects[2]);
        Assert.True(line.Rising);
        Assert.Equal("Line · X 100  Y 300  W 200  H 100 pt", w.SelectionInfo.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void Objects_move_resize_and_nudge_and_each_change_undoes()
    {
        var w = Open();
        var rect = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 100, Y = 100, W = 100, H = 80 });
        Tool(w, AVAPageStream.Tool.Select);
        Drag(w, new Pt(150, 140), new Pt(250, 190));
        Assert.Equal(new Box(200, 150, 100, 80), w.Editor.Doc.Find(rect.Id)!.Bounds);
        Drag(w, new Pt(300, 230), new Pt(340, 260)); // the bottom-right handle
        Assert.Equal(new Box(200, 150, 140, 110), w.Editor.Doc.Find(rect.Id)!.Bounds);
        Press(w, Key.Right);
        Press(w, Key.Right);
        Press(w, Key.Down, RawInputModifiers.Shift);
        Assert.Equal(new Box(202, 160, 140, 110), w.Editor.Doc.Find(rect.Id)!.Bounds);

        w.Undo(); // both nudges right and the one down were one run of nudging
        Pump();
        Assert.Equal(new Box(200, 150, 140, 110), w.Editor.Doc.Find(rect.Id)!.Bounds);
        w.Undo();
        Assert.Equal(new Box(200, 150, 100, 80), w.Editor.Doc.Find(rect.Id)!.Bounds);
        w.Undo();
        Assert.Equal(new Box(100, 100, 100, 80), w.Editor.Doc.Find(rect.Id)!.Bounds);
        Done(w);
    }

    [AvaloniaFact]
    public void Typing_into_a_frame_flows_on_into_a_linked_frame()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(272, 160));
        var first = Assert.IsType<TextFrame>(Assert.Single(w.Editor.Doc.Pages[0].Objects));
        Assert.NotNull(w.View.Editing);
        TypeLines(w, Lorem, Lorem);
        var story = w.Editor.Doc.Stories[first.StoryId];
        Assert.Equal(Lorem + "\n" + Lorem, story.Text);
        Assert.True(w.View.LayoutOf(story.Id)!.Overset);
        Assert.Contains("characters overset", w.SelectionInfo.Text);
        Shot(w, "overset");

        // A second frame, then link the first to it by clicking the overset mark.
        Press(w, Key.Escape);
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(320, 72), new Pt(540, 400));
        Press(w, Key.Escape);
        var second = (TextFrame)w.Editor.Doc.Pages[0].Objects[1];
        Tool(w, AVAPageStream.Tool.Select);
        Click(w, new Pt(272, 160)); // the overset mark at the first frame's corner
        Assert.Equal(AVAPageStream.Tool.Link, w.View.Tool);
        Click(w, new Pt(400, 200));
        story = w.Editor.Doc.Stories[first.StoryId];
        Assert.Equal([first.Id, second.Id], story.Frames);
        var layout = w.View.LayoutOf(story.Id)!;
        Assert.False(layout.Overset);
        Assert.Contains(layout.Lines, l => layout.Areas[l.Area].FrameId == second.Id);
        Assert.False(w.Editor.Doc.Stories.ContainsKey(second.StoryId) && second.StoryId != first.StoryId);
        Done(w);
    }

    [AvaloniaFact]
    public void The_caret_moves_by_keys_and_text_is_deleted_and_selected()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(400, 300));
        Type(w, "one two three");
        Press(w, Key.Left, RawInputModifiers.Control);        // before "three"
        Press(w, Key.Back, RawInputModifiers.Control);        // deletes "two "
        var t = w.View.Editing!;
        var story = () => w.Editor.Doc.Stories[t.StoryId];
        Assert.Equal("one three", story().Text);
        Press(w, Key.Home);
        Press(w, Key.Right, RawInputModifiers.Shift);
        Press(w, Key.Right, RawInputModifiers.Shift);
        Press(w, Key.Right, RawInputModifiers.Shift);
        Assert.Equal("one", w.View.SelectedText());
        Type(w, "ONE");
        Assert.Equal("ONE three", story().Text);
        Press(w, Key.End);
        Press(w, Key.Enter);
        Type(w, "second");
        Assert.Equal(2, story().ParagraphCount);
        Press(w, Key.Up);
        Assert.Equal(0, story().ParagraphAt(t.Caret));
        w.Undo();
        Assert.Equal("ONE three", story().Text);
        Done(w);
    }

    [AvaloniaFact]
    public void The_text_tab_styles_the_selection_and_paragraph_styles_apply()
    {
        var w = Open();
        Tool(w, AVAPageStream.Tool.Text);
        Drag(w, new Pt(72, 72), new Pt(540, 400));
        TypeLines(w, "A headline", "Body text goes here.");
        var t = w.View.Editing!;
        var storyId = t.StoryId;

        w.Panel.SelectedIndex = 1; // the Text tab
        Pump();

        // The first paragraph as a heading, from the style list.
        t.Caret = t.Anchor = 2;
        w.Refresh();
        w.Panel.StyleChoice.SelectedItem = "Heading";
        Pump();
        var story = w.Editor.Doc.Stories[storyId];
        Assert.Equal("Heading", story.Paras[0].Style);
        Assert.Equal(24, story.FormatAt(0).Size);
        Assert.True(story.FormatAt(0).Bold);
        Assert.Equal(12, story.FormatAt(12).Size);

        // "Body" made italic and 14 pt with the toggles and the size box.
        w.View.Editing!.Anchor = 11;
        w.View.Editing!.Caret = 15;
        w.Refresh();
        w.Panel.ItalicToggle.IsChecked = true;
        Pump();
        w.Panel.SizeBox.Focus();
        w.Panel.SizeBox.Text = "14";
        Press(w, Key.Enter);
        story = w.Editor.Doc.Stories[storyId];
        Assert.True(story.FormatAt(11).Italic);
        Assert.Equal(14, story.FormatAt(14).Size);
        Assert.False(story.FormatAt(16).Italic);

        // Centred, from the paragraph buttons.
        w.Panel.AlignButtons[Align.Centre].IsChecked = true;
        Pump();
        Assert.Equal(Align.Centre, w.Editor.Doc.Stories[storyId].Paras[1].Align);
        Shot(w, "styled");
        Done(w);
    }

    [AvaloniaFact]
    public void The_object_tab_moves_and_colours_the_selection_in_any_unit()
    {
        var w = Open();
        var rect = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 10, Y = 10, W = 50, H = 50 });
        w.View.Select([rect.Id]);
        Pump();
        Assert.Equal("10 pt", w.Panel.XBox.Text);
        w.Panel.XBox.Focus();
        w.Panel.XBox.Text = "1in";
        Press(w, Key.Enter);
        w.Panel.FillBox.Focus();
        w.Panel.FillBox.Text = "red";
        Press(w, Key.Enter);
        var o = w.Editor.Doc.Find(rect.Id)!;
        Assert.Equal(72, o.X);
        Assert.Equal("#E02020", o.Fill);
        Assert.Equal("Red", w.Panel.FillBox.Text);

        w.Panel.YBox.Focus();
        w.Panel.YBox.Text = "nonsense";
        Press(w, Key.Enter);
        Assert.Contains("Type a measure", w.Message.Text);
        Assert.Equal(10, w.Editor.Doc.Find(rect.Id)!.Y);
        Done(w);
    }

    [AvaloniaFact]
    public void Pages_are_added_and_deleted_and_master_objects_show_on_every_page()
    {
        var w = Open();
        w.View.FitPage(); // the foot of the page is out of sight at 100%
        w.ShowSheet(SheetRef.MasterAt(0));
        Assert.Equal("A-Master", w.PageInfo.Text);
        Tool(w, AVAPageStream.Tool.Rectangle);
        Drag(w, new Pt(36, 740), new Pt(576, 756));
        Assert.Single(w.Editor.Doc.Masters[0].Objects);
        w.ShowSheet(SheetRef.PageAt(0));
        w.AddPage(after: true);
        w.AddPage(after: true);
        Assert.Equal("Page 3 of 3", w.PageInfo.Text);
        Assert.All(w.Editor.Doc.Pages, p => Assert.Equal("A-Master", p.Master));
        // The master's bar is drawn on the page, but can't be picked there.
        Tool(w, AVAPageStream.Tool.Select);
        Click(w, new Pt(300, 748));
        Assert.Empty(w.View.Selected);
        w.DeletePage();
        Assert.Equal(2, w.Editor.Doc.Pages.Count);
        Assert.Equal("Page 2 of 2", w.PageInfo.Text);
        Press(w, Key.PageUp);
        Assert.Equal("Page 1 of 2", w.PageInfo.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void Guides_are_dragged_out_of_the_rulers_and_back()
    {
        var w = Open();
        var ruler = w.View.TranslatePoint(new Point(w.View.ToView(new Pt(200, 0)).X, 10), w)!.Value;
        var onPage = At(w, 200, 300);
        w.MouseMove(new Point(onPage.X, ruler.Y));
        w.MouseDown(new Point(onPage.X, ruler.Y), MouseButton.Left);
        w.MouseMove(new Point(onPage.X, onPage.Y - 40), RawInputModifiers.LeftMouseButton);
        w.MouseMove(onPage, RawInputModifiers.LeftMouseButton);
        w.MouseUp(onPage, MouseButton.Left);
        Pump();
        var guide = Assert.Single(w.Editor.Doc.Pages[0].Guides);
        Assert.False(guide.Vertical);
        Assert.Equal(300, guide.Position, 0);

        // Dragged back onto the ruler, it goes.
        Tool(w, AVAPageStream.Tool.Select);
        var g = At(w, 400, guide.Position);
        w.MouseMove(g);
        w.MouseDown(g, MouseButton.Left);
        w.MouseMove(new Point(g.X, g.Y - 100), RawInputModifiers.LeftMouseButton);
        w.MouseMove(new Point(g.X, ruler.Y), RawInputModifiers.LeftMouseButton);
        w.MouseUp(new Point(g.X, ruler.Y), MouseButton.Left);
        Pump();
        Assert.Empty(w.Editor.Doc.Pages[0].Guides);
        Done(w);
    }

    [AvaloniaFact]
    public void A_picture_is_placed_from_a_file_and_kept_in_the_document()
    {
        var dir = Folder();
        var image = Path.Combine(dir, "dots.png");
        File.WriteAllBytes(image, Png);
        var w = Open();
        w.PickOpen = kind => Task.FromResult<string?>(kind == "image" ? image : null);
        Tool(w, AVAPageStream.Tool.Picture);
        Drag(w, new Pt(100, 100), new Pt(300, 250));
        Pump();
        var picture = Assert.IsType<PictureObject>(Assert.Single(w.Editor.Doc.Pages[0].Objects));
        Assert.Equal(Png, w.Editor.Doc.Images.Get(picture.Image));

        var path = Path.Combine(dir, "with picture.avps");
        Assert.True(w.SaveTo(path));
        File.Delete(image);
        Done(w);
        var again = Open(path);
        Assert.Equal(Png, again.Editor.Doc.Images.Get(((PictureObject)again.Editor.Doc.Pages[0].Objects[0]).Image));
        Done(again);
    }

    [AvaloniaFact]
    public void Saving_and_opening_keep_everything_and_the_title_shows_unsaved_changes()
    {
        var dir = Folder();
        var w = Open();
        w.Editor.Add(SheetRef.PageAt(0), new EllipseObject { X = 10, Y = 10, W = 40, H = 40, Fill = "#2050D0" });
        Pump();
        Assert.Equal("Untitled • — AVAPageStream", w.Title);
        var path = Path.Combine(dir, "brochure");
        w.PickSave = (kind, suggested) => Task.FromResult<string?>(kind == "document" ? path : null);
        Wait(w.Save());
        Assert.Equal("brochure — AVAPageStream", w.Title);
        Assert.True(File.Exists(path + ".avps"));
        Done(w);

        var again = Open(path + ".avps");
        Assert.Equal("#2050D0", Assert.IsType<EllipseObject>(again.Editor.Doc.Pages[0].Objects[0]).Fill);
        Assert.Contains("Opened brochure.avps", again.Message.Text);
        Assert.Equal(path + ".avps", again.Settings.Recent[0]);
        Done(again);

        var bad = Path.Combine(dir, "bad.avps");
        File.WriteAllText(bad, "not a document");
        var w3 = Open(bad);
        Assert.Contains("isn't an AVAPageStream document", w3.Message.Text);
        Done(w3);
    }

    [AvaloniaFact]
    public void Closing_with_changes_asks_and_cancel_keeps_the_window()
    {
        var w = Open();
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { W = 10, H = 10 });
        var asked = 0;
        w.Ask = (_, _, _) => { asked++; return Task.FromResult(2); };
        w.Close();
        Pump();
        Assert.Equal(1, asked);
        Assert.True(w.IsVisible);
        Done(w);
    }

    [AvaloniaFact]
    public void Pages_export_to_pdf_and_png()
    {
        var dir = Folder();
        var w = Open();
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 200 });
        w.Editor.ReplaceText(frame.StoryId, 0, 0, "Exported text");
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 72, Y = 300, W = 200, H = 100, Fill = "#FFD800" });
        var picture = w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 300, Y = 300, W = 100, H = 100 });
        var key = w.Editor.Doc.Images.Add(Png);
        w.Editor.Change("Place picture", d => ((PictureObject)d.Find(picture.Id)!).Image = key);
        w.AddPage(after: true);
        var pdf = Path.Combine(dir, "out.pdf");
        var png = Path.Combine(dir, "out.png");
        w.PickSave = (kind, _) => Task.FromResult<string?>(kind == "pdf" ? pdf : png);
        w.AskExport = kind => Task.FromResult<ExportChoice?>(kind == "pdf" ? new ExportChoice([0, 1], 150) : new ExportChoice([0, 1], 72));

        Wait(w.ExportPdf());
        var bytes = File.ReadAllBytes(pdf);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Count 2", text);
        Assert.Contains("LiberationSerif", text);     // the font is embedded
        Assert.DoesNotContain("/DCTDecode", text);    // and pictures are kept losslessly, not as JPEG
        Assert.Contains("Exported 2 pages", w.Message.Text);

        w.ShowSheet(SheetRef.PageAt(0));
        Wait(w.ExportPng());
        using var page1 = new Bitmap(Path.Combine(dir, "out-1.png"));
        Assert.Equal(new PixelSize(612, 792), page1.PixelSize);
        Assert.True(File.Exists(Path.Combine(dir, "out-2.png")));
        Assert.Equal(72, w.Settings.ExportDpi); // remembered for next time

        // Just the second page, at 144 dpi: one file, named as asked, twice the size.
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([1], 144));
        var single = Path.Combine(dir, "second.png");
        w.PickSave = (_, _) => Task.FromResult<string?>(single);
        Wait(w.ExportPng());
        using (var page2 = new Bitmap(single)) Assert.Equal(new PixelSize(1224, 1584), page2.PixelSize);

        // A range of one page to PDF.
        w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([1], 150));
        w.PickSave = (_, _) => Task.FromResult<string?>(pdf);
        Wait(w.ExportPdf());
        Assert.Contains("/Count 1", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)));

        // Cancelling the question exports nothing.
        w.AskExport = _ => Task.FromResult<ExportChoice?>(null);
        w.PickSave = (_, _) => throw new Xunit.Sdk.XunitException("A cancelled export shouldn't ask where to save.");
        Wait(w.ExportPdf());
        Done(w);
    }

    [AvaloniaFact]
    public void Copy_and_paste_make_copies_with_their_own_text()
    {
        var w = Open();
        var frame = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 200, H = 100 });
        w.Editor.ReplaceText(frame.StoryId, 0, 0, "copied");
        w.View.Select([frame.Id]);
        Wait(w.Copy());
        Wait(w.Paste());
        Wait(w.Paste());
        var frames = w.Editor.Doc.Pages[0].Objects.OfType<TextFrame>().ToList();
        Assert.Equal(3, frames.Count);
        Assert.Equal(96, frames[2].X);
        Assert.Equal(3, frames.Select(f => f.StoryId).Distinct().Count());
        Assert.All(frames, f => Assert.Equal("copied", w.Editor.Doc.Stories[f.StoryId].Text));
        Done(w);
    }

    [AvaloniaFact]
    public void The_window_draws_a_laid_out_page()
    {
        var w = Open(width: 1440, height: 920);
        Sample.Build(w);
        w.View.FitPage();
        Pump();
        Shot(w, "layout");
        var layout = w.View.LayoutOf(((TextFrame)w.Editor.Doc.Find(w.View.SelectedIds[0])!).StoryId)!;
        Assert.False(layout.Overset);
        Assert.Equal(2, layout.Lines.Select(l => layout.Areas[l.Area].FrameId).Distinct().Count()); // it flows on
        if (Shots is { } dir)
        {
            w.PickSave = (_, _) => Task.FromResult<string?>(Path.Combine(dir, "AVAPageStream-sample.pdf"));
            w.AskExport = _ => Task.FromResult<ExportChoice?>(new ExportChoice([0], 150));
            Wait(w.ExportPdf());
        }
        Done(w);
    }
}
