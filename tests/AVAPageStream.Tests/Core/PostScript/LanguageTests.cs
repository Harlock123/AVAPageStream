using AVAPageStream.Core.PostScript;

namespace AVAPageStream.Tests.Core.PostScript;

/// <summary>The PostScript language, operator group by operator group: each program's stack, as == shows it.</summary>
public class LanguageTests
{
    private static string Run(string program)
    {
        var i = new Interpreter();
        if (i.Run(program) is { } e) return "error: " + e.Name;
        return string.Join(" ", i.Stack.Select(Interpreter.Write));
    }

    [Theory]
    [InlineData("1 2 add 7 3 sub 4 5 mul", "3 4 20")]
    [InlineData("7 2 div 7 2 idiv -7 2 mod", "3.5 3 -1")]
    [InlineData("2147483647 1 add", "2147483648.0")]                 // an integer too big becomes real
    [InlineData("3.7 ceiling 3.7 floor -3.5 round 3.7 truncate", "4.0 3.0 -3.0 3.0")]
    [InlineData("16 sqrt 0 1 atan 90 sin 2 10 exp", "4.0 0.0 1.0 1024.0")]
    [InlineData("16#FF 8#17 2#101 1e2 .5 -.5", "255 15 5 100.0 0.5 -0.5")]
    [InlineData("3.9 cvi (42) cvi 2 cvr", "3 42 2.0")]
    [InlineData("1 0 div", "error: undefinedresult")]
    public void Arithmetic(string program, string stack) => Assert.Equal(stack, Run(program));

    [Theory]
    [InlineData("1 2 exch 3 dup", "2 1 3 3")]
    [InlineData("1 2 3 2 copy", "1 2 3 2 3")]
    [InlineData("1 2 3 4 2 index", "1 2 3 4 2")]
    [InlineData("1 2 3 4 3 1 roll", "1 4 2 3")]
    [InlineData("1 2 3 4 3 -1 roll", "1 3 4 2")]
    [InlineData("mark 1 2 counttomark", "-mark- 1 2 2")]
    [InlineData("1 mark 2 3 cleartomark count", "1 1")]
    [InlineData("pop", "error: stackunderflow")]
    public void The_stack(string program, string stack) => Assert.Equal(stack, Run(program));

    [Theory]
    [InlineData("1 1.0 eq (a) /a eq 1 2 lt (b) (a) gt", "true true true true")]
    [InlineData("true false and true false or 12 10 xor 1 not", "false true 6 -2")]
    [InlineData("1 3 bitshift 8 -2 bitshift", "8 2")]
    public void Comparing_and_logic(string program, string stack) => Assert.Equal(stack, Run(program));

    [Theory]
    [InlineData("true {1} {2} ifelse false {3} if", "1")]
    [InlineData("0 1 1 4 {add} for", "10")]
    [InlineData("1 -0.5 0 {} for", "1.0 0.5 0.0")]                      // a real operand: a real counter
    [InlineData("0 3 {1 add} repeat", "3")]
    [InlineData("0 {1 add dup 5 eq {exit} if} loop", "5")]
    [InlineData("0 [1 2 3] {add} forall (ab) {} forall", "6 97 98")]
    [InlineData("{1 0 div} stopped {nonsense} stopped", "true true")]
    [InlineData("{2 3 add} exec /x {7} def x", "5 7")]
    [InlineData("1 {2 {3} } exec", "1 2 {3}")]                            // a procedure in a procedure is data
    [InlineData("{ {} loop } exec", "error: timeout")]
    public void Control(string program, string stack)
    {
        var i = new Interpreter { StepLimit = 100_000 };
        var result = i.Run(program) is { } e ? "error: " + e.Name : string.Join(" ", i.Stack.Select(Interpreter.Write));
        Assert.Equal(stack, result);
    }

    [Theory]
    [InlineData("[1 2 3] length [1 [2]] 1 get", "3 [2]")]
    [InlineData("3 array dup 1 /x put", "[null /x null]")]
    [InlineData("[1 2 3 4] 1 2 getinterval", "[2 3]")]
    [InlineData("[1 2 3] aload pop", "1 2 3")]
    [InlineData("1 2 3 [null null null] astore", "[1 2 3]")]
    [InlineData("(hello) 1 3 getinterval (abc) dup 0 88 put", "(ell) (Xbc)")]
    [InlineData("(a.b.c) (.) search", "(b.c) (.) (a) true")]
    [InlineData("(hello) (he) anchorsearch", "(llo) (he) true")]
    [InlineData("( 12 abc) token", "(abc) 12 true")]                    // the space after the token goes with it
    [InlineData("(abc) length (\\101\\x\\(\\)) ", "3 (Ax())")]
    [InlineData("<48 69> <~87cURD]i,\"Ebo80~>", "(Hi) (Hello World!)")]
    [InlineData("42 10 string cvs /name 10 string cvs 255 16 5 string cvrs", "(42) (name) (FF)")]
    [InlineData("/abc cvx xcheck {1} cvlit xcheck (s) cvn", "true false /s")]
    [InlineData("1 type /a type [] type (s) type", "/integertype /nametype /arraytype /stringtype")]
    public void Arrays_and_strings(string program, string stack) => Assert.Equal(stack.Replace("/integertype", "integertype").Replace("/nametype", "nametype").Replace("/arraytype", "arraytype").Replace("/stringtype", "stringtype"), Run(program));

    [Theory]
    [InlineData("/d 5 dict def d /a 1 put d /a get d /b known", "1 false")]
    [InlineData("<< /a 1 /b 2 >> dup length exch /b get", "2 2")]
    [InlineData("/x 1 def 3 dict begin /x 2 def x end x", "2 1")]
    [InlineData("/nothere where /systemdict where exch pop", "false true")]
    [InlineData("/x 5 def /x load /x 9 store x", "5 9")]
    [InlineData("<< (s) 1 >> /s get", "1")]                              // a string key is the same as the name
    [InlineData("<< 1 (one) >> 1.0 get", "(one)")]                       // 1 and 1.0 are the same key
    [InlineData("/p {add} bind def /p load 0 get type", "operatortype")]
    [InlineData("nosuchname", "error: undefined")]
    public void Dictionaries(string program, string stack) => Assert.Equal(stack, Run(program));

    [Fact]
    public void Data_is_read_after_the_operator_from_the_program_itself()
    {
        var i = new Interpreter();
        Assert.Null(i.Run("currentfile 3 string readstring\nabc\n pop currentfile 2 string readhexstring 4142 pop currentfile 20 string readline\nthe rest\npop"));
        Assert.Equal("(abc) (AB) (the rest)", string.Join(" ", i.Stack.Select(Interpreter.Write)));
        // A dictionary's own names, given with //, are looked up as the program is read.
        var j = new Interpreter();
        Assert.Null(j.Run("/k 7 def {//k} 0 get"));
        Assert.Equal("7", Interpreter.Write(j.Stack[0]));
    }

    [Fact]
    public void Printing_writes_to_the_output()
    {
        var i = new Interpreter();
        Assert.Null(i.Run("(hi) print 3 = [1 (a)] == /n ="));
        Assert.Equal("hi3\n[1 (a)]\nn\n", i.Output.ToString());
    }
}
