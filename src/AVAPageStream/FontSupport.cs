using System.Globalization;
using Avalonia.Media;
using AVAPageStream.Core;
using FontMetrics = AVAPageStream.Core.FontMetrics;
using Fonts = AVAPageStream.Core.Fonts;

namespace AVAPageStream;

/// <summary>
/// The fonts documents are set in: the Liberation fonts that ship inside the program (so documents look the same
/// everywhere), and the fonts installed on this computer.
/// </summary>
internal static class FontSupport
{
    private const string Bundled = "avares://AVAPageStream/Assets/Fonts#";
    private static readonly Dictionary<string, FontFamily> Families = [];

    public static FontFamily Family(string name)
    {
        if (Families.TryGetValue(name, out var family)) return family;
        family = Fonts.Bundled.Contains(name) ? new FontFamily(Bundled + name) : new FontFamily(name + ", " + Bundled + Fonts.Serif);
        Families[name] = family;
        return family;
    }

    public static Typeface Typeface(CharFormat f) =>
        new(Family(f.Font), f.Italic ? FontStyle.Italic : FontStyle.Normal, f.Bold ? FontWeight.Bold : FontWeight.Normal);

    /// <summary>The bundled fonts first, then the installed ones, by name.</summary>
    public static IReadOnlyList<string> Available()
    {
        var installed = FontManager.Current.SystemFonts.Select(f => f.Name)
            .Where(n => !Fonts.Bundled.Contains(n) && !n.StartsWith('.'))
            .Distinct().Order(StringComparer.OrdinalIgnoreCase);
        return [.. Fonts.Bundled, .. installed];
    }

    /// <summary>Whether a font is in the program, installed (and may be put in a PDF), missing, or not to be embedded.</summary>
    public static FontState State(string name)
    {
        if (Fonts.Bundled.Contains(name)) return FontState.Bundled;
        if (!FontManager.Current.SystemFonts.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) return FontState.Missing;
        using var typeface = SkiaSharp.SKTypeface.FromFamilyName(name);
        // The OS/2 table's fsType: 2 in its low bits means "restricted licence embedding": not to be put in a document.
        const uint os2 = ('O' << 24) | ('S' << 16) | ('/' << 8) | '2';
        if (typeface?.TryGetTableData(os2, out var table) == true && table.Length >= 10 && ((table[8] << 8 | table[9]) & 0x000F) == 0x0002) return FontState.NoEmbedding;
        return FontState.Installed;
    }

    private static Dictionary<string, List<string>>? _files;

    /// <summary>The files a font family is in on this computer, from its usual font folders (looked through once).</summary>
    public static IReadOnlyList<string> Files(string family)
    {
        if (_files is null)
        {
            _files = new(StringComparer.OrdinalIgnoreCase);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] folders = OperatingSystem.IsWindows()
                ? [Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts")]
                : OperatingSystem.IsMacOS()
                    ? ["/System/Library/Fonts", "/Library/Fonts", Path.Combine(home, "Library", "Fonts")]
                    : ["/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".local", "share", "fonts"), Path.Combine(home, ".fonts")];
            foreach (var folder in folders.Where(Directory.Exists))
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                             .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var t = SkiaSharp.SKTypeface.FromFile(file);
                        if (t?.FamilyName is not { Length: > 0 } name) continue;
                        if (!_files.TryGetValue(name, out var list)) _files[name] = list = [];
                        list.Add(file);
                    }
                    catch (Exception)
                    {
                        // a damaged font file is no font
                    }
                }
        }
        return _files.TryGetValue(family, out var found) ? found : [];
    }

    public static FormattedText Format(string text, CharFormat f, IBrush? brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface(f), f.Size, brush);
}

/// <summary>Measures text with the real fonts, the same way it will be drawn. Widths are remembered.</summary>
internal sealed class FontMeasurer : ITextMeasurer
{
    public static readonly FontMeasurer Shared = new();

    private readonly Dictionary<(string, CharFormat), double> _widths = [];
    private readonly Dictionary<CharFormat, FontMetrics> _metrics = [];

    public double Width(string text, CharFormat format)
    {
        var key = (text, format with { Colour = "", Underline = false, Tracking = 0, Shift = 0, CharStyle = null, Note = null });
        if (_widths.TryGetValue(key, out var w)) return w;
        if (_widths.Count > 50_000) _widths.Clear();
        w = FontSupport.Format(text, key.Item2, null).WidthIncludingTrailingWhitespace;
        _widths[key] = w;
        return w;
    }

    public FontMetrics Metrics(CharFormat format)
    {
        var key = format with { Colour = "", Underline = false, Tracking = 0, Shift = 0, CharStyle = null, Note = null };
        if (_metrics.TryGetValue(key, out var m)) return m;
        var ft = FontSupport.Format("Xg", key, null);
        // How tall the capitals stand: the ink of an H above the baseline.
        var h = FontSupport.Format("H", key, null);
        var ink = h.BuildGeometry(new Avalonia.Point(0, 0))?.Bounds;
        var caps = ink is { Height: > 0 } b ? h.Baseline - b.Top : 0;
        m = new FontMetrics(ft.Baseline, ft.Height - ft.Baseline, caps);
        _metrics[key] = m;
        return m;
    }
}
