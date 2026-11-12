namespace AVAPageStream.Core.PostScript;

public sealed partial class Interpreter
{
    /// <summary>Decodes a JPEG (for DCTDecode): given by the program, since the library has no image decoder of its own.</summary>
    public static Func<byte[], ColourImage?>? Jpeg { get; set; }

    /// <summary>This interpreter's JPEG decoder: <see cref="Jpeg"/> unless given another.</summary>
    public Func<byte[], ColourImage?>? JpegDecoder { get; init; } = Jpeg;

    private void RegisterImages()
    {
        Op("filter", i => i.Push(i.MakeFilter()));
        Op("image", i =>
        {
            if (i.Top is PsDict d) { i.Pop(); i.DictImage(d, mask: false); return; }
            var source = i.Pop();
            var matrix = ToMatrix(i.Pop<PsArray>("image"));
            var bits = i.Int();
            var h = i.Int();
            var w = i.Int();
            // The level 1 form: grey, whatever the colour space.
            i.Sampled(w, h, bits, matrix, [source], multiple: false, "DeviceGray", new PsName("DeviceGray"), null);
        });
        Op("colorimage", i =>
        {
            var n = i.Int();
            var multiple = i.Bool();
            var sources = new object[multiple ? n : 1];
            for (var k = sources.Length - 1; k >= 0; k--) sources[k] = i.Pop();
            var matrix = ToMatrix(i.Pop<PsArray>("colorimage"));
            var bits = i.Int();
            var h = i.Int();
            var w = i.Int();
            var space = n switch { 1 => "DeviceGray", 3 => "DeviceRGB", 4 => "DeviceCMYK", _ => throw new PsError("rangecheck", "colorimage") };
            i.Sampled(w, h, bits, matrix, sources, multiple, space, new PsName(space), null);
        });
        Op("imagemask", i =>
        {
            if (i.Top is PsDict d) { i.Pop(); i.DictImage(d, mask: true); return; }
            var source = i.Pop();
            var matrix = ToMatrix(i.Pop<PsArray>("imagemask"));
            var polarity = i.Bool();
            var h = i.Int();
            var w = i.Int();
            i.Mask(w, h, matrix, source, paintOnes: polarity);
        });
    }

    private void DictImage(PsDict d, bool mask)
    {
        int Need(string key) => d.Get(key) is { } v ? (int)ToNum(v) : throw new PsError("undefined", "image " + key);
        var type = d.Get("ImageType") is { } t ? (int)ToNum(t) : 1;
        if (type == 3 && !mask) { MaskedImage(d); return; }
        if (type is not (1 or 4)) throw new PsError("unsupported", "images of type " + type);
        var w = Need("Width");
        var h = Need("Height");
        var matrix = ToMatrix(d.Get("ImageMatrix") as PsArray ?? throw new PsError("undefined", "image ImageMatrix"));
        var source = d.Get("DataSource") ?? throw new PsError("undefined", "image DataSource");
        var decode = (d.Get("Decode") as PsArray)?.All().Select(ToNum).ToArray();
        if (mask)
        {
            Mask(w, h, matrix, source, paintOnes: decode is [1, 0, ..]);
            return;
        }
        var multiple = d.Get("MultipleDataSources") is true;
        var sources = multiple && source is PsArray a ? a.All().ToArray() : [source];
        // ImageType 4: the samples MaskColor gives (a value, or a range, for each colour) are left clear.
        var key = type == 4 ? (d.Get("MaskColor") as PsArray)?.All().Select(o => (long)ToNum(o)).ToArray() : null;
        Sampled(w, h, Need("BitsPerComponent"), matrix, sources, multiple, G.Space, G.SpaceArray, decode, colourKey: key);
    }

    /// <summary>
    /// ImageType 3: a picture with a mask of where it is painted (a mask value 0, by its decode, paints), the mask
    /// interleaved in the picture's samples (1), its rows (2), or from a source of its own (3).
    /// </summary>
    private void MaskedImage(PsDict d)
    {
        var data = d.Get("DataDict") as PsDict ?? throw new PsError("undefined", "DataDict");
        var maskDict = d.Get("MaskDict") as PsDict ?? throw new PsError("undefined", "MaskDict");
        var interleave = d.Get("InterleaveType") is { } it ? (int)ToNum(it) : 1;
        int Need(PsDict x, string key) => x.Get(key) is { } v ? (int)ToNum(v) : throw new PsError("undefined", "image " + key);
        var (w, h, bits) = (Need(data, "Width"), Need(data, "Height"), Need(data, "BitsPerComponent"));
        var matrix = ToMatrix(data.Get("ImageMatrix") as PsArray ?? throw new PsError("undefined", "ImageMatrix"));
        var decode = (data.Get("Decode") as PsArray)?.All().Select(ToNum).ToArray();
        var maskInverted = (maskDict.Get("Decode") as PsArray)?.All().Select(ToNum).ToArray() is [1, 0];
        if (interleave == 1)
        {
            var source = d.Get("DataSource") ?? data.Get("DataSource") ?? throw new PsError("undefined", "DataSource");
            Sampled(w, h, bits, matrix, [source], false, G.Space, G.SpaceArray, decode, maskFirst: maskInverted ? 2 : 1);
            return;
        }
        var (mw, mh) = (Need(maskDict, "Width"), Need(maskDict, "Height"));
        byte[] maskBits;
        if (interleave == 2)
        {
            // A mask row, then the picture's rows for it, and so on.
            var n = G.Space switch { "DeviceRGB" or "CIEBasedABC" or "CIEBasedDEF" => 3, "DeviceCMYK" or "CIEBasedDEFG" => 4, _ => 1 };
            var rowBytes = (w * n * bits + 7) / 8;
            var maskRow = (mw + 7) / 8;
            var per = Math.Max(1, h / Math.Max(1, mh));
            var all = ReadData(data.Get("DataSource") ?? throw new PsError("undefined", "DataSource"), mh * (maskRow + per * rowBytes));
            maskBits = new byte[mh * maskRow];
            var pixels = new byte[h * rowBytes];
            for (var r = 0; r < mh; r++)
            {
                var at = r * (maskRow + per * rowBytes);
                Array.Copy(all, at, maskBits, r * maskRow, maskRow);
                Array.Copy(all, at + maskRow, pixels, Math.Min(r * per, h - 1) * rowBytes, Math.Min(per * rowBytes, pixels.Length - Math.Min(r * per, h - 1) * rowBytes));
            }
            Sampled(w, h, bits, matrix, [PsString.Of(pixels)], false, G.Space, G.SpaceArray, decode);
        }
        else if (interleave == 3)
        {
            var source = data.Get("DataSource") ?? throw new PsError("undefined", "DataSource");
            Sampled(w, h, bits, matrix, [source], false, G.Space, G.SpaceArray, decode);
            maskBits = ReadData(maskDict.Get("DataSource") ?? throw new PsError("undefined", "mask DataSource"), (mw + 7) / 8 * mh);
        }
        else throw new PsError("rangecheck", "InterleaveType");
        // The mask over the picture, scaled to it.
        if (Ops.Count == 0 || Ops[^1] is not PsImage img) return;
        var maskRowBytes = (mw + 7) / 8;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (mx, my) = (x * mw / w, y * mh / h);
                var bit = maskBits[my * maskRowBytes + mx / 8] >> (7 - mx % 8) & 1;
                if ((bit == 1) != maskInverted) img.Rgba[(y * w + x) * 4 + 3] = 0;
            }
    }

    /// <summary>Where an image of so many pixels goes: its unit square through the image matrix's inverse and the current transformation.</summary>
    private PsMatrix ImagePlacement(int w, int h, PsMatrix imageMatrix) =>
        new PsMatrix(w, 0, 0, h, 0, 0).Then(imageMatrix.Inverse() ?? throw new PsError("undefinedresult", "image matrix")).Then(G.Ctm);

    /// <param name="maskFirst">For a masked image whose mask comes first in each pixel's samples: 1 (0 paints) or 2 (1 paints).</param>
    /// <param name="colourKey">For ImageType 4: the sample values (or ranges) not painted.</param>
    private void Sampled(int w, int h, int bits, PsMatrix matrix, object[] sources, bool multiple, string space, object spaceArray, double[]? decode,
        int maskFirst = 0, long[]? colourKey = null)
    {
        if (w <= 0 || h <= 0) return;
        if (w * (long)h > 40_000_000) throw new PsError("limitcheck", "image size");
        if (bits is not (1 or 2 or 4 or 8 or 12 or 16)) throw new PsError("rangecheck", "image bits");
        if (space == "Pattern") throw new PsError("typecheck", "image in a pattern colour space");
        // A JPEG decoded whole: its own pixels.
        if (sources is [PsFile f0] && f0.Picture is { } jpeg && jpeg.Width == w && jpeg.Height == h)
        {
            Ops.Add(new PsImage(w, h, jpeg.Rgba, ImagePlacement(w, h, matrix), [.. G.Clips]));
            return;
        }
        var n = space switch
        {
            "DeviceRGB" or "CIEBasedABC" or "CIEBasedDEF" => 3,
            "DeviceCMYK" or "CIEBasedDEFG" => 4,
            "DeviceN" => ((PsArray)((PsArray)spaceArray)[1]).Length,
            "ICCBased" => IccComponents((PsArray)spaceArray),
            _ => 1,
        };
        var max = (1 << bits) - 1;
        decode ??= space == "Indexed" ? [0, max] : Enumerable.Range(0, n).SelectMany(_ => new double[] { 0, 1 }).ToArray();
        byte[][] data;
        int rowBytes;
        if (multiple)
        {
            rowBytes = (w * bits + 7) / 8;
            data = sources.Select(s => ReadData(s, rowBytes * h)).ToArray();
        }
        else
        {
            rowBytes = (w * (n + (maskFirst > 0 ? 1 : 0)) * bits + 7) / 8;
            data = [ReadData(sources[0], rowBytes * h)];
        }
        var stride = n + (maskFirst > 0 ? 1 : 0);
        var lead = maskFirst > 0 ? 1 : 0;
        int Sample(byte[] d, int row, int index)
        {
            var at = row * rowBytes;
            switch (bits)
            {
                case 8: return at + index < d.Length ? d[at + index] : 0;
                case 16: return at + index * 2 < d.Length ? d[at + index * 2] : 0; // the high byte is enough
                case 12:
                {
                    var bit = index * 12;
                    var b0 = at + bit / 8 < d.Length ? d[at + bit / 8] : 0;
                    var b1 = at + bit / 8 + 1 < d.Length ? d[at + bit / 8 + 1] : 0;
                    var v = bit % 8 == 0 ? b0 << 4 | b1 >> 4 : (b0 & 15) << 8 | b1;
                    return v >> 4; // to 8 bits
                }
                default:
                {
                    var bit = index * bits;
                    var b = at + bit / 8 < d.Length ? d[at + bit / 8] : 0;
                    return (b >> (8 - bits - bit % 8)) & max;
                }
            }
        }
        var scale = bits is 12 or 16 ? 255 : max;
        var rgba = new byte[w * h * 4];
        var cache = new Dictionary<long, PsColour>();
        var comps = new double[n];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                long key = 0;
                var clear = colourKey is not null;
                for (var c = 0; c < n; c++)
                {
                    var s = multiple ? Sample(data[c], y, x) : Sample(data[0], y, x * stride + lead + c);
                    key = key * 257 + s;
                    comps[c] = decode[2 * c] + s * (decode[2 * c + 1] - decode[2 * c]) / scale;
                    if (colourKey is not null)
                        clear &= colourKey.Length >= 2 * n ? s >= colourKey[2 * c] && s <= colourKey[2 * c + 1] : c < colourKey.Length && s == colourKey[c];
                }
                if (maskFirst > 0)
                {
                    var m = Sample(data[0], y, x * stride);
                    clear = maskFirst == 1 ? m != 0 : m == 0;
                }
                if (!cache.TryGetValue(key, out var colour))
                {
                    colour = ToRgb(space, spaceArray, (double[])comps.Clone());
                    if (cache.Count < 65536) cache[key] = colour;
                }
                var o = (y * w + x) * 4;
                rgba[o] = (byte)Math.Round(colour.R * 255);
                rgba[o + 1] = (byte)Math.Round(colour.G * 255);
                rgba[o + 2] = (byte)Math.Round(colour.B * 255);
                rgba[o + 3] = clear ? (byte)0 : (byte)255;
            }
        Ops.Add(new PsImage(w, h, rgba, ImagePlacement(w, h, matrix), [.. G.Clips]));
    }

    /// <summary>imagemask: the current colour painted where the mask's bits say, clear elsewhere.</summary>
    private void Mask(int w, int h, PsMatrix matrix, object source, bool paintOnes)
    {
        if (w <= 0 || h <= 0) return;
        if (G.Space == "Pattern") throw new PsError("unsupported", "a mask painted with a pattern");
        var rowBytes = (w + 7) / 8;
        var data = ReadData(source, rowBytes * h);
        var c = G.Colour;
        var (r, g, b) = ((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var at = y * rowBytes + x / 8;
                var bit = at < data.Length && (data[at] >> (7 - x % 8) & 1) == 1;
                if (bit != paintOnes) continue;
                var o = (y * w + x) * 4;
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (r, g, b, 255);
            }
        Ops.Add(new PsImage(w, h, rgba, ImagePlacement(w, h, matrix), [.. G.Clips]));
    }

    /// <summary>
    /// An image's data from its source: a file (the program, or a filter on it) read for so many bytes; a procedure
    /// called again and again for strings; a string used again and again.
    /// </summary>
    private byte[] ReadData(object source, int length)
    {
        var o = new byte[length];
        var n = 0;
        switch (source)
        {
            case PsFile f:
                while (n < length && f.Scanner.Read() is var c and >= 0) o[n++] = (byte)c;
                break;
            case PsString s:
                if (s.Length == 0) break;
                for (; n < length; n++) o[n] = s[n % s.Length];
                break;
            case PsArray { Exec: true } proc:
                while (n < length)
                {
                    RunProc(proc);
                    var piece = Pop<PsString>("image data");
                    if (piece.Length == 0) break;
                    for (var k = 0; k < piece.Length && n < length; k++) o[n++] = piece[k];
                }
                break;
            default:
                throw new PsError("typecheck", "image data source");
        }
        return o;
    }

    /// <summary>
    /// The filter operator: a file of what a source (the program, another filter, a string or a procedure) gives,
    /// decoded. Data read from the program is taken up to its end-of-data mark, so the program carries on after it.
    /// </summary>
    private PsFile MakeFilter()
    {
        var name = Pop<PsName>("filter").Value;
        PsDict? parameters = null;
        string? eod = null;
        var count = 0;
        if (name == "SubFileDecode")
        {
            var e = Pop<PsString>("SubFileDecode").Text;
            count = Int();
            eod = e;
        }
        if (Top is PsDict d) { Pop(); parameters = d; }
        var source = Pop();

        // The encoded bytes, read from the source up to where this filter's data ends.
        byte[] Take(Func<Scanner, bool>? atEnd)
        {
            var bytes = new List<byte>();
            switch (source)
            {
                case PsFile f:
                    var sc = f.Scanner;
                    while (!sc.AtEnd)
                    {
                        if (atEnd?.Invoke(sc) == true) break;
                        bytes.Add((byte)sc.Read());
                    }
                    break;
                case PsString s:
                    bytes.AddRange(s.Span.ToArray());
                    break;
                case PsArray { Exec: true } proc:
                    for (var guard = 0; guard < 1_000_000; guard++)
                    {
                        RunProc(proc);
                        var piece = Pop<PsString>("filter source");
                        if (piece.Length == 0) break;
                        bytes.AddRange(piece.Span.ToArray());
                    }
                    break;
                default:
                    throw new PsError("typecheck", "filter source");
            }
            return [.. bytes];
        }

        static bool Marks(Scanner sc, string mark)
        {
            for (var k = 0; k < mark.Length; k++)
                if (sc.Position + k >= sc.Data.Length || sc.Data[sc.Position + k] != mark[k]) return false;
            sc.Position += mark.Length;
            return true;
        }
        if (name is not ("ASCIIHexDecode" or "AHx" or "ASCII85Decode" or "A85" or "SubFileDecode" or "RunLengthDecode" or "RL" or "LZWDecode" or "LZW" or "FlateDecode" or "Fl" or "DCTDecode" or "DCT" or "NullEncode"))
            throw new PsError("unsupported", "the " + name + " filter");
        var predictor = parameters?.Get("Predictor") is { } p ? (int)ToNum(p) : 1;
        return new PsFile(() =>
        {
            byte[] decoded = name switch
            {
                "ASCIIHexDecode" or "AHx" => Filters.AsciiHex(Take(sc => Marks(sc, ">"))),
                "ASCII85Decode" or "A85" => Filters.Ascii85(Take(sc => Marks(sc, "~>"))),
                "SubFileDecode" => Take(eod!.Length == 0 && count == 0 ? null : SubFileEnd(eod, count)),
                // From a file (the program itself, binary), each reads just its own data, to its end.
                "RunLengthDecode" or "RL" => source is PsFile rf ? Filters.RunLength(rf.Scanner) : Filters.RunLength(Take(null)),
                "LZWDecode" or "LZW" => source is PsFile lf ? Filters.Lzw(lf.Scanner) : Filters.Lzw(Take(null)),
                "FlateDecode" or "Fl" => source is PsFile ff ? Filters.Flate(ff.Scanner) : Filters.Flate(Take(null)),
                "DCTDecode" or "DCT" => source is PsFile jf ? Filters.Jpeg(jf.Scanner) : Take(null),
                _ => Take(null),
            };
            if (name is "LZWDecode" or "FlateDecode" or "LZW" or "Fl" && predictor >= 2) decoded = Predict(decoded, parameters!, predictor);
            if (name is "DCTDecode" or "DCT")
            {
                var picture = JpegDecoder?.Invoke(decoded) ?? throw new PsError("unsupported", "JPEG pictures");
                return (new Scanner(RgbBytes(picture)), picture);
            }
            return (new Scanner(decoded), null);
        });
    }


    /// <summary>SubFileDecode's end: after so many of its end-of-data strings (just the string once with a count of 0).</summary>
    private static Func<Scanner, bool> SubFileEnd(string eod, int count)
    {
        var seen = 0;
        return sc =>
        {
            if (eod.Length == 0) return false;
            for (var k = 0; k < eod.Length; k++)
                if (sc.Position + k >= sc.Data.Length || sc.Data[sc.Position + k] != eod[k]) return false;
            if (seen++ < count) return false;
            sc.Position += eod.Length;
            return true;
        };
    }

    private static byte[] RgbBytes(ColourImage p)
    {
        var o = new byte[p.Width * p.Height * 3];
        for (var k = 0; k < p.Width * p.Height; k++) (o[k * 3], o[k * 3 + 1], o[k * 3 + 2]) = (p.Rgba[k * 4], p.Rgba[k * 4 + 1], p.Rgba[k * 4 + 2]);
        return o;
    }

    /// <summary>The TIFF and PNG predictors LZW and Flate data may be encoded with.</summary>
    private static byte[] Predict(byte[] d, PsDict p, int predictor)
    {
        var colors = p.Get("Colors") is { } c ? (int)ToNum(c) : 1;
        var bpc = p.Get("BitsPerComponent") is { } b ? (int)ToNum(b) : 8;
        var columns = p.Get("Columns") is { } col ? (int)ToNum(col) : 1;
        var bpp = Math.Max(1, colors * bpc / 8);
        var row = (columns * colors * bpc + 7) / 8;
        if (predictor == 2)
        {
            for (var r = 0; r + row <= d.Length; r += row)
                for (var k = bpp; k < row; k++) d[r + k] += d[r + k - bpp];
            return d;
        }
        // PNG: each row starts with its filter's number.
        var o = new List<byte>(d.Length);
        var prev = new byte[row];
        for (var r = 0; r + row + 1 <= d.Length; r += row + 1)
        {
            var f = d[r];
            var cur = new byte[row];
            for (var k = 0; k < row; k++)
            {
                var x = d[r + 1 + k];
                var a = k >= bpp ? cur[k - bpp] : 0;
                var up = prev[k];
                var ul = k >= bpp ? prev[k - bpp] : 0;
                cur[k] = f switch
                {
                    1 => (byte)(x + a),
                    2 => (byte)(x + up),
                    3 => (byte)(x + (a + up) / 2),
                    4 => (byte)(x + Paeth(a, up, ul)),
                    _ => x,
                };
            }
            o.AddRange(cur);
            prev = cur;
        }
        return [.. o];

        static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            var (pa, pb, pc) = (Math.Abs(p - a), Math.Abs(p - b), Math.Abs(p - c));
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
