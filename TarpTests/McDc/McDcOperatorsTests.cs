using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp.McDc;

namespace TarpTests.McDc;

// decisions with & | ^ == != and ?: on bools, and with and/or/not patterns
[TestClass]
public class McDcOperatorsTests
{
    // the direct masking rule (DecisionTree.Shown) is McDcFunctions' monitor, on && || ! trees
    [TestMethod]
    public void DirectMaskingIsTheMonitor()
    {
        var shapes = new[]
        {
            "c0 && c1", "c0 || c1", "(c0 && c1) || c2", "(c0 || c1) && (c2 || c3)",
            "!(c0 && c1) || c2", "(c0 || c1 || c2) && c3", "c0 && (c1 || !c2) && c3"
        };
        foreach (var shape in shapes)
        {
            var leaves = new List<ExpressionSyntax>();
            var tree = DecisionTree.Parse(shape, leaves, allOperators: true);
            var markers = new McDcFunctions().CreateMarkers(tree);
            for (uint input = 0; input < 1u << leaves.Count; input++)
            {
                uint evaluated = 0;
                DecisionTree.Evaluate(tree, input, ref evaluated);
                var values = input & evaluated;
                DecisionTree.Shown(tree, evaluated, values).Should().Be(McDcAnalyzer.Monitor(markers, evaluated, values), $"{shape}, input {input}");
            }
        }
    }

    private const string Source = @"
public static class T
{
    public static bool Eq(bool a, bool b, bool c) => a == (b || c);
    public static bool Xor(bool a, bool b) => a ^ b;
    public static bool And(bool a, bool b) => a & b;
    public static bool Cond(bool a, bool b, bool c) => a && (b ? c : !c);
    public static bool Plain(int x) => x == 3;
    public static bool WithConstant(bool a) => a == true;

    public static bool Range(int x) => x is > 0 and < 10;
    public static bool Either(object o) => o is int or long;
    public static int Narrow(object o) => o is string s and { Length: > 2 } ? s.Length : -1;
    public static bool NotEither(int x) => x is not (1 or 2);
    public static bool InDecision(int x, bool b) => b && x is >= 0 and <= 5;
    public static bool Generic<TIn>(TIn v) => v is string and { Length: > 0 };
}";

    private static void AllInputs(McDcEndToEndTests.Program p, string method, int n)
    {
        for (var input = 0; input < 1 << n; input++)
        {
            p.Check(method, Enumerable.Range(0, n).Select(i => (object?)((input & 1 << i) != 0)).ToArray());
        }
    }

    private static void ShouldBeCovered(DecisionResult r) => r.Conditions.Should().OnlyContain(c => c.Covered);

    [TestMethod]
    public void BoolOperatorsAreOperators()
    {
        var p = McDcEndToEndTests.Build(Source);
        AllInputs(p, "Eq", 3);
        AllInputs(p, "Xor", 2);
        AllInputs(p, "Cond", 3);
        p.Check("Plain", 3);
        p.Check("WithConstant", true);

        var eq = p.Result("a == (b || c)");
        eq.Decision.Shape.Should().Be("(c0) == ((c1) || (c2))");
        eq.Decision.Conditions.Should().Equal("a", "b", "c");
        // the markers record every evaluation so far; only the complete ones count (b true: c short-circuited)
        eq.Evaluations.Should().HaveCount(6);
        ShouldBeCovered(eq);
        ShouldBeCovered(p.Result("a ^ b"));

        // b ? c : !c: the second c and the third are conditions of their own
        var cond = p.Result("a && (b ? c : !c)");
        cond.Decision.Conditions.Should().Equal("a", "b", "c", "c");
        ShouldBeCovered(cond);

        // on ints, or with a constant side: a condition, not a decision of its own
        p.Map.Decisions.Should().NotContain(d => d.Text == "x == 3" || d.Text == "a == true");
    }

    // the tutorial's xor gate (NASA/TM-2001-210876 §3.1.3): any three of the four tests give MC/DC, two never do
    [TestMethod]
    public void XorNeedsPairsWithDifferentOutcomes()
    {
        // TT and FF: a and b are each shown true and false (xor never masks), but the outcome is false both times
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Xor", true, true);
        p.Check("Xor", false, false);

        var xor = p.Result("a ^ b");
        xor.Conditions.Should().OnlyContain(c => c.ShownTrue && c.ShownFalse && !c.Paired && !c.Covered);
        // one more test makes the pairs: TF (or FT)
        xor.Conditions[0].Suggestions.Should().ContainSingle();

        var report = McDcReport.Format(p.Map, p.Results());
        report.Should().Contain("c0 a: NOT shown, missing a pair: shown true and false, but never with different outcomes");

        p.Check("Xor", true, false);
        ShouldBeCovered(p.Result("a ^ b"));
    }

    [TestMethod]
    public void NonShortCircuitOperatorsMask()
    {
        // a & b: both are evaluated; a false masks b, and b false masks a
        var p = McDcEndToEndTests.Build(Source);
        p.Check("And", false, false);
        var and = p.Result("a & b");
        and.Evaluations.Should().ContainSingle().Which.Evaluated.Should().Be(3u);
        and.Conditions.Should().OnlyContain(c => !c.ShownTrue && !c.ShownFalse);

        p.Check("And", true, true);
        p.Check("And", true, false);
        p.Check("And", false, true);
        ShouldBeCovered(p.Result("a & b"));
    }

    [TestMethod]
    public void PatternsAreConditions()
    {
        var p = McDcEndToEndTests.Build(Source);
        foreach (var x in new[] { -1, 5, 20 })
        {
            p.Check("Range", x);
        }
        foreach (var o in new object[] { 1, 2L, "s" })
        {
            p.Check("Either", o);
        }
        foreach (var o in new object[] { "abc", "a", 5 })
        {
            p.Check("Narrow", o);
        }
        foreach (var x in new[] { 1, 2, 3 })
        {
            p.Check("NotEither", x);
        }
        p.Check("InDecision", 3, true);
        p.Check("InDecision", 9, true);

        var range = p.Result("x is > 0 and < 10");
        range.Decision.Shape.Should().Be("(c0) && (c1)");
        range.Decision.Conditions.Should().Equal("x is > 0", "x is < 10");
        ShouldBeCovered(range);

        ShouldBeCovered(p.Result("o is int or long"));
        // after "and", the right side tests the narrowed value; s is still assigned for the ?: around it
        ShouldBeCovered(p.Result("o is string s and { Length: > 2 }"));
        ShouldBeCovered(p.Result("x is not (1 or 2)"));

        var inDecision = p.Result("b && x is >= 0 and <= 5");
        inDecision.Decision.Conditions.Should().Equal("b", "x is >= 0", "x is <= 5");
        inDecision.Conditions[2].ShownTrue.Should().BeTrue();
        inDecision.Conditions[2].ShownFalse.Should().BeTrue();

        // a type parameter input narrows through object: it compiles (Build checks)
        p.Map.Decisions.Should().Contain(d => d.Text == "v is string and { Length: > 0 }");
    }
}
