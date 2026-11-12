using System.Text;

namespace AVAPageStream.Core.PostScript;

/// <summary>A name: literal (/moveto, data) or executable (moveto, looked up and run).</summary>
public sealed record PsName(string Value, bool Exec = false)
{
    public override string ToString() => Exec ? Value : "/" + Value;
}

/// <summary>A string: bytes shared between a string and the pieces of it getinterval makes.</summary>
public sealed class PsString(byte[] data, int offset, int length, bool exec = false)
{
    public byte[] Data { get; } = data;
    public int Offset { get; } = offset;
    public int Length { get; } = length;
    public bool Exec { get; init; } = exec;

    public PsString(string text) : this(Encoding.Latin1.GetBytes(text), 0, text.Length) { }

    public static PsString Of(byte[] bytes) => new(bytes, 0, bytes.Length);

    public byte this[int i]
    {
        get => Data[Offset + i];
        set => Data[Offset + i] = value;
    }

    public string Text => Encoding.Latin1.GetString(Data, Offset, Length);

    public ReadOnlySpan<byte> Span => Data.AsSpan(Offset, Length);

    public PsString Interval(int start, int count) => new(Data, Offset + start, count, Exec);

    public override string ToString() => "(" + Text + ")";
}

/// <summary>An array, or (executable) a procedure: items shared between an array and the pieces of it.</summary>
public sealed class PsArray(List<object> items, int offset, int length, bool exec = false)
{
    public List<object> Items { get; } = items;
    public int Offset { get; } = offset;
    public int Length { get; } = length;
    public bool Exec { get; init; } = exec;

    public PsArray(List<object> items, bool exec = false) : this(items, 0, items.Count, exec) { }

    public object this[int i]
    {
        get => Items[Offset + i];
        set => Items[Offset + i] = value;
    }

    public PsArray Interval(int start, int count) => new(Items, Offset + start, count, Exec);

    public IEnumerable<object> All()
    {
        for (var i = 0; i < Length; i++) yield return Items[Offset + i];
    }

    public PsArray With(bool exec) => new(Items, Offset, Length, exec);
}

/// <summary>A dictionary. Names and strings are the same key; so are an integer and a real of the same value.</summary>
public sealed class PsDict(int capacity = 16)
{
    private readonly Dictionary<object, (object Key, object Value)> _map = new(capacity);

    public int Capacity { get; set; } = Math.Max(capacity, 1);
    public int Count => _map.Count;

    public static object Normal(object key) => key switch
    {
        PsName n => n.Value,
        PsString s => s.Text,
        int i => (double)i,
        _ => key,
    };

    public bool TryGet(object key, out object value)
    {
        if (_map.TryGetValue(Normal(key), out var e)) { value = e.Value; return true; }
        value = PsNull.Instance;
        return false;
    }

    public object? Get(object key) => _map.TryGetValue(Normal(key), out var e) ? e.Value : null;

    public void Put(object key, object value)
    {
        // A string key is kept as a name, as PostScript does.
        var k = key is PsString s ? new PsName(s.Text) : key is PsName n && n.Exec ? new PsName(n.Value) : key;
        _map[Normal(key)] = (k, value);
    }

    public void Put(string name, object value) => Put(new PsName(name), value);

    public bool Contains(object key) => _map.ContainsKey(Normal(key));

    public void Remove(object key) => _map.Remove(Normal(key));

    public IEnumerable<(object Key, object Value)> Entries => _map.Values.ToList();
}

/// <summary>A built-in operator.</summary>
public sealed class PsOperator(string name, Action<Interpreter> run)
{
    public string Name { get; } = name;
    public Action<Interpreter> Run { get; } = run;
    public override string ToString() => "--" + Name + "--";
}

public sealed class PsMark
{
    public static readonly PsMark Instance = new();
    public override string ToString() => "-mark-";
}

public sealed class PsNull
{
    public static readonly PsNull Instance = new();
    public override string ToString() => "null";
}

/// <summary>A file: the program being read (currentfile), or a string read as one.</summary>
public sealed class PsFile(Scanner scanner)
{
    public Scanner Scanner { get; } = scanner;
    public bool Exec { get; init; }
}

/// <summary>What save hands back: the graphics state's depth then, for restore.</summary>
public sealed class PsSave(int depth)
{
    public int Depth { get; } = depth;
}

/// <summary>A font dictionary's stand-in until fonts are drawn: what findfont found.</summary>
public sealed class PsFontId(string name)
{
    public string Name { get; } = name;
}

/// <summary>A PostScript error: the error's name ("undefined", "typecheck"…) and what it happened in.</summary>
public sealed class PsError(string name, string? what = null) : Exception($"{name}{(what is null ? "" : " in " + what)}")
{
    public string Name { get; } = name;
}

/// <summary>Leaving a loop (exit).</summary>
public sealed class PsExit : Exception;

/// <summary>Leaving to the nearest stopped (stop).</summary>
public sealed class PsStop : Exception;

/// <summary>Leaving the program (quit).</summary>
public sealed class PsQuit : Exception;
