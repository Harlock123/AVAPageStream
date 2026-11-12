namespace AVAPageStream.Core.PostScript;

/// <summary>The encodings PDF names for simple fonts, beyond Standard: WinAnsi and MacRoman.</summary>
public static class PdfEncodings
{
    public static readonly string[] WinAnsi = Build(
        Common(39, "quotesingle", 96, "grave", 45, "hyphen"),
        128, "Euro bullet quotesinglbase florin quotedblbase ellipsis dagger daggerdbl circumflex perthousand Scaron guilsinglleft OE bullet Zcaron bullet " +
             "bullet quoteleft quoteright quotedblleft quotedblright bullet endash emdash tilde trademark scaron guilsinglright oe bullet zcaron Ydieresis",
        160, "space exclamdown cent sterling currency yen brokenbar section dieresis copyright ordfeminine guillemotleft logicalnot hyphen registered macron " +
             "degree plusminus twosuperior threesuperior acute mu paragraph periodcentered cedilla onesuperior ordmasculine guillemotright onequarter onehalf threequarters questiondown " +
             "Agrave Aacute Acircumflex Atilde Adieresis Aring AE Ccedilla Egrave Eacute Ecircumflex Edieresis Igrave Iacute Icircumflex Idieresis " +
             "Eth Ntilde Ograve Oacute Ocircumflex Otilde Odieresis multiply Oslash Ugrave Uacute Ucircumflex Udieresis Yacute Thorn germandbls " +
             "agrave aacute acircumflex atilde adieresis aring ae ccedilla egrave eacute ecircumflex edieresis igrave iacute icircumflex idieresis " +
             "eth ntilde ograve oacute ocircumflex otilde odieresis divide oslash ugrave uacute ucircumflex udieresis yacute thorn ydieresis",
        127, "bullet");

    public static readonly string[] MacRoman = Build(
        Common(39, "quotesingle", 96, "grave", 45, "hyphen"),
        128, "Adieresis Aring Ccedilla Eacute Ntilde Odieresis Udieresis aacute agrave acircumflex adieresis atilde aring ccedilla eacute egrave " +
             "ecircumflex edieresis iacute igrave icircumflex idieresis ntilde oacute ograve ocircumflex odieresis otilde uacute ugrave ucircumflex udieresis " +
             "dagger degree cent sterling section bullet paragraph germandbls registered copyright trademark acute dieresis notequal AE Oslash " +
             "infinity plusminus lessequal greaterequal yen mu partialdiff summation product pi integral ordfeminine ordmasculine Omega ae oslash " +
             "questiondown exclamdown logicalnot radical florin approxequal Delta guillemotleft guillemotright ellipsis space Agrave Atilde Otilde OE oe " +
             "endash emdash quotedblleft quotedblright quoteleft quoteright divide lozenge ydieresis Ydieresis fraction currency guilsinglleft guilsinglright fi fl " +
             "daggerdbl periodcentered quotesinglbase quotedblbase perthousand Acircumflex Ecircumflex Aacute Edieresis Egrave Iacute Icircumflex Idieresis Igrave Oacute Ocircumflex " +
             "apple Ograve Uacute Ucircumflex Ugrave dotlessi circumflex tilde macron breve dotaccent ring cedilla hungarumlaut ogonek caron");

    /// <summary>An encoding by the name PDF gives it, or null.</summary>
    public static string[]? Named(string name) => name switch
    {
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "StandardEncoding" => Encodings.Standard,
        "PDFDocEncoding" => WinAnsi,
        _ => null,
    };

    /// <summary>Standard's printable ASCII, with the changes given (code, name, …).</summary>
    private static string[] Common(params object[] changes)
    {
        var e = new string[256];
        for (var c = 0; c < 256; c++) e[c] = c is >= 32 and < 127 ? Encodings.Standard[c] : ".notdef";
        for (var k = 0; k + 1 < changes.Length; k += 2) e[(int)changes[k]] = (string)changes[k + 1];
        return e;
    }

    private static string[] Build(string[] start, params object[] runs)
    {
        for (var k = 0; k + 1 < runs.Length; k += 2)
        {
            var at = (int)runs[k];
            foreach (var name in ((string)runs[k + 1]).Split(' ', StringSplitOptions.RemoveEmptyEntries)) start[at++] = name;
        }
        return start;
    }
}
