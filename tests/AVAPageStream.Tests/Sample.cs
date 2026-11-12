using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>A small newsletter laid out through the editor, for the screenshots: a masthead, a two-column story through
/// two linked frames, a picture with a caption, a pull quote and a footer from the master page.</summary>
internal static class Sample
{
    private const string Story =
        "The harbour wall was finished in the spring, two years late and a little over budget, but nobody on the quay " +
        "seems to mind. On a still evening the new lamps throw a line of light across the water, and the fishing boats " +
        "come in under it the way they always have.\n" +
        "The work was slow because the old stone had to be lifted, numbered and set back in the same order. Some of the " +
        "blocks carry masons' marks more than two hundred years old: a hooked cross, a crescent, a pair of initials " +
        "cut so deep they still catch the rain.\n" +
        "Below the lamps, a new set of steps leads down to the water at every state of the tide. The steps were the " +
        "harbour master's idea, and on the first Sunday after they opened there was a queue of swimmers along the wall " +
        "before seven in the morning.\n" +
        "There is more to come. The old net loft at the end of the quay is to become a small museum of the harbour, " +
        "with the masons' marks drawn out on its walls, and the committee is asking for photographs, letters and " +
        "anything else people have kept.\n" +
        "Anyone with something to lend can leave it at the harbour office, which is open every weekday morning, or " +
        "bring it to the meeting in the chapel hall on the first Thursday of next month.\n" +
        "The committee also wants to hear from anyone who worked on the wall before, whether as a mason, a carter or " +
        "a boy sent down with the tea. Their stories will go on cards beside the stones they remember, so that people " +
        "walking along the quay in fifty years can read who set them and when.\n" +
        "Meanwhile the lamps will stay lit until midnight through the winter, and the harbour master asks only that " +
        "nobody ties a boat to them.";

    public static void Build(MainWindow w)
    {
        var e = w.Editor;
        var page = SheetRef.PageAt(0);
        var serif = new CharFormat(Fonts.Serif, 11);

        // The master: a rule and a footer on every page.
        e.Add(SheetRef.MasterAt(0), new LineObject { X = 36, Y = 742, W = 540, H = 0, Stroke = "#1C2433", StrokeWidth = 0.75 });
        var footer = e.Add(SheetRef.MasterAt(0), new TextFrame { X = 36, Y = 748, W = 540, H = 18 });
        e.ReplaceText(footer.StoryId, 0, 0, $"Harbour Notes  ·  Autumn 2026  ·  Page {Fields.PageNumber} of {Fields.PageCount}",
            new CharFormat(Fonts.Sans, 8, Colour: "#5A6478"));

        // The masthead: a dark band with the title reversed out.
        e.Add(page, new RectangleObject { X = 36, Y = 36, W = 540, H = 92, Fill = "#1C2433", Stroke = null });
        var title = e.Add(page, new TextFrame { X = 50, Y = 44, W = 512, H = 80 });
        e.ReplaceText(title.StoryId, 0, 0, "Harbour Notes", new CharFormat(Fonts.Serif, 46, Bold: true, Colour: "#FFFFFF", Tracking: -10));
        e.ReplaceText(title.StoryId, 13, 0, "\nNews from the quay, the boats and the people who keep them", new CharFormat(Fonts.Sans, 11, Colour: "#FF9A6A", Tracking: 40));
        e.Add(page, new RectangleObject { X = 36, Y = 128, W = 540, H = 4, Fill = "#FF6A3D", Stroke = null });

        // The headline across both columns.
        var head = e.Add(page, new TextFrame { X = 36, Y = 146, W = 540, H = 60 });
        e.ReplaceText(head.StoryId, 0, 0, "A new light on the old harbour wall", new CharFormat(Fonts.Serif, 26, Bold: true, Colour: "#1C2433"));

        // The picture, drawn here so the screenshot needs no file.
        var picture = e.Add(page, new PictureObject { X = 306, Y = 210, W = 270, H = 170, Fit = Fit.Fill });
        var key = e.Doc.Images.Add(Harbour());
        e.Change("Place picture", d => ((PictureObject)d.Find(picture.Id)!).Image = key);
        var caption = e.Add(page, new TextFrame { X = 306, Y = 384, W = 270, H = 28 });
        e.ReplaceText(caption.StoryId, 0, 0, "The new lamps along the wall, lit for the first time in May.", new CharFormat(Fonts.Sans, 8.5, Italic: true, Colour: "#4A5468"));

        // The story: down the left column, then on under the picture.
        var left = e.Add(page, new TextFrame { X = 36, Y = 210, W = 255, H = 516 });
        var right = e.Add(page, new TextFrame { X = 306, Y = 420, W = 270, H = 306 });
        e.Change("Link frames", d => Chains.Link(d, (TextFrame)d.Find(left.Id)!, (TextFrame)d.Find(right.Id)!));
        e.ReplaceText(left.StoryId, 0, 0, Story, serif);
        e.FormatParas(left.StoryId, 0, Story.Length, p => p with { Align = Align.Justify, Leading = 15, FirstIndent = 12, SpaceAfter = 4 });
        e.FormatParas(left.StoryId, 0, Story.Length, p => p with { Hyphenate = true });
        e.FormatParas(left.StoryId, 0, 0, p => p with { FirstIndent = 0, DropLines = 3 });
        e.FormatChars(left.StoryId, 0, 1, f => f with { Colour = "#B0401C" });                      // the drop cap
        e.FormatChars(left.StoryId, 1, 19, f => f with { Bold = true }); // "he harbour wall was"

        // A pull quote across the gutter: both columns flow round it.
        var box = new Box(186, 556, 240, 104);
        e.Add(page, new RectangleObject { Bounds = box, Fill = "#FFF1EA", Stroke = "#FF6A3D", StrokeWidth = 0.75, Wrap = true, WrapGap = 10 });
        var quote = e.Add(page, new TextFrame { Bounds = box, Inset = 12 });
        e.ReplaceText(quote.StoryId, 0, 0, "“On the first Sunday there was a queue of swimmers along the wall before seven in the morning.”",
            new CharFormat(Fonts.Serif, 14, Italic: true, Colour: "#B0401C"));
        e.FormatParas(quote.StoryId, 0, 0, p => p with { Align = Align.Centre, Leading = 18 });

        w.View.Select([left.Id]);
    }

    /// <summary>A harbour at dusk: sky, sun, sea, a wall with lamps.</summary>
    private static byte[] Harbour()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(540, 340));
        using (var dc = bitmap.CreateDrawingContext())
        {
            dc.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#2A3B66"), 0), new GradientStop(Color.Parse("#E8875A"), 0.62), new GradientStop(Color.Parse("#F6C88A"), 0.7) },
            }, new Rect(0, 0, 540, 240));
            dc.DrawEllipse(new SolidColorBrush(Color.Parse("#FFE6B0")), null, new Point(380, 200), 34, 34);
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#1F3550")), new Rect(0, 220, 540, 120));
            for (var i = 0; i < 14; i++)
                dc.FillRectangle(new SolidColorBrush(Color.Parse("#40FFE6B0")), new Rect(330 + (i % 3) * 14, 228 + i * 8, 100 - i * 5, 2));
            var wall = new StreamGeometry();
            using (var g = wall.Open())
            {
                g.BeginFigure(new Point(0, 250), true);
                g.LineTo(new Point(300, 232));
                g.LineTo(new Point(300, 246));
                g.LineTo(new Point(0, 290));
                g.EndFigure(true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.Parse("#2B2420")), null, wall);
            for (var i = 0; i < 6; i++)
            {
                var x = 30 + i * 48;
                var top = 250 - x * 0.06;
                dc.FillRectangle(new SolidColorBrush(Color.Parse("#2B2420")), new Rect(x, top - 30, 3, 30));
                dc.DrawEllipse(new SolidColorBrush(Color.Parse("#FFE9A8")), null, new Point(x + 1.5, top - 32), 4, 4);
            }
        }
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }
}
