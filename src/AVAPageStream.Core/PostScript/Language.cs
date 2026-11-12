using System.Globalization;
using System.Text;

namespace AVAPageStream.Core.PostScript;

public sealed partial class Interpreter
{
    private readonly Random _random = new(1);

    private void RegisterLanguage()
    {
        // ---- The stack
        Op("pop", i => i.Pop());
        Op("exch", i => { var b = i.Pop(); var a = i.Pop(); i.Push(b); i.Push(a); });
        Op("dup", i => i.Push(i.Top));
        Op("copy", i =>
        {
            var top = i.Pop();
            if (top is int n)
            {
                if (n < 0 || n > i._stack.Count) throw new PsError("rangecheck", "copy");
                i._stack.AddRange(i._stack.GetRange(i._stack.Count - n, n));
                return;
            }
            var from = i.Pop();
            switch (from, top)
            {
                case (PsArray a, PsArray b):
                    if (a.Length > b.Length) throw new PsError("rangecheck", "copy");
                    for (var k = 0; k < a.Length; k++) b[k] = a[k];
                    i.Push(b.Interval(0, a.Length));
                    break;
                case (PsString a, PsString b):
                    if (a.Length > b.Length) throw new PsError("rangecheck", "copy");
                    for (var k = 0; k < a.Length; k++) b[k] = a[k];
                    i.Push(b.Interval(0, a.Length));
                    break;
                case (PsDict a, PsDict b):
                    foreach (var (k, v) in a.Entries) b.Put(k, v);
                    i.Push(b);
                    break;
                default:
                    throw new PsError("typecheck", "copy");
            }
        });
        Op("index", i => { var n = i.Int(); if (n < 0 || n >= i._stack.Count) throw new PsError("rangecheck", "index"); i.Push(i._stack[^(n + 1)]); });
        Op("roll", i =>
        {
            var j = i.Int();
            var n = i.Int();
            if (n < 0 || n > i._stack.Count) throw new PsError("rangecheck", "roll");
            if (n == 0) return;
            j = ((j % n) + n) % n;
            var part = i._stack.GetRange(i._stack.Count - n, n);
            i._stack.RemoveRange(i._stack.Count - n, n);
            i._stack.AddRange(part.Skip(n - j).Concat(part.Take(n - j)));
        });
        Op("clear", i => i._stack.Clear());
        Op("count", i => i.Push(i._stack.Count));
        Op("mark", i => i.Push(PsMark.Instance));
        Op("[", i => i.Push(PsMark.Instance));
        Op("<<", i => i.Push(PsMark.Instance));
        Op("cleartomark", i => { while (i.Pop() is not PsMark) { } });
        Op("counttomark", i => i.Push(i.CountToMark()));
        Op("]", i =>
        {
            var n = i.CountToMark();
            var items = i._stack.GetRange(i._stack.Count - n, n);
            i._stack.RemoveRange(i._stack.Count - n - 1, n + 1);
            i.Push(new PsArray(items));
        });
        Op(">>", i =>
        {
            var n = i.CountToMark();
            if (n % 2 != 0) throw new PsError("rangecheck", ">>");
            var d = new PsDict(n / 2);
            for (var k = i._stack.Count - n; k < i._stack.Count; k += 2) d.Put(i._stack[k], i._stack[k + 1]);
            i._stack.RemoveRange(i._stack.Count - n - 1, n + 1);
            i.Push(d);
        });

        // ---- Arithmetic
        void Binary(string name, Func<int, int, long>? ints, Func<double, double, double> reals) => Op(name, i =>
        {
            var b = i.Pop();
            var a = i.Pop();
            if (ints is not null && a is int x && b is int y)
            {
                var r = ints(x, y);
                i.Push(r is >= int.MinValue and <= int.MaxValue ? (object)(int)r : (double)r);
            }
            else i.Push(reals(ToNum(a), ToNum(b)));
        });
        Binary("add", (x, y) => (long)x + y, (x, y) => x + y);
        Binary("sub", (x, y) => (long)x - y, (x, y) => x - y);
        Binary("mul", (x, y) => (long)x * y, (x, y) => x * y);
        Op("div", i => { var b = i.Num(); var a = i.Num(); if (b == 0) throw new PsError("undefinedresult", "div"); i.Push(a / b); });
        Op("idiv", i => { var b = i.Int(); var a = i.Int(); if (b == 0) throw new PsError("undefinedresult", "idiv"); i.Push(a / b); });
        Op("mod", i => { var b = i.Int(); var a = i.Int(); if (b == 0) throw new PsError("undefinedresult", "mod"); i.Push(a % b); });
        void Unary(string name, Func<int, object>? ints, Func<double, object> reals) => Op(name, i =>
        {
            var a = i.Pop();
            i.Push(a is int x && ints is not null ? ints(x) : reals(ToNum(a)));
        });
        Unary("abs", x => x == int.MinValue ? -(double)x : (object)Math.Abs(x), x => Math.Abs(x));
        Unary("neg", x => x == int.MinValue ? -(double)x : (object)(-x), x => -x);
        Unary("ceiling", x => x, x => Math.Ceiling(x));
        Unary("floor", x => x, x => Math.Floor(x));
        Unary("round", x => x, x => Math.Floor(x + 0.5));
        Unary("truncate", x => x, x => Math.Truncate(x));
        Op("sqrt", i => { var a = i.Num(); if (a < 0) throw new PsError("rangecheck", "sqrt"); i.Push(Math.Sqrt(a)); });
        Op("atan", i =>
        {
            var den = i.Num();
            var num = i.Num();
            if (num == 0 && den == 0) throw new PsError("undefinedresult", "atan");
            var deg = Math.Atan2(num, den) * 180 / Math.PI;
            i.Push(deg < 0 ? deg + 360 : deg);
        });
        Op("cos", i => i.Push(Math.Cos(i.Num() * Math.PI / 180)));
        Op("sin", i => i.Push(Math.Sin(i.Num() * Math.PI / 180)));
        Op("exp", i => { var e = i.Num(); var b = i.Num(); i.Push(Math.Pow(b, e)); });
        Op("ln", i => i.Push(Math.Log(i.Num())));
        Op("log", i => i.Push(Math.Log10(i.Num())));
        Op("rand", i => i.Push(i._random.Next()));
        Op("srand", i => i.Int());
        Op("rrand", i => i.Push(0));
        Op("cvi", i =>
        {
            var a = i.Pop();
            i.Push(a switch
            {
                int x => x,
                double d => (int)Math.Truncate(d),
                PsString s => Scanner.Number(s.Text.Trim()) is { } n ? (int)Math.Truncate(ToNum(n)) : throw new PsError("typecheck", "cvi"),
                _ => throw new PsError("typecheck", "cvi"),
            });
        });
        Op("cvr", i =>
        {
            var a = i.Pop();
            i.Push(a switch
            {
                int x => (double)x,
                double d => d,
                PsString s => Scanner.Number(s.Text.Trim()) is { } n ? ToNum(n) : throw new PsError("typecheck", "cvr"),
                _ => throw new PsError("typecheck", "cvr"),
            });
        });

        // ---- Comparing and logic
        Op("eq", i => { var b = i.Pop(); var a = i.Pop(); i.Push(Same(a, b)); });
        Op("ne", i => { var b = i.Pop(); var a = i.Pop(); i.Push(!Same(a, b)); });
        void Compare(string name, Func<int, bool> test) => Op(name, i =>
        {
            var b = i.Pop();
            var a = i.Pop();
            var c = (a, b) switch
            {
                (PsString x, PsString y) => string.CompareOrdinal(x.Text, y.Text),
                _ => ToNum(a).CompareTo(ToNum(b)),
            };
            i.Push(test(c));
        });
        Compare("gt", c => c > 0);
        Compare("ge", c => c >= 0);
        Compare("lt", c => c < 0);
        Compare("le", c => c <= 0);
        void Logic(string name, Func<bool, bool, bool> bools, Func<int, int, int> ints) => Op(name, i =>
        {
            var b = i.Pop();
            var a = i.Pop();
            i.Push((a, b) switch
            {
                (bool x, bool y) => bools(x, y),
                (int x, int y) => ints(x, y),
                _ => throw new PsError("typecheck", name),
            });
        });
        Logic("and", (x, y) => x && y, (x, y) => x & y);
        Logic("or", (x, y) => x || y, (x, y) => x | y);
        Logic("xor", (x, y) => x ^ y, (x, y) => x ^ y);
        Op("not", i => i.Push(i.Pop() switch { bool b => !b, int n => ~n, _ => throw new PsError("typecheck", "not") }));
        Op("bitshift", i => { var s = i.Int(); var n = i.Int(); i.Push(s >= 0 ? n << s : (int)((uint)n >> -s)); });
        SystemDict.Put("true", true);
        SystemDict.Put("false", false);
        SystemDict.Put("null", PsNull.Instance);

        // ---- Control
        Op("exec", i => i.Execute(i.Pop()));
        Op("if", i => { var p = i.Proc(); if (i.Bool()) i.RunProc(p); });
        Op("ifelse", i => { var no = i.Proc(); var yes = i.Proc(); i.RunProc(i.Bool() ? yes : no); });
        Op("for", i =>
        {
            var p = i.Proc();
            var limitO = i.Pop();
            var incO = i.Pop();
            var initO = i.Pop();
            var whole = initO is int && incO is int && limitO is int;
            double v = ToNum(initO), inc = ToNum(incO), limit = ToNum(limitO);
            if (inc == 0) throw new PsError("rangecheck", "for");
            try
            {
                for (; inc > 0 ? v <= limit : v >= limit; v += inc)
                {
                    i.Push(whole ? (object)(int)v : v);
                    i.RunProc(p);
                }
            }
            catch (PsExit) { }
        });
        Op("repeat", i =>
        {
            var p = i.Proc();
            var n = i.Int();
            if (n < 0) throw new PsError("rangecheck", "repeat");
            try { for (var k = 0; k < n; k++) i.RunProc(p); }
            catch (PsExit) { }
        });
        Op("loop", i =>
        {
            var p = i.Proc();
            try { while (true) i.RunProc(p); }
            catch (PsExit) { }
        });
        Op("exit", _ => throw new PsExit());
        Op("forall", i =>
        {
            var p = i.Proc();
            var o = i.Pop();
            try
            {
                switch (o)
                {
                    case PsArray a:
                        for (var k = 0; k < a.Length; k++) { i.Push(a[k]); i.RunProc(p); }
                        break;
                    case PsString s:
                        for (var k = 0; k < s.Length; k++) { i.Push((int)s[k]); i.RunProc(p); }
                        break;
                    case PsDict d:
                        foreach (var (key, value) in d.Entries) { i.Push(key); i.Push(value); i.RunProc(p); }
                        break;
                    default:
                        throw new PsError("typecheck", "forall");
                }
            }
            catch (PsExit) { }
        });
        Op("stop", _ => throw new PsStop());
        Op("stopped", i =>
        {
            var o = i.Pop();
            var depth = i._depth;
            try
            {
                i.Execute(o);
                i.Push(false);
            }
            catch (Exception e) when (e is PsStop or PsError)
            {
                i._depth = depth;
                if (e is PsError err)
                {
                    i.ErrorInfo.Put("errorname", new PsName(err.Name));
                    i.ErrorInfo.Put("newerror", true);
                }
                i.Push(true);
            }
        });
        Op("quit", _ => throw new PsQuit());
        Op("countexecstack", i => i.Push(i._depth));
        Op("execstack", i => { var a = i.Pop<PsArray>(); i.Push(a.Interval(0, 0)); });

        // ---- Types and attributes
        Op("type", i => i.Push(new PsName(i.Pop() switch
        {
            int => "integertype",
            double => "realtype",
            bool => "booleantype",
            PsName => "nametype",
            PsString => "stringtype",
            PsArray => "arraytype",
            PsDict => "dicttype",
            PsOperator => "operatortype",
            PsMark => "marktype",
            PsNull => "nulltype",
            PsFile => "filetype",
            PsSave => "savetype",
            PsFontId => "fonttype",
            _ => "nulltype",
        }, true)));
        Op("cvlit", i => i.Push(i.Pop() switch
        {
            PsArray a => a.With(false),
            PsName n => n with { Exec = false },
            PsString s => s.Interval(0, s.Length) is var c ? new PsString(c.Data, c.Offset, c.Length) : s,
            var o => o,
        }));
        Op("cvx", i => i.Push(i.Pop() switch
        {
            PsArray a => a.With(true),
            PsName n => n with { Exec = true },
            PsString s => new PsString(s.Data, s.Offset, s.Length, exec: true),
            PsFile f => new PsFile(f.Scanner) { Exec = true },
            var o => o,
        }));
        Op("xcheck", i => i.Push(i.Pop() switch { PsArray a => a.Exec, PsName n => n.Exec, PsString s => s.Exec, PsOperator => true, PsFile f => f.Exec, _ => false }));
        foreach (var name in new[] { "executeonly", "noaccess", "readonly" }) Op(name, _ => { });
        Op("rcheck", i => { i.Pop(); i.Push(true); });
        Op("wcheck", i => { i.Pop(); i.Push(true); });
        Op("cvn", i =>
        {
            var s = i.Pop<PsString>("cvn");
            i.Push(new PsName(s.Text, s.Exec));
        });
        Op("cvs", i =>
        {
            var s = i.Pop<PsString>("cvs");
            var text = Show(i.Pop());
            if (text.Length > s.Length) throw new PsError("rangecheck", "cvs");
            var bytes = Encoding.Latin1.GetBytes(text);
            for (var k = 0; k < bytes.Length; k++) s[k] = bytes[k];
            i.Push(s.Interval(0, bytes.Length));
        });
        Op("cvrs", i =>
        {
            var s = i.Pop<PsString>("cvrs");
            var radix = i.Int();
            var n = i.Num();
            var text = radix == 10 ? Show(Number(n)) : Convert.ToString((long)(uint)(int)n, radix).ToUpperInvariant();
            if (radix != 10 && radix is not (2 or 8 or 16))
            {
                var v = (long)(uint)(int)n;
                var sb = new StringBuilder();
                do { sb.Insert(0, "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"[(int)(v % radix)]); v /= radix; } while (v > 0);
                text = sb.ToString();
            }
            var bytes = Encoding.Latin1.GetBytes(text);
            if (bytes.Length > s.Length) throw new PsError("rangecheck", "cvrs");
            for (var k = 0; k < bytes.Length; k++) s[k] = bytes[k];
            i.Push(s.Interval(0, bytes.Length));
        });

        // ---- Arrays, strings and dictionaries alike
        Op("array", i => { var n = i.Int(); if (n < 0) throw new PsError("rangecheck", "array"); i.Push(new PsArray(Enumerable.Repeat<object>(PsNull.Instance, n).ToList())); });
        Op("packedarray", i => { var n = i.Int(); var items = i._stack.GetRange(i._stack.Count - n, n); i._stack.RemoveRange(i._stack.Count - n, n); i.Push(new PsArray(items)); });
        Op("setpacking", i => i.Pop());
        Op("currentpacking", i => i.Push(false));
        Op("string", i => { var n = i.Int(); if (n < 0) throw new PsError("rangecheck", "string"); i.Push(PsString.Of(new byte[n])); });
        Op("length", i => i.Push(i.Pop() switch
        {
            PsArray a => a.Length,
            PsString s => s.Length,
            PsDict d => d.Count,
            PsName n => n.Value.Length,
            _ => throw new PsError("typecheck", "length"),
        }));
        Op("get", i =>
        {
            var k = i.Pop();
            var o = i.Pop();
            i.Push(o switch
            {
                PsArray a => a[Index(k, a.Length)],
                PsString s => (int)s[Index(k, s.Length)],
                PsDict d => d.Get(k) ?? throw new PsError("undefined", Show(k)),
                _ => throw new PsError("typecheck", "get"),
            });
        });
        Op("put", i =>
        {
            var v = i.Pop();
            var k = i.Pop();
            switch (i.Pop())
            {
                case PsArray a: a[Index(k, a.Length)] = v; break;
                case PsString s: s[Index(k, s.Length)] = (byte)Convert.ToInt32(ToNum(v)); break;
                case PsDict d: d.Put(k, v); break;
                default: throw new PsError("typecheck", "put");
            }
        });
        Op("getinterval", i =>
        {
            var n = i.Int();
            var start = i.Int();
            var o = i.Pop();
            var len = o switch { PsArray a => a.Length, PsString s => s.Length, _ => throw new PsError("typecheck", "getinterval") };
            if (start < 0 || n < 0 || start + n > len) throw new PsError("rangecheck", "getinterval");
            i.Push(o is PsArray arr ? arr.Interval(start, n) : ((PsString)o).Interval(start, n));
        });
        Op("putinterval", i =>
        {
            var from = i.Pop();
            var start = i.Int();
            switch (i.Pop(), from)
            {
                case (PsArray a, PsArray b):
                    if (start < 0 || start + b.Length > a.Length) throw new PsError("rangecheck", "putinterval");
                    for (var k = 0; k < b.Length; k++) a[start + k] = b[k];
                    break;
                case (PsString a, PsString b):
                    if (start < 0 || start + b.Length > a.Length) throw new PsError("rangecheck", "putinterval");
                    for (var k = 0; k < b.Length; k++) a[start + k] = b[k];
                    break;
                default:
                    throw new PsError("typecheck", "putinterval");
            }
        });
        Op("aload", i => { var a = i.Pop<PsArray>("aload"); i._stack.AddRange(a.All()); i.Push(a); });
        Op("astore", i =>
        {
            var a = i.Pop<PsArray>("astore");
            if (i._stack.Count < a.Length) throw new PsError("stackunderflow", "astore");
            for (var k = a.Length - 1; k >= 0; k--) a[k] = i.Pop();
            i.Push(a);
        });
        Op("search", i =>
        {
            var seek = i.Pop<PsString>("search");
            var s = i.Pop<PsString>("search");
            var at = s.Span.IndexOf(seek.Span);
            if (at < 0) { i.Push(s); i.Push(false); return; }
            i.Push(s.Interval(at + seek.Length, s.Length - at - seek.Length));
            i.Push(s.Interval(at, seek.Length));
            i.Push(s.Interval(0, at));
            i.Push(true);
        });
        Op("anchorsearch", i =>
        {
            var seek = i.Pop<PsString>("anchorsearch");
            var s = i.Pop<PsString>("anchorsearch");
            if (!s.Span.StartsWith(seek.Span)) { i.Push(s); i.Push(false); return; }
            i.Push(s.Interval(seek.Length, s.Length - seek.Length));
            i.Push(s.Interval(0, seek.Length));
            i.Push(true);
        });
        Op("token", i =>
        {
            var o = i.Pop();
            if (o is PsString s)
            {
                var sc = new Scanner(s.Span.ToArray());
                if (sc.Next() is { } t)
                {
                    i.Push(s.Interval(sc.Position, s.Length - sc.Position));
                    i.Push(t);
                    i.Push(true);
                }
                else i.Push(false);
            }
            else if (o is PsFile f)
            {
                if (f.Scanner.Next(i.Immediate) is { } t) { i.Push(t); i.Push(true); }
                else i.Push(false);
            }
            else throw new PsError("typecheck", "token");
        });

        // ---- Dictionaries
        Op("dict", i => i.Push(new PsDict(Math.Max(1, i.Int()))));
        Op("maxlength", i => i.Push(i.Pop<PsDict>("maxlength").Capacity));
        Op("begin", i => { if (i._dicts.Count > 100) throw new PsError("dictstackoverflow"); i._dicts.Add(i.Pop<PsDict>("begin")); });
        Op("end", i => { if (i._dicts.Count <= 2) throw new PsError("dictstackunderflow"); i._dicts.RemoveAt(i._dicts.Count - 1); });
        Op("def", i => { var v = i.Pop(); var k = i.Pop(); i.CurrentDict.Put(k, v); });
        Op("load", i => { var k = i.Pop(); i.Push((i.Where(k) ?? throw new PsError("undefined", Show(k))).Get(k)!); });
        Op("store", i => { var v = i.Pop(); var k = i.Pop(); (i.Where(k) ?? i.CurrentDict).Put(k, v); });
        Op("undef", i => { var k = i.Pop(); i.Pop<PsDict>("undef").Remove(k); });
        Op("known", i => { var k = i.Pop(); i.Push(i.Pop<PsDict>("known").Contains(k)); });
        Op("where", i =>
        {
            var k = i.Pop();
            if (i.Where(k) is { } d) { i.Push(d); i.Push(true); }
            else i.Push(false);
        });
        Op("currentdict", i => i.Push(i.CurrentDict));
        Op("countdictstack", i => i.Push(i._dicts.Count));
        Op("cleardictstack", i => i._dicts.RemoveRange(2, i._dicts.Count - 2));
        Op("dictstack", i =>
        {
            var a = i.Pop<PsArray>("dictstack");
            for (var k = 0; k < i._dicts.Count && k < a.Length; k++) a[k] = i._dicts[k];
            i.Push(a.Interval(0, Math.Min(a.Length, i._dicts.Count)));
        });
        Op("bind", i => { var p = i.Proc(); i.Bind(p, 0); i.Push(p); });

        // ---- Memory, the machine, output
        Op("save", i => { i.Gsave(); i.Push(new PsSave(i._gstack.Count)); });
        Op("restore", i =>
        {
            var s = i.Pop<PsSave>("restore");
            while (i._gstack.Count >= s.Depth) i.GrestoreOne();
        });
        Op("vmstatus", i => { i.Push(0); i.Push(1_000_000); i.Push(10_000_000); });
        Op("vmreclaim", i => i.Pop());
        Op("setglobal", i => i.Pop());
        Op("currentglobal", i => i.Push(false));
        Op("gcheck", i => { i.Pop(); i.Push(false); });
        Op("languagelevel", i => i.Push(2));
        Op("version", i => i.Push(new PsString("3010")));
        Op("product", i => i.Push(new PsString("AVAPageStream")));
        Op("revision", i => i.Push(1));
        Op("serialnumber", i => i.Push(0));
        Op("realtime", i => i.Push(0));
        Op("usertime", i => i.Push(0));
        Op("print", i => i.Output.Append(i.Pop<PsString>("print").Text));
        Op("=", i => i.Output.Append(Show(i.Pop())).Append('\n'));
        Op("==", i => i.Output.Append(Write(i.Pop())).Append('\n'));
        Op("stack", i => { foreach (var o in Enumerable.Reverse(i._stack)) i.Output.Append(Show(o)).Append('\n'); });
        Op("pstack", i => { foreach (var o in Enumerable.Reverse(i._stack)) i.Output.Append(Write(o)).Append('\n'); });
        Op("flush", _ => { });
        Op("echo", i => i.Pop());
        Op("executive", _ => { });
        Op("prompt", _ => { });
        Op("handleerror", _ => { });

        // ---- Files: only the program itself, as currentfile
        Op("currentfile", i => i.Push(i.File ?? throw new PsError("undefined", "currentfile")));
        Op("closefile", i => i.Pop());
        Op("readstring", i => i.ReadInto(hex: false));
        Op("readhexstring", i => i.ReadInto(hex: true));
        Op("readline", i =>
        {
            var s = i.Pop<PsString>("readline");
            var f = i.Pop<PsFile>("readline");
            var n = 0;
            int c;
            while ((c = f.Scanner.Read()) >= 0)
            {
                if (c == 10) break;
                if (c == 13) { if (f.Scanner.Peek() == 10) f.Scanner.Read(); break; }
                if (n >= s.Length) throw new PsError("rangecheck", "readline");
                s[n++] = (byte)c;
            }
            i.Push(s.Interval(0, n));
            i.Push(c >= 0);
        });
        Op("read", i =>
        {
            var f = i.Pop<PsFile>("read");
            var c = f.Scanner.Read();
            if (c >= 0) { i.Push(c); i.Push(true); }
            else i.Push(false);
        });
        Op("bytesavailable", i => { i.Pop(); i.Push(-1); });
        Op("flushfile", i =>
        {
            // Skips the rest of the file: the end of the data of an EPS that is being thrown away.
            var f = i.Pop<PsFile>("flushfile");
            while (f.Scanner.Read() >= 0) { }
        });
        Op("status", i => { i.Pop(); i.Push(false); });
        Op("file", i => throw new PsError("invalidfileaccess", "file"));
        Op("run", i => throw new PsError("invalidfileaccess", "run"));
        Op("deletefile", i => throw new PsError("invalidfileaccess", "deletefile"));
    }

    private int CountToMark()
    {
        for (var k = _stack.Count - 1; k >= 0; k--)
            if (_stack[k] is PsMark) return _stack.Count - 1 - k;
        throw new PsError("unmatchedmark");
    }

    private static int Index(object k, int length)
    {
        var i = k is int n ? n : throw new PsError("typecheck", "index");
        if (i < 0 || i >= length) throw new PsError("rangecheck", "index");
        return i;
    }

    /// <summary>Whether two objects are equal as eq compares them: numbers by value, strings and names by their text.</summary>
    public static bool Same(object a, object b) => (a, b) switch
    {
        (int or double, int or double) => ToNum(a) == ToNum(b),
        (PsString or PsName, PsString or PsName) => (a is PsString s1 ? s1.Text : ((PsName)a).Value) == (b is PsString s2 ? s2.Text : ((PsName)b).Value),
        (bool x, bool y) => x == y,
        (PsArray x, PsArray y) => ReferenceEquals(x.Items, y.Items) && x.Offset == y.Offset && x.Length == y.Length,
        (PsNull, PsNull) or (PsMark, PsMark) => true,
        _ => ReferenceEquals(a, b),
    };

    /// <summary>Replaces names in a procedure that are operators now with the operators, procedures in it too.</summary>
    private void Bind(PsArray p, int depth)
    {
        if (depth > 50) return;
        for (var k = 0; k < p.Length; k++)
            switch (p[k])
            {
                case PsName { Exec: true } n when Lookup(n.Value) is PsOperator op:
                    p[k] = op;
                    break;
                case PsArray { Exec: true } inner:
                    Bind(inner, depth + 1);
                    break;
            }
    }

    /// <summary>readstring and readhexstring: fill a string from the file; true if it was filled.</summary>
    private void ReadInto(bool hex)
    {
        var s = Pop<PsString>("readstring");
        var f = Pop<PsFile>("readstring");
        var n = 0;
        if (hex)
        {
            var half = -1;
            while (n < s.Length)
            {
                var c = f.Scanner.Read();
                if (c < 0) break;
                var d = Scanner.Hex(c);
                if (d < 0) continue;
                if (half < 0) half = d;
                else { s[n++] = (byte)(half << 4 | d); half = -1; }
            }
        }
        else
        {
            int c;
            while (n < s.Length && (c = f.Scanner.Read()) >= 0) s[n++] = (byte)c;
        }
        Push(s.Interval(0, n));
        Push(n == s.Length);
    }
}
