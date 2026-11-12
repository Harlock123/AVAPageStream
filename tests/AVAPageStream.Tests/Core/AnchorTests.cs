using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 9, stage 1: objects anchored in text. Sizes as TextFlowTests' (5 pt a character at 10 pt).</summary>
public class AnchorTests
{
    private static readonly FixedMeasurer M = new();
    private static readonly ParagraphStyle Plain = new("Body", new CharFormat(Size: 10), new ParaFormat());

    /// <summary>"ab" with an object anchored between the letters.</summary>
    private static (Story Story, RectangleObject Box) Story(Anchoring anchoring, double w = 50, double h = 30)
    {
        var s = AVAPageStream.Core.Story.Create("s", Plain);
        s.Insert(0, "ab");
        var box = new RectangleObject { Id = "box", W = w, H = h, Anchoring = anchoring };
        s.Anchored.Add(box);
        s.Insert(1, Fields.Anchor.ToString(), s.FormatAt(0) with { Anchor = "box" });
        return (s, box);
    }

    private static Area A(double x, double y, double w, double h) => new(new Box(x, y, w, h), "f", 0);

    [Fact]
    public void An_object_in_the_line_takes_its_width_and_height_and_sits_on_the_baseline()
    {
        var (s, box) = Story(new Anchoring());
        var l = TextFlow.Layout(s, [A(10, 20, 200, 200)], M);
        var line = Assert.Single(l.Lines);
        Assert.True(line.Ascent >= 30);
        var b = line.Pieces.Single(p => s.Text[p.Start] == 'b');
        Assert.Equal(10 + 5 + 50, b.X);                    // a, the object, then b
        Anchors.Place(s, l);
        Assert.Equal((15.0, line.Baseline - 30), (box.X, box.Y));
        Assert.Equal("f", box.PlacedIn);
        // Raised by its offset: higher, and the line taller above its baseline.
        var (raised, rbox) = Story(new Anchoring(Offset: 6));
        var rl = TextFlow.Layout(raised, [A(10, 20, 200, 200)], M);
        Anchors.Place(raised, rl);
        Assert.Equal(rl.Lines[0].Baseline - 36, rbox.Y);
        Assert.True(rl.Lines[0].Ascent >= 36);
    }

    [Fact]
    public void An_object_placed_by_hand_takes_no_room_and_sits_against_its_line()
    {
        var (s, box) = Story(new Anchoring(AnchorKind.Custom, From: AnchorFrom.ColumnRight, X: 8, Y: 2));
        var l = TextFlow.Layout(s, [A(10, 20, 200, 200)], M);
        var line = Assert.Single(l.Lines);
        Assert.Equal(10 + 5, line.Pieces.Single(p => s.Text[p.Start] == 'b').X); // no room taken
        Anchors.Place(s, l);
        Assert.Equal((218.0, line.Baseline - line.Ascent + 2), (box.X, box.Y)); // in the right margin, beside the line
    }

    [Fact]
    public void Deleting_its_marker_lets_the_object_go_and_copies_of_the_text_take_copies()
    {
        var (s, _) = Story(new Anchoring());
        var doc = Document.New();
        s.Id = "s";
        doc.Stories["s"] = s;
        var copied = s.Extract(0, s.Length);
        Assert.Single(copied.Objects!);
        var rekeyed = Anchors.Rekey(doc, copied);
        Assert.NotEqual("box", rekeyed.Objects![0].Id);
        s.InsertFragment(s.Length, rekeyed);
        Assert.Equal(2, s.Anchored.Count);
        Assert.Equal(2, s.Text.Count(c => c == Fields.Anchor));
        Assert.Equal(new[] { "box", rekeyed.Objects[0].Id }, s.Anchored.Select(o => o.Id));
        s.Delete(1, 1); // the first marker
        Assert.Equal(new[] { rekeyed.Objects[0].Id }, s.Anchored.Select(o => o.Id));
        // It is one of the document's objects, found and saved with it.
        Assert.NotNull(doc.Find(rekeyed.Objects[0].Id));
        var back = Document.FromJson(doc.ToJson(), doc.Images);
        Assert.Equal(AnchorKind.Inline, back.Stories["s"].Anchored.Single().Anchoring!.Kind);
    }

    [Fact]
    public void An_object_is_anchored_beside_its_text_released_and_deleted_with_its_marker()
    {
        var e = new Editor(Document.New());
        var frame = e.Add(SheetRef.PageAt(0), new TextFrame { X = 72, Y = 72, W = 200, H = 300 });
        e.ReplaceText(frame.StoryId, 0, 0, "First line.\nSecond line.\nThird line.");
        var icon = e.Add(SheetRef.PageAt(0), new EllipseObject { X = 40, Y = 85, W = 20, H = 20 });
        TextLayout? Lay(string id) => TextFlow.Layout(e.Doc.Stories[id], Chains.Areas(e.Doc, e.Doc.Stories[id]), M);
        Assert.Null(e.AnchorBeside(icon.Id, Lay));
        var story = e.Doc.Stories[frame.StoryId];
        var anchored = Assert.Single(story.Anchored);
        Assert.Equal(icon.Id, anchored.Id);
        Assert.DoesNotContain(e.Doc.Pages[0].Objects, o => o.Id == icon.Id);
        Assert.Equal(AnchorKind.Custom, anchored.Anchoring!.Kind);
        Assert.Equal(-32, anchored.Anchoring.X, 6);                              // into the margin, left of the column
        // Placed again where it was.
        var l = Lay(frame.StoryId)!;
        Anchors.Place(story, l);
        Assert.Equal((40.0, 85.0), (Math.Round(anchored.X, 6), Math.Round(anchored.Y, 6)));
        // Released: back on the page, its marker gone.
        Assert.Null(e.Release(icon.Id, SheetRef.PageAt(0)));
        Assert.Contains(e.Doc.Pages[0].Objects, o => o.Id == icon.Id);
        Assert.DoesNotContain(Fields.Anchor, e.Doc.Stories[frame.StoryId].Text);
        // Anchored again, then deleted: its marker goes too.
        Assert.Null(e.AnchorBeside(icon.Id, Lay));
        Assert.Equal(1, e.Delete([icon.Id]));
        Assert.Empty(e.Doc.Stories[frame.StoryId].Anchored);
        Assert.DoesNotContain(Fields.Anchor, e.Doc.Stories[frame.StoryId].Text);
        e.Undo();
        Assert.Single(e.Doc.Stories[frame.StoryId].Anchored);
    }
}
