using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class GroupPathRotationTests
{
    private static (Editor E, RectangleObject A, TextFrame B) Two()
    {
        var e = new Editor(Document.New());
        var a = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 0, Y = 0, W = 100, H = 50 });
        var b = e.Add(SheetRef.PageAt(0), new TextFrame { X = 100, Y = 50, W = 100, H = 50 });
        e.ReplaceText(b.StoryId, 0, 0, "inside");
        return (e, a, b);
    }

    [Fact]
    public void A_group_holds_its_objects_and_is_found_through()
    {
        var (e, a, b) = Two();
        var id = e.Group([a.Id, b.Id])!;
        var g = Assert.IsType<GroupObject>(Assert.Single(e.Doc.Pages[0].Objects));
        Assert.Equal(id, g.Id);
        Assert.Equal(new Box(0, 0, 200, 100), g.Bounds);
        Assert.NotNull(e.Doc.Find(b.Id));                  // found inside
        Assert.Same(e.Doc.Pages[0], e.Doc.SheetOf(b.Id));
        Assert.Equal([g.Id, a.Id, b.Id], e.Doc.Pages[0].All.Select(o => o.Id));
        Assert.Equal([b.StoryId], Search.StoryOrder(e.Doc));
        Assert.Null(e.Group([a.Id]));                      // one object, or ones already grouped, won't group
    }

    [Fact]
    public void Moving_and_resizing_a_group_moves_and_scales_what_it_holds()
    {
        var (e, a, b) = Two();
        var id = e.Group([a.Id, b.Id])!;
        e.Change("Move", d => d.Find(id)!.MoveBy(10, 20));
        Assert.Equal(new Box(10, 20, 100, 50), e.Doc.Find(a.Id)!.Bounds);
        e.Change("Resize", d => d.Find(id)!.Bounds = new Box(10, 20, 400, 50));
        Assert.Equal(new Box(10, 20, 200, 25), e.Doc.Find(a.Id)!.Bounds);
        Assert.Equal(new Box(210, 45, 200, 25), e.Doc.Find(b.Id)!.Bounds);
    }

    [Fact]
    public void Ungrouping_puts_the_objects_back_in_its_place()
    {
        var (e, a, b) = Two();
        var top = e.Add(SheetRef.PageAt(0), new EllipseObject());
        var id = e.Group([a.Id, b.Id])!;
        Assert.Equal([id, top.Id], e.Doc.Pages[0].Objects.Select(o => o.Id));
        Assert.Equal([a.Id, b.Id], e.Ungroup([id]));
        Assert.Equal([a.Id, b.Id, top.Id], e.Doc.Pages[0].Objects.Select(o => o.Id));
    }

    [Fact]
    public void Deleting_inside_a_group_shrinks_it_and_an_emptied_group_goes()
    {
        var (e, a, b) = Two();
        var id = e.Group([a.Id, b.Id])!;
        e.Delete([b.Id]);
        Assert.False(e.Doc.Stories.ContainsKey(b.StoryId)); // its story went with it
        Assert.Equal(new Box(0, 0, 100, 50), e.Doc.Find(id)!.Bounds);
        e.Delete([a.Id]);
        Assert.Empty(e.Doc.Pages[0].Objects);
    }

    [Fact]
    public void Copying_a_group_copies_its_frames_with_their_own_stories()
    {
        var (e, a, b) = Two();
        var id = e.Group([a.Id, b.Id])!;
        var copy = (GroupObject)e.Doc.Find(e.Duplicate([id]).Single())!;
        var frame = copy.Children.OfType<TextFrame>().Single();
        Assert.NotEqual(b.Id, frame.Id);
        Assert.NotEqual(b.StoryId, frame.StoryId);
        Assert.Equal("inside", e.Doc.Stories[frame.StoryId].Text);
        Assert.Equal(12, copy.Children[0].X);
        Assert.Equal(e.Doc.AllObjects.Count(), e.Doc.AllObjects.Select(o => o.Id).Distinct().Count());
    }

    [Fact]
    public void Arranging_inside_a_group_keeps_to_the_group()
    {
        var (e, a, b) = Two();
        e.Group([a.Id, b.Id]);
        e.Arrange([a.Id], Editor.Order.Front);
        var g = (GroupObject)e.Doc.Pages[0].Objects[0];
        Assert.Equal([b.Id, a.Id], g.Children.Select(c => c.Id));
    }

    [Fact]
    public void Groups_save_and_load()
    {
        var (e, a, b) = Two();
        e.Group([a.Id, b.Id]);
        using var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms);
        Assert.IsType<TextFrame>(((GroupObject)back.Pages[0].Objects[0]).Children[1]);
        Assert.Equal(e.Doc.ToJson(), back.ToJson());
    }

    [Fact]
    public void A_path_fits_its_frame_round_its_points_and_scales_with_it()
    {
        var path = new PathObject { Nodes = [PathNode.Corner(new Pt(10, 10)), PathNode.Smooth(new Pt(50, 30), new Pt(60, 20)), PathNode.Corner(new Pt(30, 50))], Closed = true };
        path.Fit();
        Assert.Equal(new Box(10, 10, 50, 40), path.Bounds); // the handles (40,40) and (60,20) count
        Assert.Equal("Path", path.Kind);
        path.Bounds = new Box(0, 0, 120, 40); // across: 50 becomes 120
        Assert.Equal(new Pt(0, 0), path.Nodes[0].At);
        Assert.Equal(new Pt(96, 20), path.Nodes[1].At);
        Assert.Equal(new Pt(120, 10), path.Nodes[1].Out);
        Assert.Equal("Polygon", new PathObject { Nodes = [PathNode.Corner(new Pt(0, 0))], Closed = true }.Kind);
    }

    [Fact]
    public void A_turned_object_covers_its_turned_box()
    {
        var o = new RectangleObject { X = 0, Y = 0, W = 100, H = 20, Rotation = 90 };
        Assert.Equal(new Box(40, -40, 20, 100), Round(Shapes.Extent(o)));
        var p = new Pt(55, 10);
        Assert.Equal(p, Round(Shapes.ToPage(o, Shapes.ToLocal(o, p))));
        // Clockwise: the top-left corner swings round to the top-right.
        Assert.Equal(new Pt(60, -40), Round(Shapes.Corners(o)[0]));
        Assert.Equal(Round(Shapes.Extent(o).Inflate(6)), Round(o.WrapBox with { }));
    }

    [Fact]
    public void Aligning_goes_by_the_turned_box()
    {
        var e = new Editor(Document.New());
        var o = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 100, Y = 100, W = 100, H = 20, Rotation = 90 });
        var other = e.Add(SheetRef.PageAt(0), new RectangleObject { X = 0, Y = 0, W = 10, H = 10 });
        e.AlignObjects([o.Id, other.Id], Editor.Alignment.Top);
        Assert.Equal(0, Math.Round(Shapes.Extent(e.Doc.Find(o.Id)!).Y, 6));
    }

    private static Box Round(Box b) => new(Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.W, 6), Math.Round(b.H, 6));
    private static Pt Round(Pt p) => new(Math.Round(p.X, 6), Math.Round(p.Y, 6));
}
