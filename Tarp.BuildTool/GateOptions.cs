using System.Globalization;
using Tarp.McDc;

namespace Tarp.BuildTool;

// the coverage gate's minimums and exclusions file, from the command line
internal sealed record GateOptions(CoverageThresholds Thresholds, string? Exclusions, bool Strict)
{
    public static GateOptions Parse(string? lines, string? branches, string? mcdc, string? exclusions, bool strict) =>
        new(new CoverageThresholds(Percent(lines, "lines"), Percent(branches, "branches"), Percent(mcdc, "MC/DC")), exclusions, strict);

    // true when it passes (or isn't asked for); prints the gate's numbers
    public bool Check(IReadOnlyList<ModuleResult> modules)
    {
        if (!Thresholds.Any)
        {
            return true;
        }
        var exclusions = Exclusions == null ? CoverageExclusions.None : CoverageExclusions.Parse(File.ReadAllLines(Exclusions));
        var (passed, text) = CoverageGate.Check(modules, Thresholds, exclusions, Strict);
        Console.WriteLine();
        Console.Write(text);
        return passed;
    }

    private static double? Percent(string? value, string what)
    {
        if (value == null)
        {
            return null;
        }
        if (!double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || percent < 0 || percent > 100)
        {
            throw new ArgumentException($"the minimum for {what} must be a percentage from 0 to 100, not '{value}'");
        }
        return percent;
    }
}
