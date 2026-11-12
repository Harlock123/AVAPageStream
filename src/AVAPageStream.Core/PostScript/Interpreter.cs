using System.Globalization;
using System.Text;

namespace AVAPageStream.Core.PostScript;

/// <summary>
/// A PostScript interpreter for what Encapsulated PostScript uses: the language (stacks, dictionaries, control,
/// strings and arrays) and the graphics (paths, painting, clipping, the current transformation and colour). It draws
/// nothing itself: painting adds to <see cref="Ops"/>, a list a program renders. Device operators do nothing, as in
/// an EPS. Anything it doesn't know is an error, which <see cref="Run"/> reports, so the caller can fall back.
/// </summary>
public sealed partial class Interpreter
{
    private readonly List<object> _stack = [];
    private readonly List<PsDict> _dicts = [];
    private long _steps;
    private int _depth;

    /// <summary>The most operators a program may run, so one that never ends is stopped.</summary>
    public long StepLimit { get; init; } = 50_000_000;

    public PsDict SystemDict { get; } = new(512);
    public PsDict UserDict { get; } = new(200);
    public PsDict StatusDict { get; } = new(32);
    public PsDict ErrorDict { get; } = new(32);
    public PsDict ErrorInfo { get; } = new(16);

    /// <summary>What has been painted, in order.</summary>
    public List<PsOp> Ops { get; } = [];

    /// <summary>The file the program is read from (currentfile).</summary>
    public PsFile? File { get; private set; }

    /// <summary>Anything written with print, = and the like (for tests).</summary>
    public StringBuilder Output { get; } = new();

    public IReadOnlyList<object> Stack => _stack;

    public Interpreter()
    {
        RegisterLanguage();
        RegisterGraphics();
        SystemDict.Put("systemdict", SystemDict);
        SystemDict.Put("userdict", UserDict);
        SystemDict.Put("globaldict", UserDict);
        SystemDict.Put("statusdict", StatusDict);
        SystemDict.Put("errordict", ErrorDict);
        SystemDict.Put("$error", ErrorInfo);
        SystemDict.Put("FontDirectory", new PsDict());
        SystemDict.Put("GlobalFontDirectory", new PsDict());
        SystemDict.Put("SharedFontDirectory", new PsDict());
        _dicts.Add(SystemDict);
        _dicts.Add(UserDict);
    }

    /// <summary>Runs a program. Returns null if it finished, or the error that stopped it.</summary>
    public PsError? Run(byte[] program, int start = 0, int end = -1)
    {
        var scanner = new Scanner(program, start, end);
        File = new PsFile(scanner);
        try
        {
            RunFile(scanner);
            return null;
        }
        catch (PsError e) { return e; }
        catch (PsStop) { return new PsError("stop"); }
        catch (PsExit) { return new PsError("invalidexit"); }
        catch (PsQuit) { return null; }
        catch (Exception e) when (e is InvalidCastException or IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
        {
            return new PsError("typecheck", e.Message);
        }
    }

    public PsError? Run(string program) => Run(Encoding.Latin1.GetBytes(program));

    private void RunFile(Scanner scanner)
    {
        while (scanner.Next(Immediate) is { } token)
        {
            // A procedure read from the file is data; an executable name is run.
            if (token is PsName { Exec: true } name) Execute(name);
            else Push(token);
        }
    }

    private object Immediate(string name) => Lookup(name) ?? throw new PsError("undefined", "//" + name);

    // ---- The operand stack --------------------------------------------------------------------------------------

    public void Push(object o) => _stack.Add(o);

    public object Pop()
    {
        if (_stack.Count == 0) throw new PsError("stackunderflow");
        var o = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return o;
    }

    public object Top => _stack.Count > 0 ? _stack[^1] : throw new PsError("stackunderflow");

    public T Pop<T>(string what = "")
    {
        var o = Pop();
        return o is T t ? t : throw new PsError("typecheck", what.Length > 0 ? what : typeof(T).Name);
    }

    public double Num()
    {
        var o = Pop();
        return o switch { int i => i, double d => d, _ => throw new PsError("typecheck", "number") };
    }

    public int Int()
    {
        var o = Pop();
        return o switch
        {
            int i => i,
            double d when d == Math.Floor(d) && Math.Abs(d) < int.MaxValue => (int)d,
            _ => throw new PsError("typecheck", "integer"),
        };
    }

    public bool Bool() => Pop() is bool b ? b : throw new PsError("typecheck", "boolean");

    public PsArray Proc() => Pop() is PsArray a ? a : throw new PsError("typecheck", "procedure");

    public static double ToNum(object o) => o switch { int i => i, double d => d, _ => throw new PsError("typecheck", "number") };

    /// <summary>A whole number as an integer, a real number as itself.</summary>
    public static object Number(double v) => v == Math.Floor(v) && Math.Abs(v) <= int.MaxValue ? (object)(int)v : v;

    // ---- Dictionaries -------------------------------------------------------------------------------------------

    public object? Lookup(string name)
    {
        for (var i = _dicts.Count - 1; i >= 0; i--)
            if (_dicts[i].Get(name) is { } v) return v;
        return null;
    }

    public PsDict? Where(object key)
    {
        for (var i = _dicts.Count - 1; i >= 0; i--)
            if (_dicts[i].Contains(key)) return _dicts[i];
        return null;
    }

    public PsDict CurrentDict => _dicts[^1];

    private void Op(string name, Action<Interpreter> run) => SystemDict.Put(name, new PsOperator(name, run));

    // ---- Execution ----------------------------------------------------------------------------------------------

    /// <summary>Runs an object: looks up a name, calls an operator, runs a procedure; pushes anything else.</summary>
    public void Execute(object o)
    {
        if (++_steps > StepLimit) throw new PsError("timeout");
        switch (o)
        {
            case PsName { Exec: true } n:
                var v = Lookup(n.Value) ?? throw new PsError("undefined", n.Value);
                if (v is PsName { Exec: true } again && again.Value == n.Value) throw new PsError("undefined", n.Value);
                Execute(v);
                break;
            case PsOperator op:
                op.Run(this);
                break;
            case PsArray { Exec: true } proc:
                RunProc(proc);
                break;
            case PsString { Exec: true } s:
                RunFile(new Scanner(s.Span.ToArray()));
                break;
            case PsFile { Exec: true } f:
                RunFile(f.Scanner);
                break;
            default:
                Push(o);
                break;
        }
    }

    /// <summary>Runs a procedure: its names and operators are run, and everything else (procedures in it too) pushed.</summary>
    public void RunProc(PsArray proc)
    {
        if (++_depth > 2000) { _depth = 0; throw new PsError("execstackoverflow"); }
        if (++_steps > StepLimit) { _depth--; throw new PsError("timeout"); }
        try
        {
            for (var i = 0; i < proc.Length; i++)
            {
                var item = proc[i];
                if (item is PsName { Exec: true } or PsOperator) Execute(item);
                else Push(item);
            }
        }
        finally
        {
            _depth--;
        }
    }

    // ---- Text -----------------------------------------------------------------------------------------------------

    /// <summary>An object as = shows it.</summary>
    public static string Show(object o) => o switch
    {
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => Real(d),
        bool b => b ? "true" : "false",
        PsString s => s.Text,
        PsName n => n.Value,
        PsOperator op => op.Name,
        _ => "--nostringval--",
    };

    /// <summary>An object as == shows it.</summary>
    public static string Write(object o) => o switch
    {
        PsString s => "(" + s.Text + ")",
        PsName n => n.ToString(),
        PsArray a => (a.Exec ? "{" : "[") + string.Join(" ", a.All().Select(Write)) + (a.Exec ? "}" : "]"),
        PsOperator op => "--" + op.Name + "--",
        PsDict => "-dict-",
        PsMark => "-mark-",
        PsNull => "null",
        _ => Show(o),
    };

    private static string Real(double d)
    {
        var s = d.ToString("0.#####", CultureInfo.InvariantCulture);
        return s.Contains('.') ? s : s + ".0";
    }
}
