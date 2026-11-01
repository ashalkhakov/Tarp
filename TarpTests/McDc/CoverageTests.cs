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

        var sw = c.Switches.Single();
        (sw.Taken, sw.Total).Should().Be((1, 3));
        sw.NotTaken.Should().Equal("case 0:", "no case matched");

        var functions = c.Functions.ToDictionary(f => f.Function.Name);
        functions["T.Classify(int)"].Entered.Should().BeTrue();
        functions["T.Classify(int)"].ExitsNotTaken.Should().Equal(LineOf("return 0;"), LineOf("return 2;"));
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

    [TestMethod]
    public void TopLevelStatements()
    {
        var p = McDcEndToEndTests.Build("var x = args.Length;\nif (x > 0)\n    return 1;\nreturn 0;\n", OutputKind.ConsoleApplication);

        var main = p.Map.Functions.Single(f => f.Name == "<top-level statements>");
        main.Exits.Should().HaveCount(2);
        p.Map.Probes.Where(x => x.Kind == ProbeEntry.Statement).Select(x => x.Line).Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }
}
