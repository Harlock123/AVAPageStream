namespace AVAPageStream.Core;

/// <summary>
/// The document being edited and its undo history. Every change goes through <see cref="Change"/> (or
/// <see cref="Begin"/> and <see cref="Commit"/> for a drag), which keeps the document as it was beforehand as one
/// undo step. A step is a whole copy of the document but for its images, which every copy shares.
/// </summary>
public sealed class Editor
{
    private readonly List<(Document Doc, string Label, int Stamp)> _undo = [];
    private readonly List<(Document Doc, string Label, int Stamp)> _redo = [];
    private string? _mergeKey;
    private Document? _begun;
    private int _stamp;
    private int _nextStamp = 1;
    private int _savedStamp;

    public Editor(Document doc) => Doc = doc;

    /// <summary>The layer new objects go on, by name (null: the top layer).</summary>
    public string? CurrentLayer { get; set; }

    private string? NewLayer(Document d) => d.Layers.Any(l => l.Name == CurrentLayer) ? CurrentLayer : d.Layers.LastOrDefault()?.Name;

    public Document Doc { get; private set; }

    /// <summary>Raised after every change, undo and redo.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>Whether there are changes since the document was opened or last saved.</summary>
    public bool Dirty => _stamp != _savedStamp;

    public void MarkSaved() => _savedStamp = _stamp;

    /// <summary>
    /// Brings linked pictures up to date with their files; one undo step if any had changed. Returns how many.
    /// </summary>
    public int UpdateLinks(string? folder)
    {
        var changed = Links.Changed(Doc, folder);
        if (changed.Count == 0) return 0;
        var keys = changed.Select(c => (c.Id, c.Path, Key: Doc.Images.Add(c.Bytes))).ToList();
        Change("Update linked pictures", d =>
        {
            foreach (var (id, path, key) in keys)
                if (d.Find(id) is PictureObject p)
                {
                    // A protected PDF's new version keeps its password.
                    if (d.Passwords.TryGetValue(p.Image, out var password)) d.Passwords.TryAdd(key, password);
                    p.Image = key;
                    p.Source = path;
                }
        });
        return changed.Count;
    }

    /// <summary>Links a picture to the file it was placed from, or keeps a copy of it in the document. Returns why not, or null.</summary>
    public string? SetLinked(string pictureId, bool linked)
    {
        if (Doc.Find(pictureId) is not PictureObject p) return "That isn't a picture.";
        if (p.Linked == linked) return null;
        if (linked && p.Source is null) return "This picture wasn't placed from a file, so it can't be linked. Replace it from the file first.";
        if (!linked && Doc.Images.Get(p.Image) is null) return "The linked file is missing, so there is no copy to keep. Find the file first.";
        Change(linked ? "Link picture" : "Embed picture", d => { if (d.Find(pictureId) is PictureObject q) q.Linked = linked; });
        return null;
    }

    /// <summary>
    /// Puts a footnote's reference in a story at a place, as a superscript number, with a note of its own to write.
    /// Returns the note's story id, or null if the story can't take one (a footnote or a table's cell).
    /// </summary>
    public string? InsertFootnote(string storyId, int at, string text = "")
    {
        if (!Doc.Stories.TryGetValue(storyId, out var s) || s.NoteOf is not null || s.Table is not null || s.OnPath is not null) return null;
        return Change("Insert footnote", d =>
        {
            var story = d.Stories[storyId];
            var note = Footnotes.NewNote(d, storyId, text);
            var f = story.TypingFormat(at) with { Note = note.Id, Script = Script.Super, CharStyle = null };
            story.Insert(at, Fields.Footnote.ToString(), f);
            return note.Id;
        });
    }

    /// <summary>Sets text along a path (a story of its own, in the Body style), or returns why not.</summary>
    public string? PutTextOnPath(string pathId, string text = "Text on a path")
    {
        if (Doc.Find(pathId) is not PathObject p) return "Select a path or polyline drawn with the pen first.";
        if (p.StoryId is not null) return null;
        if (p.Nodes.Count < 2) return "The path needs at least two points.";
        Change("Text on path", d =>
        {
            var path = (PathObject)d.Find(pathId)!;
            var style = d.Style("Body");
            var story = Story.Create(d.NewId("story"), style);
            story.OnPath = pathId;
            story.Insert(0, text);
            d.Stories[story.Id] = story;
            path.StoryId = story.Id;
        });
        return null;
    }

    /// <summary>Takes the text off a path.</summary>
    public void TakeTextOffPath(string pathId)
    {
        if (Doc.Find(pathId) is not PathObject { StoryId: not null }) return;
        Change("Remove text from path", d =>
        {
            var path = (PathObject)d.Find(pathId)!;
            Chains.Forget(d, path);
            path.StoryId = null;
        });
    }

    /// <summary>Marks the document as having changes to save (a recovered one, which was never saved as it is).</summary>
    public void MarkUnsaved() => _savedStamp = -1;

    /// <summary>Which state of the document this is: it changes with every change, undo and redo.</summary>
    public int Stamp => _stamp;

    /// <summary>
    /// Makes a change as one undo step. Consecutive changes with the same <paramref name="merge"/> key (typing into
    /// one story, nudging) make one step between them.
    /// </summary>
    public T Change<T>(string label, Func<Document, T> change, string? merge = null)
    {
        if (merge is null || merge != _mergeKey || _undo.Count == 0) Record(label);
        _mergeKey = merge;
        var result = change(Doc);
        if (Footnotes.Any(Doc)) Footnotes.Tidy(Doc);
        _stamp = _nextStamp++;
        Changed?.Invoke();
        return result;
    }

    public void Change(string label, Action<Document> change, string? merge = null) =>
        Change<bool>(label, d => { change(d); return true; }, merge);

    /// <summary>
    /// Makes a change that may refuse (returning why): it is tried on a copy first, and only made, as one undo step,
    /// if the copy took it.
    /// </summary>
    public string? TryChange(string label, Func<Document, string?> change)
    {
        if (change(Doc.Clone()) is { } why) return why;
        return Change(label, change);
    }

    /// <summary>Starts a change made a bit at a time, such as a drag: the document can be changed directly until <see cref="Commit"/> or <see cref="Cancel"/>.</summary>
    public void Begin() => _begun = Doc.Clone();

    public bool Busy => _begun is not null;

    /// <summary>Ends a change started with <see cref="Begin"/> as one undo step, if it changed anything.</summary>
    public void Commit(string label)
    {
        if (_begun is not { } before) return;
        _begun = null;
        if (before.ToJson(false) == Doc.ToJson(false)) return;
        _undo.Add((before, label, _stamp));
        _redo.Clear();
        _mergeKey = null;
        _stamp = _nextStamp++;
        Changed?.Invoke();
    }

    /// <summary>Puts the document back as it was at <see cref="Begin"/>.</summary>
    public void Cancel()
    {
        if (_begun is not { } before) return;
        _begun = null;
        Doc = before;
        Changed?.Invoke();
    }

    /// <summary>Tells listeners the document changed during a <see cref="Begin"/> without recording anything.</summary>
    public void Preview() => Changed?.Invoke();

    /// <summary>Ends any merging, so the next change is a step of its own.</summary>
    public void Break() => _mergeKey = null;

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (doc, label, stamp) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((Doc, label, _stamp));
        Doc = doc;
        _stamp = stamp;
        _mergeKey = null;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (doc, label, stamp) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((Doc, label, _stamp));
        Doc = doc;
        _stamp = stamp;
        _mergeKey = null;
        Changed?.Invoke();
    }

    private void Record(string label)
    {
        _undo.Add((Doc.Clone(), label, _stamp));
        _redo.Clear();
    }

    // ---- Objects --------------------------------------------------------------------------------------------

    /// <summary>Puts a new object on top of a sheet, giving it an id (and a text frame its own story).</summary>
    public T Add<T>(SheetRef sheet, T obj, string? label = null) where T : PageObject => Change(label ?? "New " + obj.Kind.ToLowerInvariant(), d =>
    {
        sheet.Resolve(d).Objects.Add(obj);
        obj.Id = d.NewId(obj is TextFrame ? "frame" : "object");
        obj.Layer ??= NewLayer(d);
        if (obj is TextFrame frame && !d.Stories.ContainsKey(frame.StoryId)) Chains.NewStory(d, frame, d.Style("Body"));
        return obj;
    });

    /// <summary>
    /// Deletes objects (locked ones stay), from groups too: text frames leave their chains, a group left empty goes,
    /// and the others shrink to what is left in them.
    /// </summary>
    public int Delete(IReadOnlyCollection<string> ids)
    {
        var doomed = ids.Select(Doc.Find).OfType<PageObject>().Where(o => !o.Locked).Select(o => o.Id).ToHashSet();
        if (doomed.Count == 0) return 0;
        Change(doomed.Count == 1 ? "Delete" : $"Delete {doomed.Count} objects", d =>
        {
            // An anchored object goes with its marker.
            foreach (var story in d.Stories.Values)
                foreach (var o in story.Anchored.Where(o => doomed.Contains(o.Id)).ToList())
                    if (story.AnchorIndex(o.Id) is var at and >= 0) story.Delete(at, 1);
                    else story.Anchored.Remove(o);
            foreach (var sheet in d.Sheets)
            {
                var going = sheet.All.Where(o => doomed.Contains(o.Id)).ToList();
                foreach (var frame in Shapes.Flatten(going).OfType<TextFrame>()) Chains.Forget(d, frame);
                foreach (var table in Shapes.Flatten(going).OfType<TableObject>()) Chains.Forget(d, table);
                foreach (var path in Shapes.Flatten(going).OfType<PathObject>()) Chains.Forget(d, path);
                foreach (var o in going)
                {
                    var around = Shapes.GroupsAround(sheet.Objects, o.Id);
                    Shapes.ContainerOf(sheet.Objects, o.Id)?.Remove(o);
                    foreach (var g in around) g.Fit();
                }
                RemoveEmptyGroups(sheet.Objects);
            }
        });
        return doomed.Count;
    }

    private static void RemoveEmptyGroups(List<PageObject> list)
    {
        foreach (var g in list.OfType<GroupObject>().ToList())
        {
            RemoveEmptyGroups(g.Children);
            if (g.Children.Count == 0) list.Remove(g);
        }
    }

    /// <summary>Copies objects onto the same sheet, offset; a copied text frame gets a copy of its whole story. Returns the new ids.</summary>
    public List<string> Duplicate(IReadOnlyCollection<string> ids, double offset = 12) => Change("Duplicate", d =>
    {
        var made = new List<string>();
        foreach (var id in ids)
        {
            if (d.Find(id) is not { } obj || d.SheetOf(id) is not { } sheet) continue;
            var copy = Copy(d, obj);
            copy.MoveBy(offset, offset);
            sheet.Objects.Add(copy);
            made.Add(copy.Id);
        }
        return made;
    });

    /// <summary>
    /// A copy of an object for document <paramref name="d"/>, with new ids (for everything in a group too); each text
    /// frame's story is copied with it, from <paramref name="from"/> (the document the object came from, when not
    /// <paramref name="d"/>).
    /// </summary>
    public static PageObject Copy(Document d, PageObject obj, Document? from = null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(obj, Document.Json);
        var copy = System.Text.Json.JsonSerializer.Deserialize<PageObject>(json, Document.Json)!;
        copy.Locked = false;
        foreach (var (o, original) in Shapes.Flatten([copy]).Zip(Shapes.Flatten([obj])))
        {
            o.Id = d.NewId(o is TextFrame ? "frame" : "object");
            if (o is PictureObject pic && from is not null && from.Passwords.TryGetValue(pic.Image, out var password)) d.Passwords.TryAdd(pic.Image, password);
            if (o is TextFrame frame && original is TextFrame f && (from ?? d).StoryOf(f) is { } story)
            {
                var copied = System.Text.Json.JsonSerializer.Deserialize<Story>(System.Text.Json.JsonSerializer.Serialize(story, Document.Json), Document.Json)!;
                copied.Id = d.NewId("story");
                copied.Frames = [frame.Id];
                d.Stories[copied.Id] = copied;
                frame.StoryId = copied.Id;
                Footnotes.Bring(from ?? d, d, copied);
            }
            if (o is PathObject path && original is PathObject op && op.StoryId is { } sid && (from ?? d).Stories.GetValueOrDefault(sid) is { } onPath)
            {
                var copied = System.Text.Json.JsonSerializer.Deserialize<Story>(System.Text.Json.JsonSerializer.Serialize(onPath, Document.Json), Document.Json)!;
                copied.Id = d.NewId("story");
                copied.OnPath = path.Id;
                d.Stories[copied.Id] = copied;
                path.StoryId = copied.Id;
            }
            else if (o is PathObject bare) bare.StoryId = null;
            if (o is TableObject table && original is TableObject t)
            {
                // A copy is a table of its own: of the whole table, for a frame it runs on into.
                if (ReferenceEquals(o, copy) && t.Continues is { } headId && (from ?? d).Find(headId) is TableObject head)
                {
                    var whole = (TableObject)Copy(d, head, from);
                    whole.MoveBy(t.X - head.X, t.Y - head.Y);
                    return whole;
                }
                table.Next = null;
                table.Continues = null;
                table.Fit();
                // A copied table gets copies of its cells' stories.
                for (var i = 0; i < table.Cells.Count; i++)
                {
                    if ((from ?? d).Stories.GetValueOrDefault(t.Cells[i]) is not { } cell) { table.Cells[i] = Tables.NewCell(d, table).Id; continue; }
                    var copied = System.Text.Json.JsonSerializer.Deserialize<Story>(System.Text.Json.JsonSerializer.Serialize(cell, Document.Json), Document.Json)!;
                    copied.Id = d.NewId("story");
                    copied.Table = table.Id;
                    copied.Frames = [table.Id];
                    d.Stories[copied.Id] = copied;
                    table.Cells[i] = copied.Id;
                }
            }
        }
        return copy;
    }

    /// <summary>Pastes copies of objects from another document (the clipboard's) on top of a sheet. Returns the new ids.</summary>
    public List<string> Paste(SheetRef sheet, Document from, IReadOnlyList<string> ids, double offset) => Change("Paste", d =>
    {
        var made = new List<string>();
        foreach (var id in ids)
        {
            if (from.Find(id) is not { } obj) continue;
            var copy = Copy(d, obj, from);
            copy.MoveBy(offset, offset);
            if (!d.Layers.Any(l => l.Name == copy.Layer)) copy.Layer = NewLayer(d); // pasted from a document without its layer
            sheet.Resolve(d).Objects.Add(copy);
            made.Add(copy.Id);
        }
        return made;
    });

    /// <summary>
    /// Groups objects on one sheet (two or more, none in a group already) into a group drawn where the top-most of them
    /// was. Returns its id, or null with nothing done.
    /// </summary>
    public string? Group(IReadOnlyCollection<string> ids)
    {
        if (ids.Count < 2 || Doc.SheetOf(ids.First()) is not { } first) return null;
        if (ids.Any(id => !first.Objects.Any(o => o.Id == id))) return null;
        return Change("Group", d =>
        {
            var sheet = d.SheetOf(ids.First())!;
            var chosen = sheet.Objects.Where(o => ids.Contains(o.Id)).ToList();
            var at = sheet.Objects.IndexOf(chosen[^1]) - (chosen.Count - 1);
            sheet.Objects.RemoveAll(chosen.Contains);
            var group = new GroupObject { Id = d.NewId("object"), Children = chosen };
            group.Fit();
            sheet.Objects.Insert(Math.Clamp(at, 0, sheet.Objects.Count), group);
            return group.Id;
        });
    }

    /// <summary>Takes groups apart, leaving what they held in their place. Returns the ids of what was in them.</summary>
    public List<string> Ungroup(IReadOnlyCollection<string> ids)
    {
        var groups = ids.Select(Doc.Find).OfType<GroupObject>().ToList();
        if (groups.Count == 0) return [];
        return Change("Ungroup", d =>
        {
            var freed = new List<string>();
            foreach (var g in groups.Select(g => (GroupObject)d.Find(g.Id)!))
            {
                var sheet = d.SheetOf(g.Id)!;
                var list = Shapes.ContainerOf(sheet.Objects, g.Id)!;
                var at = list.IndexOf(g);
                list.RemoveAt(at);
                list.InsertRange(at, g.Children);
                freed.AddRange(g.Children.Select(c => c.Id));
            }
            return freed;
        });
    }

    /// <summary>Draws a table of so many rows and columns in a box on a sheet.</summary>
    public TableObject AddTable(SheetRef sheet, Box box, int rows, int columns) => Change("New table", d =>
    {
        var t = Tables.Make(d, box, rows, columns);
        t.Layer = NewLayer(d);
        sheet.Resolve(d).Objects.Add(t);
        return t;
    });

    /// <summary>Changes a table as one step: rows and columns put in or taken out, fitted, or resized.</summary>
    /// <summary>Makes a table run on into a new frame on the next page. Returns the new frame's id, or null.</summary>
    public string? RunOnTable(string tableId)
    {
        if (Doc.Find(tableId) is not TableObject t || Tables.Head(Doc, t) is not { } head) return null;
        var last = Tables.Chain(Doc, head)[^1];
        if (!Doc.Pages.Any(p => p.All.Any(o => o.Id == last.Id))) return null; // on a master
        return Change("Run table on", d => Tables.RunOn(d, (TableObject)d.Find(tableId)!)?.Id);
    }

    public void ChangeTable(string tableId, string label, Action<Document, TableObject> change) => Change(label, d =>
    {
        if (d.Find(tableId) is not TableObject t) return;
        change(d, t);
        if (Tables.Head(d, t) is { } head) Tables.Sync(d, head);
    });

    public enum Order { Front, Forward, Backward, Back }

    /// <summary>Moves objects up or down the order they are drawn in, within their sheet or group.</summary>
    public void Arrange(IReadOnlyCollection<string> ids, Order order) => Change(order switch
    {
        Order.Front => "Bring to front",
        Order.Forward => "Bring forward",
        Order.Backward => "Send backward",
        _ => "Send to back",
    }, d =>
    {
        var lists = ids.Select(id => d.SheetOf(id) is { } sh ? Shapes.ContainerOf(sh.Objects, id) : null).OfType<List<PageObject>>().Distinct();
        foreach (var list in lists)
        {
            var chosen = list.Where(o => ids.Contains(o.Id)).ToList();
            switch (order)
            {
                case Order.Front:
                    list.RemoveAll(chosen.Contains);
                    list.AddRange(chosen);
                    break;
                case Order.Back:
                    list.RemoveAll(chosen.Contains);
                    list.InsertRange(0, chosen);
                    break;
                case Order.Forward:
                    for (var i = list.Count - 2; i >= 0; i--)
                        if (chosen.Contains(list[i]) && !chosen.Contains(list[i + 1])) (list[i], list[i + 1]) = (list[i + 1], list[i]);
                    break;
                case Order.Backward:
                    for (var i = 1; i < list.Count; i++)
                        if (chosen.Contains(list[i]) && !chosen.Contains(list[i - 1])) (list[i], list[i - 1]) = (list[i - 1], list[i]);
                    break;
            }
        }
    });

    public enum Alignment { Left, Centre, Right, Top, Middle, Bottom }

    /// <summary>
    /// Lines objects up with the edge or centre of all of them together (or of the page, for one object), by the
    /// box each covers (a rotated object's turned box).
    /// </summary>
    public void AlignObjects(IReadOnlyCollection<string> ids, Alignment how)
    {
        var objects = ids.Select(Doc.Find).OfType<PageObject>().Where(o => !o.Locked).ToList();
        if (objects.Count == 0) return;
        var target = objects.Count == 1 ? Doc.Setup.Page : Box.Union(objects.Select(Shapes.Extent));
        Change("Align", d =>
        {
            foreach (var o in objects.Select(o => d.Find(o.Id)!))
            {
                var e = Shapes.Extent(o);
                var (dx, dy) = how switch
                {
                    Alignment.Left => (target.X - e.X, 0.0),
                    Alignment.Centre => (target.CentreX - e.CentreX, 0.0),
                    Alignment.Right => (target.Right - e.Right, 0.0),
                    Alignment.Top => (0.0, target.Y - e.Y),
                    Alignment.Middle => (0.0, target.CentreY - e.CentreY),
                    _ => (0.0, target.Bottom - e.Bottom),
                };
                o.MoveBy(dx, dy);
                d.Refit(o.Id);
            }
        });
    }

    /// <summary>
    /// Spaces three or more objects evenly across (or down) the room between the outermost two, which stay where they
    /// are. Returns why not, or null.
    /// </summary>
    public string? Distribute(IReadOnlyCollection<string> ids, bool across)
    {
        var objects = ids.Select(Doc.Find).OfType<PageObject>().Where(o => !o.Locked).ToList();
        if (objects.Count < 3) return "Select three or more objects (not locked) to space evenly.";
        var boxes = objects.ToDictionary(o => o.Id, Shapes.Extent);
        var sorted = across ? objects.OrderBy(o => boxes[o.Id].X).ToList() : objects.OrderBy(o => boxes[o.Id].Y).ToList();
        var span = across ? sorted.Max(o => boxes[o.Id].Right) - boxes[sorted[0].Id].X : sorted.Max(o => boxes[o.Id].Bottom) - boxes[sorted[0].Id].Y;
        var gap = (span - sorted.Sum(o => across ? boxes[o.Id].W : boxes[o.Id].H)) / (sorted.Count - 1);
        Change(across ? "Space across" : "Space down", d =>
        {
            var at = across ? boxes[sorted[0].Id].X : boxes[sorted[0].Id].Y;
            foreach (var o in sorted.Select(o => d.Find(o.Id)!))
            {
                var b = boxes[o.Id];
                if (across) { o.MoveBy(Math.Round(at - b.X, 2), 0); at += b.W + gap; }
                else { o.MoveBy(0, Math.Round(at - b.Y, 2)); at += b.H + gap; }
                d.Refit(o.Id);
            }
        });
        return null;
    }

    // ---- Pages ----------------------------------------------------------------------------------------------

    /// <summary>Adds a page at <paramref name="index"/> using the same master as its neighbour.</summary>
    public int AddPage(int index)
    {
        index = Math.Clamp(index, 0, Doc.Pages.Count);
        var master = Doc.Pages.Count > 0 ? Doc.Pages[Math.Clamp(index - 1, 0, Doc.Pages.Count - 1)].Master : Doc.Masters.FirstOrDefault()?.Name;
        Change("Add page", d => d.Pages.Insert(index, new Page { Master = master }));
        return index;
    }

    /// <summary>Deletes a page and everything on it (never the last page).</summary>
    public string? DeletePage(int index)
    {
        if (Doc.Pages.Count <= 1) return "A document needs at least one page.";
        if (index < 0 || index >= Doc.Pages.Count) return "There is no such page.";
        Change("Delete page", d =>
        {
            var page = d.Pages[index];
            foreach (var frame in page.All.OfType<TextFrame>().ToList()) Chains.Forget(d, frame);
            foreach (var table in page.All.OfType<TableObject>().ToList()) Chains.Forget(d, table);
            foreach (var path in page.All.OfType<PathObject>().ToList()) Chains.Forget(d, path);
            d.Pages.RemoveAt(index);
        });
        return null;
    }

    public void MovePage(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Doc.Pages.Count || to >= Doc.Pages.Count) return;
        Change("Move page", d =>
        {
            var page = d.Pages[from];
            d.Pages.RemoveAt(from);
            d.Pages.Insert(to, page);
        });
    }

    // ---- Layers ----------------------------------------------------------------------------------------------

    /// <summary>Adds a layer above the current one (or at the top), and makes it current.</summary>
    public string AddLayer(string? name = null)
    {
        var n = Doc.Layers.Count + 1;
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        while (name is null || Doc.Layers.Any(l => l.Name == name)) name = $"Layer {n++}";
        var at = Doc.Layers.FindIndex(l => l.Name == CurrentLayer) is var i and >= 0 ? i + 1 : Doc.Layers.Count;
        var made = name;
        Change("Add layer", d => d.Layers.Insert(Math.Min(at, d.Layers.Count), new Layer(made)));
        CurrentLayer = made;
        return made;
    }

    public string? RenameLayer(string old, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Type the layer's new name first.";
        if (name == old) return null;
        if (Doc.Layers.Any(l => l.Name == name)) return $"There is already a layer called {name}.";
        var i = Doc.Layers.FindIndex(l => l.Name == old);
        if (i < 0) return $"There is no layer called {old}.";
        var bottom = i == 0;
        Change("Rename layer", d =>
        {
            d.Layers[i] = d.Layers[i] with { Name = name };
            foreach (var sheet in d.Sheets)
                foreach (var o in sheet.Objects)
                    if (o.Layer == old || (bottom && !d.Layers.Any(l => l.Name == o.Layer))) o.Layer = name;
        });
        if (CurrentLayer == old) CurrentLayer = name;
        return null;
    }

    /// <summary>Deletes a layer (never the last); what was on it goes onto the layer below it (or above, for the bottom one).</summary>
    public string? DeleteLayer(string name)
    {
        if (Doc.Layers.Count <= 1) return "A document needs at least one layer.";
        var i = Doc.Layers.FindIndex(l => l.Name == name);
        if (i < 0) return $"There is no layer called {name}.";
        var onto = Doc.Layers[i == 0 ? 1 : i - 1].Name;
        Change("Delete layer " + name, d =>
        {
            foreach (var sheet in d.Sheets)
                foreach (var o in sheet.Objects.Where(o => d.LayerIndex(o) == i)) o.Layer = onto;
            d.Layers.RemoveAt(i);
        });
        if (CurrentLayer == name) CurrentLayer = onto;
        return null;
    }

    /// <summary>Moves a layer up (toward the top, drawn later) or down the list.</summary>
    public void MoveLayer(string name, int delta)
    {
        var i = Doc.Layers.FindIndex(l => l.Name == name);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Doc.Layers.Count) return;
        Change(delta > 0 ? "Move layer up" : "Move layer down", d =>
        {
            // Name every object's layer first, so the bottom layer's unnamed objects stay on it.
            foreach (var sheet in d.Sheets)
                foreach (var o in sheet.Objects) o.Layer = d.LayerOf(o).Name;
            (d.Layers[i], d.Layers[j]) = (d.Layers[j], d.Layers[i]);
        });
    }

    /// <summary>Shows or hides, locks or unlocks a layer, or has it print or not.</summary>
    public void SetLayer(string name, Func<Layer, Layer> change) => Change("Layer " + name, d =>
    {
        var i = d.Layers.FindIndex(l => l.Name == name);
        if (i >= 0) d.Layers[i] = change(d.Layers[i]) with { Name = name };
    });

    /// <summary>Puts objects (on top of their sheets) onto a layer.</summary>
    public void MoveToLayer(IReadOnlyCollection<string> ids, string layer) => Change("Move to layer " + layer, d =>
    {
        foreach (var sheet in d.Sheets)
            foreach (var o in sheet.Objects.Where(o => ids.Contains(o.Id))) o.Layer = layer;
    });

    /// <summary>Starts a section of page numbering on a page (or, with null, has it go on from the page before).</summary>
    public void SetSection(int page, PageNumbering? section) =>
        Change(section is null ? "No section" : "Page numbering", d => d.Pages[page].Section = section);

    /// <summary>Adds a master page with a name no other has.</summary>
    public string AddMaster()
    {
        var n = 0;
        string name;
        do name = $"{(char)('A' + n++ % 26)}{(n > 26 ? (n / 26).ToString() : "")}-Master";
        while (Doc.Masters.Any(m => m.Name == name));
        Change("Add master page", d => d.Masters.Add(new MasterPage { Name = name, Left = d.Setup.Facing ? new MasterSide() : null }));
        return name;
    }

    public string? RenameMaster(string old, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "A master page needs a name.";
        if (name == old) return null;
        if (Doc.Masters.Any(m => m.Name == name)) return $"There is already a master page called {name}.";
        Change("Rename master page", d =>
        {
            if (d.Master(old) is { } m) m.Name = name;
            foreach (var p in d.Pages.Where(p => p.Master == old)) p.Master = name;
        });
        return null;
    }

    // ---- Text -----------------------------------------------------------------------------------------------

    /// <summary>Replaces a stretch of a story's text (typing, deleting, pasting). Returns where the caret goes.</summary>
    public int ReplaceText(string storyId, int start, int length, string text, CharFormat? format = null, string? merge = null)
    {
        if (!Doc.Stories.ContainsKey(storyId)) return start;
        return Change(text.Length == 0 ? "Delete text" : "Typing", d =>
        {
            var story = d.Stories[storyId];
            var f = format ?? story.TypingFormat(start);
            story.Delete(start, length);
            story.Insert(start, text, f);
            return start + text.Replace("\r\n", "\n").Length;
        }, merge);
    }

    /// <summary>Pastes a styled stretch of text over a selection. Returns where the caret goes: after it.</summary>
    public int PasteFragment(string storyId, int start, int length, Fragment fragment) => Change("Paste", d =>
    {
        var story = d.Stories[storyId];
        story.Delete(start, length);
        story.InsertFragment(start, Anchors.Rekey(d, fragment)); // its anchored objects as new ones
        return start + fragment.Text.Length;
    });

    /// <summary>Replaces every match of a search in one step, each replacement in the format of the text it replaces. Returns how many.</summary>
    public int ReplaceAll(string query, string replacement, FindOptions options)
    {
        var matches = Search.FindAll(Doc, query, options);
        if (matches.Count == 0) return 0;
        Change($"Replace {matches.Count}", d =>
        {
            // Each one's replacement worked out before any changes (a pattern's groups come from the text as found).
            var withs = matches.ToDictionary(m => m, m => Search.Replacement(d, m, query, replacement, options));
            foreach (var group in matches.GroupBy(m => m.StoryId))
            {
                var story = d.Stories[group.Key];
                foreach (var m in group.OrderByDescending(m => m.Start)) Swap(story, m.Start, m.Length, withs[m]);
            }
        });
        return matches.Count;
    }

    /// <summary>Replaces one stretch of text, keeping the format of its first character. Returns where the caret goes.</summary>
    public int ReplaceOne(Match m, string replacement) => Change("Replace", d => Swap(d.Stories[m.StoryId], m.Start, m.Length, replacement));

    private static int Swap(Story story, int start, int length, string replacement)
    {
        var format = story.FormatAt(start);
        story.Delete(start, length);
        story.Insert(start, replacement, format);
        return start + replacement.Replace("\r\n", "\n").Length;
    }

    /// <summary>
    /// Places paragraphs read from a file into a story at <paramref name="index"/>, as paragraphs of their own, each
    /// in its style with its bold and italic kept, in one step. Returns where the caret goes: after them.
    /// </summary>
    public int Place(string storyId, int index, IReadOnlyList<PlacedPara> paras)
    {
        if (paras.Count == 0 || !Doc.Stories.ContainsKey(storyId)) return index;
        return Change("Place text", d => PlaceInto(d, storyId, index, paras));
    }

    private static int PlaceInto(Document d, string storyId, int index, IReadOnlyList<PlacedPara> paras)
    {
        var story = d.Stories[storyId];
        index = Math.Clamp(index, 0, story.Length);
        var before = index > 0 && story.Text[index - 1] != '\n';
        var after = index < story.Length && story.Text[index] != '\n';
        var body = string.Join("\n", paras.Select(p => p.Text));
        story.Insert(index, (before ? "\n" : "") + body + (after ? "\n" : ""), d.Style("Body").Char);
        var at = index + (before ? 1 : 0);
        foreach (var para in paras)
        {
            story.ApplyStyle(at, 0, d.Style(para.Style));
            if (para.LeftIndent != 0 || para.FirstIndent != 0)
                story.FormatParas(at, 0, f => f with { LeftIndent = para.LeftIndent, FirstIndent = para.FirstIndent });
            var run = at;
            foreach (var r in para.Runs)
            {
                // Bold and italic go in as the Strong and Emphasis character styles where the document has them.
                var style = r.Bold && r.Italic ? null : r.Bold ? d.CharStyle("Strong") : r.Italic ? d.CharStyle("Emphasis") : null;
                if (style is not null) story.ApplyCharStyle(run, r.Text.Length, style, d.CharStyle, d.Style);
                else if (r.Bold || r.Italic) story.FormatChars(run, r.Text.Length, f => f with { Bold = f.Bold || r.Bold, Italic = f.Italic || r.Italic });
                run += r.Text.Length;
            }
            at += para.Text.Length + 1;
        }
        return index + (before ? 1 : 0) + body.Length;
    }

    /// <summary>
    /// Puts a table of contents of the paragraphs in <paramref name="styles"/> into a story at <paramref name="index"/>,
    /// as one step: a "Contents" style for each level (made, or remade, with its page-number tab at
    /// <paramref name="width"/>), then a paragraph an entry. With the place in a contents already, it is made again
    /// there instead. Gives where it ends, or -1 if there is nothing to list.
    /// </summary>
    public int InsertContents(string storyId, int index, IReadOnlyList<string> styles, double width, ITextMeasurer measurer)
    {
        if (!Doc.Stories.ContainsKey(storyId) || Contents.Find(Doc, styles, measurer).Count == 0) return -1;
        var existing = GeneratedLists.At(Doc, storyId, index) is { Kind: ListKind.Contents } l ? l.Id : null;
        return Change("Table of contents", d =>
        {
            var list = new GeneratedList(existing ?? d.NewId("list"), ListKind.Contents, [.. styles], width);
            return Settle(d, list, PutList(d, list, storyId, index, measurer), measurer);
        });
    }

    /// <summary>The paragraph styles a list is set in, made or remade.</summary>
    private static void ListStyles(Document d, GeneratedList list)
    {
        var styles = list.Kind == ListKind.Index ? Indexes.MakeStyles(d).ToList()
            : Enumerable.Range(0, list.Styles.Count).Select(level => Contents.MakeStyle(d, level, list.Width)).ToList();
        foreach (var style in styles)
        {
            var i = d.Styles.FindIndex(s => s.Name == style.Name);
            if (i >= 0) d.Styles[i] = style;
            else d.Styles.Add(style);
        }
    }

    /// <summary>
    /// Makes a list (again, where it is, if the document has it; otherwise at a place in a story), and remembers it.
    /// Gives where its text ends.
    /// </summary>
    private static int PutList(Document d, GeneratedList list, string storyId, int index, ITextMeasurer measurer)
    {
        ListStyles(d, list);
        var paras = GeneratedLists.Make(d, list, measurer);
        if (GeneratedLists.Where(d, list) is var (id, start, end))
        {
            (storyId, index) = (id, start);
            d.Stories[id].Delete(start, end - start);
        }
        var story = d.Stories[storyId];
        index = Math.Clamp(index, 0, story.Length);
        var first = index > 0 && story.Text[index - 1] != '\n' ? index + 1 : index;
        var caret = PlaceInto(d, storyId, index, paras);
        story.Insert(first, Fields.ListStart.ToString(), story.FormatAt(first) with { List = list.Id });
        var i = d.Lists.FindIndex(x => x.Id == list.Id);
        if (i >= 0) d.Lists[i] = list;
        else d.Lists.Add(list);
        return caret + 1;
    }

    /// <summary>A list just put in, made again while that moved what it lists (a few times at most). Gives where it ends.</summary>
    private static int Settle(Document d, GeneratedList list, int caret, ITextMeasurer measurer)
    {
        for (var pass = 0; pass < 3 && GeneratedLists.IsStale(d, list, measurer); pass++) caret = PutList(d, list, "", 0, measurer);
        return caret;
    }

    /// <summary>
    /// Makes every contents and index that would say something different again, in place, as one step; round again
    /// (a few times at most) while that moves pages about. Forgets any whose text has been deleted. Gives how many
    /// were made again.
    /// </summary>
    public int UpdateLists(ITextMeasurer measurer)
    {
        bool Gone(Document d, GeneratedList l) => GeneratedLists.Where(d, l) is null;
        if (!Doc.Lists.Any(l => Gone(Doc, l) || GeneratedLists.IsStale(Doc, l, measurer))) return 0;
        var updated = new HashSet<string>();
        Change("Update contents and index", d =>
        {
            d.Lists.RemoveAll(l => Gone(d, l));
            for (var pass = 0; pass < 4; pass++)
            {
                var stale = d.Lists.Where(l => GeneratedLists.IsStale(d, l, measurer)).ToList();
                if (stale.Count == 0) break;
                foreach (var l in stale)
                {
                    PutList(d, l, "", 0, measurer);
                    updated.Add(l.Id);
                }
            }
        });
        return updated.Count;
    }

    public void FormatChars(string storyId, int start, int length, Func<CharFormat, CharFormat> change, string label = "Format text") =>
        Change(label, d => d.Stories[storyId].FormatChars(start, length, change));

    public void FormatParas(string storyId, int start, int length, Func<ParaFormat, ParaFormat> change, string label = "Format paragraph") =>
        Change(label, d => d.Stories[storyId].FormatParas(start, length, change));

    public void ApplyStyle(string storyId, int start, int length, string style) =>
        Change("Apply style " + style, d => d.Stories[storyId].ApplyStyle(start, length, d.Style(style), d.CharStyle));

    /// <summary>Puts a stretch of text in a character style, or (with null) out of one.</summary>
    public void ApplyCharStyle(string storyId, int start, int length, string? style) =>
        Change(style is null ? "No character style" : "Character style " + style,
            d => d.Stories[storyId].ApplyCharStyle(start, length, style is null ? null : d.CharStyle(style), d.CharStyle, d.Style));

    /// <summary>Makes or redefines a character style; the text in it changes with it.</summary>
    public string? DefineCharStyle(CharacterStyle style)
    {
        if (style.Name.Trim().Length == 0) return "A character style needs a name.";
        Change("Character style " + style.Name, d =>
        {
            var i = d.CharStyles.FindIndex(s => s.Name == style.Name);
            if (i < 0) { d.CharStyles.Add(style); return; }
            var was = d.CharStyles[i];
            d.CharStyles[i] = style;
            foreach (var story in d.Stories.Values) story.RestyleChars(was, style, d.Style);
        });
        return null;
    }

    public string? RenameCharStyle(string old, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Type the character style's new name first.";
        if (name == old) return null;
        if (Doc.CharStyles.Any(s => s.Name == name)) return $"There is already a character style called {name}.";
        if (Doc.CharStyle(old) is null) return $"There is no character style called {old}.";
        Change("Rename character style " + old, d =>
        {
            var i = d.CharStyles.FindIndex(s => s.Name == old);
            d.CharStyles[i] = d.CharStyles[i] with { Name = name };
            foreach (var story in d.Stories.Values)
                story.FormatChars(0, story.Length + 1, f => f.CharStyle == old ? f with { CharStyle = name } : f);
        });
        return null;
    }

    /// <summary>Deletes a character style; its text goes back to its paragraph style's settings.</summary>
    public string? DeleteCharStyle(string name)
    {
        if (Doc.CharStyle(name) is null) return $"There is no character style called {name}.";
        Change("Delete character style " + name, d =>
        {
            var style = d.CharStyle(name)!;
            foreach (var story in d.Stories.Values)
                for (var p = 0; p < story.ParagraphCount; p++)
                {
                    var (start, end) = story.ParagraphRange(p);
                    var basis = d.Style(story.Paras[p].Style).Char;
                    story.FormatChars(start, end - start + 1, f => f.CharStyle == name ? style.Set.Revert(f, basis) with { CharStyle = null } : f);
                }
            d.CharStyles.RemoveAll(s => s.Name == name);
        });
        return null;
    }

    /// <summary>Makes or replaces a style, and brings every paragraph using it up to date.</summary>
    public void DefineStyle(ParagraphStyle style) => Change("Define style " + style.Name, d =>
    {
        var i = d.Styles.FindIndex(s => s.Name == style.Name);
        if (i >= 0) d.Styles[i] = style;
        else d.Styles.Add(style);
        foreach (var story in d.Stories.Values) story.Restyle(style, d.CharStyle);
    });

    // ---- Swatches -------------------------------------------------------------------------------------------

    private const string SwatchColourHelp = "Type the swatch's colour as #RRGGBB, or as CMYK per cents: 0 100 100 0.";

    /// <summary>Adds a swatch: a colour typed as #RRGGBB or as CMYK, and whether it is a spot ink.</summary>
    public string? AddSwatch(string name, string colour, bool spot = false)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Contains('@') || name.EndsWith('%')) return "A swatch needs a name (without @, and not ending in %).";
        if (Doc.Swatches.Any(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return $"There is already a swatch called {name}.";
        if (!Colours.TryReadSwatch(colour, out var screen, out var inks)) return SwatchColourHelp;
        Change("Add swatch " + name, d => d.Swatches.Add(new Swatch(name, screen, inks, spot)));
        return null;
    }

    /// <summary>Gives a swatch a new colour (and spot or not): everything using it changes with it.</summary>
    public string? ChangeSwatch(string name, string colour, bool? spot = null)
    {
        if (!Colours.TryReadSwatch(colour, out var screen, out var inks)) return SwatchColourHelp;
        var i = Doc.Swatches.FindIndex(w => w.Name == name);
        if (i < 0) return $"There is no swatch called {name}.";
        Change("Change swatch " + name, d => d.Swatches[i] = d.Swatches[i] with { Colour = screen, Process = inks, Spot = spot ?? d.Swatches[i].Spot });
        return null;
    }

    /// <summary>Renames a swatch, and everything using it follows.</summary>
    public string? RenameSwatch(string old, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Contains('@')) return "A swatch needs a name (without @).";
        if (name == old) return null;
        if (Doc.Swatches.Any(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return $"There is already a swatch called {name}.";
        var i = Doc.Swatches.FindIndex(w => w.Name == old);
        if (i < 0) return $"There is no swatch called {old}.";
        Change("Rename swatch " + old, d =>
        {
            d.Swatches[i] = d.Swatches[i] with { Name = name };
            Recolour(d, c => Colours.IsRef(c) && Colours.Parse(c!) is var (n, tint) && n == old ? Colours.Ref(name, tint) : c);
        });
        return null;
    }

    /// <summary>Deletes a swatch; what used it keeps its colour as a plain value.</summary>
    public string? DeleteSwatch(string name)
    {
        var i = Doc.Swatches.FindIndex(w => w.Name == name);
        if (i < 0) return $"There is no swatch called {name}.";
        Change("Delete swatch " + name, d =>
        {
            var before = d.Swatches.ToList();
            d.Swatches.RemoveAt(i);
            Recolour(d, c => Colours.IsRef(c) && Colours.Parse(c!).Name == name ? Colours.Resolve(c, before) : c);
        });
        return null;
    }

    /// <summary>The colour text outlined from a selection is left in: clear, so it keeps its place and nothing moves.</summary>
    public const string Clear = "#00000000";

    /// <summary>
    /// Puts outlines of text above the frame they were made from: one path, or a group of a path for each colour.
    /// With <paramref name="replace"/>, they take the frame's place and its effects: the frame goes, or keeps only its
    /// own fill and stroke if it has them, its text gone. Otherwise <paramref name="clear"/>'s characters are made clear.
    /// Gives the new object's id.
    /// </summary>
    public string? AddOutlines(string frameId, IReadOnlyList<PathObject> paths, bool replace, (int Start, int Length)? clear = null)
    {
        if (paths.Count == 0 || Doc.Find(frameId) is not TextFrame) return null;
        return Change(replace ? "Text to outlines" : "Selection to outlines", d =>
        {
            var frame = (TextFrame)d.Find(frameId)!;
            var list = Shapes.ContainerOf(d.SheetOf(frameId)!.Objects, frameId)!;
            PageObject made;
            if (paths.Count == 1) made = paths[0];
            else
            {
                var group = new GroupObject { Children = [.. paths], Layer = frame.Layer };
                group.Fit();
                made = group;
            }
            foreach (var p in paths) p.Id = d.NewId("object");
            if (made is GroupObject) made.Id = d.NewId("object");
            made.Name = "Outlines";
            made.Layer = frame.Layer;
            list.Insert(list.IndexOf(frame) + 1, made);
            var story = d.Stories[frame.StoryId];
            if (replace)
            {
                (made.Opacity, made.Blend, made.Shadow) = (frame.Opacity, frame.Blend, frame.Shadow);
                if (frame.Fill is not null || frame.Gradient is not null || frame.Stroke is not null)
                {
                    story.Delete(0, story.Length);
                    (frame.Opacity, frame.Blend, frame.Shadow) = (1, BlendMode.Normal, null);
                }
                else
                {
                    Chains.Forget(d, frame);
                    var around = Shapes.GroupsAround(d.SheetOf(frameId)!.Objects, frameId);
                    list.Remove(frame);
                    foreach (var g in around) g.Fit();
                }
            }
            else if (clear is var (start, length))
                story.FormatChars(start, length, f => f with { Colour = Clear });
            return made.Id;
        });
    }

    /// <summary>
    /// Anchors copies of objects in a story at a place in its text, set in the line, one after another; the objects
    /// on the page go. Gives the anchored objects' ids.
    /// </summary>
    public List<string> AnchorAt(string storyId, int index, IReadOnlyList<PageObject> objects, bool removeFromPage = false) => Change("Anchor in text", d =>
    {
        var story = d.Stories[storyId];
        var made = new List<string>();
        index = Math.Clamp(index, 0, story.Length);
        foreach (var o in objects)
        {
            var copy = Copy(d, o);
            copy.Anchoring = new Anchoring();
            copy.Rotation = o.Rotation;
            story.Anchored.Add(copy);
            story.Insert(index, Fields.Anchor.ToString(), story.TypingFormat(index) with { Anchor = copy.Id });
            index++;
            made.Add(copy.Id);
            if (removeFromPage && d.SheetOf(o.Id) is { } sheet && d.Find(o.Id) is { } onPage) Shapes.ContainerOf(sheet.Objects, o.Id)?.Remove(onPage);
        }
        return made;
    });

    /// <summary>
    /// Anchors an object on the page to the text beside it: to the start of the line its top is level with (or the
    /// nearest), in a frame on the same page, placed by hand so it stays where it is. Gives why not, or null.
    /// </summary>
    public string? AnchorBeside(string objectId, Func<string, TextLayout?> layoutOf)
    {
        if (Doc.Find(objectId) is not { } o || Doc.SheetOf(objectId) is not { } sheet) return "Select an object on the page to anchor.";
        if (o is TextFrame || o is TableObject) return "A text frame or table can be anchored by pasting it into text.";
        var b = o.Bounds;
        (TextLayout Layout, Line Line, double Distance)? best = null;
        foreach (var frame in Shapes.Flatten(sheet.Objects).OfType<TextFrame>())
        {
            if (frame.Id == objectId || layoutOf(frame.StoryId) is not { } layout) continue;
            foreach (var line in layout.Lines)
            {
                if (layout.Areas[line.Area].FrameId != frame.Id) continue;
                var top = line.Baseline - line.Ascent;
                var dist = Math.Abs(top - b.Y) + Math.Max(0, Math.Max(layout.Areas[line.Area].Box.X - b.Right, b.X - layout.Areas[line.Area].Box.Right)) * 0.5;
                if (best is null || dist < best.Value.Distance) best = (layout, line, dist);
            }
        }
        if (best is not { } chosen) return "There is no text on this page to anchor it to.";
        var (lay, ln, _) = chosen;
        var col = lay.Areas[ln.Area].Box;
        var anchoring = new Anchoring(AnchorKind.Custom, 0, AnchorFrom.ColumnLeft, b.X - col.X, b.Y - (ln.Baseline - ln.Ascent));
        var storyId = lay.Story.Id;
        var at = ln.Start;
        Change("Anchor to text", d =>
        {
            var story = d.Stories[storyId];
            var live = d.Find(objectId)!;
            var sh = d.SheetOf(objectId)!;
            var around = Shapes.GroupsAround(sh.Objects, objectId);
            Shapes.ContainerOf(sh.Objects, objectId)?.Remove(live);
            foreach (var g in around) g.Fit();
            live.Anchoring = anchoring;
            story.Anchored.Add(live);
            story.Insert(at, Fields.Anchor.ToString(), story.TypingFormat(at) with { Anchor = live.Id });
        });
        return null;
    }

    /// <summary>Lets an anchored object go from its text: onto the page it is drawn on, where it is, its marker gone.</summary>
    public string? Release(string objectId, SheetRef? onto)
    {
        if (Doc.AnchoredIn(objectId) is not { } story || story.Anchored.FirstOrDefault(o => o.Id == objectId) is not { } o) return "That object isn't anchored in text.";
        if (onto is null) return "Its text isn't set anywhere, so it has no place on a page: make room for the text first.";
        Change("Release from text", d =>
        {
            var s = d.Stories[story.Id];
            var live = s.Anchored.First(x => x.Id == objectId);
            s.Anchored.Remove(live);
            if (s.AnchorIndex(objectId) is var at and >= 0) s.Delete(at, 1);
            live.Anchoring = null;
            onto.Value.Resolve(d).Objects.Add(live);
        });
        return null;
    }

    /// <summary>Changes how an anchored object is placed.</summary>
    public void Reanchor(string objectId, Anchoring anchoring) => Change("Anchored object", d => { if (d.Find(objectId) is { } o) o.Anchoring = anchoring; });

    /// <summary>Names a place in the text, for cross-references to point at. Gives why not, or null.</summary>
    public string? NamePlace(string storyId, int index, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Contains('|')) return "A place needs a name (without |).";
        if (References.Places(Doc).Any(p => p.Name == name)) return $"There is already a place called {name}.";
        Change("Name place " + name, d =>
        {
            var story = d.Stories[storyId];
            index = Math.Clamp(index, 0, story.Length);
            story.Insert(index, Fields.Place.ToString(), story.TypingFormat(index) with { Mark = name });
        });
        return null;
    }

    /// <summary>The name of the place at a paragraph's start, made (from its words) if it has none.</summary>
    public string PlaceForParagraph(string storyId, int paragraph)
    {
        var story = Doc.Stories[storyId];
        var (start, end) = story.ParagraphRange(paragraph);
        foreach (var p in References.Places(Doc))
            if (p.StoryId == storyId && p.Index >= start && p.Index <= end && story.Text[start..p.Index].All(Fields.IsField)) return p.Name;
        var name = References.NameFor(Doc, Fields.Expand(story.Text[start..end], null).Trim());
        NamePlace(storyId, start, name);
        return name;
    }

    /// <summary>Puts a cross-reference to a place in the text, showing its page, its text or both.</summary>
    public void InsertReference(string storyId, int index, string place, RefKind kind) => Change("Cross-reference", d =>
    {
        var story = d.Stories[storyId];
        index = Math.Clamp(index, 0, story.Length);
        story.Insert(index, Fields.Reference.ToString(), story.TypingFormat(index) with { Ref = References.Key(place, kind) });
    });

    /// <summary>Marks an index entry at a place in the text.</summary>
    public string? MarkIndex(string storyId, int index, IndexMark mark)
    {
        if (mark.Topic.Trim().Length == 0) return "An index entry needs a topic.";
        if ((mark.Topic + mark.Sub + mark.See).Contains('\t')) return "An index entry can't have a tab in it.";
        Change("Index entry", d =>
        {
            var story = d.Stories[storyId];
            index = Math.Clamp(index, 0, story.Length);
            story.Insert(index, Fields.IndexEntry.ToString(), story.TypingFormat(index) with { Index = mark.Key });
        });
        return null;
    }

    /// <summary>
    /// Puts an index made from the document's entries at a place in a story, with its styles, or (with the place in
    /// an index already) makes it again there. Gives where it ends, or -1 if nothing is marked for it.
    /// </summary>
    public int InsertIndex(string storyId, int index, ITextMeasurer measurer)
    {
        if (!Doc.Stories.ContainsKey(storyId) || Indexes.Entries(Doc, measurer).Count == 0) return -1;
        var existing = GeneratedLists.At(Doc, storyId, index) is { Kind: ListKind.Index } l ? l.Id : null;
        return Change("Index", d =>
        {
            var list = new GeneratedList(existing ?? d.NewId("list"), ListKind.Index, []);
            return Settle(d, list, PutList(d, list, storyId, index, measurer), measurer);
        });
    }

    /// <summary>Gives objects an object style's look, or (with null) takes them out of their style, keeping their look.</summary>
    public string? ApplyObjectStyle(IEnumerable<string> ids, string? style)
    {
        var list = ids.ToList();
        if (style is not null && Doc.ObjectStyle(style) is null) return $"There is no object style called {style}.";
        if (list.Count == 0) return "Select the objects to give the style to.";
        Change(style is null ? "No object style" : "Object style " + style, d =>
        {
            foreach (var id in list)
                if (d.Find(id) is { } o)
                {
                    if (style is null) o.ObjectStyle = null;
                    else d.ObjectStyle(style)!.ApplyTo(o);
                }
        });
        return null;
    }

    /// <summary>Makes or redefines an object style; every object in it takes its new look.</summary>
    public string? DefineObjectStyle(ObjectStyle style, IEnumerable<string>? andGive = null)
    {
        var name = style.Name.Trim();
        if (name.Length == 0) return "Type a name for the object style first.";
        style = style with { Name = name };
        var give = andGive?.ToList() ?? [];
        Change("Object style " + name, d =>
        {
            var i = d.ObjectStyles.FindIndex(s => s.Name == name);
            if (i < 0) d.ObjectStyles.Add(style);
            else d.ObjectStyles[i] = style;
            foreach (var o in d.AllObjects)
                if (o.ObjectStyle == name || give.Contains(o.Id)) style.ApplyTo(o);
        });
        return null;
    }

    public string? RenameObjectStyle(string old, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Type the object style's new name first.";
        if (name == old) return null;
        if (Doc.ObjectStyles.Any(s => s.Name == name)) return $"There is already an object style called {name}.";
        if (Doc.ObjectStyle(old) is null) return $"There is no object style called {old}.";
        Change("Rename object style " + old, d =>
        {
            var i = d.ObjectStyles.FindIndex(s => s.Name == old);
            d.ObjectStyles[i] = d.ObjectStyles[i] with { Name = name };
            foreach (var o in d.AllObjects)
                if (o.ObjectStyle == old) o.ObjectStyle = name;
        });
        return null;
    }

    /// <summary>Deletes an object style; its objects keep their look.</summary>
    public string? DeleteObjectStyle(string name)
    {
        if (Doc.ObjectStyle(name) is null) return $"There is no object style called {name}.";
        Change("Delete object style " + name, d =>
        {
            foreach (var o in d.AllObjects)
                if (o.ObjectStyle == name) o.ObjectStyle = null;
            d.ObjectStyles.RemoveAll(s => s.Name == name);
        });
        return null;
    }

    /// <summary>
    /// Changes colours everywhere: objects' fills, strokes, gradients and shadows, tables' headers, text, paragraphs'
    /// rules, and styles.
    /// </summary>
    private static void Recolour(Document d, Func<string?, string?> change)
    {
        string Must(string c) => change(c) ?? c;
        ParaRule? Rule(ParaRule? r) => r is null ? null : r with { Colour = Must(r.Colour) };
        ParaFormat Para(ParaFormat p) => p.RuleAbove is null && p.RuleBelow is null ? p : p with { RuleAbove = Rule(p.RuleAbove), RuleBelow = Rule(p.RuleBelow) };
        foreach (var o in d.AllObjects)
        {
            o.Fill = change(o.Fill);
            o.Stroke = change(o.Stroke);
            if (o is TableObject t) t.HeaderFill = change(t.HeaderFill);
            if (o.Shadow is { } s) o.Shadow = s with { Colour = Must(s.Colour) };
            if (o.Gradient is { } g) o.Gradient = g with { Stops = g.Stops.Select(st => st with { Colour = Must(st.Colour) }).ToList() };
        }
        foreach (var story in d.Stories.Values)
        {
            story.FormatChars(0, story.Length + 1, f => change(f.Colour) is { } c && c != f.Colour ? f with { Colour = c } : f);
            for (var k = 0; k < story.Paras.Count; k++) story.Paras[k] = Para(story.Paras[k]);
        }
        for (var k = 0; k < d.Styles.Count; k++)
        {
            var style = d.Styles[k];
            if (change(style.Char.Colour) is { } c && c != style.Char.Colour) style = style with { Char = style.Char with { Colour = c } };
            d.Styles[k] = style with { Para = Para(style.Para) };
        }
        for (var k = 0; k < d.ObjectStyles.Count; k++)
        {
            var os = d.ObjectStyles[k];
            d.ObjectStyles[k] = os with
            {
                Fill = change(os.Fill), Stroke = change(os.Stroke),
                Shadow = os.Shadow is { } s ? s with { Colour = Must(s.Colour) } : null,
                Gradient = os.Gradient is { } g ? g with { Stops = g.Stops.Select(st => st with { Colour = Must(st.Colour) }).ToList() } : null,
            };
        }
    }

    /// <summary>Makes the document facing or single-sided; a facing document's masters each get a left side.</summary>
    public void SetFacing(bool facing) => Change(facing ? "Facing pages" : "Single pages", d =>
    {
        d.Setup = d.Setup with { Facing = facing };
        if (facing) d.EnsureSides();
    });

    /// <summary>Gives a master margins and columns of its own (or, with null, back to the document's).</summary>
    public void SetMasterGuides(int master, Margins? margins, int? columns, double? gutter) => Change("Master margins", d =>
    {
        var m = d.Masters[master];
        (m.Margins, m.Columns, m.Gutter) = (margins, columns, gutter);
    });

    /// <summary>Renames a style, and every paragraph using it follows.</summary>
    public string? RenameStyle(string old, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Type the style's new name first.";
        if (name == old) return null;
        if (Doc.Styles.All(s => s.Name != old)) return $"There is no style called {old}.";
        if (Doc.Styles.Any(s => s.Name == name)) return $"There is already a style called {name}.";
        Change("Rename style " + old, d =>
        {
            var i = d.Styles.FindIndex(s => s.Name == old);
            d.Styles[i] = d.Styles[i] with { Name = name, Para = d.Styles[i].Para with { Style = name } };
            foreach (var story in d.Stories.Values)
                for (var p = 0; p < story.Paras.Count; p++)
                    if (story.Paras[p].Style == old) story.Paras[p] = story.Paras[p] with { Style = name };
        });
        return null;
    }

    public string? DeleteStyle(string name)
    {
        if (Doc.Styles.Count <= 1) return "A document needs at least one style.";
        Change("Delete style " + name, d => d.Styles.RemoveAll(s => s.Name == name));
        return null;
    }
}
