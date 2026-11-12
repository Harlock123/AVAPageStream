using System.IO.Compression;
using System.Text;

namespace AVAPageStream.Core.PostScript;

/// <summary>
/// Adobe's predefined CMaps (BSD-licensed; CMaps/LICENSE.md), bundled compactly by tools/make_cmaps.py: each CMap's
/// code space and codes to CIDs, and each character collection's CIDs to Unicode.
/// </summary>
public static class PredefinedCMaps
{
    private sealed record Raw(string Use, List<(byte[] Lo, byte[] Hi)> Spaces, List<(int Bytes, uint Lo, uint Hi, int Cid)> Ranges);

    private static readonly Lazy<(Dictionary<string, Raw> Maps, Dictionary<string, Dictionary<int, int>> Unicode)> Bundle = new(Load);
    private static readonly Dictionary<string, CMapData> Made = [];

    private static (Dictionary<string, Raw>, Dictionary<string, Dictionary<int, int>>) Load()
    {
        var maps = new Dictionary<string, Raw>();
        var unicode = new Dictionary<string, Dictionary<int, int>>();
        using var res = typeof(PredefinedCMaps).Assembly.GetManifestResourceStream("CMaps.cmaps.bin");
        if (res is null) return (maps, unicode);
        using var z = new ZLibStream(res, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        var b = ms.ToArray();
        var at = 4;
        uint V()
        {
            uint v = 0;
            for (var shift = 0; at < b.Length; shift += 7)
            {
                var x = b[at++];
                v |= (uint)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) break;
            }
            return v;
        }
        string T() { var n = (int)V(); var s = Encoding.UTF8.GetString(b, at, n); at += n; return s; }
        var count = V();
        for (var i = 0; i < count; i++)
        {
            var name = T();
            var use = T();
            var spaces = new List<(byte[], byte[])>();
            for (var n = V(); n > 0; n--)
            {
                var len = b[at++];
                spaces.Add((b[at..(at + len)], b[(at + len)..(at + 2 * len)]));
                at += 2 * len;
            }
            var ranges = new List<(int, uint, uint, int)>();
            for (var n = V(); n > 0; n--)
            {
                var bytes = b[at++];
                var lo = V();
                var span = V();
                ranges.Add((bytes, lo, lo + span, (int)V()));
            }
            maps[name] = new Raw(use, spaces, ranges);
        }
        for (var n = V(); n > 0; n--)
        {
            var ordering = T();
            var m = new Dictionary<int, int>();
            var cid = 0;
            for (var k = V(); k > 0; k--)
            {
                cid += (int)V();
                m[cid] = (int)V();
            }
            unicode[ordering] = m;
        }
        return (maps, unicode);
    }

    /// <summary>A predefined CMap by name, with the CMap it uses folded in; null if there is no such CMap.</summary>
    public static CMapData? Get(string name)
    {
        lock (Made)
        {
            if (Made.TryGetValue(name, out var known)) return known;
            var maps = Bundle.Value.Maps;
            if (!maps.ContainsKey(name)) return null;
            var data = new CMapData();
            // Its own ranges first, so they win over those of the CMap it uses.
            for (string? n = name, guard = null; n is { Length: > 0 } && maps.TryGetValue(n, out var raw) && guard != n; guard = n, n = raw.Use)
            {
                foreach (var s in raw.Spaces) if (!data.CodeSpaces.Any(c => c.Lo.AsSpan().SequenceEqual(s.Lo) && c.Hi.AsSpan().SequenceEqual(s.Hi))) data.CodeSpaces.Add(s);
                data.Ranges.AddRange(raw.Ranges.Select(r => (r.Lo, r.Hi, r.Bytes, r.Cid)));
            }
            return Made[name] = data;
        }
    }

    /// <summary>Whether a CMap is for vertical writing (its name ends in -V).</summary>
    public static bool Vertical(string name) => name.EndsWith("-V", StringComparison.Ordinal);

    /// <summary>The character a CID stands for in a collection (its Ordering: Japan1, GB1, CNS1, Korea1, KR), or null.</summary>
    public static int? Unicode(string ordering, int cid) =>
        Bundle.Value.Unicode.TryGetValue(ordering, out var m) && m.TryGetValue(cid, out var u) ? u : null;

    public static int Count => Bundle.Value.Maps.Count;
}
