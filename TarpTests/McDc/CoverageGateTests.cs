using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp.McDc;

namespace TarpTests.McDc;

// the fail-under checks and their exclusions, on ReportFormatsTests' run: a && b with a never false
[TestClass]
public class CoverageGateTests
{
    [TestMethod]
    public void FailsUnderTheMinimum()
    {
        var modules = ReportFormatsTests.Run();

        var (passed, text) = CoverageGate.Check(modules, new CoverageThresholds(McDc: 50), CoverageExclusions.None);
        passed.Should().BeTrue();
        text.Should().MatchRegex(@"MC/DC +1/2 +50.0%  minimum 50%  ok").And.NotContain("Lines");

        (passed, text) = CoverageGate.Check(modules, new CoverageThresholds(Lines: 50, McDc: 75), CoverageExclusions.None);
        passed.Should().BeFalse();
        text.Should().Contain("minimum 75%  FAILED").And.Contain("Lines");
    }

    [TestMethod]
    public void ExclusionsLeaveOutLinesAndFiles()
    {
        var modules = ReportFormatsTests.Run();
        var decision = ReportFormatsTests.LineOf("if (a && b)");

        var exclusions = CoverageExclusions.Parse(new[] { "# known gaps", $"T.cs:{decision}  # a can't be false here", "", "Other/**/*.cs" });
        var (passed, text) = CoverageGate.Check(modules, new CoverageThresholds(McDc: 100), exclusions);
        passed.Should().BeTrue();
        text.Should().Contain("(2 exclusions)").And.MatchRegex(@"MC/DC +0/0 +100.0%")
            .And.Contain("1 exclusion leaves out no gap").And.Contain("    Other/**/*.cs");

        (passed, _) = CoverageGate.Check(modules, new CoverageThresholds(Lines: 100, Branches: 100), CoverageExclusions.Parse(new[] { "*.cs" }));
        passed.Should().BeTrue();
    }

    // a condition entry leaves out that condition only: b, shown, is still checked
    [TestMethod]
    public void ExclusionsOfConditions()
    {
        var modules = ReportFormatsTests.Run();
        var decision = ReportFormatsTests.LineOf("if (a && b)");

        var (passed, text) = CoverageGate.Check(modules, new CoverageThresholds(McDc: 100), CoverageExclusions.Parse(new[] { $"T.cs:{decision}   a" }));
        passed.Should().BeTrue();
        text.Should().MatchRegex(@"MC/DC +1/1 +100.0%").And.NotContain("no gap");
    }

    // as a ratchet: an entry with nothing left to exclude (here b is shown; line 'return true;' ran, and
    // lines aren't checked anyway) is listed, and with strict fails the gate
    [TestMethod]
    public void ExclusionsWithNoGapLeft()
    {
        var modules = ReportFormatsTests.Run();
        var decision = ReportFormatsTests.LineOf("if (a && b)");
        var exclusions = CoverageExclusions.Parse(new[]
        {
            $"T.cs:{decision} a", $"T.cs:{decision} b", $"T.cs:{ReportFormatsTests.LineOf("return true;")}"
        });

        var (passed, text) = CoverageGate.Check(modules, new CoverageThresholds(McDc: 100), exclusions);
        passed.Should().BeTrue();
        text.Should().Contain("2 exclusions leave out no gap (covered now, or no such code); remove them")
            .And.Contain($"    T.cs:{decision} b\n").And.NotContain($"    T.cs:{decision} a\n");

        (passed, text) = CoverageGate.Check(modules, new CoverageThresholds(McDc: 100), exclusions, strict: true);
        passed.Should().BeFalse();
        text.Should().Contain(": FAILED, remove them");
    }

    [TestMethod]
    public void Globs()
    {
        var exclusions = CoverageExclusions.Parse(new[] { "Shop.Core/**/Generated/*.cs", "Shop.?pi/Program.cs" });
        exclusions.ExcludesFile("Shop.Core/Generated/A.cs").Should().BeTrue();
        exclusions.ExcludesFile("Shop.Core/X/Y/Generated/A.cs").Should().BeTrue();
        exclusions.ExcludesFile("Shop.Core/Generated/Sub/A.cs").Should().BeFalse();
        exclusions.ExcludesFile("Shop.Api/Program.cs").Should().BeTrue();
        exclusions.ExcludesFile(@"Shop.Api\Program.cs").Should().BeTrue();
        exclusions.ExcludesFile("Shop.Core/Orders.cs").Should().BeFalse();
    }
}
