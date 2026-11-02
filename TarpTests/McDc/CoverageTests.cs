using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp.McDc;

namespace TarpTests.McDc;

// line, branch and function coverage (CoverageRewriter), run on a compiled class like McDcEndToEndTests
[TestClass]
public class CoverageTests
{
    private const string Source = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
public static class T
{
    public static int Classify(int x)
    {
        if (x < 0)
            return -1;
        var y = x * 2;
        switch (y)
        {
            case 0:
                return 0;
            case 2:
                return 1;
        }
        return 2;
    }

    public static int Expression(int x) => x + 1;
    public static int Never(int x) => x - 1;

    public static int Throws(bool b)
    {
        var a = 1;
        if (b) throw new InvalidOperationException();
        a++;
        return a;
    }

    public static bool Empty(bool b)
    {
        if (b) { } else { }
        return b;
    }

    public static int Prop => 42;
    public static int UseProp() => Prop;

    static IEnumerable<int> Iterator(int n) { for (var i = 0; i < n; i++) yield return i; }
    public static int Sum(int n) { var s = 0; foreach (var v in Iterator(n)) s += v; return s; }

    static int _f;
    static int Field { get => _f; set => _f = value; }
    public static int SetGet(int v) { Field = v; return Field; }

    public static int Label(int n) { var i = 0; again: i++; if (i < n) goto again; return i; }
    public static int Local(int n) { int Twice(int v) => v * 2; return Twice(n); }
    public static int Lambda(int n) { Func<int, int> f = v => { var w = v + 1; return w; }; return f(n); }

    public static int Bodies() { var r = new R(); r.First = 7; return r.First + r.At(1) + r[0] + (int)(r + r); }
    public static bool Async() { R.Delay().GetAwaiter().GetResult(); return R.DelayValue().GetAwaiter().GetResult() == 3; }
    public static bool Fails() { try { R.Fail(); return false; } catch (InvalidOperationException) { return true; } }
}

// every kind of expression body
public class R
{
    int[] _a = { 1, 2 };
    public ref int First => ref _a[0];
    public ref int At(int i) => ref _a[i];
    public int this[int i] => _a[i];
    public R() => _a[1] = 5;
    ~R() => GC.KeepAlive(this);
    public static R operator +(R a, R b) => a;
    public static implicit operator int(R r) => r._a[0];
    public int Init { get; init => _ = value; }
    event Action? E;
    public event Action? E2 { add => E += value; remove => E -= value; }
    public static void Fail() => throw new InvalidOperationException();
    public static async Task Delay() => await Task.Yield();
    public static async Task<int> DelayValue() => await Task.FromResult(3);
}";

    // the (1-based) line of Source that contains text
    private static int LineOf(string text) =>
        Source.Split('\n').Select((l, i) => (l, i)).Single(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;

    [TestMethod]
    public void LinesFunctionsAndBranches()
    {
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Classify", -5);
        p.Check("Classify", 1);
        p.Check("Expression", 1);
        var throws = () => p.Run("Throws", true);
        throws.Should().Throw<System.Reflection.TargetInvocationException>();

        var c = p.Coverage();

        c.LinesRun.Should().Contain(new[] { LineOf("if (x < 0)"), LineOf("return -1;"), LineOf("var y = x * 2;"), LineOf("switch (y)"), LineOf("return 1;") });
        c.Lines.Except(c.LinesRun).Should().Contain(new[] { LineOf("return 0;"), LineOf("return 2;") });

        // a statement after one that throws doesn't count as run
        c.LinesRun.Should().Contain(LineOf("if (b) throw new InvalidOperationException();"));
        c.Lines.Except(c.LinesRun).Should().Contain(new[] { LineOf("a++;"), LineOf("return a;") });

        var sw = c.Switches.Single(s => s.Switch.Kind == SwitchEntry.Statement);
        (sw.Taken, sw.Total).Should().Be((1, 3));
        sw.NotTaken.Should().Equal("case 0:", "no case matched");

        var functions = c.Functions.ToDictionary(f => f.Function.Name);
        functions["T.Classify(int)"].Entered.Should().BeTrue();
        functions["T.Classify(int)"].ExitsNotTaken.Should().Equal($"line {LineOf("return 0;")}", $"line {LineOf("return 2;")}");
        functions["T.Expression(int)"].Entered.Should().BeTrue();
        functions["T.Never(int)"].Entered.Should().BeFalse();
        functions["T.Throws(bool)"].ExitsTaken.Should().Be(1);
    }

    [TestMethod]
    public void EveryKindOfBodyStillWorks()
    {
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Empty", true);
        p.Check("UseProp");
        p.Check("Sum", 3);
        p.Check("SetGet", 4);
        p.Check("Label", 3);
        p.Check("Local", 2);
        p.Check("Lambda", 1);
        p.Check("Bodies");
        p.Check("Async");
        p.Check("Fails");

        var c = p.Coverage();
        var entered = c.Functions.Where(f => f.Entered).Select(f => f.Function.Name).ToList();
        entered.Should().Contain(new[]
        {
            "T.Prop.get", "T.Iterator(int)", "T.Field.get", "T.Field.set", "Twice(int)",
            "R.First.get", "R.At(int)", "R.this[int].get", "R.R()", "R.operator +(R, R)", "R.implicit operator int(R)",
            "R.Fail()", "R.Delay()", "R.DelayValue()"
        });

        // if (b) { } else { }: the then block ran, the else block didn't, the decision was only true
        c.Decisions.Single(d => d.Decision.Line == LineOf("if (b) { } else { }")).OutcomeFalse.Should().BeFalse();
        c.LinesRun.Should().Contain(LineOf("var w = v + 1;"));
    }

    // where there's no room for a statement: probes in expressions
    private const string Expressions = @"
using System;
using System.Collections.Generic;
public static class T
{
    public static int DoWhile(int n)
    {
        var i = 0;
        do
        {
            i++;
        }
        while (i < n);
        return i;
    }

    public static int Lambdas(int n)
    {
        Func<int, int> twice = x =>
            x * 2;
        Func<int, int> never = x =>
            x - 1;
        Action nothing = () => { };
        nothing();
        return twice(n);
    }

    public static string Arms(int x) => x switch
    {
        0 => ""zero"",
        > 0 when x < 10 => ""small"",
        _ => ""other""
    };

    public static string Coalesce(string? a, string? b) => a ?? b ?? ""none"";
    public static int? NullableValue(int? a) => a ?? 3;
    public static TValue Generic<TValue>(TValue? a, TValue b) where TValue : class => a ?? b;

    // the second condition needs the first one's pattern variable, inside the probed do condition
    public static int DoPattern(object[] xs) { var i = 0; object o; do { o = xs[i++]; } while (!(o is string s) || s.Length == 0); return i; }

    static readonly int Initialized = Compute();
    static int Compute() => 7;
    static int[] Array = { 1, 2 };
    public static int Field() => Initialized + Array.Length;
    public static List<int> Items { get; } = new() { 1 };
    public static int Count() => Items.Count;

    public static int Guard(object o) { switch (o) { case int i when i > 0: return 1; default: return 0; } }
    public static bool Filter(bool b) { try { throw new InvalidOperationException(); } catch (InvalidOperationException) when (b) { return true; } catch { return false; } }
}";

    private static int LineIn(string source, string text) =>
        source.Split('\n').Select((l, i) => (l, i)).Single(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;

    [TestMethod]
    public void ProbesInExpressions()
    {
        var p = McDcEndToEndTests.Build(Expressions);
        p.Check("DoWhile", 3);
        p.Check("Lambdas", 2);
        p.Check("Arms", 0);
        p.Check("Arms", 5);
        p.Check("Coalesce", null, "b");
        p.Check("Coalesce", "a", null);
        p.Check("Field");
        p.Check("Count");
        p.Check("NullableValue", new object?[] { null });
        p.Check("NullableValue", 1);
        p.Check("DoPattern", new object[] { new object[] { 1, "", "x" } });
        p.Check("Guard", 5);
        p.Check("Filter", true);
        p.Check("Filter", false);

        var c = p.Coverage();
        int Line(string text) => LineIn(Expressions, text);

        c.LinesRun.Should().Contain(new[]
        {
            Line("while (i < n);"), Line("x * 2;"), Line("0 => \"zero\""), Line("> 0 when x < 10 =>"),
            Line("static readonly int Initialized = Compute();"), Line("Items { get; } = new() { 1 };")
        });
        // inside a statement that ran: the lambda that never ran, the arm never taken
        c.Lines.Except(c.LinesRun).Should().Contain(new[] { Line("x - 1;"), Line("_ =>") });

        var arms = c.Switches.Single(s => s.Switch.Kind == SwitchEntry.Expression);
        arms.NotTaken.Should().Equal("_");

        // a ?? (b ?? "none"): the inner right side was never used
        var coalesces = c.Switches.Where(s => s.Switch.Kind == SwitchEntry.Coalesce).ToList();
        coalesces.Should().HaveCount(4);
        // and Generic was never called
        coalesces.SelectMany(s => s.NotTaken).Should().Equal("right side used", "left side not null", "right side used");

        // single-condition when guards and catch filters are decisions
        var guard = p.Result("i > 0");
        (guard.OutcomeTrue, guard.OutcomeFalse).Should().Be((true, false));
        var filter = p.Result("b");
        (filter.OutcomeTrue, filter.OutcomeFalse).Should().Be((true, true));
    }

    private const string More = @"
using System;
using System.Linq;
public class Base { public Base(int x) { X = x; } public int X; }
public class Derived : Base { public Derived(int x) : base(x + 1) { } }
public class Primary(int x) : Base(x * 2) { }
public static class Ext
{
    extension(int n)
    {
        public bool IsSmall() => n < 10 && n > -10;
    }
}
public static class T
{
    static int[] Numbers = { 1, 2, 3 };
    static event Action Changed = () => { };
    public static int Arrays() { Changed(); return Numbers.Length; }

    public static int Bases() => new Derived(1).X + new Primary(2).X;

    public static int InMemory(int[] xs) => (from x in xs where x > 0 && x < 10 select x).Count();
    public static int Queryable(int[] xs) => (from x in xs.AsQueryable() where x > 0 && x < 10 select x).Count();

    public static int Loop(int[] xs) { var s = 0; foreach (var x in xs) s += x; return s; }
    public static int? Length(string? s) => s?.Length;
    public static bool Small(int n) => n.IsSmall();
    public static bool Coupled(bool y) => y && !y;
}";

    [TestMethod]
    public void InitializersQueriesLoopsAndNullConditionals()
    {
        var p = McDcEndToEndTests.Build(More);
        p.Check("Arrays");
        p.Check("Bases");
        p.Check("InMemory", new[] { 5, 20 });
        p.Check("Queryable", new[] { 5, 20 });
        p.Check("Loop", new int[0]);
        p.Check("Loop", new[] { 1, 2 });
        p.Check("Length", new object?[] { null });
        p.Check("Length", "ab");
        p.Check("Small", 3);
        p.Check("Coupled", true);

        var c = p.Coverage();
        int Line(string text) => LineIn(More, text);

        c.LinesRun.Should().Contain(new[]
        {
            Line("static int[] Numbers = { 1, 2, 3 };"), Line("static event Action Changed"),
            Line(": base(x + 1)"), Line("public class Primary(int x) : Base(x * 2)")
        });

        // over an array: instrumented; over IQueryable: an expression tree, left alone
        p.Result("x > 0 && x < 10").Executed.Should().BeTrue();
        p.Map.Skipped.Should().ContainSingle(s => s.Reason == "in a query that becomes an expression tree");

        var loop = c.Switches.Single(s => s.Switch.Kind == SwitchEntry.ForEach);
        (loop.Taken, loop.Total).Should().Be((2, 2));
        var access = c.Switches.Single(s => s.Switch.Kind == SwitchEntry.ConditionalAccess);
        (access.Taken, access.Total).Should().Be((2, 2));

        p.Result("n < 10 && n > -10").Executed.Should().BeTrue();

        // y && !y: y can't be true then false in one evaluation, so no test shows the second y false
        var coupled = p.Result("y && !y").Conditions[1];
        coupled.ShownFalse.Should().BeFalse();
        coupled.Suggestions.Should().BeEmpty();
    }

    [TestMethod]
    public void ForEachWithNoIterations()
    {
        var p = McDcEndToEndTests.Build(More);
        p.Check("Loop", new int[0]);

        var loop = p.Coverage().Switches.Single(s => s.Switch.Kind == SwitchEntry.ForEach);
        loop.NotTaken.Should().Equal("iterated");
    }

    [TestMethod]
    public void TopLevelStatements()
    {
        var p = McDcEndToEndTests.Build("var x = args.Length;\nif (x > 0)\n    return 1;\nreturn 0;\n", OutputKind.ConsoleApplication);

        var main = p.Map.Functions.Single(f => f.Name == "<top-level statements>");
        main.Exits.Should().HaveCount(2);
        p.Map.Probes.Where(x => x.Kind == ProbeEntry.Statement).Select(x => x.Line).Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }
}
