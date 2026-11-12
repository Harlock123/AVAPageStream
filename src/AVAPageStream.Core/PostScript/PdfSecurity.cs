using System.Security.Cryptography;
using System.Text;

namespace AVAPageStream.Core.PostScript;

/// <summary>A PDF that needs a password to be read: none was given, or the one given is wrong.</summary>
public sealed class PdfPasswordException(bool wrong) : Exception(wrong ? "the password is wrong" : "it is protected by a password")
{
    public bool Wrong { get; } = wrong;
}

/// <summary>
/// The standard security handler: works out a protected PDF's key from its password (the empty one first, as most
/// protected files open with it) and decrypts its strings and streams. Revisions 2 to 4 (RC4 of 40 to 128 bits, and
/// AES-128) and 5 and 6 (AES-256).
/// </summary>
public sealed class PdfSecurity
{
    private static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private readonly byte[] _key;
    private readonly int _revision;
    private readonly string _strings, _streams; // each a method: V2 (RC4), AESV2, AESV3 or Identity

    /// <summary>Whether the document's metadata stream is left unencrypted.</summary>
    public bool PlainMetadata { get; }

    private PdfSecurity(byte[] key, int revision, string strings, string streams, bool plainMetadata)
    {
        (_key, _revision, _strings, _streams, PlainMetadata) = (key, revision, strings, streams, plainMetadata);
    }

    /// <summary>
    /// Opens a protected file's security with a password (null: the empty one), as its user or its owner.
    /// Throws <see cref="PdfPasswordException"/> if it isn't the password, and <see cref="PdfException"/> for a
    /// handler other than the standard one.
    /// </summary>
    public static PdfSecurity Open(PsDict e, byte[] id, string? password)
    {
        if ((e.Get("Filter") as PsName)?.Value is { } filter && filter != "Standard")
            throw new PdfException($"it is protected by the {filter} security handler, which AVAPageStream can't open");
        var v = e.Get("V") is int vv ? vv : 0;
        var r = e.Get("R") is int rr ? rr : 2;
        var o = (e.Get("O") as PsString)?.Span.ToArray() ?? [];
        var u = (e.Get("U") as PsString)?.Span.ToArray() ?? [];
        var p = e.Get("P") is int pp ? pp : unchecked((int)0xFFFFFFFC);
        var plainMetadata = e.Get("EncryptMetadata") is false;
        string strings = "V2", streams = "V2";
        if (v >= 4)
        {
            string Method(string key)
            {
                var name = (e.Get(key) as PsName)?.Value ?? "Identity";
                if (name == "Identity") return "Identity";
                var cfm = ((e.Get("CF") as PsDict)?.Get(name) as PsDict)?.Get("CFM") as PsName;
                return cfm?.Value switch { "AESV2" => "AESV2", "AESV3" => "AESV3", "None" => "Identity", _ => "V2" };
            }
            (strings, streams) = (Method("StrF"), Method("StmF"));
        }
        if (r >= 5)
        {
            var oe = (e.Get("OE") as PsString)?.Span.ToArray() ?? [];
            var ue = (e.Get("UE") as PsString)?.Span.ToArray() ?? [];
            if (o.Length < 48 || u.Length < 48) throw new PdfException("its protection is damaged");
            var pw = Encoding.UTF8.GetBytes(password ?? "");
            if (pw.Length > 127) pw = pw[..127];
            byte[]? fileKey = null;
            if (Hash(r, pw, u[32..40], []).AsSpan(0, 32).SequenceEqual(u.AsSpan(0, 32)))
                fileKey = AesNoPad(Hash(r, pw, u[40..48], []), ue);
            else if (Hash(r, pw, o[32..40], u[..48]).AsSpan(0, 32).SequenceEqual(o.AsSpan(0, 32)))
                fileKey = AesNoPad(Hash(r, pw, o[40..48], u[..48]), oe);
            if (fileKey is null) throw new PdfPasswordException(password is not null);
            return new PdfSecurity(fileKey, r, strings, streams, plainMetadata);
        }
        var length = r == 2 ? 5 : Math.Clamp((e.Get("Length") is int len ? len : 40) / 8, 5, 16);
        var given = Latin1(password);
        // As the user; then as the owner, whose password gives the user's.
        var userKey = Key(given, o, p, id, r, length, plainMetadata);
        if (UserMatches(userKey, u, id, r)) return new PdfSecurity(userKey, r, strings, streams, plainMetadata);
        var user = OwnerToUser(given, o, r, length);
        var ownerKey = Key(user, o, p, id, r, length, plainMetadata);
        if (UserMatches(ownerKey, u, id, r)) return new PdfSecurity(ownerKey, r, strings, streams, plainMetadata);
        throw new PdfPasswordException(password is not null);
    }

    private static byte[] Latin1(string? s) => s is null ? [] : Encoding.Latin1.GetBytes(s.Length > 32 ? s[..32] : s);

    private static byte[] Padded(byte[] pw)
    {
        var b = new byte[32];
        var n = Math.Min(32, pw.Length);
        Array.Copy(pw, b, n);
        Array.Copy(Pad, 0, b, n, 32 - n);
        return b;
    }

    /// <summary>Algorithm 2: the file key from a password.</summary>
    private static byte[] Key(byte[] pw, byte[] o, int p, byte[] id, int r, int length, bool plainMetadata)
    {
        var input = new List<byte>(Padded(pw));
        input.AddRange(o.Length >= 32 ? o[..32] : o);
        input.AddRange(BitConverter.GetBytes(p)); // low byte first
        input.AddRange(id);
        if (r >= 4 && plainMetadata) input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        var h = MD5.HashData([.. input]);
        if (r >= 3) for (var k = 0; k < 50; k++) h = MD5.HashData(h.AsSpan(0, length));
        return h[..length];
    }

    /// <summary>Algorithms 4 and 5: whether a key is the file's, by its user string.</summary>
    private static bool UserMatches(byte[] key, byte[] u, byte[] id, int r)
    {
        if (r == 2) return u.Length >= 32 && Rc4(key, Pad).AsSpan().SequenceEqual(u.AsSpan(0, 32));
        var x = Rc4(key, MD5.HashData([.. Pad, .. id]));
        for (var i = 1; i <= 19; i++) x = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), x);
        return u.Length >= 16 && x.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
    }

    /// <summary>Algorithm 7's first half: the (padded) user password the owner's opens.</summary>
    private static byte[] OwnerToUser(byte[] owner, byte[] o, int r, int length)
    {
        var h = MD5.HashData(Padded(owner));
        if (r >= 3) for (var k = 0; k < 50; k++) h = MD5.HashData(h);
        var key = h[..length];
        var x = o.Length >= 32 ? o[..32] : o;
        if (r == 2) return Rc4(key, x);
        for (var i = 19; i >= 0; i--) x = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), x);
        return x;
    }

    /// <summary>Algorithm 2.B (revision 6), or plain SHA-256 (revision 5): the hash a password is checked by.</summary>
    private static byte[] Hash(int r, byte[] pw, byte[] salt, byte[] udata)
    {
        var k = SHA256.HashData([.. pw, .. salt, .. udata]);
        if (r == 5) return k;
        using var aes = System.Security.Cryptography.Aes.Create();
        for (var round = 0; ; round++)
        {
            var one = (byte[])[.. pw, .. k, .. udata];
            var k1 = new byte[one.Length * 64];
            for (var i = 0; i < 64; i++) one.CopyTo(k1, i * one.Length);
            aes.Key = k[..16];
            var e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            var sum = 0;
            for (var i = 0; i < 16; i++) sum += e[i];
            k = (sum % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
            if (round >= 63 && e[^1] <= round - 31) break;
        }
        return k[..32];
    }

    private static byte[] AesNoPad(byte[] key, byte[] data)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key[..32];
        return aes.DecryptCbc(data.Length >= 32 ? data[..32] : data, new byte[16], PaddingMode.None);
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var o = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 255;
            j = (j + s[i]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
            o[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 255]);
        }
        return o;
    }

    /// <summary>Decrypts an object's string or stream data (an object's own key, before revision 5; the file's after).</summary>
    public byte[] Decrypt(byte[] data, int number, int generation, bool stream)
    {
        var method = stream ? _streams : _strings;
        if (method == "Identity" || data.Length == 0) return data;
        var key = _key;
        if (_revision < 5)
        {
            var input = new List<byte>(_key)
            {
                (byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)generation, (byte)(generation >> 8),
            };
            if (method == "AESV2") input.AddRange("sAlT"u8.ToArray());
            key = MD5.HashData([.. input])[..Math.Min(_key.Length + 5, 16)];
        }
        if (method == "V2") return Rc4(key, data);
        // AES in CBC: the first 16 bytes are the vector; the end padded as PKCS#7 says.
        if (data.Length < 32 || data.Length % 16 != 0) return data.Length >= 16 ? AesCbc(key, data, PaddingMode.None) : [];
        try { return AesCbc(key, data, PaddingMode.PKCS7); }
        catch (CryptographicException) { return AesCbc(key, data, PaddingMode.None); }
    }

    private static byte[] AesCbc(byte[] key, byte[] data, PaddingMode padding)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        var body = data.Length - 16;
        body -= body % 16;
        return aes.DecryptCbc(data.AsSpan(16, body), data.AsSpan(0, 16), padding);
    }
}
