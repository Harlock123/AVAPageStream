using System.Globalization;
using System.Text;

namespace AVAPageStream.Core;

/// <summary>A picture's preview: its pixels as greys (0 black, 255 white), a row at a time.</summary>
public sealed record GreyImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Reading Encapsulated PostScript without a PostScript interpreter: its size (the bounding box), its PostScript (out
/// of the DOS binary form), and the previews EPS files carry: a TIFF in the DOS binary form's header (as Illustrator,
/// CorelDRAW and most Windows programs write), or an EPSI preview in the PostScript. Drawing the PostScript itself
/// needs Ghostscript.
/// </summary>
public static class Eps
{
    /// <summary>The PostScript text of an EPS: all of it, or the PostScript section of the DOS binary form.</summary>
    public static byte[] PostScript(byte[] eps)
    {
        if (eps is not [0xC5, 0xD0, 0xD3, 0xC6, ..] || eps.Length < 12) return eps;
        var offset = BitConverter.ToInt32(eps, 4);
        var length = BitConverter.ToInt32(eps, 8);
        if (offset < 0 || length < 0 || offset + length > eps.Length) return [];
        return eps[offset..(offset + length)];
    }

    /// <summary>
    /// Draws an EPS with AVAPageStream's own PostScript interpreter: what it paints, in its own points, or null (with
    /// why) if it uses something the interpreter doesn't do or stops with an error, so the caller can fall back.
    /// </summary>
    public static PostScript.PsDrawing? Draw(byte[] eps, out string? why)
    {
        why = null;
        if (BoundingBox(eps) is not { W: > 0, H: > 0 } box) { why = "it has no bounding box"; return null; }
        var ps = PostScript(eps);
        var interpreter = new PostScript.Interpreter { BoundingBox = box };
        // What an EPS may count on finding when it is placed in a page: an empty userdict to define into.
        if (interpreter.Run(ps) is { } error)
        {
            why = error.Name == "unsupported" ? $"it uses {error.Message.Replace("unsupported in ", "")}, which AVAPageStream doesn't draw yet" : $"its PostScript stopped with {error.Message}";
            return null;
        }
        return new PostScript.PsDrawing(box, interpreter.Ops);
    }

    /// <summary>The colour preview in a DOS binary EPS's TIFF section, if it has one that can be read.</summary>
    public static ColourImage? TiffPreview(byte[] eps)
    {
        if (eps is not [0xC5, 0xD0, 0xD3, 0xC6, ..] || eps.Length < 28) return null;
        var offset = BitConverter.ToInt32(eps, 20);
        var length = BitConverter.ToInt32(eps, 24);
        if (offset <= 0 || length <= 0 || offset + (long)length > eps.Length) return null;
        return Tiff.Read(eps[offset..(offset + length)]);
    }

    private static IEnumerable<string> Lines(byte[] eps) =>
        Encoding.Latin1.GetString(PostScript(eps)).Split('\n').Select(l => l.TrimEnd('\r'));

    /// <summary>The bounding box, in points: from %%HiResBoundingBox if there is one, else %%BoundingBox. Null if neither.</summary>
    public static Box? BoundingBox(byte[] eps)
    {
        Box? plain = null;
        foreach (var line in Lines(eps).Take(400))
        {
            var hi = line.StartsWith("%%HiResBoundingBox:", StringComparison.Ordinal);
            if (!hi && !line.StartsWith("%%BoundingBox:", StringComparison.Ordinal)) continue;
            var parts = line[(line.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) continue;
            var v = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var box = new Box(v[0], v[1], v[2] - v[0], v[3] - v[1]);
            if (hi) return box;
            plain ??= box;
        }
        return plain;
    }

    /// <summary>
    /// The EPSI preview, if the file has one: the hex lines between %%BeginPreview: width height depth lines and
    /// %%EndPreview, in which 0 is white and the most a pixel can hold is black.
    /// </summary>
    public static GreyImage? Preview(byte[] eps)
    {
        int width = 0, height = 0, depth = 0;
        var hex = new StringBuilder();
        var inside = false;
        foreach (var line in Lines(eps))
        {
            if (!inside)
            {
                if (!line.StartsWith("%%BeginPreview:", StringComparison.Ordinal)) continue;
                var p = line["%%BeginPreview:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 3 || !int.TryParse(p[0], out width) || !int.TryParse(p[1], out height) || !int.TryParse(p[2], out depth)) return null;
                inside = true;
                continue;
            }
            if (line.StartsWith("%%EndPreview", StringComparison.Ordinal)) break;
            if (line.StartsWith('%')) hex.Append(line.AsSpan(1).Trim());
        }
        if (!inside || width <= 0 || height <= 0 || depth is not (1 or 2 or 4 or 8)) return null;
        byte[] data;
        try { data = Convert.FromHexString(hex.ToString()); }
        catch (FormatException) { return null; }
        var rowBytes = (width * depth + 7) / 8;
        if (data.Length < rowBytes * height) return null;
        var pixels = new byte[width * height];
        var max = (1 << depth) - 1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var bit = x * depth;
                var b = data[y * rowBytes + bit / 8];
                var v = (b >> (8 - depth - bit % 8)) & max;
                pixels[y * width + x] = (byte)(255 - v * 255 / max);
            }
        return new GreyImage(width, height, pixels);
    }
}
