using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Tarp.McDc;

public static class McDcReport
{
    public static List<DecisionResult> Analyze(McDcMap map, IEnumerable<McDcHit> hits)
    {
        var all = hits.ToList();
        return map.Decisions.Select(d => McDcAnalyzer.Analyze(d, all)).ToList();
    }

    public static string Format(McDcMap map, IReadOnlyList<DecisionResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tarp MC/DC report (masking MC/DC)");
        sb.AppendLine();

        foreach (var r in results)
        {
            FormatDecision(sb, r);
        }

        var conditions = results.SelectMany(r => r.Conditions).ToList();
        var shown = conditions.Count(c => c.Covered);
        var notExecuted = results.Count(r => !r.Executed);
        var percent = conditions.Count == 0 ? 100.0 : 100.0 * shown / conditions.Count;

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Summary: {results.Count} decisions ({notExecuted} never executed), {shown}/{conditions.Count} conditions shown ({percent:0.0}%), {map.Skipped.Count} decisions not instrumented");

        if (map.Skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not instrumented:");
            foreach (var s in map.Skipped)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.File}:{s.Line}:{s.Column}  {OneLine(s.Text)}  ({s.Reason})");
            }
        }

        return sb.ToString();
    }

    private static void FormatDecision(StringBuilder sb, DecisionResult r)
    {
        var d = r.Decision;
        sb.AppendLine(CultureInfo.InvariantCulture, $"{d.File}:{d.Line}:{d.Column}  {OneLine(d.Text)}");

        if (!r.Executed)
        {
            sb.AppendLine("  never executed");
            sb.AppendLine();
            return;
        }

        // the evaluations, one column per condition
        var n = d.Conditions.Count;
        var header = string.Join(" ", Enumerable.Range(0, n).Select(i => $"c{i}"));
        sb.AppendLine(CultureInfo.InvariantCulture, $"    {header}  -> outcome");
        foreach (var h in r.Evaluations)
        {
            var cells = Enumerable.Range(0, n).Select(i => Cell((h.Evaluated & (1u << i)) != 0 ? (h.Values & (1u << i)) != 0 : null, i));
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {string.Join(" ", cells)}  -> {(h.Outcome ? "T" : "F")}");
        }

        foreach (var c in r.Conditions)
        {
            var label = $"  c{c.Index} {OneLine(c.Text)}";
            if (c.Covered)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{label}: shown");
                continue;
            }

            var missing = !c.ShownTrue && !c.ShownFalse ? "true and false" : !c.ShownTrue ? "true" : "false";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{label}: NOT shown, missing {missing}");
            if (c.Suggestions.Count == 0)
            {
                sb.AppendLine("      no test can show it (infeasible)");
            }
            foreach (var v in c.Suggestions)
            {
                var test = string.Join(" ", v.Select((x, i) => $"c{i}={(x == null ? "-" : x.Value ? "T" : "F")}"));
                sb.AppendLine(CultureInfo.InvariantCulture, $"      try: {test}");
            }
        }

        if (r.Inconsistent.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  WARNING: {r.Inconsistent.Count} recorded evaluations don't match the decision (ignored)");
        }

        sb.AppendLine();
    }

    private static string Cell(bool? value, int index)
    {
        var width = $"c{index}".Length;
        var s = value == null ? "-" : value.Value ? "T" : "F";
        return s.PadRight(width);
    }

    private static string OneLine(string text) =>
        string.Join(" ", text.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
}
