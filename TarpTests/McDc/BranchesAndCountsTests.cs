using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp.McDc;

namespace TarpTests.McDc;

// hit counts (#7), exception paths (#9), and case labels as conditions (#8)
[TestClass]
public class BranchesAndCountsTests
{
    private const string Source = @"
using System;
public static class T
{
    public static int Loop(int n) { var s = 0; for (var i = 0; i < n; i++) s += i; return s; }

    public static int Parse(string s)
    {
        try
        {
            return int.Parse(s);
        }
        catch (FormatException)
        {
            return -1;
        }
        catch (OverflowException e) when (e.Message.Length > 0)
        {
            return -2;
        }
    }

    public static int Guarded(bool fail)
    {
        var r = 0;
        try
        {
            if (fail) throw new InvalidOperationException();
            r = 1;
        }
        catch (InvalidOperationException)
        {
            r = 2;
        }
        return r;
    }

    public static string Kind(int x)
    {
        switch (x)
        {
            case 1:
            case 2:
                return ""small"";
            case 3:
                return ""three"";
            default:
                return ""other"";
        }
    }

    public static string Patterns(object o)
    {
        switch (o)
        {
            case int i when i > 0:
            case long:
                return ""number"";
            case string s:
                return s;
        }
        return ""none"";
    }

    // every value matched: the end of the switch can't be reached, and must stay so
    public static int Exhaustive(bool b) { switch (b) { case true: return 1; case false: return 0; } }

    // goto case needs a plain case 1: label
    public static int Goto(int x) { switch (x) { case 1: return 1; case 2: goto case 1; default: return 0; } }
}";

    private static int LineOf(string text) =>
        Source.Split('\n').Select((l, i) => (l, i)).Single(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;

    [TestMethod]
    public void CountsHowManyTimesLinesRan()
    {
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Loop", 5);
        p.Check("Loop", 2);

        // the for header and its body share the line: the body ran 5 + 2 times
        var line = LineOf("for (var i = 0; i < n; i++) s += i;");
        p.Coverage().LineCounts[line].Should().Be(7);

        var report = McDcReport.Format(p.Map, p.Results(), p.Counts());
        report.Should().Contain($"most run: line {line} (7 times)");
    }

    [TestMethod]
    public void CountsAddUpAcrossRunsAndOlderFilesCountOnce()
    {
        McDcHits.Sum(new[] { (1, 2L), (1, 3L), (4, 1L) }).Should().Equal(new Dictionary<int, long> { [1] = 5, [4] = 1 });
        McDcHits.ParseProbes("tarp-mcdc 2 m\np 3\n", "m").Should().Equal((3, 1L));
        McDcHits.ParseProbes("tarp-mcdc 3 m\np 3 12\n", "m").Should().Equal((3, 12L));
    }

    [TestMethod]
    public void CatchClausesAreBranches()
    {
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Parse", "12");
        p.Check("Parse", "x");
        p.Check("Guarded", false);
        p.Check("Guarded", true);

        var tries = p.Coverage().Switches.Where(s => s.Switch.Kind == SwitchEntry.Try).OrderBy(s => s.Switch.Line).ToList();

        // Parse's try block ends in a return: no "completed" branch, only the catches
        tries[0].Switch.Labels.Should().Equal("catch (FormatException)", "catch (OverflowException e) when (e.Message.Length > 0)");
        tries[0].NotTaken.Should().Equal("catch (OverflowException e) when (e.Message.Length > 0)");

        tries[1].Switch.Labels.Should().Equal("completed without an exception", "catch (InvalidOperationException)");
        (tries[1].Taken, tries[1].Total).Should().Be((2, 2));
    }

    [TestMethod]
    public void CaseLabelsAreConditions()
    {
        var p = McDcEndToEndTests.Build(Source);
        foreach (var x in new[] { 1, 3, 9 })
        {
            p.Check("Kind", x);
        }

        // case 1 matched; a later section (case 3) and default: both labels false
        var kind = p.Result("case 1: case 2:");
        kind.Decision.Conditions.Should().Equal("case 1", "case 2");
        kind.Conditions[0].Covered.Should().BeTrue();
        kind.Conditions[1].Covered.Should().BeFalse();
        kind.Conditions[1].Suggestions.Should().ContainSingle().Which.Should().Equal(false, true);

        p.Check("Kind", 2);
        p.Result("case 1: case 2:").Conditions.Should().OnlyContain(c => c.Covered);
    }

    [TestMethod]
    public void PatternLabelsAreConditions()
    {
        var p = McDcEndToEndTests.Build(Source);
        foreach (var o in new object[] { 5, 5L, "s", 1.0, -1 })
        {
            p.Check("Patterns", o);
        }

        var patterns = p.Result("case int i when i > 0: case long:");
        patterns.Decision.Conditions.Should().Equal("case int i when i > 0", "case long");
        patterns.Conditions.Should().OnlyContain(c => c.Covered);

        // the when guard is still a decision of its own
        p.Result("i > 0").Executed.Should().BeTrue();
    }

    [TestMethod]
    public void SwitchesThatMatchEverythingOrGotoCaseStillWork()
    {
        // Build fails if they don't compile; Check runs them against the original
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Exhaustive", true);
        p.Check("Exhaustive", false);
        p.Check("Goto", 2);
        p.Check("Goto", 7);

        var switches = p.Map.Switches.Where(s => s.Kind == SwitchEntry.Statement).OrderBy(s => s.Line).ToList();
        var exhaustive = switches.Single(s => s.Line == LineOf("public static int Exhaustive"));
        exhaustive.Labels.Should().Equal("case true:", "case false:");
        exhaustive.NoMatchProbe.Should().Be(-1);

        var withGoto = switches.Single(s => s.Line == LineOf("public static int Goto"));
        withGoto.LabelProbes.Should().BeEmpty();
    }

    // #18: a case label's when guard, and a catch filter, are lines of their own, run as often as the
    // guard was evaluated; the reports' totals count them like the other lines
    private const string Guards = @"
using System;
public abstract record Observation
{
    public sealed record Active : Observation;
    public sealed record Pending : Observation;
}
public static class T
{
    public static string Classify(Observation observation, bool late)
    {
        switch (observation)
        {
            case Observation.Active:
                return ""active"";
            case Observation.Pending when late:
                return ""late"";
            case Observation.Pending:
                return ""pending"";
            default:
                throw new ArgumentOutOfRangeException(nameof(observation));
        }
    }

    public static int Checked(int x, bool handle)
    {
        try { return checked(x * x); }
        catch (OverflowException)
            when (handle) { return -1; }
    }
}";

    [TestMethod]
    public void GuardsAreLines()
    {
        var p = McDcEndToEndTests.Build(Guards);
        object Make(string name) => Activator.CreateInstance(p.Instrumented.GetType("Observation+" + name)!)!;
        p.Run("Classify", Make("Active"), false);
        p.Run("Classify", Make("Pending"), true);
        p.Run("Classify", Make("Pending"), false);
        p.Check("Checked", 3, true);
        p.Check("Checked", int.MaxValue, true);

        int LineOf(string text) => Guards.Split('\n').Select((l, i) => (l, i)).Single(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;
        var f = p.Coverage();
        f.LineCounts[LineOf("case Observation.Pending when late:")].Should().Be(2, "the guard ran for both Pending observations");
        f.LineCounts[LineOf("when (handle)")].Should().Be(1, "the filter ran for the overflow");
        f.LinesRun.Should().Contain(LineOf("case Observation.Pending when late:"));

        // Cobertura: the line has the guard's count, and the totals are the lines with hits
        var modules = new List<ModuleResult> { new(p.Map, p.Results(), p.Counts()) };
        var cobertura = System.Xml.Linq.XDocument.Parse(CoberturaReport.Format(modules));
        var lines = cobertura.Descendants("class").Single().Element("lines")!.Elements("line").ToList();
        lines.Single(l => (int)l.Attribute("number")! == LineOf("case Observation.Pending when late:")).Attribute("hits")!.Value.Should().Be("2");
        var root = cobertura.Root!;
        ((int)root.Attribute("lines-valid")!).Should().Be(lines.Count);
        ((int)root.Attribute("lines-covered")!).Should().Be(lines.Count(l => (int)l.Attribute("hits")! > 0));
    }
}
