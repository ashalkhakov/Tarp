using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using System.Runtime.Loader;
using Tarp.McDc;

namespace TarpTests.McDc;

/*
 * Instrument a class, compile it together with the runtime, run it on chosen inputs, and check the
 * MC/DC the analysis computes from the recorded hits against hand-worked truth tables.
 */
[TestClass]
public class McDcEndToEndTests
{
    internal sealed class Program
    {
        public required McDcMap Map { get; init; }
        public required Assembly Original { get; init; }
        public required Assembly Instrumented { get; init; }
        public required string InstrumentedSource { get; init; }

        public object? Run(string method, params object?[] args) => Call(Instrumented, method, args);

        // runs the original and the instrumented program, which must agree
        public object? Check(string method, params object?[] args)
        {
            var expected = Call(Original, method, args);
            var actual = Call(Instrumented, method, args);
            actual.Should().Be(expected, $"instrumentation must not change {method}({string.Join(", ", args)})");
            return actual;
        }

        private string Snapshot() =>
            (string)Instrumented.GetType("TarpGen.McDc")!
                .GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!;

        public Dictionary<int, long> Counts() => McDcHits.Sum(McDcHits.ParseProbes(Snapshot(), Map.ModuleId));

        public FileCoverage Coverage() => FileCoverage.Of(new ModuleResult(Map, Results(), Counts())).Single();

        public List<DecisionResult> Results()
        {
            var results = McDcReport.Analyze(Map, McDcHits.Parse(Snapshot(), Map.ModuleId), Counts().Keys.ToHashSet());
            results.SelectMany(r => r.Inconsistent).Should().BeEmpty();
            return results;
        }

        public DecisionResult Result(string decisionText) =>
            Results().Single(r => r.Decision.Text == decisionText);

        private static object? Call(Assembly assembly, string method, object?[] args)
        {
            var result = assembly.GetType("T")!.GetMethod(method)!.Invoke(null, args);
            return result is Task<bool> task ? task.GetAwaiter().GetResult() : result;
        }
    }

    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p))
        .ToArray();

    internal static Program Build(string source, OutputKind kind = OutputKind.DynamicallyLinkedLibrary, bool exceptionExits = false)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "T.cs");
        var compilation = Compile("Original", kind, tree);

        var map = new McDcMap { ModuleId = "test" };
        var rewriter = new CoverageRewriter(compilation.GetSemanticModel(tree), map, "T.cs", exceptionExits: exceptionExits);
        var root = McDcProjectInstrumenter.WithPrologue((CompilationUnitSyntax)rewriter.Visit(tree.GetRoot())!, "T.cs", compilation.Options.NullableContextOptions);
        var instrumentedSource = root.ToFullString();

        var instrumented = Compile("Instrumented", kind,
            CSharpSyntaxTree.ParseText(instrumentedSource, path: "T.instrumented.cs"),
            CSharpSyntaxTree.ParseText(McDcRuntimeSource.Generate(map.ModuleId, "", map.Probes.Count), path: McDcRuntimeSource.FileName));

        return new Program
        {
            Map = map,
            Original = Load(compilation),
            Instrumented = Load(instrumented, instrumentedSource),
            InstrumentedSource = instrumentedSource
        };
    }

    private static CSharpCompilation Compile(string name, OutputKind kind, params SyntaxTree[] trees) =>
        CSharpCompilation.Create(name, trees, References, new CSharpCompilationOptions(kind));

    private static Assembly Load(CSharpCompilation compilation, string? source = null)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        emit.Success.Should().BeTrue(
            string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + source);

        stream.Position = 0;
        return new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true).LoadFromStream(stream);
    }

    private static void ShouldBeShown(DecisionResult r, params string[] expected)
    {
        var shown = r.Conditions.SelectMany(c =>
            (c.ShownTrue ? new[] { c.Text + "=T" } : Array.Empty<string>())
            .Concat(c.ShownFalse ? new[] { c.Text + "=F" } : Array.Empty<string>()));
        shown.Should().BeEquivalentTo(expected);
    }

    private static void ShouldBeCovered(DecisionResult r) =>
        r.Conditions.Should().OnlyContain(c => c.Covered);

    private const string Decisions = @"
public static class T
{
    public static bool And(bool a, bool b) => a && b;
    public static bool Or(bool a, bool b) => a || b;
    public static bool AndOr(bool a, bool b, bool c) => (a && b) || c;
    public static bool Masked(bool A, bool B, bool C, bool D) => (A || B) && (C || D);
    public static bool Not(bool a, bool b, bool c) => !(a && b) || c;
    public static bool Wide(bool a, bool b, bool c, bool d) => (a || b || c) && d;
}";

    [TestMethod]
    public void AndNeedsThreeTests()
    {
        var p = Build(Decisions);
        p.Check("And", true, true);
        p.Check("And", true, false);
        p.Check("And", false, true);

        var r = p.Result("a && b");
        r.Evaluations.Should().HaveCount(3);
        ShouldBeCovered(r);
    }

    [TestMethod]
    public void AndWithoutAFalseIsNotCovered()
    {
        var p = Build(Decisions);
        p.Check("And", true, true);
        p.Check("And", true, false);

        var r = p.Result("a && b");
        ShouldBeShown(r, "a=T", "b=T", "b=F");
        var a = r.Conditions[0];
        a.Covered.Should().BeFalse();
        // a=F short-circuits b
        a.Suggestions.Should().ContainSingle().Which.Should().Equal(false, null);
    }

    [TestMethod]
    public void OrNeedsThreeTests()
    {
        var p = Build(Decisions);
        p.Check("Or", false, false);
        p.Check("Or", false, true);
        p.Check("Or", true, false);

        ShouldBeCovered(p.Result("a || b"));
    }

    [TestMethod]
    public void AndOrMinimalSet()
    {
        // (a && b) || c: TT-, F-F, TFF, F-T
        var p = Build(Decisions);
        p.Check("AndOr", true, true, false);
        p.Check("AndOr", false, false, false);
        p.Check("AndOr", true, false, false);
        p.Check("AndOr", false, false, true);

        var r = p.Result("(a && b) || c");
        r.Evaluations.Should().HaveCount(4);
        ShouldBeCovered(r);
    }

    [TestMethod]
    public void AndOrWithoutTffMissesBFalse()
    {
        var p = Build(Decisions);
        p.Check("AndOr", true, true, false);
        p.Check("AndOr", false, false, false);
        p.Check("AndOr", false, false, true);

        var r = p.Result("(a && b) || c");
        ShouldBeShown(r, "a=T", "a=F", "b=T", "c=T", "c=F");
        r.Conditions[1].Suggestions.Should().ContainSingle().Which.Should().Equal(true, false, false);
    }

    [TestMethod]
    public void MaskedConditionIsNotShown()
    {
        // A=T short-circuits B, and D=T masks C (see TestSources/MaskedConditions.cs)
        var p = Build(Decisions);
        p.Check("Masked", true, false, false, true);

        ShouldBeShown(p.Result("(A || B) && (C || D)"), "A=T", "D=T");
    }

    [TestMethod]
    public void MaskingCoversWholeLeftOperand()
    {
        // d=F masks all of (a || b || c), including the middle condition b
        var p = Build(Decisions);
        p.Check("Wide", false, true, false, false);

        ShouldBeShown(p.Result("(a || b || c) && d"), "d=F");
    }

    [TestMethod]
    public void AllInputsCoverEveryCondition()
    {
        var p = Build(Decisions);
        foreach (var (method, n) in new[] { ("And", 2), ("Or", 2), ("AndOr", 3), ("Masked", 4), ("Not", 3), ("Wide", 4) })
        {
            for (var input = 0; input < 1 << n; input++)
            {
                p.Check(method, Enumerable.Range(0, n).Select(i => (object?)((input & 1 << i) != 0)).ToArray());
            }
        }

        var results = p.Results();
        results.Should().HaveCount(6);
        results.Should().OnlyContain(r => r.Conditions.All(c => c.Covered));
    }

    private const string Statements = @"
using System.Linq;
public static class T
{
    public static int If(bool a) { if (a) return 1; return 0; }

    public static int Loop(int n, bool stop)
    {
        var i = 0;
        while (i < n && !stop) i++;
        return i;
    }

    public static bool Nested(bool a, int[] xs) => a && xs.Any(x => x > 0 && x < 10);

    public static int Patterns(object o)
    {
        if (o is string s && s.Length > 0) return s.Length;
        return -1;
    }

    public static int OutVar(string t)
    {
        if (!int.TryParse(t, out var n)) return -1;
        return n;
    }

    public static int Forever()
    {
        while (true) { return 1; }
    }
}";

    [TestMethod]
    public void StatementsAndNestedDecisions()
    {
        var p = Build(Statements);
        p.Check("If", true);
        p.Check("If", false);
        p.Check("Loop", 3, false);
        p.Check("Loop", 3, true);
        p.Check("Nested", true, new[] { 20, 5 });
        p.Check("Nested", false, new[] { 5 });
        p.Check("Patterns", "abc");
        p.Check("OutVar", "12");
        p.Check("OutVar", "x");
        p.Check("Forever");

        ShouldBeCovered(p.Result("a"));
        ShouldBeShown(p.Result("i < n && !stop"), "i < n=T", "i < n=F", "stop=T", "stop=F");
        ShouldBeShown(p.Result("a && xs.Any(x => x > 0 && x < 10)"), "a=T", "a=F", "xs.Any(x => x > 0 && x < 10)=T");
        ShouldBeShown(p.Result("x > 0 && x < 10"), "x > 0=T", "x < 10=T", "x < 10=F");
        ShouldBeCovered(p.Result("!int.TryParse(t, out var n)"));
        ShouldBeShown(p.Result("o is string s && s.Length > 0"), "o is string s=T", "s.Length > 0=T");

        p.Map.Skipped.Select(s => (s.Text, s.Reason)).Should().BeEquivalentTo(new[]
        {
            ("true", "constant decision")
        });
    }

    // what the compiler knows after each condition must survive the markers (see DecisionRewriter)
    private const string FlowSensitive = @"
using System;
using System.Threading.Tasks;
public class Base { public Base(bool b) { B = b; } public bool B; }
public class Derived : Base { public Derived(bool a, bool b) : base(a && b) { } }
public class Primary(bool a, bool b) : Base(a || b) { }
public static class T
{
    static readonly bool Field = Id(true) && Id(false);
    static bool Id(bool x) => x;
    public static bool FieldValue() => Field;

    public static int WhenTrue(object o) { if (o is string s && s.Length > 0) return s.Length; return -1; }
    public static int WhenFalse(object o) { if (o is not string s || s.Length == 0) return -1; return s.Length; }
    public static int Later(bool a, string t) { if (a && int.TryParse(t, out var n)) return n; return -1; }

    public static async Task<bool> Await(bool a, bool b) => a && await Yielding(b);
    static async Task<bool> Yielding(bool b) { await Task.Yield(); return b; }

    public static int Recursive(int n) => n > 0 && Recursive(n - 1) >= 0 ? n : 0;
    public static bool Throws(bool a, bool b) => a && (b ? throw new InvalidOperationException() : true);

    public static bool Constructor(bool a, bool b) => new Derived(a, b).B;
    public static bool PrimaryConstructor(bool a, bool b) => new Primary(a, b).B;
}";

    [TestMethod]
    public void PatternVariablesAssignmentsAndAwait()
    {
        var p = Build(FlowSensitive);
        foreach (var o in new object[] { "abc", "", 1 })
        {
            p.Check("WhenTrue", o);
            p.Check("WhenFalse", o);
        }
        p.Check("Later", true, "12");
        p.Check("Later", true, "x");
        p.Check("Later", false, "1");
        p.Check("Await", true, true);
        p.Check("Await", true, false);
        p.Check("Await", false, true);

        p.Map.Skipped.Should().BeEmpty();
        ShouldBeCovered(p.Result("o is string s && s.Length > 0"));
        ShouldBeCovered(p.Result("o is not string s || s.Length == 0"));
        ShouldBeCovered(p.Result("a && int.TryParse(t, out var n)"));
        ShouldBeCovered(p.Result("a && await Yielding(b)"));
    }

    [TestMethod]
    public void RecursionAndExceptionsKeepEvaluationsApart()
    {
        var p = Build(FlowSensitive);
        p.Check("Recursive", 2);

        // the evaluation that throws is never recorded, and doesn't disturb the next ones
        var throws = () => p.Run("Throws", true, true);
        throws.Should().Throw<TargetInvocationException>().WithInnerException<InvalidOperationException>();
        p.Check("Throws", true, false);
        p.Check("Throws", false, true);

        var recursive = p.Result("n > 0 && Recursive(n - 1) >= 0");
        recursive.Evaluations.Select(h => (h.Evaluated, h.Values)).Should().BeEquivalentTo(new[] { (3u, 3u), (1u, 0u) });

        var outer = p.Result("a && (b ? throw new InvalidOperationException() : true)");
        outer.Evaluations.Select(h => (h.Evaluated, h.Values)).Should().BeEquivalentTo(new[] { (3u, 3u), (1u, 0u) });
    }

    [TestMethod]
    public void InitializersAndConstructorArguments()
    {
        var p = Build(FlowSensitive);
        p.Check("FieldValue");
        p.Check("Constructor", true, false);
        p.Check("PrimaryConstructor", false, false);

        // a single evaluation each: in "true && false", the false right side masks the left one
        ShouldBeShown(p.Result("Id(true) && Id(false)"), "Id(false)=F");
        ShouldBeShown(p.Result("a && b"), "b=F");
        ShouldBeShown(p.Result("a || b"), "a=F", "b=F");
    }

    [TestMethod]
    public void ReportShowsMissingValuesAndTests()
    {
        var p = Build(Decisions);
        p.Check("And", true, true);
        p.Check("And", true, false);

        var report = McDcReport.Format(p.Map, p.Results());

        report.Should().Contain("T.cs:4:47  a && b");
        report.Should().Contain("c0 a: NOT shown, missing false");
        report.Should().Contain("try: c0=F c1=-");
        report.Should().Contain("c1 b: shown");
        report.Should().Contain("never executed");
        report.Should().Contain("Summary: 6 decisions (5 never executed), 1/18 conditions shown (5.6%), 0 decisions not instrumented");
    }
}
