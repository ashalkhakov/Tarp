using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Tarp.McDc;

// one instrumented assembly: what was instrumented, and what ran
public record ModuleResult(McDcMap Map, List<DecisionResult> Decisions, IReadOnlyDictionary<int, long> Counts)
{
    // the probes that ran
    public IReadOnlySet<int> Probes { get; } = Counts.Where(c => c.Value > 0).Select(c => c.Key).ToHashSet();
}

public static class McDcReport
{
    // probes: the ones that ran, for the decisions over case labels (see CaseLabelHits)
    public static List<DecisionResult> Analyze(McDcMap map, IEnumerable<McDcHit> hits, IReadOnlySet<int>? probes = null)
    {
        var all = hits.Concat(CaseLabelHits(map, probes ?? new HashSet<int>())).ToList();
        return map.Decisions.Select(d => McDcAnalyzer.Analyze(d, all)).ToList();
    }

    /*
     * The evaluations of a switch section's labels, from which label matched. As the chain
     * "if (labels of section 0) ... else if (labels of section 1) ...": when a label in section t matched,
     * the sections before t were evaluated and false, section t's labels up to that one were evaluated
     * (that one true), and the sections after t weren't evaluated; when no case matched, all were false.
     */
    private static IEnumerable<McDcHit> CaseLabelHits(McDcMap map, IReadOnlySet<int> ran)
    {
        foreach (var d in map.Decisions.Where(d => d.SwitchId != null))
        {
            var sw = map.Switches[d.SwitchId!.Value];
            var s = d.SwitchSection!.Value;
            var all = (1u << sw.LabelProbes[s].Count) - 1;
            for (var t = s; t < sw.LabelProbes.Count; t++)
            {
                for (var j = 0; j < sw.LabelProbes[t].Count; j++)
                {
                    if (!ran.Contains(sw.LabelProbes[t][j]))
                    {
                        continue;
                    }
                    yield return t == s
                        ? new McDcHit(d.Id, (1u << (j + 1)) - 1, 1u << j, true)
                        : new McDcHit(d.Id, all, 0, false);
                }
            }
            if (sw.NoMatchProbe >= 0 && ran.Contains(sw.NoMatchProbe))
            {
                yield return new McDcHit(d.Id, all, 0, false);
            }
        }
    }

    /*
     * Loads every map (*.map.json) in a directory, and the hits (*.hits) under it or in hitsDirectory:
     * the results directory of `dotnet tarp test`, or the output directory of mcdc-instrument.
     */
    public static List<ModuleResult> AnalyzeDirectory(string directory, string? hitsDirectory = null)
    {
        var maps = Directory.EnumerateFiles(directory, "*" + McDcMap.FileSuffix)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => McDcMap.FromJson(File.ReadAllText(p)))
            .ToList();

        var hitFiles = Directory.EnumerateFiles(hitsDirectory ?? directory, "*.hits", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        return maps
            .Select(m =>
            {
                var counts = McDcHits.Sum(hitFiles.SelectMany(f => McDcHits.ParseProbes(f, m.ModuleId)));
                var ran = counts.Where(c => c.Value > 0).Select(c => c.Key).ToHashSet();
                return new ModuleResult(m, Analyze(m, hitFiles.SelectMany(f => McDcHits.Parse(f, m.ModuleId)).Distinct(), ran), counts);
            })
            .ToList();
    }

    public static string Format(McDcMap map, IReadOnlyList<DecisionResult> results, IReadOnlyDictionary<int, long>? counts = null) =>
        Format(new List<ModuleResult> { new(map, results.ToList(), counts ?? new Dictionary<int, long>()) });

    public static string Format(IReadOnlyList<ModuleResult> modules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tarp coverage report");
        sb.AppendLine();

        FormatSummary(sb, modules);

        foreach (var file in modules.SelectMany(FileCoverage.Of).GroupBy(f => f.File).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            FormatFile(sb, file.Key, file.ToList());
        }

        sb.AppendLine("MC/DC by decision (masking MC/DC)");
        sb.AppendLine();

        var results = modules.SelectMany(m => m.Decisions).ToList();
        var skipped = modules.SelectMany(m => m.Map.Skipped).ToList();

        foreach (var r in results)
        {
            FormatDecision(sb, r);
        }

        var conditions = results.SelectMany(r => r.Conditions).ToList();
        var shown = conditions.Count(c => c.Covered);
        var notExecuted = results.Count(r => !r.Executed);
        var percent = conditions.Count == 0 ? 100.0 : 100.0 * shown / conditions.Count;

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Summary: {results.Count} decisions ({notExecuted} never executed), {shown}/{conditions.Count} conditions shown ({percent:0.0}%), {skipped.Count} decisions not instrumented");

        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not instrumented:");
            foreach (var s in skipped)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.File}:{s.Line}:{s.Column}  {OneLine(s.Text)}  ({s.Reason})");
            }
        }

        return sb.ToString();
    }

    private static void FormatSummary(StringBuilder sb, IReadOnlyList<ModuleResult> modules)
    {
        var files = modules.SelectMany(FileCoverage.Of).ToList();
        var decisions = modules.SelectMany(m => m.Decisions).ToList();
        var conditions = decisions.SelectMany(d => d.Conditions).ToList();

        var lines = (files.Sum(f => f.LinesRun.Count), files.Sum(f => f.Lines.Count));
        var branches = (files.Sum(f => f.BranchesTaken), files.Sum(f => f.Branches));
        var functions = (files.Sum(f => f.Functions.Count(x => x.Entered)), files.Sum(f => f.Functions.Count));
        var exits = (files.Sum(f => f.Functions.Sum(x => x.ExitsTaken)), files.Sum(f => f.Functions.Sum(x => x.Function.Exits.Count)));
        var seen = (conditions.Count(c => c.SeenTrue && c.SeenFalse), conditions.Count);
        var mcdc = (conditions.Count(c => c.Covered), conditions.Count);

        sb.AppendLine("Summary");
        sb.AppendLine(Row("Lines", lines, ""));
        sb.AppendLine(Row("Branches", branches, "decision outcomes, switch sections and arms, ??, ?., foreach, catch"));
        sb.AppendLine(Row("Functions", functions, $"entered; exits {exits.Item1}/{exits.Item2}"));
        sb.AppendLine(Row("Conditions", seen, "seen both true and false"));
        sb.AppendLine(Row("MC/DC", mcdc, "conditions shown to affect their decision"));
        sb.AppendLine();
    }

    private static string Row(string name, (int Covered, int Total) n, string note)
    {
        var percent = n.Total == 0 ? 100.0 : 100.0 * n.Covered / n.Total;
        var text = string.Format(CultureInfo.InvariantCulture, "  {0,-11}{1,6}/{2,-6}{3,6:0.0}%", name, n.Covered, n.Total, percent);
        return note.Length == 0 ? text : text + "  " + note;
    }

    private static void FormatFile(StringBuilder sb, string file, List<FileCoverage> parts)
    {
        var lines = parts.SelectMany(p => p.Lines).ToHashSet();
        var run = parts.SelectMany(p => p.LinesRun).ToHashSet();
        var notRun = lines.Except(run).OrderBy(l => l).ToList();

        sb.AppendLine(CultureInfo.InvariantCulture, $"{file}: lines {run.Count}/{lines.Count}");
        if (notRun.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  lines not run: {Ranges(notRun)}");
        }

        // the hot spots: lines run more than once, most first
        var counts = parts.SelectMany(p => p.LineCounts)
            .GroupBy(c => c.Key)
            .Select(g => (Line: g.Key, Count: g.Sum(c => c.Value)))
            .Where(c => c.Count > 1)
            .OrderByDescending(c => c.Count).ThenBy(c => c.Line)
            .Take(3)
            .ToList();
        if (counts.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  most run: {string.Join(", ", counts.Select(c => string.Format(CultureInfo.InvariantCulture, "line {0} ({1:N0} times)", c.Line, c.Count)))}");
        }

        foreach (var f in parts.SelectMany(p => p.Functions).OrderBy(f => f.Function.Line))
        {
            if (!f.Entered)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  never entered: {f.Function.Name} (line {f.Function.Line})");
            }
            else if (f.ExitsNotTaken.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.Function.Name}: exits not taken: {string.Join(", ", f.ExitsNotTaken)}");
            }
        }

        foreach (var s in parts.SelectMany(p => p.Switches).OrderBy(s => s.Switch.Line))
        {
            if (s.NotTaken.Count > 0)
            {
                var what = s.Switch.Kind switch
                {
                    SwitchEntry.Expression => "arms",
                    SwitchEntry.Coalesce => "sides",
                    SwitchEntry.ConditionalAccess or SwitchEntry.ForEach or SwitchEntry.Try => "branches",
                    _ => "sections"
                };
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.Switch.Kind} at line {s.Switch.Line}: {what} not taken: {string.Join("; ", s.NotTaken)}");
            }
        }

        foreach (var d in parts.SelectMany(p => p.Decisions).Where(d => d.Executed && !(d.OutcomeTrue && d.OutcomeFalse)))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  line {d.Decision.Line}: {OneLine(d.Decision.Text)} was only {(d.OutcomeTrue ? "true" : "false")}");
        }

        sb.AppendLine();
    }

    // 3, 4, 5, 9 -> "3-5, 9"
    private static string Ranges(List<int> sorted)
    {
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }
            parts.Add(i == j ? sorted[i].ToString(CultureInfo.InvariantCulture) : $"{sorted[i]}-{sorted[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
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

            var missing = !c.ShownTrue && !c.ShownFalse ? "true and false"
                : !c.ShownTrue ? "true"
                : !c.ShownFalse ? "false"
                : "a pair: shown true and false, but never with different outcomes";
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
