using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class FacingTests
{
    private static Editor Facing(int pages = 4)
    {
        var e = new Editor(Document.New(new Setup(Margins: new Margins(36, 36, 54, 36))));
        for (var i = 1; i < pages; i++) e.AddPage(i);
        e.SetFacing(true);
        return e;
    }

    [Fact]
    public void Odd_pages_are_on_the_right_and_even_ones_on_the_left()
    {
        var e = Facing();
        Assert.Equal([false, true, false, true], e.Doc.Pages.Select(e.Doc.IsLeft));
        e.SetFacing(false);
        Assert.All(e.Doc.Pages, p => Assert.False(e.Doc.IsLeft(p)));
    }

    [Fact]
    public void Inside_and_outside_margins_mirror_on_left_pages()
    {
        var e = Facing();
        // Inside 54, outside 36: a right page's inside is its left edge, a left page's its right.
        Assert.Equal(new Box(54, 36, 612 - 90, 720), e.Doc.GuidesFor(e.Doc.Pages[0]).Live);
        Assert.Equal(new Box(36, 36, 612 - 90, 720), e.Doc.GuidesFor(e.Doc.Pages[1]).Live);
    }

    [Fact]
    public void Masters_get_a_left_side_that_left_pages_use()
    {
        var e = Facing();
        var master = e.Doc.Masters[0];
        Assert.NotNull(master.Left);
        e.Add(SheetRef.MasterAt(0, left: true), new RectangleObject { Name = "left folio" });
        e.Add(SheetRef.MasterAt(0), new RectangleObject { Name = "right folio" });
        Assert.Equal("right folio", Assert.Single(e.Doc.MasterFor(e.Doc.Pages[0])!.Objects).Name);
        Assert.Equal("left folio", Assert.Single(e.Doc.MasterFor(e.Doc.Pages[1])!.Objects).Name);
        Assert.Contains(e.Doc.Sheets, s => s is MasterSide);
        Assert.Equal(2, e.Doc.AllObjects.Count());
        var added = e.AddMaster();
        Assert.NotNull(e.Doc.Master(added)!.Left); // a new master in a facing document has both sides
    }

    [Fact]
    public void A_master_can_have_its_own_margins_and_columns()
    {
        var e = Facing();
        e.SetMasterGuides(0, new Margins(72, 72, 72, 36), 3, 12);
        var (live, columns) = e.Doc.GuidesFor(e.Doc.Pages[1]); // a left page
        Assert.Equal(new Box(36, 72, 612 - 108, 792 - 144), live);
        Assert.Equal(3, columns.Count);
        Assert.Equal(12, columns[1].X - columns[0].Right, 6);
        // And snapping follows them.
        var t = SnapTargets.For(e.Doc, e.Doc.Pages[1]);
        Assert.Contains(36, t.Xs);
        Assert.DoesNotContain(54, t.Xs);
        e.SetMasterGuides(0, null, null, null);
        Assert.Single(e.Doc.GuidesFor(e.Doc.Pages[1]).Columns);
    }

    [Fact]
    public void Facing_documents_save_and_load_with_their_sides()
    {
        var e = Facing();
        e.Add(SheetRef.MasterAt(0, left: true), new EllipseObject());
        using var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms);
        Assert.True(back.Setup.Facing);
        Assert.Single(back.Masters[0].Left!.Objects);
        Assert.Equal(e.Doc.ToJson(), back.ToJson());
    }
}
