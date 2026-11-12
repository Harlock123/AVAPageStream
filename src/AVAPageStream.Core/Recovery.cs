using System.Text.Json;

namespace AVAPageStream.Core;

/// <summary>A document saved for recovery: where its copy is, the file it was (null for one never saved), and when.</summary>
public sealed record Recovered(string Id, string Copy, string? Original, DateTime Saved)
{
    public string Name => Original is null ? "a new document" : Path.GetFileName(Original);
}

/// <summary>
/// Autosave: copies of documents with unsaved changes, kept in a folder of their own while the program runs. A copy
/// still there when the program starts was left by one that stopped without saving, and can be recovered.
/// </summary>
public sealed class Recovery(string folder)
{
    public string Folder => folder;

    private sealed record Note(string? Original, DateTime Saved);

    /// <summary>Saves a copy of the document under an id (one per window), with the file it belongs to.</summary>
    public void Save(Document doc, string? original, string id, DateTime? now = null)
    {
        Directory.CreateDirectory(folder);
        DocumentFile.Save(doc, Path.Combine(folder, id + DocumentFile.Extension));
        File.WriteAllText(Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(new Note(original, now ?? DateTime.UtcNow)));
    }

    /// <summary>The copies there are, newest first, leaving out the one with <paramref name="except"/>'s id.</summary>
    public IReadOnlyList<Recovered> Found(string? except = null)
    {
        if (!Directory.Exists(folder)) return [];
        var found = new List<Recovered>();
        foreach (var copy in Directory.EnumerateFiles(folder, "*" + DocumentFile.Extension))
        {
            var id = Path.GetFileNameWithoutExtension(copy);
            if (id == except) continue;
            Note? note = null;
            try
            {
                var json = Path.Combine(folder, id + ".json");
                if (File.Exists(json)) note = JsonSerializer.Deserialize<Note>(File.ReadAllText(json));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
            }
            found.Add(new Recovered(id, copy, note?.Original, note?.Saved ?? File.GetLastWriteTimeUtc(copy)));
        }
        return [.. found.OrderByDescending(r => r.Saved)];
    }

    /// <summary>Removes a copy: its document was saved or closed, or the person chose not to recover it.</summary>
    public void Discard(string id)
    {
        foreach (var ext in new[] { DocumentFile.Extension, ".json" })
        {
            try
            {
                File.Delete(Path.Combine(folder, id + ext));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Gives a copy another id: a recovered document's copy goes on as the copy of the window it opened in.</summary>
    public void Adopt(string id, string newId)
    {
        foreach (var ext in new[] { DocumentFile.Extension, ".json" })
        {
            var from = Path.Combine(folder, id + ext);
            if (File.Exists(from)) File.Move(from, Path.Combine(folder, newId + ext), overwrite: true);
        }
    }
}
