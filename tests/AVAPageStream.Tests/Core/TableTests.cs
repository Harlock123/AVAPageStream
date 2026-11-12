using AVAPageStream.Core;

namespace AVAPageStream.Tests.Core;

public class TableTests
{
    private static readonly FixedMeasurer M = new();

    private static (Editor E, TableObject T) Make(int rows = 3, int columns = 2)
    {
        var e = new Editor(Document.New());
        var t = e.AddTable(SheetRef.PageAt(0), new Box(100, 100, 200, 90), rows, columns);
        return (e, (TableObject)e.Doc.Find(t.Id)!);
    }

    private static string Text(Editor e, TableObject t, int r, int c) => e.Doc.Stories[t.Cells[r * t.Columns + c]].Text;

    private static void Put(Editor e, TableObject t, int r, int c, string text) => e.ReplaceText(t.Cells[r * t.Columns + c], 0, 0, text);

    [Fact]
    public void A_table_has_a_story_for_each_cell_set_in_the_cell()
    {
        var (e, t) = Make();
        Assert.Equal((3, 2, 6), (t.Rows, t.Columns, t.Cells.Count));
        Assert.Equal(new Box(100, 100, 200, 90), t.Bounds);
        Assert.Equal(new Box(200, 130, 100, 30), t.CellBox(1, 1));
        Assert.All(t.Cells, id => Assert.Equal(t.Id, e.Doc.Stories[id].Table));
        Assert.True(e.Doc.Stories[t.Cells[0]].FormatAt(0).Bold);   // the header row
        Assert.False(e.Doc.Stories[t.Cells[2]].FormatAt(0).Bold);
        var area = Assert.Single(Chains.Areas(e.Doc, e.Doc.Stories[t.Cells[3]]));
        Assert.Equal((new Box(204, 134, 92, 22), $"{t.Id}#3"), (area.Box, area.FrameId));
        Assert.Equal(t.Id, Chains.ObjectOf(area.FrameId));
        Assert.Equal(3, t.CellAt(new Pt(250, 145)));
        Assert.Equal(-1, t.CellAt(new Pt(50, 50)));
    }

    [Fact]
    public void Rows_and_columns_go_in_and_out_with_their_cells()
    {
        var (e, t) = Make();
        Put(e, t, 1, 0, "a");
        Put(e, t, 1, 1, "b");
        Put(e, t, 2, 0, "c");
        e.ChangeTable(t.Id, "Insert", (d, table) => Tables.InsertRow(d, table, 1));
        t = (TableObject)e.Doc.Find(t.Id)!;
        Assert.Equal(4, t.Rows);
        Assert.Equal(("", "a", "c"), (Text(e, t, 1, 0), Text(e, t, 2, 0), Text(e, t, 3, 0)));
        Assert.Equal(120, t.H);
        e.ChangeTable(t.Id, "Insert", (d, table) => Tables.InsertColumn(d, table, 1));
        t = (TableObject)e.Doc.Find(t.Id)!;
        Assert.Equal(3, t.Columns);
        Assert.Equal(("a", "", "b"), (Text(e, t, 2, 0), Text(e, t, 2, 1), Text(e, t, 2, 2)));
        var before = e.Doc.Stories.Count;
        e.ChangeTable(t.Id, "Delete", (d, table) => Tables.DeleteColumn(d, table, 0));
        t = (TableObject)e.Doc.Find(t.Id)!;
        Assert.Equal(before - 4, e.Doc.Stories.Count); // its cells' stories went
        Assert.Equal(("", "b"), (Text(e, t, 2, 0), Text(e, t, 2, 1)));
        e.ChangeTable(t.Id, "Delete", (d, table) => Tables.DeleteRow(d, table, 0));
        t = (TableObject)e.Doc.Find(t.Id)!;
        Assert.Equal(0, t.HeaderRows); // the header row went
        Assert.All(t.Cells, id => Assert.True(e.Doc.Stories.ContainsKey(id)));
        Assert.False(Tables.DeleteColumn(e.Doc, new TableObject { ColumnWidths = [10], RowHeights = [10], Cells = ["x"] }, 0)); // never the last
    }

    [Fact]
    public void Rows_fit_the_text_of_their_tallest_cell()
    {
        var (e, t) = Make();
        Put(e, t, 1, 1, string.Join(" ", Enumerable.Repeat("word", 30))); // 92 pt wide: 3 words a line, 10 lines
        Assert.True(TextFlow.Layout(e.Doc.Stories[t.Cells[3]], Chains.Areas(e.Doc, e.Doc.Stories[t.Cells[3]]), M).Overset);
        e.ChangeTable(t.Id, "Fit", (d, table) => Tables.FitRows(d, table, M));
        t = (TableObject)e.Doc.Find(t.Id)!;
        Assert.True(t.RowHeights[1] > 100, $"{t.RowHeights[1]}");
        Assert.Equal(t.RowHeights.Sum(), t.H);
        Assert.False(TextFlow.Layout(e.Doc.Stories[t.Cells[3]], Chains.Areas(e.Doc, e.Doc.Stories[t.Cells[3]]), M).Overset);
    }

    [Fact]
    public void Resizing_a_table_scales_its_columns_and_rows()
    {
        var (e, t) = Make();
        e.Change("Resize", d => d.Find(t.Id)!.Bounds = new Box(0, 0, 400, 45));
        t = (TableObject)e.Doc.Find(t.Id)!;
        Assert.Equal([200, 200], t.ColumnWidths);
        Assert.Equal([15, 15, 15], t.RowHeights);
    }

    [Fact]
    public void Copying_deleting_and_saving_a_table_take_its_cells_along()
    {
        var (e, t) = Make();
        Put(e, t, 0, 0, "Name");
        var copy = (TableObject)e.Doc.Find(e.Duplicate([t.Id]).Single())!;
        Assert.Equal("Name", e.Doc.Stories[copy.Cells[0]].Text);
        Assert.Empty(copy.Cells.Intersect(t.Cells));
        Assert.All(copy.Cells, id => Assert.Equal(copy.Id, e.Doc.Stories[id].Table));

        using var ms = new MemoryStream();
        DocumentFile.Write(e.Doc, ms);
        ms.Position = 0;
        Assert.Equal(e.Doc.ToJson(), DocumentFile.Read(ms).ToJson());

        var stories = e.Doc.Stories.Count;
        e.Delete([t.Id]);
        Assert.Equal(stories - 6, e.Doc.Stories.Count);
    }

    [Fact]
    public void Find_spelling_and_contents_reach_into_cells()
    {
        var (e, t) = Make();
        Put(e, t, 1, 0, "Lamps lit");
        Put(e, t, 2, 1, "mispelt");
        Assert.Contains(t.Cells[2], Search.StoryOrder(e.Doc));
        Assert.Equal(new Match(t.Cells[2], 0, 5), Search.Next(e.Doc, "lamps", new FindOptions(), null, 0));
        Assert.Equal(new Match(t.Cells[5], 0, 7), Speller.For("en-US").Next(e.Doc, null, null, 0));
        // A 16 pt subheading fits the 22 pt inside the cell (a 24 pt heading wouldn't, and would be overset, with no page).
        e.ApplyStyle(t.Cells[2], 0, 0, "Subheading");
        Assert.Equal(("Lamps lit", (int?)1), Contents.Find(e.Doc, ["Subheading"], M).Select(x => (x.Text, x.Page)).Single());
    }
}
