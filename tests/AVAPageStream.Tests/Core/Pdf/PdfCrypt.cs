using System.Security.Cryptography;
using System.Text;
using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.Pdf;

/// <summary>
/// Protects a test PDF as the standard security handler does: RC4 of 40 or 128 bits (revisions 2 and 3), AES-128
/// (revision 4) or AES-256 (revision 6). Its files are checked against Poppler, so the reader is checked against
/// something other than itself.
/// </summary>
internal sealed class PdfCrypt
{
    private static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    public static readonly byte[] Id = Encoding.ASCII.GetBytes("0123456789abcdef");
    public const int P = -3904; // printing allowed, copying not

    public int Revision { get; }
    public string Dictionary { get; }
    private readonly byte[] _key;

    public PdfCrypt(int revision, string user = "", string owner = "owner")
    {
        Revision = revision;
        if (revision == 6)
        {
            _key = RandomNumberGenerator.GetBytes(32);
            var (uv, uk) = (RandomNumberGenerator.GetBytes(8), RandomNumberGenerator.GetBytes(8));
            var up = Encoding.UTF8.GetBytes(user);
            var u = (byte[])[.. Hash(up, uv, []), .. uv, .. uk];
            var ue = AesEnc(Hash(up, uk, []), _key);
            var (ov, ok) = (RandomNumberGenerator.GetBytes(8), RandomNumberGenerator.GetBytes(8));
            var op = Encoding.UTF8.GetBytes(owner);
            var o = (byte[])[.. Hash(op, ov, u), .. ov, .. ok];
            var oe = AesEnc(Hash(op, ok, u), _key);
            var perms = new byte[16];
            BitConverter.GetBytes(P).CopyTo(perms, 0);
            (perms[4], perms[5], perms[6], perms[7]) = (0xFF, 0xFF, 0xFF, 0xFF);
            (perms[8], perms[9], perms[10], perms[11]) = ((byte)'T', (byte)'a', (byte)'d', (byte)'b');
            using var aes = Aes.Create();
            aes.Key = _key;
            var permsEnc = aes.EncryptEcb(perms, PaddingMode.None);
            Dictionary = $"<< /Filter /Standard /V 5 /R 6 /Length 256 /P {P} /O <{Hex(o)}> /U <{Hex(u)}> /OE <{Hex(oe)}> /UE <{Hex(ue)}> /Perms <{Hex(permsEnc)}> " +
                         "/CF << /StdCF << /CFM /AESV3 /Length 32 /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF >>";
            return;
        }
        var length = revision == 2 ? 5 : 16;
        // Algorithm 3: the owner string, the user password encrypted by a key from the owner's.
        var h = MD5.HashData(Padded(Encoding.Latin1.GetBytes(owner)));
        if (revision >= 3) for (var k = 0; k < 50; k++) h = MD5.HashData(h);
        var okey = h[..length];
        var ostr = PdfSecurity.Rc4(okey, Padded(Encoding.Latin1.GetBytes(user)));
        if (revision >= 3) for (var i = 1; i <= 19; i++) ostr = PdfSecurity.Rc4(okey.Select(b => (byte)(b ^ i)).ToArray(), ostr);
        // Algorithm 2: the file key.
        var input = (byte[])[.. Padded(Encoding.Latin1.GetBytes(user)), .. ostr, .. BitConverter.GetBytes(P), .. Id];
        h = MD5.HashData(input);
        if (revision >= 3) for (var k = 0; k < 50; k++) h = MD5.HashData(h.AsSpan(0, length));
        _key = h[..length];
        byte[] ustr;
        if (revision == 2) ustr = PdfSecurity.Rc4(_key, Pad);
        else
        {
            var x = PdfSecurity.Rc4(_key, MD5.HashData([.. Pad, .. Id]));
            for (var i = 1; i <= 19; i++) x = PdfSecurity.Rc4(_key.Select(b => (byte)(b ^ i)).ToArray(), x);
            ustr = [.. x, .. new byte[16]];
        }
        Dictionary = revision switch
        {
            2 => $"<< /Filter /Standard /V 1 /R 2 /P {P} /O <{Hex(ostr)}> /U <{Hex(ustr)}> >>",
            3 => $"<< /Filter /Standard /V 2 /R 3 /Length 128 /P {P} /O <{Hex(ostr)}> /U <{Hex(ustr)}> >>",
            _ => $"<< /Filter /Standard /V 4 /R 4 /Length 128 /P {P} /O <{Hex(ostr)}> /U <{Hex(ustr)}> " +
                 "/CF << /StdCF << /CFM /AESV2 /Length 16 /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF >>",
        };
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    private static byte[] Padded(byte[] pw)
    {
        var b = new byte[32];
        var n = Math.Min(32, pw.Length);
        Array.Copy(pw, b, n);
        Array.Copy(Pad, 0, b, n, 32 - n);
        return b;
    }

    private static byte[] Hash(byte[] pw, byte[] salt, byte[] udata)
    {
        var k = SHA256.HashData([.. pw, .. salt, .. udata]);
        using var aes = Aes.Create();
        for (var round = 0; ; round++)
        {
            var one = (byte[])[.. pw, .. k, .. udata];
            var k1 = new byte[one.Length * 64];
            for (var i = 0; i < 64; i++) one.CopyTo(k1, i * one.Length);
            aes.Key = k[..16];
            var e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            var sum = e.Take(16).Sum(b => b);
            k = (sum % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
            if (round >= 63 && e[^1] <= round - 31) break;
        }
        return k[..32];
    }

    private static byte[] AesEnc(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }

    /// <summary>An object's string or stream data, encrypted.</summary>
    public byte[] Encrypt(byte[] data, int number)
    {
        var key = _key;
        if (Revision < 6)
        {
            var input = new List<byte>(_key) { (byte)number, (byte)(number >> 8), (byte)(number >> 16), 0, 0 };
            if (Revision == 4) input.AddRange("sAlT"u8.ToArray());
            key = MD5.HashData([.. input])[..Math.Min(_key.Length + 5, 16)];
        }
        if (Revision < 4) return PdfSecurity.Rc4(key, data);
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = RandomNumberGenerator.GetBytes(16);
        return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
    }
}
