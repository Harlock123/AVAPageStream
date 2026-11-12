namespace AVAPageStream.Core;

/// <summary>Stories and the frames they flow through: the areas text fills, and linking frames together.</summary>
public static class Chains
{
    /// <summary>The frames a story flows through, in order (frames that no longer exist are left out).</summary>
    public static List<TextFrame> FramesOf(Document doc, Story story) =>
        story.Frames.Select(id => doc.Find(id)).OfType<TextFrame>().ToList();

    /// <summary>
    /// Every column of every frame in the story's chain, in the order text fills them, each with the wrap boxes it
    /// must keep out of. Wrap affects the text under it: a frame keeps out of the wrapping objects drawn above it on
    /// its page, and (on a page) of every wrapping object on its master, which is drawn under the page. A text frame
    /// over a wrapping box, such as a pull quote on its tinted panel, keeps its own text.
    /// </summary>
    public static List<Area> Areas(Document doc, Story story)
    {
        if (story.NoteOf is not null) return []; // a footnote is set where its story's layout puts it
        if (story.Table is { } tableId)
        {
            // A table cell's story is set in its cell, less the inset.
            if (doc.Find(tableId) is not TableObject table) return [];
            var i = table.Cells.IndexOf(story.Id);
            if (i < 0) return [];
            var cell = table.CellBox(i);
            var inner = new Box(cell.X + table.CellInset, cell.Y + table.CellInset, Math.Max(0, cell.W - 2 * table.CellInset), Math.Max(0, cell.H - 2 * table.CellInset));
            return [new Area(inner, table.CellId(i), 0)];
        }
        var own = story.Frames.ToHashSet();
        var areas = new List<Area>();
        foreach (var frame in FramesOf(doc, story))
        {
            var sheet = doc.SheetOf(frame.Id);
            // In drawing order, layers included; a hidden layer wraps nothing.
            var objects = sheet is null ? [] : Shapes.Flatten(doc.Ordered(sheet.Objects)).ToList();
            var wrapping = objects.Skip(objects.IndexOf(frame) + 1);
            if (sheet is Page page && doc.MasterFor(page) is { } master) wrapping = Shapes.Flatten(doc.Ordered(master.Objects)).Concat(wrapping);
            var hidden = (sheet?.Objects ?? []).Concat(sheet is Page pg && doc.MasterFor(pg) is { } m ? m.Objects : [])
                .Where(o => !doc.LayerOf(o).Visible).SelectMany(o => Shapes.Flatten([o])).Select(o => o.Id).ToHashSet();
            wrapping = wrapping.Where(o => !hidden.Contains(o.Id));
            // A turned frame sets its text in its own unturned frame, where the page's wrap boxes don't fall: it keeps its text whole.
            var keep = frame.Rotation != 0 ? [] : wrapping.Where(o => o.Wrap && !own.Contains(o.Id)).Select(Shapes.KeepOf).ToList();
            var column = 0;
            foreach (var box in frame.ColumnBoxes())
            {
                var mine = keep.Where(k => k.Bounds.Intersects(box)).ToList();
                areas.Add(new Area(box, frame.Id, column++, mine.Count > 0 ? mine : null));
            }
        }
        return areas;
    }

    /// <summary>
    /// The page a story's fields count from when it is set on its own: the page its first frame is on (from 1), or 0
    /// when that frame is on a master page (a master's text is set again for each page it is drawn under).
    /// </summary>
    public static int PageOf(Document doc, Story story)
    {
        if (story.Frames.Count == 0) return 0;
        var first = story.Frames[0];
        for (var i = 0; i < doc.Pages.Count; i++)
            if (doc.Pages[i].All.Any(o => o.Id == first)) return i + 1;
        return 0;
    }

    /// <summary>
    /// Makes <paramref name="to"/> follow <paramref name="from"/> in its story's chain. The frame joining must hold
    /// no text of its own (an empty frame, or one whose text has nowhere else to go is refused). Returns why not, or null.
    /// </summary>
    public static string? Link(Document doc, TextFrame from, TextFrame to)
    {
        if (from.Id == to.Id) return "A frame can't follow itself.";
        if (doc.StoryOf(from) is not { } story) return "That frame has no story.";
        if (to.StoryId == from.StoryId) return "Those frames are already in the same chain.";
        if (doc.StoryOf(to) is { } other)
        {
            if (other.Length > 0 && other.Frames.Count <= 1)
                return "The second frame already holds text. Clear it, or link from it instead.";
            other.Frames.Remove(to.Id);
            if (other.Frames.Count == 0) doc.Stories.Remove(other.Id);
        }
        to.StoryId = story.Id;
        story.Frames.Remove(to.Id);
        story.Frames.Insert(story.Frames.IndexOf(from.Id) + 1, to.Id);
        return null;
    }

    /// <summary>Takes a frame out of its chain and gives it a new empty story; the text flows on through the rest.</summary>
    public static string? Unlink(Document doc, TextFrame frame)
    {
        if (doc.StoryOf(frame) is not { } story || story.Frames.Count <= 1) return "That frame isn't linked to another.";
        story.Frames.Remove(frame.Id);
        NewStory(doc, frame, doc.Style(story.Paras[0].Style));
        return null;
    }

    /// <summary>Gives a frame a new, empty story of its own in a style.</summary>
    public static Story NewStory(Document doc, TextFrame frame, ParagraphStyle style)
    {
        var story = Story.Create(doc.NewId("story"), style);
        story.Frames.Add(frame.Id);
        doc.Stories[story.Id] = story;
        frame.StoryId = story.Id;
        return story;
    }

    /// <summary>The object an area of text is in: a text frame's id, or the table of a cell ("table#3").</summary>
    public static string ObjectOf(string areaId) => areaId.IndexOf('#') is var i and > 0 ? areaId[..i] : areaId;

    /// <summary>Takes a table that is being deleted away with its cells' stories.</summary>
    public static void Forget(Document doc, TableObject table)
    {
        foreach (var id in table.Cells) doc.Stories.Remove(id);
    }

    /// <summary>Takes a path that is being deleted away with the text on it.</summary>
    public static void Forget(Document doc, PathObject path)
    {
        if (path.StoryId is { } id) doc.Stories.Remove(id);
    }

    /// <summary>Takes a frame that is being deleted out of its chain; a story left with no frames goes too.</summary>
    public static void Forget(Document doc, TextFrame frame)
    {
        if (doc.StoryOf(frame) is not { } story) return;
        story.Frames.Remove(frame.Id);
        if (story.Frames.Count == 0) doc.Stories.Remove(story.Id);
    }
}
