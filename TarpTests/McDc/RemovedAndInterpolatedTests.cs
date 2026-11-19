using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp.McDc;

namespace TarpTests.McDc;

// ?? ?. and decisions in interpolation holes (#20), and code the compiler removes (#21)
[TestClass]
public class RemovedAndInterpolatedTests
{
    private const string Holes = @"
public static class T
{
    public static string Paid(string? by) => $""Paid by {by ?? ""nobody""}."";
    public static string Length(string? s) => $""Length {s?.Length}."";
    public static string Verbatim(string? by) => $@""Paid\by {by ?? ""nobody""}"";
    public static string Raw(string? by) => $""""""Paid by {by ?? ""nobody""}"""""";
    public static string Formatted(double? x) => $""[{x ?? 0,8:N2}]"";
    public static string Decision(bool a, bool b) => $""{a && b} {(a || b ? ""y"" : ""n"")}"";
}";

    // it compiles (Build checks), and each method gives what the original gives
    [TestMethod]
    public void InterpolationHolesAreParenthesized()
    {
        var p = McDcEndToEndTests.Build(Holes);
        foreach (var by in new[] { null, "Ann" })
        {
            p.Check("Paid", by);
            p.Check("Length", by);
            p.Check("Verbatim", by);
            p.Check("Raw", by);
        }
        p.Check("Formatted", (double?)null);
        p.Check("Formatted", 3.14159);
        p.Check("Decision", true, false);
        p.Check("Decision", false, false);

        p.InstrumentedSource.Should().Contain("{(global::TarpGen.McDc.").And.Contain("),8:N2}");
        p.Result("a && b").Executed.Should().BeTrue();
    }

    private const string Removed = @"#define KEPT
using System;
using System.Diagnostics;

public static partial class T
{
    [Conditional(""NEVER_DEFINED"")]
    static void Check(Func<int, bool> p) { }

    [Conditional(""NEVER_DEFINED"")]
    static void Log(string m)
    {
        if (m.Length > 0 && m != ""x"")
            Console.WriteLine(m);
    }

    [Conditional(""KEPT"")]
    static void Kept(bool b) { }

    static partial void Hook(bool b);

    public static int Abs(int x, string? s)
    {
        Check(r => r >= 0 && r != int.MinValue);
        Debug.Assert(x > 0 || x <= 0, s ?? ""none"");
        Log(s?.Trim() ?? """");
        Hook(x > 1 && x < 5);
        Kept(x > 2 && x < 9);
        return x < 0 ? -x : x;
    }
}";

    // the compiler drops the calls (arguments included) to [Conditional] methods whose symbols aren't
    // defined, and to partial methods without an implementation: nothing in them is a gap
    [TestMethod]
    public void CallsTheCompilerRemovesAreNotCovered()
    {
        var p = McDcEndToEndTests.Build(Removed);
        p.Check("Abs", -2, null);
        p.Check("Abs", 2, "a");

        p.Map.Decisions.Select(d => d.Text).Should().BeEquivalentTo("x > 2 && x < 9", "x < 0");
        p.Map.Switches.Should().BeEmpty("the ?? and ?. are in removed calls");
        p.Map.Functions.Select(f => f.Name).Should().BeEquivalentTo(new[] { "T.Kept(bool)", "T.Abs(int, string?)" },
            "Check and Log are never called in this project");

        var f = p.Coverage();
        int LineOf(string text) => Removed.Split('\n').Select((l, i) => (l, i)).Single(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;
        f.Lines.Should().NotContain(new[] { LineOf("Check(r =>"), LineOf("Debug.Assert"), LineOf("Log(s?"), LineOf("Hook(x"), LineOf("Console.WriteLine") })
            .And.Contain(LineOf("Kept(x > 2"));
        f.Lines.Except(f.LinesRun).Should().BeEmpty();
    }
}
