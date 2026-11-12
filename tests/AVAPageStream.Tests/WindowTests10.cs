using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 3: tables.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void A_table_is_drawn_typed_into_and_tabbed_through()
    {
        var w = Open();
        w.View.Snap = false;
        Tool(w, AVAPageStream.Tool.Table);
        Drag(w, new Pt(72, 72), new Pt(372, 192));
        var t = Assert.IsType<TableObject>(Assert.Single(w.Editor.Doc.Pages[0].Objects));
        Assert.Equal((4, 3), (t.Rows, t.Columns));
        Assert.Equal(t.Cells[0], w.View.Editing!.StoryId); // straight into the first cell
        Type(w, "Item");
        Press(w, Key.Tab);
        Type(w, "Price");
        Press(w, Key.Tab);
        Type(w, "Notes");
        Press(w, Key.Tab);
        Type(w, "Lamp");
        var doc = w.Editor.Doc;
        Assert.Equal(["Item", "Price", "Notes", "Lamp"], t.Cells.Take(4).Select(id => doc.Stories[id].Text));
        Press(w, Key.Tab, RawInputModifiers.Shift);
        Assert.Equal(t.Cells[2], w.View.Editing!.StoryId);

        // Tab from the last cell adds a row.
        w.View.StartEditing((TableObject)doc.Find(t.Id)!, 11);
        Press(w, Key.Tab);
        t = (TableObject)w.Editor.Doc.Find(t.Id)!;
        Assert.Equal(5, t.Rows);
        Assert.Equal(t.Cells[12], w.View.Editing!.StoryId);

        // Clicking another cell of the table moves there.
        Click(w, new Pt(72 + 150, 72 + 45));
        Assert.Equal(t.Cells[4], w.View.Editing!.StoryId);
        Shot(w, "table");
        Done(w);
    }

    [AvaloniaFact]
    public void Rows_and_columns_change_from_the_object_tab_and_the_table_menu()
    {
        var w = Open();
        var t = w.Editor.AddTable(SheetRef.PageAt(0), new Box(72, 72, 300, 120), 3, 3);
        w.View.Select([t.Id]);
        Pump();
        Assert.Equal("Table", w.Panel.ObjectTitle.Text);
        w.Panel.TableRowsBox.Value = 5;
        Pump();
        Assert.Equal(5, ((TableObject)w.Editor.Doc.Find(t.Id)!).Rows);
        w.Panel.ColumnWidthsBox.Focus();
        w.Panel.ColumnWidthsBox.Text = "1in, 2in, 1in";
        Press(w, Key.Enter);
        t = (TableObject)w.Editor.Doc.Find(t.Id)!;
        Assert.Equal([72, 144, 72], t.ColumnWidths);
        Assert.Equal(288, t.W);

        // With the caret in a cell, the Table menu puts a column in beside it.
        w.View.StartEditing(t, 4); // row 1, column 1
        w.Editor.ReplaceText(t.Cells[4], 0, 0, "middle");
        w.TableEdit("Insert column", (d, table, _, c) => Tables.InsertColumn(d, table, c + 1));
        t = (TableObject)w.Editor.Doc.Find(t.Id)!;
        Assert.Equal(4, t.Columns);
        Assert.Equal("middle", w.Editor.Doc.Stories[t.Cells[1 * 4 + 1]].Text);
        Assert.Equal("", w.Editor.Doc.Stories[t.Cells[1 * 4 + 2]].Text);
        w.Undo();
        Assert.Equal(3, ((TableObject)w.Editor.Doc.Find(t.Id)!).Columns);
        Done(w);
    }
}
