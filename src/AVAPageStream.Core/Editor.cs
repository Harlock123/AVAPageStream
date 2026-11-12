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
    /// Makes a change as one undo step. Consecutive changes with the same <paramref name="merge"/> key (typing into
    /// one story, nudging) make one step between them.
    /// </summary>
    public T Change<T>(string label, Func<Document, T> change, string? merge = null)
    {
        if (merge is null || merge != _mergeKey || _undo.Count == 0) Record(label);
        _mergeKey = merge;
        var result = change(Doc);
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
        if (obj is TextFrame frame && !d.Stories.ContainsKey(frame.StoryId)) Chains.NewStory(d, frame, d.Style("Body"));
        return obj;
    });

    /// <summary>Deletes objects (locked ones stay); text frames leave their chains.</summary>
    public int Delete(IReadOnlyCollection<string> ids)
    {
        var doomed = ids.Select(Doc.Find).OfType<PageObject>().Where(o => !o.Locked).Select(o => o.Id).ToHashSet();
        if (doomed.Count == 0) return 0;
        Change(doomed.Count == 1 ? "Delete" : $"Delete {doomed.Count} objects", d =>
        {
            foreach (var sheet in d.Sheets)
            {
                foreach (var frame in sheet.Objects.OfType<TextFrame>().Where(f => doomed.Contains(f.Id)).ToList()) Chains.Forget(d, frame);
                sheet.Objects.RemoveAll(o => doomed.Contains(o.Id));
            }
        });
        return doomed.Count;
    }

    /// <summary>Copies objects onto the same sheet, offset; a copied text frame gets a copy of its whole story. Returns the new ids.</summary>
    public List<string> Duplicate(IReadOnlyCollection<string> ids, double offset = 12) => Change("Duplicate", d =>
    {
        var made = new List<string>();
        foreach (var id in ids)
        {
            if (d.Find(id) is not { } obj || d.SheetOf(id) is not { } sheet) continue;
            var copy = Copy(d, obj);
            copy.X += offset;
            copy.Y += offset;
            sheet.Objects.Add(copy);
            made.Add(copy.Id);
        }
        return made;
    });

    /// <summary>
    /// A copy of an object for document <paramref name="d"/>, with a new id; a text frame's story is copied with it,
    /// from <paramref name="from"/> (the document the object came from, when not <paramref name="d"/>).
    /// </summary>
    public static PageObject Copy(Document d, PageObject obj, Document? from = null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(obj, Document.Json);
        var copy = System.Text.Json.JsonSerializer.Deserialize<PageObject>(json, Document.Json)!;
        copy.Id = d.NewId(copy is TextFrame ? "frame" : "object");
        copy.Locked = false;
        if (copy is TextFrame frame && obj is TextFrame original && (from ?? d).StoryOf(original) is { } story)
        {
            var copied = System.Text.Json.JsonSerializer.Deserialize<Story>(System.Text.Json.JsonSerializer.Serialize(story, Document.Json), Document.Json)!;
            copied.Id = d.NewId("story");
            copied.Frames = [frame.Id];
            d.Stories[copied.Id] = copied;
            frame.StoryId = copied.Id;
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
            copy.X += offset;
            copy.Y += offset;
            sheet.Resolve(d).Objects.Add(copy);
            made.Add(copy.Id);
        }
        return made;
    });

    public enum Order { Front, Forward, Backward, Back }

    /// <summary>Moves objects up or down the order they are drawn in on their sheet.</summary>
    public void Arrange(IReadOnlyCollection<string> ids, Order order) => Change(order switch
    {
        Order.Front => "Bring to front",
        Order.Forward => "Bring forward",
        Order.Backward => "Send backward",
        _ => "Send to back",
    }, d =>
    {
        foreach (var sheet in d.Sheets)
        {
            var list = sheet.Objects;
            var chosen = list.Where(o => ids.Contains(o.Id)).ToList();
            if (chosen.Count == 0) continue;
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

    /// <summary>Lines objects up with the edge or centre of all of them together (or of the page, for one object).</summary>
    public void AlignObjects(IReadOnlyCollection<string> ids, Alignment how)
    {
        var objects = ids.Select(Doc.Find).OfType<PageObject>().Where(o => !o.Locked).ToList();
        if (objects.Count == 0) return;
        var target = objects.Count == 1 ? Doc.Setup.Page : Box.Union(objects.Select(o => o.Bounds));
        Change("Align", d =>
        {
            foreach (var o in objects.Select(o => d.Find(o.Id)!))
            {
                switch (how)
                {
                    case Alignment.Left: o.X = target.X; break;
                    case Alignment.Centre: o.X = target.CentreX - o.W / 2; break;
                    case Alignment.Right: o.X = target.Right - o.W; break;
                    case Alignment.Top: o.Y = target.Y; break;
                    case Alignment.Middle: o.Y = target.CentreY - o.H / 2; break;
                    case Alignment.Bottom: o.Y = target.Bottom - o.H; break;
                }
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
        var sorted = across ? objects.OrderBy(o => o.X).ToList() : objects.OrderBy(o => o.Y).ToList();
        var span = across ? sorted.Max(o => o.X + o.W) - sorted[0].X : sorted.Max(o => o.Y + o.H) - sorted[0].Y;
        var gap = (span - sorted.Sum(o => across ? o.W : o.H)) / (sorted.Count - 1);
        Change(across ? "Space across" : "Space down", d =>
        {
            var at = across ? sorted[0].X : sorted[0].Y;
            foreach (var o in sorted.Select(o => d.Find(o.Id)!))
            {
                if (across) { o.X = Math.Round(at, 2); at += o.W + gap; }
                else { o.Y = Math.Round(at, 2); at += o.H + gap; }
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
            foreach (var frame in page.Objects.OfType<TextFrame>()) Chains.Forget(d, frame);
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

    /// <summary>Adds a master page with a name no other has.</summary>
    public string AddMaster()
    {
        var n = 0;
        string name;
        do name = $"{(char)('A' + n++ % 26)}{(n > 26 ? (n / 26).ToString() : "")}-Master";
        while (Doc.Masters.Any(m => m.Name == name));
        Change("Add master page", d => d.Masters.Add(new MasterPage { Name = name }));
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

    /// <summary>Replaces every match of a search in one step, each replacement in the format of the text it replaces. Returns how many.</summary>
    public int ReplaceAll(string query, string replacement, FindOptions options)
    {
        var matches = Search.FindAll(Doc, query, options);
        if (matches.Count == 0) return 0;
        Change($"Replace {matches.Count}", d =>
        {
            foreach (var group in matches.GroupBy(m => m.StoryId))
            {
                var story = d.Stories[group.Key];
                foreach (var m in group.OrderByDescending(m => m.Start)) Swap(story, m.Start, m.Length, replacement);
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
        return Change("Place text", d =>
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
                    if (r.Bold || r.Italic) story.FormatChars(run, r.Text.Length, f => f with { Bold = f.Bold || r.Bold, Italic = f.Italic || r.Italic });
                    run += r.Text.Length;
                }
                at += para.Text.Length + 1;
            }
            return index + (before ? 1 : 0) + body.Length;
        });
    }

    public void FormatChars(string storyId, int start, int length, Func<CharFormat, CharFormat> change, string label = "Format text") =>
        Change(label, d => d.Stories[storyId].FormatChars(start, length, change));

    public void FormatParas(string storyId, int start, int length, Func<ParaFormat, ParaFormat> change, string label = "Format paragraph") =>
        Change(label, d => d.Stories[storyId].FormatParas(start, length, change));

    public void ApplyStyle(string storyId, int start, int length, string style) =>
        Change("Apply style " + style, d => d.Stories[storyId].ApplyStyle(start, length, d.Style(style)));

    /// <summary>Makes or replaces a style, and brings every paragraph using it up to date.</summary>
    public void DefineStyle(ParagraphStyle style) => Change("Define style " + style.Name, d =>
    {
        var i = d.Styles.FindIndex(s => s.Name == style.Name);
        if (i >= 0) d.Styles[i] = style;
        else d.Styles.Add(style);
        foreach (var story in d.Stories.Values) story.Restyle(style);
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
