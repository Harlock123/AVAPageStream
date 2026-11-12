using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>
/// The pictures in USERGUIDE.md: each sets up what a part of the guide talks about and, with AVAPAGESTREAM_SHOTS set,
/// saves it as guide-NAME.png (copied to docs/images/guide/NAME.png). Each also checks what it shows is there.
/// </summary>
public partial class WindowTests
{
    private static void Guide(Window w, string name)
    {
        if (Shots is not { } dir) return;
        Directory.CreateDirectory(dir);
        Pump();
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"guide-{name}.png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>Shows a side panel tab, scrolled down so far.</summary>
    private static void ShowTab(MainWindow w, int tab, double scroll = 0)
    {
        w.Panel.SelectedIndex = tab;
        Pump();
        if (((TabItem)w.Panel.Items[tab]!).Content is ScrollViewer sv) sv.Offset = new Vector(0, scroll);
        Pump();
    }

    /// <summary>The guide's examples are British (harbours, colours), so they are checked as British English.</summary>
    private static MainWindow GuideWindow()
    {
        var w = Open(width: 1360, height: 860);
        w.Editor.Doc.Language = "en-GB";
        return w;
    }

    private const string Prose =
        "The harbour wall was finished in the spring, two years late and a little over budget, but nobody on the quay " +
        "seems to mind. On a still evening the new lamps throw a line of light across the water, and the fishing boats " +
        "come in under it the way they always have.";

    [AvaloniaFact]
    public void Guide_the_window()
    {
        var w = GuideWindow();
        Sample.Build(w);
        w.View.FitPage();
        Guide(w, "overview");
        Assert.NotEmpty(w.Editor.Doc.Pages[0].Objects);
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_setting_up_a_document()
    {
        var w = GuideWindow();
        w.Editor.SetFacing(true);
        w.AddPage(after: true);
        w.AddPage(after: true);
        w.ShowSheet(SheetRef.PageAt(1));
        w.View.FitPage();
        ShowTab(w, 3);
        Guide(w, "document");
        Assert.True(w.Panel.FacingBox.IsChecked);
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_master_pages()
    {
        var w = GuideWindow();
        w.AddPage(after: true);
        w.AddPage(after: true);
        var footer = w.Editor.Add(SheetRef.MasterAt(0), new TextFrame { X = 36, Y = 740, W = 540, H = 18 });
        w.Editor.ReplaceText(footer.StoryId, 0, 0, $"Parish News  ·  Page {Fields.PageNumber} of {Fields.PageCount}", new CharFormat(Fonts.Sans, 9));
        w.Editor.FormatParas(footer.StoryId, 0, 0, p => p with { Align = Align.Centre });
        w.Editor.Add(SheetRef.MasterAt(0), new LineObject { X = 36, Y = 734, W = 540, H = 0, StrokeWidth = 0.75 });
        w.ShowSheet(SheetRef.MasterAt(0));
        w.View.FitPage();
        ShowTab(w, 2);
        Guide(w, "masters");
        Assert.Equal("A-Master", w.PageInfo.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_drawing()
    {
        var w = GuideWindow();
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 72, Y = 90, W = 160, H = 100, Fill = "@Yellow", CornerRadius = 10 });
        w.Editor.Add(SheetRef.PageAt(0), new EllipseObject { X = 270, Y = 90, W = 120, H = 100, Fill = "@Cyan", Stroke = null });
        w.Editor.Add(SheetRef.PageAt(0), new LineObject { X = 420, Y = 90, W = 120, H = 100, StrokeWidth = 2, ArrowEnd = true });
        var turned = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 120, Y = 260, W = 180, H = 70, Fill = "@Light grey", Rotation = -12 });
        w.View.Select([turned.Id]);
        Pump();
        Guide(w, "drawing");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_text_frames_and_flowing()
    {
        var w = GuideWindow();
        var a = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 80, W = 220, H = 180 });
        var b = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 320, Y = 80, W = 220, H = 120 });
        w.Editor.Change("Link", d => Chains.Link(d, (TextFrame)d.Find(a.Id)!, (TextFrame)d.Find(b.Id)!));
        w.Editor.ReplaceText(a.StoryId, 0, 0, string.Join(" ", Enumerable.Repeat(Prose, 5)));
        w.View.Select([a.Id]);
        Pump();
        Guide(w, "flowing");
        Assert.True(w.View.LayoutOf(a.StoryId)!.Overset); // the second frame fills too, and the mark shows on it
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_formatting_text()
    {
        var w = GuideWindow();
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 460, H = 300 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, "Harvest supper\nThe harvest supper is on Saturday in the church hall, starting at seven. Bring a dish to share, and a chair if you can.\nTickets are free from the post office.");
        w.Editor.ApplyStyle(f.StoryId, 0, 0, "Heading");
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(f.Id)!);
        w.View.Editing!.Anchor = 15;
        w.View.Editing!.Caret = 37;
        w.View.ApplyChar(c => c with { Italic = true }, "Italic");
        ShowTab(w, 1);
        Guide(w, "text");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_type_features()
    {
        var w = GuideWindow();
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 240, H = 300 });
        var text = "Once the harbour wall was finished, the town turned to the old net loft at the end of the quay, which is to become a small museum of the harbour and the people who built it.";
        w.Editor.ReplaceText(f.StoryId, 0, 0, text);
        w.Editor.FormatParas(f.StoryId, 0, 0, p => p with { DropLines = 3, Align = Align.Justify, Hyphenate = true });
        w.Editor.FormatChars(f.StoryId, 0, 1, c => c with { Colour = "@Red" });
        var toc = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 340, Y = 72, W = 200, H = 120 });
        w.Editor.ReplaceText(toc.StoryId, 0, 0, "The harbour wall\t3\nThe net loft\t7\nTide tables\t12");
        w.Editor.FormatParas(toc.StoryId, 0, 40, p => p with { Tabs = "200R." });
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(f.Id)!);
        w.View.Editing!.Anchor = w.View.Editing!.Caret = 20;
        w.Refresh();
        ShowTab(w, 1, 1000);
        Guide(w, "type");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_pictures_and_wrap()
    {
        var w = GuideWindow();
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 468, H = 300 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, string.Join(" ", Enumerable.Repeat(Prose, 3)));
        w.Editor.FormatParas(f.StoryId, 0, 0, p => p with { Align = Align.Justify });
        var circle = w.Editor.Add(SheetRef.PageAt(0), new EllipseObject { X = 230, Y = 120, W = 150, H = 150, Fill = "@Light grey", Stroke = null, Wrap = true, WrapShape = true, WrapGap = 8 });
        w.View.Select([circle.Id]);
        ShowTab(w, 0, 360);
        Guide(w, "wrap");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_paths()
    {
        var w = GuideWindow();
        var path = new PathObject
        {
            Nodes = [PathNode.Corner(new Pt(120, 300)), PathNode.Smooth(new Pt(220, 160), new Pt(300, 160)), PathNode.Smooth(new Pt(380, 260), new Pt(400, 320)), PathNode.Corner(new Pt(470, 180))],
            StrokeWidth = 3,
            Stroke = "@Blue",
        };
        path.Fit();
        var made = w.Editor.Add(SheetRef.PageAt(0), path);
        w.View.Select([made.Id]);
        var inside = At(w, 220, 160);
        w.MouseDown(inside, MouseButton.Left);
        w.MouseUp(inside, MouseButton.Left);
        w.MouseDown(inside, MouseButton.Left);
        w.MouseUp(inside, MouseButton.Left);
        Pump();
        Guide(w, "points");
        Assert.Equal(made.Id, w.View.EditingPoints);
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_swatches()
    {
        var w = GuideWindow();
        w.Editor.AddSwatch("Harbour orange", "0 60 80 0", spot: true);
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 72, Y = 72, W = 200, H = 120, Fill = "@Harbour orange", Stroke = null });
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 290, Y = 72, W = 200, H = 120, Fill = "@Harbour orange 40%", Stroke = null });
        ShowTab(w, 3, 520);
        w.Panel.SwatchList.SelectedItem = w.Editor.Doc.Swatches[^1];
        Pump();
        Guide(w, "swatches");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_grid_and_guides()
    {
        var w = GuideWindow();
        w.SetGrid(show: true, snap: true);
        w.Editor.Change("Guides", d => d.Pages[0].Guides.AddRange([new Guide(true, 306), new Guide(false, 200)]));
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 90, Y = 90, W = 216, H = 108, Fill = "@Light grey" });
        Guide(w, "grid");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_find_and_spelling()
    {
        var w = GuideWindow();
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 400, H = 200 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, "The harbour lamps are lit from dusk. The harbour master keeps the keys to the lamp room.");
        w.ShowFind();
        w.FindBox.Text = "harbour";
        w.FindNext();
        Guide(w, "find");
        w.HideFind();

        w.Editor.ReplaceText(f.StoryId, 0, 0, "Teh ");
        w.View.StopEditing();
        var sw = w.OpenSpelling();
        Pump();
        Guide(sw, "spelling");
        Assert.Equal("Teh", sw.Word.Text);
        sw.Close();
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_story_editor()
    {
        var w = GuideWindow();
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 200 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, "Harvest supper\nThe harvest supper is on Saturday in the church hall, starting at seven.\nBring a dish to share.");
        w.Editor.ApplyStyle(f.StoryId, 0, 0, "Heading");
        w.View.Select([f.Id]);
        var se = w.OpenStoryEditor()!;
        se.Width = 560;
        se.Height = 360;
        Pump();
        Guide(se, "story-editor");
        Assert.True(se.Text.Bounds.Width > se.Width * 0.9, "The text fills the window's width, under its bar.");
        se.Close();
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_tables()
    {
        var w = GuideWindow();
        var t = w.Editor.AddTable(SheetRef.PageAt(0), new Box(72, 72, 420, 125), 5, 3);
        string[] cells = ["Tide", "Morning", "Evening", "Monday", "06:12", "18:40", "Tuesday", "06:58", "19:21", "Wednesday", "07:41", "20:03", "Thursday", "08:22", "20:44"];
        for (var i = 0; i < cells.Length; i++) w.Editor.ReplaceText(t.Cells[i], 0, 0, cells[i]);
        w.View.Select([t.Id]);
        ShowTab(w, 0, 300);
        Guide(w, "tables");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_character_styles_and_keeps()
    {
        var w = GuideWindow();
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 460, H = 300 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, "The new harbour lamps\n" + Prose);
        w.Editor.ApplyStyle(f.StoryId, 0, 0, "Heading");
        w.Editor.FormatParas(f.StoryId, 0, 0, p => p with { KeepWithNext = true });
        var story = w.Editor.Doc.Stories[f.StoryId];
        w.Editor.ApplyCharStyle(f.StoryId, story.Text.IndexOf("new lamps", StringComparison.Ordinal), 9, "Emphasis");
        var at = story.Text.IndexOf("fishing boats", StringComparison.Ordinal);
        w.Editor.ApplyCharStyle(f.StoryId, at, 13, "Strong");
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(f.Id)!);
        w.View.Editing!.Anchor = at;
        w.View.Editing!.Caret = at + 13;
        w.Refresh();
        ShowTab(w, 1, 150);
        Guide(w, "charstyles");
        Assert.Equal("Strong", w.Panel.CharStyleChoice.SelectedItem);
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_layers_and_sections()
    {
        var w = GuideWindow();
        for (var i = 0; i < 5; i++) w.AddPage(after: true);
        w.Editor.SetSection(0, new PageNumbering(1, NumberStyle.LowerRoman));
        w.Editor.SetSection(2, new PageNumbering(1));
        w.ShowSheet(SheetRef.PageAt(2));
        var f = w.Editor.Add(SheetRef.PageAt(2), new TextFrame { X = 72, Y = 72, W = 468, H = 600 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, Prose + "\n" + Prose);
        w.Editor.AddLayer("Notes");
        var note = w.Editor.Add(SheetRef.PageAt(2), new TextFrame { X = 340, Y = 200, W = 180, H = 60, Fill = "#FFF2A8", Stroke = "#C8A800" });
        w.Editor.ReplaceText(note.StoryId, 0, 0, "Check the lamp count with the harbour master", new CharFormat(Fonts.Sans, 11));
        w.Editor.SetLayer("Notes", l => l with { Print = false });
        w.View.FitPage();
        ShowTab(w, 2, 330);
        Guide(w, "layers");
        Assert.Equal("Page 3 (1) of 6", w.PageInfo.Text);
        Done(w);
    }
}
