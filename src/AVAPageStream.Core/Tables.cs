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

    // ---- Tables that run on ----------------------------------------------------------------------------------------

    /// <summary>The table a frame belongs to: its first frame (itself, unless it continues another).</summary>
    public static TableObject? Head(Document doc, TableObject t) => t.Continues is null ? t : doc.Find(t.Continues) as TableObject;

    /// <summary>A table's frames in order, from its first.</summary>
    public static List<TableObject> Chain(Document doc, TableObject head)
    {
        var chain = new List<TableObject> { head };
        var seen = new HashSet<string> { head.Id };
        for (var next = head.Next; next is not null && seen.Add(next) && doc.Find(next) is TableObject t; next = t.Next) chain.Add(t);
        return chain;
    }

    /// <summary>Where a table's rows are: in which of its frames, and how far down it.</summary>
    public static TablePlacement? Place(Document doc, TableObject frame)
    {
        if (Head(doc, frame) is not { } head) return null;
        var frames = Chain(doc, head);
        var rows = new (int Frame, double Y)[head.Rows];
        var header = head.RowHeights.Take(head.HeaderRows).Sum();
        var (f, y, body) = (0, 0.0, false);
        for (var r = 0; r < head.Rows; r++)
        {
            var h = head.RowHeights[r];
            if (r < head.HeaderRows || frames.Count == 1) { rows[r] = (0, y); y += h; continue; }
            // A row that doesn't fit goes on to the next frame, under the header rows again; one too tall for any
            // frame is set anyway, so it isn't lost.
            while (y + h > frames[f].H + 0.01 && body && f + 1 < frames.Count) (f, y, body) = (f + 1, header, false);
            if (y + h > frames[f].H + 0.01 && body)
            {
                for (; r < head.Rows; r++) rows[r] = (-1, 0); // overset: no frame left for them
                break;
            }
            rows[r] = (f, y);
            y += h;
            body = true;
        }
        return new TablePlacement(head, frames, rows);
    }

    /// <summary>Gives a table's other frames its columns, so they line up with its first.</summary>
    public static void Sync(Document doc, TableObject head)
    {
        foreach (var t in Chain(doc, head).Skip(1))
        {
            t.ColumnWidths = [.. head.ColumnWidths];
            t.W = head.ColumnWidths.Sum();
        }
        if (head.Next is null) head.Fit();
    }

    /// <summary>
    /// Makes a table run on into a new frame on the next page (added if there is none), at the same place across and
    /// filling the page's live area down: the table's last frame stops at the foot of its page's live area, and the
    /// rows that don't fit go on. Returns the new frame, or null if the table can't be found.
    /// </summary>
    public static TableObject? RunOn(Document doc, TableObject frame)
    {
        if (Head(doc, frame) is not { } head) return null;
        var last = Chain(doc, head)[^1];
        var index = doc.Pages.FindIndex(p => p.All.Any(o => o.Id == last.Id));
        if (index < 0) return null; // a master's table stays on its master
        if (index + 1 >= doc.Pages.Count) doc.Pages.Insert(index + 1, new Page());
        var live = doc.Setup.Live;
        if (last.Y + last.H > live.Bottom) last.H = Math.Max(head.RowHeights.Take(head.HeaderRows).Sum() + 12, live.Bottom - last.Y);
        var next = new TableObject
        {
            Id = doc.NewId("object"),
            Continues = head.Id,
            X = last.X,
            Y = live.Y,
            H = live.H,
            HeaderRows = 0,
            Fill = head.Fill,
            Stroke = head.Stroke,
            StrokeWidth = head.StrokeWidth,
            Layer = head.Layer,
        };
        last.Next = next.Id;
        doc.Pages[index + 1].Objects.Add(next);
        Sync(doc, head);
        return next;
    }
}

/// <summary>
/// Where a table's rows are set: for each, the frame of its chain it is in (-1 if none had room) and how far down
/// the frame. Each frame after the first shows the header rows again at its top.
/// </summary>
public sealed record TablePlacement(TableObject Head, IReadOnlyList<TableObject> Frames, (int Frame, double Y)[] Rows)
{
    public double HeaderHeight => Head.RowHeights.Take(Head.HeaderRows).Sum();

    /// <summary>The rows a frame shows, top first: repeated ones are header rows set again from the first frame.</summary>
    public IEnumerable<(int Row, double Y, bool Repeated)> RowsIn(int frame)
    {
        if (frame > 0 && Rows.Any(r => r.Frame == frame))
        {
            var y = 0.0;
            for (var r = 0; r < Head.HeaderRows; r++) { yield return (r, y, true); y += Head.RowHeights[r]; }
        }
        for (var r = 0; r < Rows.Length; r++)
            if (Rows[r].Frame == frame) yield return (r, Rows[r].Y, false);
    }

    /// <summary>The frame a cell is set in, or null if its row didn't fit.</summary>
    public TableObject? FrameOf(int cell) => Rows[cell / Math.Max(1, Head.Columns)].Frame is var f and >= 0 ? Frames[f] : null;

    /// <summary>A cell's box on its frame's page, or null if its row didn't fit.</summary>
    public Box? CellBox(int cell)
    {
        var (row, column) = (cell / Math.Max(1, Head.Columns), cell % Math.Max(1, Head.Columns));
        if (Rows[row].Frame is not (var f and >= 0)) return null;
        var frame = Frames[f];
        return new Box(frame.X + Head.ColumnWidths.Take(column).Sum(), frame.Y + Rows[row].Y, Head.ColumnWidths[column], Head.RowHeights[row]);
    }

    /// <summary>The cell under a point on a frame of the table, and whether it is a header row shown again there.</summary>
    public (int Cell, bool Repeated)? CellAt(TableObject frame, Pt p)
    {
        var f = Frames.ToList().FindIndex(t => t.Id == frame.Id);
        if (f < 0 || Head.Columns == 0) return null;
        foreach (var (row, y, repeated) in RowsIn(f))
        {
            if (p.Y < frame.Y + y - 0.01 || p.Y > frame.Y + y + Head.RowHeights[row] + 0.01) continue;
            var x = frame.X;
            for (var c = 0; c < Head.Columns; c++)
            {
                if (p.X >= x - 0.01 && p.X <= x + Head.ColumnWidths[c] + 0.01) return (row * Head.Columns + c, repeated);
                x += Head.ColumnWidths[c];
            }
        }
        return null;
    }
}
