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
            .And.Contain("exclusion matches nothing: Other/**/*.cs");

        (passed, _) = CoverageGate.Check(modules, new CoverageThresholds(Lines: 100, Branches: 100), CoverageExclusions.Parse(new[] { "*.cs" }));
        passed.Should().BeTrue();
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
