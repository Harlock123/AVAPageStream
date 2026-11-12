namespace AVAPageStream.Core;

/// <summary>Making tables, and changing their rows and columns. Each works on the document given: call inside an Editor change.</summary>
public static class Tables
{
    /// <summary>A new, empty story for a cell of a table, in the Body style (bold in a header row).</summary>
    public static Story NewCell(Document doc, TableObject table, bool header = false)
    {
        var style = doc.Style("Body");
        var story = Story.Create(doc.NewId("story"), header ? style with { Char = style.Char with { Bold = true } } : style);
        story.Paras[0] = story.Paras[0] with { SpaceAfter = 0 };
        story.Table = table.Id;
        story.Frames = [table.Id];
        doc.Stories[story.Id] = story;
        return story;
    }

    /// <summary>A table filling a box with so many rows and columns of equal size, its first row a header.</summary>
    public static TableObject Make(Document doc, Box box, int rows, int columns)
    {
        rows = Math.Clamp(rows, 1, 200);
        columns = Math.Clamp(columns, 1, 50);
        var t = new TableObject
        {
            Id = doc.NewId("object"),
            X = box.X,
            Y = box.Y,
            ColumnWidths = Enumerable.Repeat(box.W / columns, columns).ToList(),
            RowHeights = Enumerable.Repeat(box.H / rows, rows).ToList(),
            HeaderRows = rows > 1 ? 1 : 0,
        };
        t.Fit();
        for (var i = 0; i < rows * columns; i++) t.Cells.Add(NewCell(doc, t, i / columns < t.HeaderRows).Id);
        return t;
    }

    /// <summary>Puts a row in before row <paramref name="at"/> (at the end for the count), as tall as its neighbour.</summary>
    public static void InsertRow(Document doc, TableObject t, int at)
    {
        at = Math.Clamp(at, 0, t.Rows);
        var height = t.RowHeights[Math.Clamp(at - 1, 0, t.Rows - 1)];
        t.RowHeights.Insert(at, height);
        var header = at < t.HeaderRows;
        t.Cells.InsertRange(at * t.Columns, Enumerable.Range(0, t.Columns).Select(_ => NewCell(doc, t, header).Id));
        if (header) t.HeaderRows++;
        t.Fit();
    }

    /// <summary>Takes a row out with its cells' text (never the last row).</summary>
    public static bool DeleteRow(Document doc, TableObject t, int row)
    {
        if (t.Rows <= 1 || row < 0 || row >= t.Rows) return false;
        foreach (var id in t.Cells.GetRange(row * t.Columns, t.Columns)) doc.Stories.Remove(id);
        t.Cells.RemoveRange(row * t.Columns, t.Columns);
        t.RowHeights.RemoveAt(row);
        if (row < t.HeaderRows) t.HeaderRows--;
        t.Fit();
        return true;
    }

    /// <summary>Puts a column in before column <paramref name="at"/>, as wide as its neighbour.</summary>
    public static void InsertColumn(Document doc, TableObject t, int at)
    {
        at = Math.Clamp(at, 0, t.Columns);
        t.ColumnWidths.Insert(at, t.ColumnWidths[Math.Clamp(at - 1, 0, t.Columns - 1)]);
        var columns = t.Columns;
        for (var r = t.Rows - 1; r >= 0; r--) t.Cells.Insert(r * (columns - 1) + at, NewCell(doc, t, r < t.HeaderRows).Id);
        t.Fit();
    }

    /// <summary>Takes a column out with its cells' text (never the last column).</summary>
    public static bool DeleteColumn(Document doc, TableObject t, int column)
    {
        if (t.Columns <= 1 || column < 0 || column >= t.Columns) return false;
        for (var r = t.Rows - 1; r >= 0; r--)
        {
            var i = r * t.Columns + column;
            doc.Stories.Remove(t.Cells[i]);
            t.Cells.RemoveAt(i);
        }
        t.ColumnWidths.RemoveAt(column);
        t.Fit();
        return true;
    }

    /// <summary>
    /// Makes each row as tall as the text in its tallest cell needs (no shorter than <paramref name="least"/>), by
    /// setting each cell's story in a cell of the column's width and unlimited height.
    /// </summary>
    public static void FitRows(Document doc, TableObject t, ITextMeasurer measurer, double least = 12)
    {
        for (var r = 0; r < t.Rows; r++)
        {
            var need = least;
            for (var c = 0; c < t.Columns; c++)
            {
                if (!doc.Stories.TryGetValue(t.Cells[r * t.Columns + c], out var story)) continue;
                var width = Math.Max(1, t.ColumnWidths[c] - 2 * t.CellInset);
                var layout = TextFlow.Layout(story, [new Area(new Box(0, 0, width, 100_000), "fit", 0)], measurer);
                var bottom = layout.Lines.Count == 0 ? 0 : layout.Lines.Max(l => l.Baseline + l.Descent);
                need = Math.Max(need, Math.Ceiling(bottom + 2 * t.CellInset));
            }
            t.RowHeights[r] = need;
        }
        t.Fit();
    }
}
