using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Core;

/// <summary>
/// The pages of a PDF, placed as pictures: read and drawn by AVAPageStream itself, as vector, so they stay sharp on
/// screen and in the PDFs it makes.
/// </summary>
public static class PdfPages
{
    public static bool IsPdf(byte[] b) => b.Length > 8 && PdfLexer.Find(b, "%PDF-", 0, Math.Min(b.Length, 1024)) >= 0;

    /// <summary>How many pages a PDF has (0 if it can't be read).</summary>
    public static int Count(byte[] pdf)
    {
        try { return new PdfReader(pdf).Pages.Count; }
        catch (Exception e) when (e is PdfException or IndexOutOfRangeException or ArgumentException or InvalidCastException or InvalidDataException) { return 0; }
    }

    /// <summary>
    /// Draws a page (from 1): what it paints, in its own points, its box from (0, 0) as it is shown (turned as the
    /// page says). Null, with why, if it can't be read; otherwise <paramref name="notes"/> say what it has that isn't drawn.
    /// </summary>
    public static PsDrawing? Draw(byte[] pdf, int page, out string? why, out IReadOnlyList<string> notes)
    {
        why = null;
        notes = [];
        PdfReader reader;
        try { reader = new PdfReader(pdf); }
        catch (PdfException e) { why = e.Message; return null; }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidCastException or InvalidDataException) { why = "it is damaged"; return null; }
        var index = Math.Clamp(page, 1, reader.Pages.Count) - 1;
        var interpreter = new Interpreter();
        try
        {
            var box = interpreter.RunPdfPage(reader, index);
            notes = interpreter.PdfNotes;
            return new PsDrawing(box, interpreter.Ops);
        }
        catch (Exception e) when (e is PsError or PdfException or IndexOutOfRangeException or ArgumentException or InvalidCastException or InvalidDataException or InvalidOperationException)
        {
            why = e is PdfException ? e.Message : "it is damaged";
            return null;
        }
    }
}
