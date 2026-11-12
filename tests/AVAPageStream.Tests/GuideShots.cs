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

    [AvaloniaFact]
    public void Guide_text_on_a_path_and_footnotes()
    {
        var w = GuideWindow();
        var curve = new PathObject
        {
            Nodes = [new PathNode(110, 210, OutX: 180, OutY: 90), new PathNode(500, 210, InX: 430, InY: 90)],
            Stroke = null,
        };
        curve.Fit();
        var arc = w.Editor.Add(SheetRef.PageAt(0), curve);
        w.Editor.PutTextOnPath(arc.Id, "The Harbour Lamps Festival");
        var onPath = ((PathObject)w.Editor.Doc.Find(arc.Id)!).StoryId!;
        w.Editor.FormatChars(onPath, 0, w.Editor.Doc.Stories[onPath].Length, c => c with { Font = Fonts.Sans, Size = 28, Bold = true, Colour = "@Blue" });
        w.Editor.FormatParas(onPath, 0, 0, p => p with { Align = Align.Centre });
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 240, W = 468, H = 300, Columns = 2 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, Prose + "\n" + Prose);
        var at = w.Editor.Doc.Stories[f.StoryId].Text.IndexOf("budget", StringComparison.Ordinal) + 6;
        var note = w.Editor.InsertFootnote(f.StoryId, at, "The council's figures, published in March.")!;
        var at2 = w.Editor.Doc.Stories[f.StoryId].Text.LastIndexOf("lamps", StringComparison.Ordinal) + 5;
        w.Editor.InsertFootnote(f.StoryId, at2, "Forty of them, made in the town.");
        w.View.Select([arc.Id]);
        w.View.FitPage();
        ShowTab(w, 0);
        w.Refresh();
        Guide(w, "pathtext");
        Assert.Equal(2, w.View.Layouts.Of(w.Editor.Doc, f.StoryId)!.Notes.Count);
        Assert.NotNull(w.Editor.Doc.Stories[note]);
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_a_table_running_on()
    {
        var w = GuideWindow();
        var t = w.Editor.AddTable(SheetRef.PageAt(0), new Box(72, 560, 468, 300), 14, 3);
        string[] head = ["Date", "High water", "Low water"];
        for (var c = 0; c < 3; c++) w.Editor.ReplaceText(t.Cells[c], 0, 0, head[c]);
        for (var r = 1; r < 14; r++)
        {
            w.Editor.ReplaceText(t.Cells[r * 3], 0, 0, $"{r} June");
            w.Editor.ReplaceText(t.Cells[r * 3 + 1], 0, 0, $"{5 + r % 7:00}:{(r * 13) % 60:00}");
            w.Editor.ReplaceText(t.Cells[r * 3 + 2], 0, 0, $"{11 + r % 7:00}:{(r * 29) % 60:00}");
        }
        w.Editor.ChangeTable(t.Id, "Fit rows", (d, table) => Tables.FitRows(d, table, FontMeasurer.Shared));
        w.View.Select([t.Id]);
        w.RunOnTable();
        var next = (TableObject)w.Editor.Doc.Pages[1].Objects.Single();
        w.ShowSheet(SheetRef.PageAt(1));
        w.View.Select([next.Id]);
        w.View.FitPage();
        ShowTab(w, 0, 300);
        w.Refresh();
        Guide(w, "runon");
        Assert.Contains(Tables.Place(w.Editor.Doc, next)!.Rows, r => r.Frame == 1);
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_preflight()
    {
        var w = GuideWindow();
        w.Editor.Change("Setup", d => d.Setup = d.Setup with { Bleed = 9 });
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 120, W = 220, H = 60 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, Prose);
        w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 0, Y = 0, W = 612, H = 90, Fill = "@Blue", Stroke = null });
        w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 320, Y = 120, W = 220, H = 160 });
        // A small photograph, 120 by 90 pixels, printed 4 inches wide.
        byte[] photo;
        using (var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(120, 90), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque))
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            photo = ms.ToArray();
        }
        var key = w.Editor.Doc.Images.Add(photo);
        w.Editor.Add(SheetRef.PageAt(0), new PictureObject { X = 72, Y = 320, W = 288, H = 216, Image = key, Source = "/photos/quay.png" });
        var pre = w.OpenPreflight();
        pre.Width = 600;
        Pump();
        Guide(pre, "preflight");
        Assert.Equal(4, pre.Issues.Count);
        pre.Close();
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_effects()
    {
        var w = GuideWindow();
        var e = w.Editor;
        var card = e.Add(SheetRef.PageAt(0), new RectangleObject
        {
            X = 72, Y = 90, W = 300, H = 180, Stroke = null,
            Gradient = new Gradient(false, [new ColourStop(0, "@Blue"), new ColourStop(1, "@Cyan")], 30),
            Shadow = new Shadow(Colours.Black, 0.45, 8, 10, 12), Corners = Corners.All(18),
        });
        e.Add(SheetRef.PageAt(0), new EllipseObject
        {
            X = 300, Y = 60, W = 200, H = 200, Stroke = null,
            Gradient = new Gradient(true, [new ColourStop(0, "@Yellow"), new ColourStop(1, "@Red")]), Opacity = 0.85, Blend = BlendMode.Multiply,
        });
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 96, Y = 120, W = 240, H = 60, Stroke = null });
        e.ReplaceText(f.StoryId, 0, 0, "Harbour Lights", new CharFormat(Fonts.Sans, 30, Bold: true, Colour: "@White"));
        e.Change("Glow", d => d.Find(f.Id)!.Shadow = new Shadow("@Yellow", 0.9, 0, 0, 10, Glow: true));
        e.Add(SheetRef.PageAt(0), new RectangleObject { X = 96, Y = 320, W = 220, H = 80, Fill = "@Green", Stroke = null, FillOpacity = 0.7, Corners = new Corners(24, 0, 24, 0, CornerStyle.Cut) });
        w.View.Select([card.Id]);
        w.View.FitPage();
        ShowTab(w, 0, 180);
        w.Refresh();
        Guide(w, "effects");
        Assert.NotNull(w.Editor.Doc.Find(card.Id)!.Gradient);
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_rules()
    {
        var w = GuideWindow();
        var e = w.Editor;
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 320, H = 420 });
        e.ReplaceText(f.StoryId, 0, 0, "Tide Tables\nHigh water at the harbour mouth is about forty minutes before it reaches the town quay, so allow for it when you plan a crossing.\nWeather\nThe harbour master posts the shipping forecast on the board by the lifeboat station each morning at seven.");
        e.ApplyStyle(f.StoryId, 0, 0, "Heading");
        var rules = new Func<ParaFormat, ParaFormat>(p => p with
        {
            RuleAbove = new ParaRule(3, "@Blue", RuleLength.Column, Offset: 6),
            RuleBelow = new ParaRule(1, "@Blue", RuleLength.Text, Offset: 3),
            SpaceBefore = 14, SpaceAfter = 8,
        });
        e.FormatParas(f.StoryId, 0, 0, rules);
        var weather = e.Doc.Stories[f.StoryId].Text.IndexOf("Weather", StringComparison.Ordinal);
        e.ApplyStyle(f.StoryId, weather, 0, "Heading");
        e.FormatParas(f.StoryId, weather, 0, rules);
        w.View.StartEditing((TextFrame)e.Doc.Find(f.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = weather + 2;
        w.View.FitPage();
        ShowTab(w, 1, 1250);
        w.Refresh();
        Guide(w, "rules");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_object_styles()
    {
        var w = GuideWindow();
        var e = w.Editor;
        var style = new ObjectStyle("Notice", "@Yellow", "@Red", 2, Shadow: new Shadow(Colours.Black, 0.35, 5, 6, 8), Corners: Corners.All(14), Wrap: true);
        var ids = new List<string>();
        foreach (var (x, y) in new[] { (72.0, 90.0), (300.0, 90.0), (72.0, 260.0) })
            ids.Add(e.Add(SheetRef.PageAt(0), new RectangleObject { X = x, Y = y, W = 190, H = 130 }).Id);
        e.DefineObjectStyle(style, ids);
        var odd = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 300, Y = 260, W = 190, H = 130 });
        e.ApplyObjectStyle([odd.Id], "Notice");
        e.Change("Fill", d => d.Find(odd.Id)!.Fill = "@Cyan");
        w.View.Select([odd.Id]);
        w.View.FitPage();
        ShowTab(w, 0);
        w.Refresh();
        Guide(w, "objectstyles");
        Done(w);
    }

    [AvaloniaFact]
    public void Guide_outlines()
    {
        var w = GuideWindow();
        var e = w.Editor;
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 80, W = 460, H = 140 });
        e.ReplaceText(f.StoryId, 0, 0, "HARBOUR", new CharFormat(Fonts.Sans, 80, Bold: true));
        w.View.Select([f.Id]);
        w.TextToOutlines();
        var path = (PathObject)w.View.Selected.Single();
        e.Change("Gradient", d =>
        {
            var o = d.Find(path.Id)!;
            o.Gradient = new Gradient(false, [new ColourStop(0, "@Blue"), new ColourStop(1, "@Cyan")], 90);
            (o.Stroke, o.StrokeWidth) = ("@Blue", 1.5);
        });
        var below = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 230, W = 460, H = 60 });
        e.ReplaceText(below.StoryId, 0, 0, "Each letter is a path now: filled with a gradient, stroked, and its points free to move.", new CharFormat(Fonts.Serif, 14, Italic: true));
        w.View.Select([path.Id]);
        w.View.FitPage();
        ShowTab(w, 0, 200);
        Assert.Equal(13, path.Parts().Count); // H, U one each; A, R, O, R two; B three
        w.Refresh();
        Guide(w, "outlines");
        Done(w);
    }
}
