using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;


namespace AVAPageStream.Core;

/// <summary>What a colour prints with: process inks, or a spot ink at a tint (its process inks how it looks).</summary>
public sealed record Ink(double C, double M, double Y, double K, string? Spot = null, double Tint = 1, Cmyk? SpotLooks = null);

/// <summary>What converting a PDF to CMYK did: and the pictures it left in RGB (JPEGs, which would lose to re-encoding).</summary>
public sealed record CmykResult(byte[] Pdf, int Colours, int Pictures, int PicturesLeft, int Shadings);

/// <summary>
/// Turning a PDF SkiaSharp wrote in RGB into one that prints in CMYK: solid colours (the inks of the CMYK swatch they
/// came from, or a spot ink as a Separation colour space), Flate pictures, and gradients' functions. Colours that
/// came from no CMYK swatch are converted the plain way a PDF reader converts back (c = 1 - r - k, k = 1 - the
/// largest), so the file looks the same on screen.
/// </summary>
public static class PdfColour
{
    /// <summary>The plain conversion, which a reader turns back into the same red, green and blue.</summary>
    public static (double C, double M, double Y, double K) Plain(double r, double g, double b)
    {
        var k = 1 - Math.Max(r, Math.Max(g, b));
        return (1 - r - k, 1 - g - k, 1 - b - k, k);
    }

    /// <summary>The inks for each colour a document uses, by its RGB as SkiaSharp writes it (0 to 255).</summary>
    public static Dictionary<(int R, int G, int B), Ink> InksOf(Document doc)
    {
        var inks = new Dictionary<(int, int, int), Ink>();
        var used = new List<string?>();
        foreach (var o in doc.AllObjects) used.AddRange([o.Fill, o.Stroke, (o as TableObject)?.HeaderFill]);
        foreach (var s in doc.Stories.Values) used.AddRange(s.Runs.Select(r => r.Format.Colour));
        foreach (var c in used.Where(Colours.IsRef).Distinct())
        {
            var (name, tint) = Colours.Parse(c!);
            if (doc.Swatches.FirstOrDefault(w => w.Name == name) is not { } swatch || Colours.Resolve(c, doc.Swatches) is not { } rgb || !Colours.TryParse(rgb, out var v)) continue;
            var key = (v.R, v.G, v.B);
            var t = tint / 100;
            if (swatch.Spot)
            {
                var looks = swatch.Process ?? Cmyk(Plain(v.R / 255.0, v.G / 255.0, v.B / 255.0));
                inks[key] = new Ink(looks.C / 100 * t, looks.M / 100 * t, looks.Y / 100 * t, looks.K / 100 * t, swatch.Name, t, looks);
            }
            else if (swatch.Process is { } p) inks[key] = new Ink(p.C / 100 * t, p.M / 100 * t, p.Y / 100 * t, p.K / 100 * t);
        }
        return inks;
    }

    private static Cmyk Cmyk((double C, double M, double Y, double K) v) => new(v.C * 100, v.M * 100, v.Y * 100, v.K * 100);

    private static string N(double v) => Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>A PostScript calculator function's ending that turns its red, green and blue into cyan, magenta, yellow and black (the plain way).</summary>
    public const string CalculatorToCmyk = " 3 copy 2 copy lt {exch} if pop 2 copy lt {exch} if pop 1 exch sub 3 index 1 exch sub 1 index sub 3 index 1 exch sub 2 index sub 3 index 1 exch sub 3 index sub 4 -1 roll 7 -3 roll pop pop pop";

    public static CmykResult? Convert(byte[] pdf, IReadOnlyDictionary<(int R, int G, int B), Ink> inks)
    {
        if (PdfFile.Read(pdf) is not { } file) return null;
        var written = new Dictionary<int, (string Body, byte[]? Stream)>();
        var spots = new Dictionary<string, int>(); // name → its colour space's object
        var next = file.Size;
        var (colours, pictures, left, shadings) = (0, 0, 0, 0);

        string SpotResource(Ink ink)
        {
            if (!spots.TryGetValue(ink.Spot!, out var n))
            {
                n = next++;
                spots[ink.Spot!] = n;
                var l = ink.SpotLooks ?? new Cmyk(0, 0, 0, 100);
                var name = string.Concat(ink.Spot!.Select(c => char.IsAsciiLetterOrDigit(c) ? c.ToString() : $"#{(int)c:X2}"));
                written[n] = ($"[/Separation /{name} /DeviceCMYK <</FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [{N(l.C / 100)} {N(l.M / 100)} {N(l.Y / 100)} {N(l.K / 100)}] /N 1>>]", null);
            }
            return "AVS" + n;
        }

        // Solid colours in the content streams: pages, forms and tiling patterns.
        var number = @"(-?\d*\.?\d+)";
        var colour = new Regex(number + @"\s+" + number + @"\s+" + number + @"\s+(rg|RG)(?=[\s\[/<(]|$)");
        var spotsIn = new Dictionary<int, HashSet<string>>();
        var contents = new HashSet<int>();
        foreach (var page in file.Pages())
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(Regex.Match(file.Objects[page].Body, @"/Contents\s*(\[[^\]]*\]|\d+\s+0\s+R)").Value, @"(\d+)\s+0\s+R"))
                contents.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        foreach (var o in file.Objects.Values)
        {
            if (o.Stream is null) continue;
            var isContent = contents.Contains(o.Number) || o.Has("Subtype") && Regex.IsMatch(o.Body, @"/Subtype\s*/Form") || Regex.IsMatch(o.Body, @"/PatternType\s+1");
            if (isContent && o.Decoded() is { } data)
            {
                var used = new HashSet<string>();
                var text = colour.Replace(Encoding.Latin1.GetString(data), m =>
                {
                    double V(int k) => double.Parse(m.Groups[k].Value, CultureInfo.InvariantCulture);
                    var (r, g, b) = (V(1), V(2), V(3));
                    var stroke = m.Groups[4].Value == "RG";
                    colours++;
                    if (inks.TryGetValue(((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255)), out var ink))
                    {
                        if (ink.Spot is not null)
                        {
                            var res = SpotResource(ink);
                            used.Add(res);
                            return stroke ? $"/{res} CS {N(ink.Tint)} SCN" : $"/{res} cs {N(ink.Tint)} scn";
                        }
                        return $"{N(ink.C)} {N(ink.M)} {N(ink.Y)} {N(ink.K)} {(stroke ? "K" : "k")}";
                    }
                    var (c, mg, y, k) = Plain(r, g, b);
                    return $"{N(c)} {N(mg)} {N(y)} {N(k)} {(stroke ? "K" : "k")}";
                });
                written[o.Number] = (Regex.Replace(o.Body, @"/Filter\s*/FlateDecode", "/Filter /FlateDecode"), PdfFile.Deflate(Encoding.Latin1.GetBytes(text)));
                if (used.Count > 0) spotsIn[o.Number] = used;
                continue;
            }
            // Pictures in RGB: Flate ones converted pixel by pixel.
            if (Regex.IsMatch(o.Body, @"/Subtype\s*/Image") && Regex.IsMatch(o.Body, @"/ColorSpace\s*/DeviceRGB"))
            {
                if (Regex.IsMatch(o.Body, @"/BitsPerComponent\s+8") && o.Decoded() is { } px && px.Length % 3 == 0)
                {
                    var cmyk = new byte[px.Length / 3 * 4];
                    for (var i = 0; i < px.Length / 3; i++)
                    {
                        var (c, m, y, k) = Plain(px[i * 3] / 255.0, px[i * 3 + 1] / 255.0, px[i * 3 + 2] / 255.0);
                        (cmyk[i * 4], cmyk[i * 4 + 1], cmyk[i * 4 + 2], cmyk[i * 4 + 3]) = ((byte)Math.Round(c * 255), (byte)Math.Round(m * 255), (byte)Math.Round(y * 255), (byte)Math.Round(k * 255));
                    }
                    written[o.Number] = (Regex.Replace(o.Body, @"/ColorSpace\s*/DeviceRGB", "/ColorSpace /DeviceCMYK"), PdfFile.Deflate(cmyk));
                    pictures++;
                }
                else left++;
                continue;
            }
            // A gradient's calculator function giving red, green and blue: given the inks' conversion at its end.
            if (Regex.IsMatch(o.Body, @"/FunctionType\s+4") && Regex.Match(o.Body, @"/Range\s*\[([^\]]*)\]") is { Success: true } range &&
                range.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 && o.Decoded() is { } code)
            {
                var program = Encoding.Latin1.GetString(code).TrimEnd();
                var close = program.LastIndexOf('}');
                if (close < 0) continue;
                program = program[..close] + CalculatorToCmyk + " }";
                written[o.Number] = (Regex.Replace(o.Body, @"/Range\s*\[[^\]]*\]", "/Range [0 1 0 1 0 1 0 1]"), PdfFile.Deflate(Encoding.Latin1.GetBytes(program)));
                shadings++;
            }
        }
        // Shadings and exponential functions in RGB, wherever they are (in their own objects, or within a pattern's).
        foreach (var o in file.Objects.Values)
        {
            var body = written.TryGetValue(o.Number, out var w) ? w.Body : o.Body;
            var changed = Regex.Replace(body, @"(/ShadingType\s+\d+[^>]*?)/ColorSpace\s*/DeviceRGB|/ColorSpace\s*/DeviceRGB(?=[^>]*?/ShadingType)", m => m.Value.Replace("/DeviceRGB", "/DeviceCMYK"));
            changed = Regex.Replace(changed, @"/C([01])\s*\[\s*" + number + @"\s+" + number + @"\s+" + number + @"\s*\]", m =>
            {
                double V(int k) => double.Parse(m.Groups[k].Value, CultureInfo.InvariantCulture);
                var (c, mg, y, k) = Plain(V(2), V(3), V(4));
                return $"/C{m.Groups[1].Value} [{N(c)} {N(mg)} {N(y)} {N(k)}]";
            });
            if (changed == body) continue;
            shadings++;
            written[o.Number] = (changed, w.Stream ?? (written.ContainsKey(o.Number) ? null : o.Stream));
        }
        // The spot colours' spaces in the resources of what uses them.
        if (spots.Count > 0)
        {
            var space = string.Concat(spots.Select(s => $" /AVS{s.Value} {s.Value} 0 R"));
            var holders = file.Pages().Concat(spotsIn.Keys.Where(k => !contents.Contains(k))).Distinct();
            foreach (var n in holders)
            {
                var (body, stream) = written.TryGetValue(n, out var w) ? w : (file.Objects[n].Body, file.Objects[n].Stream);
                if (Regex.Match(body, @"/Resources\s+(\d+)\s+0\s+R") is { Success: true } rr)
                {
                    var r = int.Parse(rr.Groups[1].Value, CultureInfo.InvariantCulture);
                    var rb = written.TryGetValue(r, out var rw) ? rw.Body : file.Objects[r].Body;
                    written[r] = (AddSpaces(rb, space), null);
                }
                else written[n] = (AddSpaces(body, space), stream);
            }
        }
        return new CmykResult(file.Update(written), colours, pictures, left, shadings);

        static string AddSpaces(string dict, string space)
        {
            var cs = Regex.Match(dict, @"/ColorSpace\s*<<");
            if (cs.Success) return dict.Insert(cs.Index + cs.Length, space);
            var res = Regex.Match(dict, @"/Resources\s*<<");
            if (res.Success) return dict.Insert(res.Index + res.Length, $"\n/ColorSpace <<{space}>>");
            return dict.EndsWith(">>", StringComparison.Ordinal) ? dict[..^2] + $"\n/ColorSpace <<{space}>>>>" : dict;
        }
    }
}
