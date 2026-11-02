using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Tarp.McDc;

/*
 * The report in Markdown, for a CI job summary or a pull request comment: the summary, a table of files,
 * and per file what wasn't covered. MC/DC gaps are tables, per decision: its recorded evaluations, then
 * its conditions, what each is missing and a test that would show it.
 */
public static class MarkdownReport
{
    public static string Format(IReadOnlyList<ModuleResult> modules)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');

        var files = modules.SelectMany(FileCoverage.Of).GroupBy(f => f.File).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var all = files.SelectMany(g => g).ToList();
        var conditions = all.SelectMany(f => f.Decisions).SelectMany(d => d.Conditions).ToList();

        Line("# Tarp coverage report");
        Line();
        Line("| | Covered | Total | % | |");
        Line("|---|---:|---:|---:|---|");
        Row(sb, "Lines", all.Sum(f => f.LinesRun.Count), all.Sum(f => f.Lines.Count), "");
        Row(sb, "Branches", all.Sum(f => f.BranchesTaken), all.Sum(f => f.Branches), "decision outcomes, switch sections and arms, ??, ?., foreach, catch, finally");
        Row(sb, "Functions", all.Sum(f => f.Functions.Count(x => x.Entered)), all.Sum(f => f.Functions.Count),
            Invariant($"entered; exits {all.Sum(f => f.Functions.Sum(x => x.ExitsTaken))}/{all.Sum(f => f.Functions.Sum(x => x.Exits))}"));
        if (all.Any(f => f.Functions.Any(x => x.LeftByException != null)))
        {
            Row(sb, "Exceptions", all.Sum(f => f.Functions.Count(x => x.LeftByException == true)), all.Sum(f => f.Functions.Count(x => x.LeftByException != null)),
                McDcReport.ExceptionsNote);
        }
        Row(sb, "Conditions", conditions.Count(c => c.SeenTrue && c.SeenFalse), conditions.Count, "seen both true and false");
        Row(sb, "MC/DC", conditions.Count(c => c.Covered), conditions.Count, "conditions shown to affect their decision (masking)");
        Line();

        Line("| File | Lines | Branches | Functions | MC/DC |");
        Line("|---|---:|---:|---:|---:|");
        foreach (var g in files)
        {
            var parts = g.ToList();
            var fileConditions = parts.SelectMany(f => f.Decisions).SelectMany(d => d.Conditions).ToList();
            Line(Invariant($"| {Cell(g.Key)} | {Ratio(parts.Sum(f => f.LinesRun.Count), parts.Sum(f => f.Lines.Count))} | {Ratio(parts.Sum(f => f.BranchesTaken), parts.Sum(f => f.Branches))} | {Ratio(parts.Sum(f => f.Functions.Count(x => x.Entered)), parts.Sum(f => f.Functions.Count))} | {Ratio(fileConditions.Count(c => c.Covered), fileConditions.Count)} |"));
        }
        Line();

        foreach (var g in files)
        {
            var gaps = Gaps(g.ToList());
            var mcdc = g.SelectMany(f => f.Decisions).Where(d => d.Executed && d.Conditions.Any(c => !c.Covered)).OrderBy(d => d.Decision.Line).ToList();
            if (gaps.Count == 0 && mcdc.Count == 0)
            {
                continue;
            }
            Line($"## {Code(g.Key)}");
            Line();
            foreach (var gap in gaps)
            {
                Line(gap);
            }
            if (gaps.Count > 0)
            {
                Line();
            }
            foreach (var d in mcdc)
            {
                FormatDecision(sb, d);
            }
        }

        var skipped = modules.SelectMany(m => m.Map.Skipped).ToList();
        if (skipped.Count > 0)
        {
            Line(Invariant($"<details><summary>Not instrumented: {skipped.Count} decisions</summary>"));
            Line();
            foreach (var s in skipped)
            {
                Line(Invariant($"- {Code(s.File)}:{s.Line}: {Code(McDcReport.OneLine(s.Text))} ({s.Reason})"));
            }
            Line();
            Line("</details>");
        }
        return sb.ToString();
    }

    // what a file's tests didn't cover, as list items
    private static List<string> Gaps(List<FileCoverage> parts)
    {
        var gaps = new List<string>();
        var notRun = parts.SelectMany(p => p.Lines).Except(parts.SelectMany(p => p.LinesRun)).OrderBy(l => l).ToList();
        if (notRun.Count > 0)
        {
            gaps.Add($"- Lines not run: {McDcReport.Ranges(notRun)}");
        }
        foreach (var f in parts.SelectMany(p => p.Functions).OrderBy(f => f.Function.Line))
        {
            if (!f.Entered)
            {
                gaps.Add(Invariant($"- Never entered: {Code(f.Function.Name)} (line {f.Function.Line})"));
            }
            else if (f.ExitsNotTaken.Count > 0)
            {
                gaps.Add($"- {Code(f.Function.Name)}: exits not taken: {string.Join(", ", f.ExitsNotTaken)}");
            }
        }
        foreach (var s in parts.SelectMany(p => p.Switches).Where(s => s.NotTaken.Count > 0).OrderBy(s => s.Switch.Line))
        {
            gaps.Add(Invariant($"- Line {s.Switch.Line}, {Code(s.Switch.Kind)}: not taken: {string.Join("; ", s.NotTaken.Select(Code))}"));
        }
        foreach (var d in parts.SelectMany(p => p.Decisions).OrderBy(d => d.Decision.Line))
        {
            var text = Code(McDcReport.OneLine(d.Decision.Text));
            if (!d.Executed)
            {
                gaps.Add(Invariant($"- Line {d.Decision.Line}: {text} never ran"));
                continue;
            }
            if (!(d.OutcomeTrue && d.OutcomeFalse))
            {
                gaps.Add(Invariant($"- Line {d.Decision.Line}: {text} was only {(d.OutcomeTrue ? "true" : "false")}"));
            }
        }
        return gaps;
    }

    // a decision whose MC/DC isn't complete: its evaluations (c0.. and the outcome), then its conditions
    private static void FormatDecision(StringBuilder sb, DecisionResult d)
    {
        void Line(string text = "") => sb.Append(text).Append('\n');
        var n = d.Decision.Conditions.Count;

        Line(Invariant($"### Line {d.Decision.Line}: {Code(McDcReport.OneLine(d.Decision.Text))}"));
        Line();
        Line("| " + string.Join(" | ", Enumerable.Range(0, n).Select(i => $"c{i}")) + " | Outcome |");
        Line("|" + string.Concat(Enumerable.Repeat(":-:|", n + 1)));
        // sorted, so that the report doesn't change with the order the tests ran in
        var rows = d.Evaluations
            .Select(h => Enumerable.Range(0, n).Select(i => Value((h.Evaluated & (1u << i)) != 0 ? (h.Values & (1u << i)) != 0 : null)).Append(Value(h.Outcome)).ToList())
            .OrderBy(r => string.Concat(r), StringComparer.Ordinal);
        foreach (var r in rows)
        {
            Line($"| {string.Join(" | ", r)} |");
        }
        Line();
        Line("| | Condition | Shown true | Shown false | MC/DC | Test to add |");
        Line("|---|---|:-:|:-:|:-:|---|");
        foreach (var c in d.Conditions)
        {
            var tests = c.Covered ? ""
                : c.Suggestions.Count == 0 ? "none can show it"
                : string.Join(", then ", c.Suggestions.Select(v => Code(string.Join(" ", v.Select((x, i) => $"c{i}={Value(x)}")))));
            Line(Invariant($"| c{c.Index} | {Cell(McDcReport.OneLine(c.Text))} | {Check(c.ShownTrue)} | {Check(c.ShownFalse)} | {McDcStatus(c)} | {tests} |"));
        }
        Line();
    }

    // shown both ways, but for ^, ==, != or ?: never in two evaluations with different outcomes: "no pair"
    private static string McDcStatus(ConditionResult c) =>
        c.Covered ? "shown" : c.ShownTrue && c.ShownFalse ? "**no pair**" : "**not shown**";

    private static string Value(bool? value) => value == null ? "-" : value.Value ? "T" : "F";

    private static string Check(bool value) => value ? "✓" : "✗";

    private static void Row(StringBuilder sb, string name, int covered, int total, string note) =>
        sb.Append(Invariant($"| {name} | {covered} | {total} | {Percent(covered, total)} | {note} |\n"));

    private static string Ratio(int covered, int total) => Invariant($"{covered}/{total} ({Percent(covered, total)})");

    private static string Percent(int covered, int total) =>
        (total == 0 ? 100.0 : 100.0 * covered / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    // inline code: with as many backticks around it as it needs
    private static string Code(string text)
    {
        var fence = new string('`', LongestRun(text, '`') + 1);
        var padding = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return fence + padding + text + padding + fence;
    }

    private static string Cell(string text) => Code(text).Replace("|", "\\|", StringComparison.Ordinal);

    private static int LongestRun(string text, char c)
    {
        int longest = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
