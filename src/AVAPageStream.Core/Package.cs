namespace AVAPageStream.Core;

/// <summary>What packaging made: the folder, the copied document, how many files went with it, and the report.</summary>
public sealed record PackageResult(string Folder, string Document, int Pictures, int Fonts, IReadOnlyList<string> Missing, string Report);

/// <summary>
/// Gathers a document and everything it needs into one folder to hand on (to a printer, or another computer): a copy
/// of the document, its linked pictures in Links (the copy linked to them there), the font files of the fonts it uses
/// that aren't built into the program in Fonts, and a report of what is there and what preflight found.
/// </summary>
public static class Package
{
    /// <param name="fontFiles">The files a font family is in on this computer (none if it isn't installed).</param>
    public static PackageResult Make(Document doc, string name, string? docFolder, string folder, Func<string, IReadOnlyList<string>> fontFiles, IReadOnlyList<Issue> issues)
    {
        Directory.CreateDirectory(folder);
        var copy = doc.Clone();
        var missing = new List<string>();

        // Linked pictures: each file once, under its own name (numbered if two differ but share a name).
        var links = Path.Combine(folder, "Links");
        var copied = new Dictionary<string, string>(StringComparer.Ordinal); // source → copy
        foreach (var p in copy.AllObjects.OfType<PictureObject>().Where(p => p.Linked))
        {
            if (Links.Find(p, docFolder) is not { } source) { missing.Add($"Linked picture {Path.GetFileName(p.Source ?? "")}: not found"); continue; }
            if (!copied.TryGetValue(source, out var to))
            {
                Directory.CreateDirectory(links);
                to = Unique(links, Path.GetFileName(source));
                File.Copy(source, to);
                copied[source] = to;
            }
            p.Source = to;
        }

        // Fonts that aren't built in.
        var fontsCopied = 0;
        var fontLines = new List<string>();
        foreach (var font in copy.Stories.Values.SelectMany(s => s.Runs).Select(r => r.Format.Font).Distinct().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Core.Fonts.Bundled.Contains(font)) { fontLines.Add($"  {font}: built into AVAPageStream"); continue; }
            var files = fontFiles(font);
            if (files.Count == 0) { missing.Add($"Font {font}: not on this computer"); fontLines.Add($"  {font}: NOT FOUND"); continue; }
            var dir = Path.Combine(folder, "Fonts");
            Directory.CreateDirectory(dir);
            foreach (var f in files)
            {
                var to = Path.Combine(dir, Path.GetFileName(f));
                if (!File.Exists(to)) { File.Copy(f, to); fontsCopied++; }
            }
            fontLines.Add($"  {font}: {string.Join(", ", files.Select(Path.GetFileName))}");
        }

        var docPath = Path.Combine(folder, name + DocumentFile.Extension);
        DocumentFile.Save(copy, docPath);

        var s = copy.Setup;
        var report = new System.Text.StringBuilder();
        report.AppendLine($"{name}: packaged by AVAPageStream on {DateTime.Now:d MMMM yyyy 'at' HH:mm}");
        report.AppendLine();
        report.AppendLine($"Pages: {copy.Pages.Count}, {Units.Format(s.PageWidth, s.Unit)} × {Units.Format(s.PageHeight, s.Unit)}{(s.Facing ? ", facing" : "")}" +
                          (s.Bleed > 0 ? $", bleed {Units.Format(s.Bleed, s.Unit)}" : ""));
        var embedded = copy.AllObjects.OfType<PictureObject>().Count(p => !p.Linked && p.Image.Length > 0);
        report.AppendLine($"Pictures: {copied.Count} linked file{(copied.Count == 1 ? "" : "s")} copied to Links, {embedded} kept inside the document");
        report.AppendLine("Fonts:");
        foreach (var l in fontLines) report.AppendLine(l);
        var (process, spots) = Colours.Inks(copy);
        report.AppendLine($"Inks: {(process ? "process (CMYK)" : "none")}{(spots.Count > 0 ? ", spot: " + string.Join(", ", spots) : "")}");
        report.AppendLine();
        report.AppendLine(issues.Count == 0 ? "Preflight found nothing wrong." : $"Preflight found {issues.Count}:");
        foreach (var i in issues) report.AppendLine("  " + i.Message);
        if (missing.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("Missing:");
            foreach (var m in missing) report.AppendLine("  " + m);
        }
        File.WriteAllText(Path.Combine(folder, "Report.txt"), report.ToString());
        return new PackageResult(folder, docPath, copied.Count, fontsCopied, missing, report.ToString());
    }

    private static string Unique(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}-{n}{Path.GetExtension(file)}");
        return path;
    }
}
