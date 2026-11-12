using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

/// <summary>Milestone 4: tables that run on into other frames, their header rows repeated.</summary>
public class RunOnTableTests
{
    // Letter, half-inch margins: the live area runs down to 756. Six rows of 20 pt from 680 go past it.
    private static (Editor E, TableObject Head, TableObject Next) RunOn()
    {
        var e = new Editor(Document.New());
        var t = e.AddTable(SheetRef.PageAt(0), new Box(72, 680, 200, 120), 6, 2);
        var next = e.RunOnTable(t.Id)!;
        return (e, (TableObject)e.Doc.Find(t.Id)!, (TableObject)e.Doc.Find(next)!);
    }

    [Fact]
    public void Rows_that_dont_fit_go_on_to_the_next_page_under_the_header_again()
    {
        var (e, head, next) = RunOn();
        Assert.Equal(2, e.Doc.Pages.Count);                 // a page was added for it
        Assert.Contains(e.Doc.Pages[1].Objects, o => o.Id == next.Id);
        Assert.Equal(76, head.H, 6);                        // the first frame stops at the live area's foot
        Assert.Equal((head.Id, (string?)next.Id), (next.Continues, head.Next));
        Assert.Equal((72.0, 36.0, 200.0), (next.X, next.Y, next.W));

        var placed = Tables.Place(e.Doc, next)!;
        Assert.Equal([0, 0, 0, 1, 1, 1], placed.Rows.Select(r => r.Frame));
        Assert.Equal([0.0, 20, 40, 20, 40, 60], placed.Rows.Select(r => r.Y));
        Assert.Equal([(0, 0.0, true), (3, 20, false), (4, 40, false), (5, 60, false)], placed.RowsIn(1));

        // A cell's text is set where its row is: in the second frame, below the repeated header.
        var cell = head.Cells[8]; // row 4, column 0
        var area = Assert.Single(Chains.Areas(e.Doc, e.Doc.Stories[cell]));
        Assert.Equal(next.CellId(8), area.FrameId);
        Assert.Equal(new Box(72 + 4, 36 + 40 + 4, 92, 12), area.Box);

        // Clicking the header again in the second frame finds the header's cell, shown again.
        Assert.Equal((1, true), placed.CellAt(next, new Pt(72 + 150, 36 + 10)));
        Assert.Equal((8, false), placed.CellAt(next, new Pt(72 + 10, 36 + 50)));
        Assert.Null(placed.CellAt(next, new Pt(72 + 10, 36 + 300)));
    }

    [Fact]
    public void New_rows_flow_on_and_rows_with_nowhere_to_go_are_overset()
    {
        var (e, head, next) = RunOn();
        e.ChangeTable(head.Id, "Rows", (d, t) => { for (var i = 0; i < 4; i++) Tables.InsertRow(d, t, t.Rows); });
        head = (TableObject)e.Doc.Find(head.Id)!;
        Assert.Equal(76, head.H, 6); // its height kept: it runs on
        Assert.Equal([0, 0, 0, 1, 1, 1, 1, 1, 1, 1], Tables.Place(e.Doc, head)!.Rows.Select(r => r.Frame));

        // The second frame made short: the rows past it have nowhere to go.
        e.Change("Short", d => d.Find(next.Id)!.Bounds = d.Find(next.Id)!.Bounds with { H = 60 });
        var placed = Tables.Place(e.Doc, head)!;
        Assert.Equal([0, 0, 0, 1, 1, -1, -1, -1, -1, -1], placed.Rows.Select(r => r.Frame));
        Assert.Empty(Chains.Areas(e.Doc, e.Doc.Stories[head.Cells[12]]));
        Assert.Equal(200, e.Doc.Find(next.Id)!.W); // resizing a frame of it changes how much it holds, not its columns
    }

    [Fact]
    public void Deleting_a_frame_it_runs_on_into_brings_the_table_back_whole()
    {
        var (e, head, next) = RunOn();
        e.Delete([next.Id]);
        head = (TableObject)e.Doc.Find(head.Id)!;
        Assert.Null(head.Next);
        Assert.Equal(120, head.H, 6);
        Assert.All(Tables.Place(e.Doc, head)!.Rows, r => Assert.Equal(0, r.Frame));
    }

    [Fact]
    public void Deleting_the_table_deletes_its_other_frames_and_cells()
    {
        var (e, head, next) = RunOn();
        var cells = head.Cells.ToList();
        e.Delete([head.Id]);
        Assert.Null(e.Doc.Find(next.Id));
        Assert.All(cells, c => Assert.False(e.Doc.Stories.ContainsKey(c)));
    }

    [Fact]
    public void Copying_a_frame_it_runs_on_into_copies_the_whole_table()
    {
        var (e, head, next) = RunOn();
        e.ReplaceText(head.Cells[10], 0, 0, "Last row");
        var copy = (TableObject)e.Doc.Find(Assert.Single(e.Duplicate([next.Id])))!;
        Assert.Null(copy.Continues);
        Assert.Null(copy.Next);
        Assert.Equal(6, copy.Rows);
        Assert.Equal(120, copy.H, 6);
        Assert.Equal("Last row", e.Doc.Stories[copy.Cells[10]].Text);
        Assert.Equal((next.X + 12, next.Y + 12), (copy.X, copy.Y));
    }

    [Fact]
    public void The_chain_survives_a_file_and_a_broken_one_is_repaired()
    {
        var (e, head, next) = RunOn();
        using var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        var back = DocumentFile.Read(ms);
        Assert.Equal(next.Id, ((TableObject)back.Find(head.Id)!).Next);
        Assert.Equal(head.Id, ((TableObject)back.Find(next.Id)!).Continues);

        e.Change("Break", d => d.Pages[0].Objects.Remove(d.Find(head.Id)!)); // its first frame gone, by hand
        using var ms2 = new MemoryStream();
        DocumentFile.Write(e.Doc, ms2);
        ms2.Position = 0;
        Assert.Null(DocumentFile.Read(ms2).Find(next.Id));
    }
}
