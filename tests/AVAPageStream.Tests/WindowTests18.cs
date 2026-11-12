using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 7, part 2: paragraph rules, object styles, text to outlines and placed PDF pages.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void Paragraph_rules_are_drawn_above_and_below()
    {
        var w = Open();
        var e = w.Editor;
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 50, Y = 50, W = 300, H = 200 });
        e.ReplaceText(f.StoryId, 0, 0, "Heading\nBody text.");
        e.FormatParas(f.StoryId, 0, 0, p => p with
        {
            RuleAbove = new ParaRule(4, "@Red", RuleLength.Column, Offset: 4),
            RuleBelow = new ParaRule(2, "@Blue", RuleLength.Width, Width: 100, Offset: 2),
            SpaceBefore = 12, SpaceAfter = 10,
        });
        var layout = w.View.Layouts.Of(e.Doc, f.StoryId, 0)!;
        var first = layout.Lines[0];
        using var bitmap = DrawPage(e.Doc);
        var above = (int)Math.Round(first.Baseline - first.Ascent - 4 - 2);
        var below = (int)Math.Round(first.Baseline + first.Descent + 2 + 1);
        var red = Pixel(bitmap, 340, above);                     // the column-long rule reaches near the frame's right
        Assert.True(red.R > 180 && red.G < 80, $"{red}");
        var blue = Pixel(bitmap, 140, below);                    // the 100 pt rule
        Assert.True(blue.B > 150 && blue.B - blue.R > 100, $"{blue}");
        Assert.Equal((255, 255, 255), Pixel(bitmap, 160, below)); // and no further
        // Renaming the swatch keeps the rule's colour, by its new name.
        Assert.Null(e.RenameSwatch("Red", "Signal"));
        Assert.Equal("@Signal", e.Doc.Stories[f.StoryId].Paras[0].RuleAbove!.Colour);
        Assert.Contains("@Signal", Colours.Used(e.Doc));
        Done(w);
    }

    [AvaloniaFact]
    public void The_text_tab_sets_paragraph_rules_and_styles_keep_them()
    {
        var w = Open();
        var f = w.Editor.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 200 });
        w.Editor.ReplaceText(f.StoryId, 0, 0, "Section\nText.");
        w.View.StartEditing((TextFrame)w.Editor.Doc.Find(f.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = 2;
        w.Panel.SelectedIndex = 1;
        w.Refresh();
        Pump();
        void Commit(Avalonia.Controls.TextBox box, string text) { box.Focus(); box.Text = text; Press(w, Key.Enter); }
        var rc = w.Panel.RuleBelowControls;
        Assert.False(rc.Weight.IsEnabled);
        rc.Choice.SelectedIndex = 2;            // as long as the text
        Pump();
        Commit(rc.Weight, "3pt");
        Commit(rc.Colour, "Green");
        Commit(rc.Offset, "5pt");
        var para = w.Editor.Doc.Stories[f.StoryId].Paras[0];
        Assert.Equal(new ParaRule(3, "@Green", RuleLength.Text, 72, 5), para.RuleBelow);
        Assert.Null(para.RuleAbove);
        Assert.Null(w.Editor.Doc.Stories[f.StoryId].Paras[1].RuleBelow); // only the caret's paragraph
        Assert.True(rc.Weight.IsEnabled);
        Assert.False(rc.Width.IsEnabled);
        // A style made from it has the rule, and gives it to the paragraphs it is applied to.
        w.Panel.StyleNameBox.Text = "Ruled";
        w.Panel.NewStyle();
        Assert.Equal(para.RuleBelow, w.Editor.Doc.Style("Ruled").Para.RuleBelow);
        w.Editor.ApplyStyle(f.StoryId, 9, 0, "Ruled");
        Assert.Equal(para.RuleBelow, w.Editor.Doc.Stories[f.StoryId].Paras[1].RuleBelow);
        // The document keeps it.
        var copy = Document.FromJson(w.Editor.Doc.ToJson(), w.Editor.Doc.Images);
        Assert.Equal(para.RuleBelow, copy.Stories[f.StoryId].Paras[0].RuleBelow);
        rc.Choice.SelectedIndex = 0;
        Pump();
        Assert.Null(w.Editor.Doc.Stories[f.StoryId].Paras[0].RuleBelow);
        Done(w);
    }

    [AvaloniaFact]
    public void The_object_tab_makes_applies_and_redefines_object_styles()
    {
        var w = Open();
        var a = w.Editor.Add(SheetRef.PageAt(0), new RectangleObject { X = 72, Y = 72, W = 100, H = 60, Fill = "@Red" });
        var b = w.Editor.Add(SheetRef.PageAt(0), new EllipseObject { X = 272, Y = 72, W = 100, H = 60, Fill = "@Green" });
        w.Editor.Change("Shadow", d => d.Find(a.Id)!.Shadow = new Shadow());
        w.View.Select([a.Id]);
        w.Panel.SelectedIndex = 0;
        w.Refresh();
        Pump();
        w.Panel.ObjectStyleNameBox.Text = "Alert";
        w.Panel.NewObjectStyle();
        Assert.Equal("Alert", w.Editor.Doc.Find(a.Id)!.ObjectStyle);
        Assert.Equal("Alert", w.Panel.ObjectStyleChoice.SelectedItem);
        Assert.Contains("fill Red", w.Panel.ObjectStyleInfo.Text);
        w.View.Select([b.Id]);
        w.Refresh();
        Pump();
        Assert.Equal("(none)", w.Panel.ObjectStyleChoice.SelectedItem);
        w.Panel.ObjectStyleChoice.SelectedItem = "Alert";
        Pump();
        Assert.Equal(("@Red", true), (w.Editor.Doc.Find(b.Id)!.Fill, w.Editor.Doc.Find(b.Id)!.Shadow is not null));
        // Changed, then redefined from: the other object follows.
        w.Editor.Change("Fill", d => d.Find(b.Id)!.Fill = "@Blue");
        w.Refresh();
        Pump();
        Assert.Contains("Changed since", w.Panel.ObjectStyleInfo.Text);
        w.Panel.RedefineObjectStyle();
        Assert.Equal("@Blue", w.Editor.Doc.Find(a.Id)!.Fill);
        Assert.DoesNotContain("Changed since", w.Panel.ObjectStyleInfo.Text);
        w.Panel.ObjectStyleChoice.SelectedIndex = 0;            // out of the style, keeping its look
        Pump();
        Assert.Equal((null, "@Blue"), (w.Editor.Doc.Find(b.Id)!.ObjectStyle, w.Editor.Doc.Find(b.Id)!.Fill));
        Done(w);
    }

    private static byte[] Bytes(Avalonia.Media.Imaging.RenderTargetBitmap b)
    {
        var px = new byte[b.PixelSize.Width * b.PixelSize.Height * 4];
        unsafe { fixed (byte* p = px) b.CopyPixels(new Avalonia.PixelRect(b.PixelSize), (nint)p, px.Length, b.PixelSize.Width * 4); }
        return px;
    }

    /// <summary>How many pixels are dark in a bitmap, and how many of them are darkish in the other too.</summary>
    private static (int Dark, int Both) Overlap(Avalonia.Media.Imaging.RenderTargetBitmap a, Avalonia.Media.Imaging.RenderTargetBitmap b, Avalonia.PixelRect r)
    {
        var (pa, pb, w) = (Bytes(a), Bytes(b), a.PixelSize.Width);
        int Sum(byte[] px, int x, int y) { var o = (y * w + x) * 4; return px[o] + px[o + 1] + px[o + 2]; }
        var (dark, both) = (0, 0);
        for (var y = r.Y; y < r.Bottom; y++)
            for (var x = r.X; x < r.Right; x++)
            {
                if (Sum(pa, x, y) > 300) continue;
                dark++;
                if (Sum(pb, x, y) < 450) both++;
            }
        return (dark, both);
    }

    [AvaloniaFact]
    public void Text_turned_to_outlines_draws_where_the_text_did()
    {
        var w = Open();
        var e = w.Editor;
        e.Change("Page", d => d.Setup = d.Setup with { PageWidth = 400, PageHeight = 200 });
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 20, Y = 20, W = 360, H = 160 });
        e.ReplaceText(f.StoryId, 0, 0, "Bog & Quay", new CharFormat(Fonts.Sans, 48, Bold: true));
        e.ReplaceText(f.StoryId, e.Doc.Stories[f.StoryId].Length, 0, "\nunderlined serif", new CharFormat(Fonts.Serif, 28, Italic: true, Underline: true, Colour: "@Red"));
        using var before = DrawPage(e.Doc);
        w.View.Select([f.Id]);
        w.TextToOutlines();
        Assert.Null(e.Doc.Find(f.Id));                                     // the frame has gone, and its story
        Assert.False(e.Doc.Stories.ContainsKey(f.StoryId));
        var group = Assert.IsType<GroupObject>(e.Doc.Pages[0].Objects.Single());
        Assert.Equal("Outlines", group.Name);
        var paths = group.Children.Cast<PathObject>().ToList();
        Assert.Equal(["#000000", "@Red"], paths.Select(p => p.Fill).Order());
        Assert.All(paths, p => Assert.True(p.NonZero && p.Closed && p.Stroke is null));
        Assert.True(paths[0].Parts().Count >= 10, $"{paths[0].Parts().Count} parts");      // a part for each contour: B's two holes, o's, &'s…
        using var after = DrawPage(e.Doc);
        var area = new Avalonia.PixelRect(20, 20, 360, 120);
        var (dark, both) = Overlap(before, after, area);
        Assert.True(dark > 1500 && both > dark * 0.95, $"{both} of {dark}");
        var (dark2, both2) = Overlap(after, before, area);                  // and nothing where there wasn't text (edges are smoothed a little differently)
        Assert.True(both2 > dark2 * 0.95, $"{both2} of {dark2}");
        e.Undo();
        Assert.NotNull(e.Doc.Find(f.Id));
        Assert.Single(e.Doc.Pages[0].Objects);
        Done(w);
    }

    [AvaloniaFact]
    public void Selected_text_is_outlined_where_it_is_and_left_clear()
    {
        var w = Open();
        var e = w.Editor;
        var f = e.Add(SheetRef.PageAt(0), new TextFrame { X = 50, Y = 50, W = 400, H = 100, Fill = "@Yellow" });
        e.ReplaceText(f.StoryId, 0, 0, "Big Harbour Lights", new CharFormat(Fonts.Sans, 30));
        var lineBefore = w.View.Layouts.Of(e.Doc, f.StoryId)!.Lines[0];
        w.View.StartEditing((TextFrame)e.Doc.Find(f.Id)!);
        (w.View.Editing!.Anchor, w.View.Editing.Caret) = (4, 11);          // "Harbour"
        w.TextToOutlines();
        var story = e.Doc.Stories[f.StoryId];
        Assert.Equal("Big Harbour Lights", story.Text);                    // the text stays,
        Assert.Equal(Editor.Clear, story.FormatAt(6).Colour);              // clear where it was outlined,
        Assert.NotEqual(Editor.Clear, story.FormatAt(2).Colour);
        Assert.Equal(lineBefore.Pieces.Last().X, w.View.Layouts.Of(e.Doc, f.StoryId)!.Lines[0].Pieces.Last().X); // and nothing moves
        var path = Assert.IsType<PathObject>(e.Doc.Pages[0].Objects[1]);
        var measure = w.View.Layouts.Measurer;
        var harbourX = 50 + TextFlow.Measure(measure, "Big ", story.FormatAt(0));
        Assert.InRange(path.X, harbourX - 1, harbourX + 4);               // the H's stem, just inside where the word starts
        Assert.InRange(path.X + path.W, harbourX + TextFlow.Measure(measure, "Harbour", story.FormatAt(0)) - 4, harbourX + TextFlow.Measure(measure, "Harbour", story.FormatAt(0)) + 1);
        e.Undo();                                                          // one step puts it all back
        Assert.Single(e.Doc.Pages[0].Objects);
        Assert.NotEqual(Editor.Clear, e.Doc.Stories[f.StoryId].FormatAt(6).Colour);
        Done(w);
    }

    [AvaloniaFact]
    public void Linked_frames_are_not_outlined_and_a_filled_frame_stays()
    {
        var w = Open();
        var e = w.Editor;
        var a = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 120, H = 40 });
        var b = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 300, W = 120, H = 40 });
        e.ReplaceText(a.StoryId, 0, 0, "A long story that runs on into the next frame and further still.");
        Assert.Null(e.TryChange("Link", d => Chains.Link(d, (TextFrame)d.Find(a.Id)!, (TextFrame)d.Find(b.Id)!)));
        w.View.Select([a.Id]);
        w.TextToOutlines();
        Assert.Contains("unlink it first", w.Message.Text);
        Assert.NotNull(e.Doc.Find(a.Id));
        var c = e.Add(SheetRef.PageAt(0), new TextFrame { X = 300, Y = 72, W = 200, H = 60, Fill = "@Cyan", Shadow = new Shadow() });
        e.ReplaceText(c.StoryId, 0, 0, "Kept box");
        w.View.Select([c.Id]);
        w.TextToOutlines();
        var kept = (TextFrame)e.Doc.Find(c.Id)!;
        Assert.Equal(("", (Shadow?)null), (e.Doc.Stories[kept.StoryId].Text, kept.Shadow)); // its text gone, its shadow gone to the outlines
        Assert.NotNull(e.Doc.Pages[0].Objects.OfType<PathObject>().Single().Shadow);
        Done(w);
    }

    [AvaloniaFact]
    public void The_gradient_tool_drags_a_gradient_across_an_object()
    {
        var w = Open();
        var e = w.Editor;
        var r = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 100, Y = 100, W = 200, H = 100, Fill = "@Red" });
        w.View.Tool = AVAPageStream.Tool.Gradient;
        Drag(w, new Pt(150, 110), new Pt(150, 190));                          // top to bottom, within the box
        var g = e.Doc.Find(r.Id)!.Gradient!;
        Assert.Equal(["@Red", "@White"], g.Stops.Select(s => s.Colour));       // from its fill to white
        var (from, to) = g.Ends(e.Doc.Find(r.Id)!.Bounds);
        Assert.Equal((150, 110, 150, 190), (Math.Round(from.X), Math.Round(from.Y), Math.Round(to.X), Math.Round(to.Y)));
        Assert.Equal(90, g.Angle, 1);
        // Its end dragged, with Shift: the line keeps to 45°.
        Drag(w, to, new Pt(232, 175), RawInputModifiers.Shift);
        var g2 = e.Doc.Find(r.Id)!.Gradient!;
        Assert.Equal(45, g2.Angle, 1);
        Assert.Equal(from.X, g2.Ends(e.Doc.Find(r.Id)!.Bounds).From.X, 1);   // the start stays put
        e.Undo();
        Assert.Equal(90, e.Doc.Find(r.Id)!.Gradient!.Angle, 1);
        e.Undo();
        Assert.Null(e.Doc.Find(r.Id)!.Gradient);
        // Not on an object: it says what to do.
        Drag(w, new Pt(400, 400), new Pt(450, 450));
        Assert.Contains("Drag across a shape", w.Message.Text);
        Done(w);
    }
}
