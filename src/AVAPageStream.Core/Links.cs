namespace AVAPageStream.Core;

/// <summary>How a picture's link stands.</summary>
public enum LinkState { Embedded, UpToDate, Changed, Missing }

/// <summary>
/// Linked pictures: kept as files of their own, beside the document or anywhere, and read from them, so changing the
/// file (retouching a photograph, redrawing a logo) changes the picture.
/// </summary>
public static class Links
{
    /// <summary>
    /// Where a linked picture's file is: where it was placed from, or else a file of the same name in the document's
    /// folder (for a document moved with its pictures beside it). Null if neither is there.
    /// </summary>
    public static string? Find(PictureObject p, string? folder)
    {
        if (p.Source is not { Length: > 0 } source) return null;
        if (File.Exists(source)) return source;
        if (folder is not null && Path.Combine(folder, Path.GetFileName(source)) is var beside && File.Exists(beside)) return beside;
        return null;
    }

    public static LinkState StateOf(Document doc, PictureObject p, string? folder)
    {
        if (!p.Linked) return LinkState.Embedded;
        if (Find(p, folder) is not { } path) return LinkState.Missing;
        var bytes = Read(path);
        return bytes is not null && ImageStore.Key(bytes) == p.Image && doc.Images.Get(p.Image) is not null ? LinkState.UpToDate : LinkState.Changed;
    }

    /// <summary>The linked pictures whose files differ from what the document shows, with the files' bytes.</summary>
    public static List<(string Id, string Path, byte[] Bytes)> Changed(Document doc, string? folder)
    {
        var changed = new List<(string, string, byte[])>();
        foreach (var p in doc.AllObjects.OfType<PictureObject>().Where(p => p.Linked))
        {
            if (Find(p, folder) is not { } path || Read(path) is not { } bytes || !ImageStore.IsImage(bytes)) continue;
            if (ImageStore.Key(bytes) != p.Image || doc.Images.Get(p.Image) is null) changed.Add((p.Id, path, bytes));
        }
        return changed;
    }

    /// <summary>The linked pictures whose files can't be found.</summary>
    public static List<PictureObject> Missing(Document doc, string? folder) =>
        doc.AllObjects.OfType<PictureObject>().Where(p => p.Linked && Find(p, folder) is null).ToList();

    /// <summary>Brings the document's linked pictures up to date with their files (for a document being opened).</summary>
    public static void Refresh(Document doc, string? folder)
    {
        foreach (var (id, path, bytes) in Changed(doc, folder))
            if (doc.Find(id) is PictureObject p)
            {
                p.Image = doc.Images.Add(bytes);
                p.Source = path;
            }
    }

    private static byte[]? Read(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
