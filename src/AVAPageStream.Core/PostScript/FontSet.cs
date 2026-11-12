namespace AVAPageStream.Core.PostScript;

public sealed partial class Interpreter
{
    private const string CffKey = "AVAPageStream.CFF";

    /// <summary>
    /// The FontSetInit procedure set: its StartData reads a CFF font set (name, length, then the binary data) and
    /// defines each of its fonts: a FontType 2 font, or a CIDFont for a CID-keyed one.
    /// </summary>
    private PsDict FontSetInit()
    {
        if (SystemDict.Get("AVAPageStream.FontSetInit") is PsDict made) return made;
        var d = new PsDict(4);
        d.Put("StartData", new PsOperator("StartData", i =>
        {
            var count = i.Int();
            var setName = i.Pop();
            var sc = (i.File ?? throw new PsError("undefined", "StartData")).Scanner;
            var data = new byte[count];
            for (var k = 0; k < count && sc.Read() is var c and >= 0; k++) data[k] = (byte)c;
            List<Cff> fonts;
            try { fonts = Cff.Read(data); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { throw new PsError("invalidfont", "CFF"); }
            foreach (var cff in fonts)
            {
                var font = new PsDict(12);
                font.Put("FontName", new PsName(cff.Name));
                font.Put("FontMatrix", NewMatrix(cff.FontMatrix));
                font.Put("FontBBox", new PsArray([0, 0, 1000, 1000]));
                font.Put(CffKey, cff);
                font.Put("FID", new PsFontId(cff.Name));
                if (cff.IsCid)
                {
                    font.Put("CIDFontType", 0);
                    font.Put("FontType", 9);
                    font.Put("CIDFontName", new PsName(cff.Name));
                    i.Resources("CIDFont").Put(new PsName(cff.Name), font);
                }
                else
                {
                    font.Put("FontType", 2);
                    font.Put("PaintType", 0);
                    var encoding = Enumerable.Repeat<object>(new PsName(".notdef"), 256).ToList();
                    for (var c = 0; c < 256; c++)
                    {
                        if (cff.Encoding is { } enc) { if (enc.TryGetValue(c, out var g) && g < cff.Names.Length) encoding[c] = new PsName(cff.Names[g]); }
                        else if (Encodings.Standard[c] is { } n) encoding[c] = new PsName(n);
                    }
                    font.Put("Encoding", new PsArray(encoding));
                    i.FontDirectory.Put(new PsName(cff.Name), font);
                    i.Resources("Font").Put(new PsName(cff.Name), font);
                }
            }
            i.Resources("FontSet").Put(setName, new PsDict());
        }));
        SystemDict.Put("AVAPageStream.FontSetInit", d);
        return d;
    }

    /// <summary>A glyph of a CFF font, by its name (a plain font) or its CID (a CID-keyed one).</summary>
    private (PsPath Path, double Wx, double Wy) CffGlyph(PsDict font, Cff cff, int code, string name)
    {
        var key = ((object)cff, cff.IsCid ? "cid" + code : name);
        if (_glyphs.TryGetValue(key, out var cached)) return cached;
        var gid = cff.IsCid ? Array.IndexOf(cff.Cids, code) : Array.IndexOf(cff.Names, name);
        if (gid < 0) gid = 0;
        var g = cff.Glyph(gid);
        var r = (g.Path, g.AdvanceX, g.AdvanceY);
        _glyphs[key] = r;
        return r;
    }
}
