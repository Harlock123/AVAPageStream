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

    /// <summary>A PDF of two pages: a red square on the first, a blue one on the second.</summary>
    private static byte[] TwoPagePdf()
    {
        var m = new Core.Pdf.PdfMaker();
        m.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        m.Add(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 /MediaBox [0 0 100 50] >>");
        m.Add(3, "<< /Type /Page /Parent 2 0 R /Resources << >> /Contents 4 0 R >>");
        m.Add(4, "<< >>", "1 0 0 rg 0 0 100 50 re f");
        m.Add(5, "<< /Type /Page /Parent 2 0 R /Resources << >> /Contents 6 0 R >>");
        m.Add(6, "<< >>", "0 0 1 rg 0 0 100 50 re f");
        return m.Build();
    }

    [AvaloniaFact]
    public void A_pdf_page_is_placed_as_a_picture_and_its_page_chosen()
    {
        var dir = Folder();
        var file = Path.Combine(dir, "two.pdf");
        File.WriteAllBytes(file, TwoPagePdf());
        var w = Open();
        var e = w.Editor;
        e.Change("Page", d => d.Setup = d.Setup with { PageWidth = 200, PageHeight = 100 });
        var pic = e.Add(SheetRef.PageAt(0), new PictureObject { X = 50, Y = 25, W = 100, H = 50, Fit = Fit.Actual });
        w.PickOpen = _ => Task.FromResult<string?>(file);
        Wait(w.View.ReplaceImage(pic.Id));
        Assert.Contains("Placed page 1 of 2 of a PDF, kept as vector.", w.Message.Text);
        Assert.Equal("pdf", ImageStore.Extension(e.Doc.Images.Get(((PictureObject)e.Doc.Find(pic.Id)!).Image)!));
        using (var first = DrawPage(e.Doc)) Assert.Equal((255, 0, 0), Pixel(first, 100, 50));
        w.View.Select([pic.Id]);
        w.Panel.SelectedIndex = 0;
        w.Refresh();
        Pump();
        Assert.Equal(2m, w.Panel.PdfPageBox.Maximum);
        w.Panel.PdfPageBox.Value = 2;
        Pump();
        Assert.Equal(2, ((PictureObject)e.Doc.Find(pic.Id)!).Page);
        using (var second = DrawPage(e.Doc)) Assert.Equal((0, 0, 255), Pixel(second, 100, 50));
        // Kept in the file, page and all.
        using var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        var back = (PictureObject)DocumentFile.Read(ms).Find(pic.Id)!;
        Assert.Equal((2, "pdf"), (back.Page, ImageStore.Extension(e.Doc.Images.Get(back.Image)!)));
        Done(w);
    }

    [AvaloniaFact]
    public void A_page_exported_as_pdf_places_back_as_vector_text_and_all()
    {
        // A page of AVAPageStream's own (SkiaSharp's PDF: TrueType fonts by glyph number), placed in another document.
        var doc = Document.New(new Setup(300, 120));
        var f = new TextFrame { Id = "f", X = 20, Y = 20, W = 260, H = 80 };
        doc.Pages[0].Objects.Add(f);
        Chains.NewStory(doc, f, doc.Style("Body"));
        doc.Stories[f.StoryId].Insert(0, "Harbour Lights", new CharFormat(Fonts.Sans, 40, Bold: true));
        var pdf = Path.Combine(Folder(), "page.pdf");
        Wait(Export.Pdf(doc, pdf));
        var bytes = File.ReadAllBytes(pdf);
        var drawing = AVAPageStream.Core.PdfPages.Draw(bytes, 1, out var why, out var notes)!;
        Assert.True(drawing is not null, why);
        Assert.Empty(notes);
        Assert.Equal(new Box(0, 0, 300, 120), drawing.BoundingBox);
        var text = drawing.Ops.OfType<AVAPageStream.Core.PostScript.PsFill>().Where(o => o.Colour == AVAPageStream.Core.PostScript.PsColour.Black).ToList();
        Assert.NotEmpty(text); // the words, as outlines

        var w = Open();
        var e = w.Editor;
        e.Change("Page", d => d.Setup = d.Setup with { PageWidth = 300, PageHeight = 120 });
        var pic = e.Add(SheetRef.PageAt(0), new PictureObject { X = 0, Y = 0, W = 300, H = 120, Fit = Fit.Actual });
        w.PickOpen = _ => Task.FromResult<string?>(pdf);
        Wait(w.View.ReplaceImage(pic.Id));
        using var original = DrawPage(doc);
        using var placed = DrawPage(e.Doc);
        var (dark, both) = Overlap(original, placed, new Avalonia.PixelRect(20, 20, 260, 60));
        Assert.True(dark > 400 && both > dark * 0.95, $"{both} of {dark}");
        // Exported again, it is still drawing, not a picture of it.
        var again = Path.Combine(Folder(), "again.pdf");
        Wait(Export.Pdf(e.Doc, again));
        var exported = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(again));
        Assert.DoesNotContain("/Subtype /Image", exported);
        Done(w);
    }

    [AvaloniaFact]
    public void A_protected_pdf_asks_for_its_password_and_keeps_it()
    {
        var m = Core.Pdf.PdfMaker.Page("1 0 0 rg 0 0 100 50 re f", box: "[0 0 100 50]");
        var file = Path.Combine(Folder(), "locked.pdf");
        File.WriteAllBytes(file, m.Build(crypt: new Core.Pdf.PdfCrypt(4, user: "harbour")));
        var w = Open();
        var e = w.Editor;
        e.Change("Page", d => d.Setup = d.Setup with { PageWidth = 200, PageHeight = 100 });
        var pic = e.Add(SheetRef.PageAt(0), new PictureObject { X = 50, Y = 25, W = 100, H = 50, Fit = Fit.Actual });
        w.PickOpen = _ => Task.FromResult<string?>(file);
        var asked = new List<bool>();
        var answers = new Queue<string?>(["lighthouse", "harbour"]);
        w.View.AskPassword = (name, wrong) => { asked.Add(wrong); Assert.Equal("locked.pdf", name); return Task.FromResult(answers.Dequeue()); };
        Wait(w.View.ReplaceImage(pic.Id));
        Assert.Equal([false, true], asked); // asked again after the wrong one
        var placed = (PictureObject)e.Doc.Find(pic.Id)!;
        Assert.Equal("harbour", e.Doc.Passwords[placed.Image]);
        using (var page = DrawPage(e.Doc)) Assert.Equal((255, 0, 0), Pixel(page, 100, 50));
        // Saved and opened again, it draws with the password it kept.
        using var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        using (var again = DrawPage(DocumentFile.Read(ms))) Assert.Equal((255, 0, 0), Pixel(again, 100, 50));
        // Cancelled: nothing is placed.
        var other = e.Add(SheetRef.PageAt(0), new PictureObject { X = 0, Y = 0, W = 10, H = 10 });
        w.View.AskPassword = (_, _) => Task.FromResult<string?>(null);
        Wait(w.View.ReplaceImage(other.Id));
        Assert.Equal("", ((PictureObject)e.Doc.Find(other.Id)!).Image);
        Assert.Contains("needs its password", w.Message.Text);
        Done(w);
    }

    [AvaloniaFact]
    public void A_pdf_x4_has_its_output_intent_and_nothing_in_rgb()
    {
        var doc = Document.New(new Setup(300, 200));
        doc.Title = "Harbour notice";
        var f = new TextFrame { Id = "f", X = 20, Y = 20, W = 260, H = 40 };
        doc.Pages[0].Objects.Add(f);
        Chains.NewStory(doc, f, doc.Style("Body"));
        doc.Stories[f.StoryId].Insert(0, "Tides at https://example.com", new CharFormat(Fonts.Sans, 14, Colour: "@Red"));
        // A JPEG photograph, a gradient and a see-through box.
        using (var bmp = new SkiaSharp.SKBitmap(40, 30))
        {
            using (var c = new SkiaSharp.SKCanvas(bmp)) { c.Clear(new SkiaSharp.SKColor(30, 120, 200)); }
            using var data = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
            var key = doc.Images.Add(data.ToArray());
            doc.Pages[0].Objects.Add(new PictureObject { Id = "p", X = 20, Y = 80, W = 80, H = 60, Image = key, Fit = Fit.Fill });
        }
        doc.Pages[0].Objects.Add(new RectangleObject { Id = "g", X = 120, Y = 80, W = 80, H = 60, Stroke = null, Gradient = new Gradient(false, [new ColourStop(0, "@Blue"), new ColourStop(1, "@Yellow")]) });
        doc.Pages[0].Objects.Add(new RectangleObject { Id = "o", X = 150, Y = 100, W = 100, H = 60, Stroke = null, Fill = "@Green", Opacity = 0.5 });
        var path = Path.Combine(Folder(), "x4.pdf");
        Wait(Export.Pdf(doc, path, cmyk: true, pdfx: true));
        if (Environment.GetEnvironmentVariable("AVAPS_KEEP") is { } keep) File.Copy(path, keep, true);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("%PDF-1.6", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
        var r = new AVAPageStream.Core.PostScript.PdfReader(bytes);
        Assert.IsType<AVAPageStream.Core.PostScript.PsString>(((AVAPageStream.Core.PostScript.PsArray)r.Trailer.Get("ID")!)[0]);
        var catalog = (AVAPageStream.Core.PostScript.PsDict)r.Deep(r.Trailer.Get("Root"));
        var intent = (AVAPageStream.Core.PostScript.PsDict)((AVAPageStream.Core.PostScript.PsArray)catalog.Get("OutputIntents")!)[0];
        Assert.Equal("GTS_PDFX", ((AVAPageStream.Core.PostScript.PsName)intent.Get("S")!).Value);
        var profile = (AVAPageStream.Core.PostScript.PsDict)intent.Get("DestOutputProfile")!;
        Assert.Equal(4, profile.Get("N"));
        Assert.Equal(IccCmyk.Profile, r.Decode(profile));
        var xmp = System.Text.Encoding.UTF8.GetString(r.Decode((AVAPageStream.Core.PostScript.PsDict)catalog.Get("Metadata")!));
        Assert.Contains("<pdfxid:GTS_PDFXVersion>PDF/X-4</pdfxid:GTS_PDFXVersion>", xmp);
        Assert.Contains("Harbour notice", xmp);
        var info = (AVAPageStream.Core.PostScript.PsDict)r.Deep(r.Trailer.Get("Info"));
        Assert.Equal("PDF/X-4", ((AVAPageStream.Core.PostScript.PsString)info.Get("GTS_PDFXVersion")!).Text);
        Assert.Equal("False", ((AVAPageStream.Core.PostScript.PsName)info.Get("Trapped")!).Value);
        var page = r.Pages[0];
        Assert.NotNull(page.Get("TrimBox"));
        Assert.Null(page.Get("Annots"));                 // the web address's link is left out
        // Nothing in RGB: not the text's colour, the picture, the gradient, nor the group the transparency is blended in.
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var objects = System.Text.RegularExpressions.Regex.Matches(text, @"(\d+) 0 obj").Select(m => int.Parse(m.Groups[1].Value)).Distinct();
        foreach (var n in objects)
        {
            if (r.Fetch(new AVAPageStream.Core.PostScript.PdfRef(n, 0)) is not AVAPageStream.Core.PostScript.PsDict d) continue;
            var dict = Describe(d);
            // A stream compressed as zlib says so (CMYK conversion once lost a small form's filter).
            if (d.Get(AVAPageStream.Core.PostScript.PdfReader.StreamKey) is AVAPageStream.Core.PostScript.PdfStreamBytes raw && raw.Span is [0x78, 0x9C or 0xDA or 0x01 or 0x5E, ..])
                Assert.Contains("FlateDecode", dict);
            Assert.DoesNotContain("DeviceRGB", dict);
            Assert.DoesNotContain("DCTDecode", dict);
            if (d.Get(AVAPageStream.Core.PostScript.PdfReader.StreamKey) is not null && d.Get("Subtype") is null && d.Get("Type") is null && d.Get("FunctionType") is null && d.Get("ShadingType") is null && d.Get("N") is null)
                Assert.DoesNotMatch(@"\d\s+rg\b|\d\s+RG\b", System.Text.Encoding.Latin1.GetString(r.Decode(d))); // a content stream
        }
        // And it still draws.
        var drawn = AVAPageStream.Core.PdfPages.Draw(bytes, 1, out var why, out _);
        Assert.True(drawn is not null, why);
        Assert.True(drawn!.Ops.Count > 3);
    }

    private static string Describe(AVAPageStream.Core.PostScript.PsDict d, int depth = 0) => depth > 4 ? "" :
        string.Join(" ", d.Entries.Select(e => $"{e.Key} {e.Value switch { AVAPageStream.Core.PostScript.PsDict inner => "<<" + Describe(inner, depth + 1) + ">>", AVAPageStream.Core.PostScript.PsArray a => "[" + string.Join(" ", a.All().Select(x => x is AVAPageStream.Core.PostScript.PsDict xd ? "<<" + Describe(xd, depth + 1) + ">>" : x.ToString())) + "]", var v => v?.ToString() }}"));

    [AvaloniaFact]
    public void An_object_pasted_into_text_moves_with_it()
    {
        var w = Open();
        var e = w.Editor;
        e.Change("Page", d => d.Setup = d.Setup with { PageWidth = 400, PageHeight = 200 });
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { X = 20, Y = 20, W = 360, H = 160 });
        e.ReplaceText(frame.StoryId, 0, 0, "Harbour lights", new CharFormat(Fonts.Sans, 14));
        var red = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 300, Y = 150, W = 60, H = 40, Fill = "@Red", Stroke = null });
        w.View.Select([red.Id]);
        Wait(w.Copy());
        e.Delete([red.Id]);
        w.View.StartEditing((TextFrame)e.Doc.Find(frame.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = 8; // after "Harbour "
        Wait(w.Paste());
        Assert.Contains("Anchored in the text", w.Message.Text);
        var story = e.Doc.Stories[frame.StoryId];
        var box = Assert.Single(story.Anchored);
        Assert.Equal(Fields.Anchor, story.Text[8]);
        Assert.Empty(e.Doc.Pages[0].Objects.OfType<RectangleObject>());
        var before = (box.X, box.Y);
        var measure = w.View.Layouts.Measurer;
        var harbour = TextFlow.Measure(measure, "Harbour ", story.FormatAt(0));
        using (var page = DrawPage(e.Doc)) Assert.Equal((0xE0, 0x20, 0x20), Pixel(page, (int)(20 + harbour + 30), (int)(box.Y + 20))); // the Red swatch
        // Words typed before it move it on.
        e.ReplaceText(frame.StoryId, 0, 0, "Big ");
        _ = w.View.Layouts.Of(e.Doc, frame.StoryId);
        Assert.Equal(before.X + TextFlow.Measure(measure, "Big ", story.FormatAt(0)), box.X, 3);
        // Clicked, it is selected; dragged, it goes back to its line.
        w.View.StopEditing();
        w.Refresh();
        Pump();
        Click(w, new Pt(box.X + 30, box.Y + 20));
        Assert.Equal(box.Id, Assert.Single(w.View.Selected).Id);
        var at = (box.X, box.Y);
        // (Pressed away from where it was clicked, so the press isn't a double-click, which edits the text.)
        Drag(w, new Pt(box.X + 45, box.Y + 32), new Pt(box.X + 45, box.Y + 102));
        Assert.Null(w.View.Editing);
        _ = w.View.Layouts.Of(e.Doc, frame.StoryId);
        Assert.Equal(at, (box.X, box.Y));
        Assert.Contains("moves with its text", w.Message.Text);
        // Placed by hand (Object tab), then dragged: it keeps where it was put, as offsets against its line.
        w.View.Select([box.Id]);
        w.Panel.SelectedIndex = 0;
        w.Refresh();
        Pump();
        Assert.True(w.Panel.AnchorKindChoice.IsVisible);
        w.Panel.AnchorKindChoice.SelectedIndex = 1;
        Pump();
        var placed = (PageObject)e.Doc.Find(box.Id)!;
        Assert.Equal(AnchorKind.Custom, placed.Anchoring!.Kind);
        _ = w.View.Layouts.Of(e.Doc, frame.StoryId);
        var from = (placed.X, placed.Y);
        // Pressed away from where the last drag was, so the press isn't taken as a double-click (which edits the text).
        Drag(w, new Pt(from.X + 10, from.Y + 34), new Pt(from.X + 10, from.Y + 94));
        placed = (PageObject)e.Doc.Find(box.Id)!;
        _ = w.View.Layouts.Of(e.Doc, frame.StoryId);
        Assert.Null(w.View.Editing);
        Assert.Equal(from.Y + 60, placed.Y, 0);
        Assert.Equal(60, placed.Anchoring!.Y, 0);
        Done(w);
    }

    [AvaloniaFact]
    public void An_object_placed_by_hand_in_the_text_wraps_it()
    {
        var w = Open();
        var e = w.Editor;
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 400 });
        e.ReplaceText(frame.StoryId, 0, 0, string.Join(" ", Enumerable.Repeat("harbour", 80)));
        var icon = new RectangleObject { W = 80, H = 60, Fill = "@Blue", Stroke = null, Wrap = true, WrapGap = 6 };
        var id = e.AnchorAt(frame.StoryId, 0, [icon]).Single();
        e.Reanchor(id, new Anchoring(AnchorKind.Custom, From: AnchorFrom.ColumnLeft));
        var layout = w.View.Layouts.Of(e.Doc, frame.StoryId)!;
        var placed = e.Doc.Find(id)!;
        Assert.Equal((72.0, 72.0), (Math.Round(placed.X, 3), Math.Round(placed.Y - (layout.Lines[0].Baseline - layout.Lines[0].Ascent) + 72, 3)));
        // The lines beside it start past it and its standoff; those below it at the column's edge.
        Assert.All(layout.Lines.Where(l => l.Baseline - l.Ascent < placed.Y + placed.H), l => Assert.True(l.Pieces[0].X >= 72 + 80 + 6 - 0.01, $"{l.Pieces[0].X}"));
        Assert.Contains(layout.Lines, l => l.Baseline - l.Ascent > placed.Y + placed.H + 6 && Math.Abs(l.Pieces[0].X - 72) < 0.01);
        Done(w);
    }

    [AvaloniaFact]
    public void A_cross_reference_keeps_up_with_its_heading_and_links_to_it()
    {
        var w = Open();
        var e = w.Editor;
        e.AddPage(1);
        var a = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        var b = e.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        e.ReplaceText(a.StoryId, 0, 0, "The tides are on page .");
        e.ReplaceText(b.StoryId, 0, 0, "Tide tables\nHigh water is at noon.");
        e.ApplyStyle(b.StoryId, 0, 0, "Heading");
        w.View.StartEditing((TextFrame)e.Doc.Find(a.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = 22; // before the full stop
        IReadOnlyList<string>? offered = null;
        w.AskReference = targets => { offered = targets; return Task.FromResult<ReferenceChoice?>(new ReferenceChoice(targets.ToList().IndexOf("Tide tables"), RefKind.Page)); };
        Wait(w.InsertCrossReference());
        Assert.Contains("Tide tables", offered!);
        string Shown() => w.View.Layouts.Of(e.Doc, a.StoryId)!.Display(0, e.Doc.Stories[a.StoryId].Length);
        Assert.Equal("The tides are on page 2.", Shown());
        // A page before the heading's: the reference follows it.
        e.AddPage(1);
        Assert.Equal("The tides are on page 3.", Shown());
        // Named by hand, a place can be referred to by its text too.
        w.View.StartEditing((TextFrame)e.Doc.Find(b.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = e.Doc.Stories[b.StoryId].Text.IndexOf("High", StringComparison.Ordinal);
        w.AskText = (_, _, _) => Task.FromResult<string?>("Noon");
        Wait(w.NamePlace());
        w.View.StartEditing((TextFrame)e.Doc.Find(a.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = e.Doc.Stories[a.StoryId].Length;
        w.AskReference = targets => Task.FromResult<ReferenceChoice?>(new ReferenceChoice(targets.ToList().IndexOf("Place: Noon"), RefKind.Text));
        Wait(w.InsertCrossReference());
        Assert.Equal("The tides are on page 3.High water is at noon.", Shown());
        // In a PDF, it links to its place.
        var pdf = Path.Combine(Folder(), "xref.pdf");
        Wait(Export.Pdf(e.Doc, pdf));
        if (Environment.GetEnvironmentVariable("AVAPS_KEEP") is { } keep) File.Copy(pdf, keep, true);
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Assert.Contains("/Dest /p:Tide#20tables", text); // the link, to the place by name (its space written as #20)
        Assert.Contains("/Dests", text);
        Assert.Contains("/Link", text);
        Done(w);
    }

    [AvaloniaFact]
    public void Index_entries_marked_from_the_selection_make_an_index()
    {
        var w = Open();
        var e = w.Editor;
        e.AddPage(1);
        var a = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        var b = e.Add(SheetRef.PageAt(1), new TextFrame { X = 72, Y = 72, W = 300, H = 300 });
        e.ReplaceText(a.StoryId, 0, 0, "Tides follow the moon.");
        e.ReplaceText(b.StoryId, 0, 0, "Spring tides are the highest.");
        // Nothing marked yet: it says how.
        w.View.StartEditing((TextFrame)e.Doc.Find(b.Id)!);
        w.View.Editing!.Caret = w.View.Editing.Anchor = e.Doc.Stories[b.StoryId].Length;
        w.MakeIndex();
        Assert.Contains("Mark index entry", w.Message.Text);
        // The selected word starts the topic.
        w.View.StartEditing((TextFrame)e.Doc.Find(a.Id)!);
        (w.View.Editing!.Anchor, w.View.Editing.Caret) = (0, 5);
        string? offered = null;
        w.AskIndex = topic => { offered = topic; return Task.FromResult<IndexMark?>(new IndexMark(topic)); };
        Wait(w.MarkIndexEntry());
        Assert.Equal("Tides", offered);
        Assert.Equal((1, 6), (w.View.Editing.Anchor, w.View.Editing.Caret)); // the selection still the word
        Assert.Equal("Tides follow the moon.", w.View.Layouts.Of(e.Doc, a.StoryId)!.Display(0, e.Doc.Stories[a.StoryId].Length));
        w.View.StartEditing((TextFrame)e.Doc.Find(b.Id)!);
        (w.View.Editing!.Anchor, w.View.Editing.Caret) = (7, 12);
        w.AskIndex = _ => Task.FromResult<IndexMark?>(new IndexMark("Tides", "spring"));
        Press(w, Key.I, RawInputModifiers.Control | RawInputModifiers.Alt); // the key, not italic
        Pump();
        Assert.False(e.Doc.Stories[b.StoryId].FormatAt(8).Italic);
        Assert.Equal(2, e.Doc.Stories.Values.Sum(s => s.Text.Count(c => c == Fields.IndexEntry)));
        // The index, made at the end of the second page's text.
        w.View.Editing!.Caret = w.View.Editing.Anchor = e.Doc.Stories[b.StoryId].Length;
        w.MakeIndex();
        Assert.Contains("index of 1 topic.", w.Message.Text);
        var text = Fields.Expand(e.Doc.Stories[b.StoryId].Text, null);
        Assert.EndsWith("T\nTides, 1\nspring, 2", text);
        Done(w);
    }
}
