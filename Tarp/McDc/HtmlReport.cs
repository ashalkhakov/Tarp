using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Tarp.McDc;

/*
 * The report as one self-contained HTML page: the summary, a table of files, and per file its source
 * with how many times each line ran (run, not run, or partly: a decision, branch point or condition on
 * it not covered, said when hovering), what wasn't covered, and the MC/DC of its decisions as tables:
 * the recorded evaluations, and per condition what it's missing and a test that would show it.
 * The source is read from the files the map names (McDcMap.Sources); without them, no listing.
 */
public static class HtmlReport
{
    public static string Format(IReadOnlyList<ModuleResult> modules)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');

        var files = modules
            .SelectMany(m => FileCoverage.Of(m).Select(f => (Module: m, File: f)))
            .GroupBy(x => x.File.File)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var all = files.SelectMany(g => g.Select(x => x.File)).ToList();
        var conditions = all.SelectMany(f => f.Decisions).SelectMany(d => d.Conditions).ToList();

        Line("<!doctype html>");
        Line("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        Line("<title>Tarp coverage report</title>");
        Line("<style>" + Style + "</style></head><body>");
        Line("<h1>Tarp coverage report</h1>");

        Line("<table class=\"summary\"><thead><tr><th></th><th>Covered</th><th>Total</th><th>%</th><th></th></tr></thead><tbody>");
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
        Line("</tbody></table>");

        Line("<table class=\"files\"><thead><tr><th>File</th><th>Lines</th><th>Branches</th><th>Functions</th><th>MC/DC</th></tr></thead><tbody>");
        for (var i = 0; i < files.Count; i++)
        {
            var parts = files[i].Select(x => x.File).ToList();
            var fileConditions = parts.SelectMany(f => f.Decisions).SelectMany(d => d.Conditions).ToList();
            Line(Invariant($"<tr><td><a href=\"#f{i}\">{E(files[i].Key)}</a></td>") +
                 $"<td>{Ratio(parts.Sum(f => f.LinesRun.Count), parts.Sum(f => f.Lines.Count))}</td>" +
                 $"<td>{Ratio(parts.Sum(f => f.BranchesTaken), parts.Sum(f => f.Branches))}</td>" +
                 $"<td>{Ratio(parts.Sum(f => f.Functions.Count(x => x.Entered)), parts.Sum(f => f.Functions.Count))}</td>" +
                 $"<td>{Ratio(fileConditions.Count(c => c.Covered), fileConditions.Count)}</td></tr>");
        }
        Line("</tbody></table>");

        for (var i = 0; i < files.Count; i++)
        {
            FormatFile(sb, i, files[i].Key, files[i].ToList());
        }

        var skipped = modules.SelectMany(m => m.Map.Skipped).ToList();
        if (skipped.Count > 0)
        {
            Line(Invariant($"<details><summary>Not instrumented: {skipped.Count} decisions</summary><ul>"));
            foreach (var s in skipped)
            {
                Line(Invariant($"<li><code>{E(s.File)}:{s.Line}</code> <code>{E(McDcReport.OneLine(s.Text))}</code> ({E(s.Reason)})</li>"));
            }
            Line("</ul></details>");
        }
        Line("</body></html>");
        return sb.ToString();
    }

    private static void FormatFile(StringBuilder sb, int index, string file, List<(ModuleResult Module, FileCoverage File)> parts)
    {
        void Line(string text) => sb.Append(text).Append('\n');
        var coverage = parts.Select(p => p.File).ToList();
        var lines = coverage.SelectMany(f => f.Lines).ToHashSet();
        var counts = coverage.SelectMany(f => f.LineCounts).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Value));

        // what isn't covered, by line
        var missed = new Dictionary<int, List<string>>();
        void Miss(int line, string what) => (missed.TryGetValue(line, out var l) ? l : missed[line] = new List<string>()).Add(what);
        foreach (var d in coverage.SelectMany(f => f.Decisions))
        {
            var text = McDcReport.OneLine(d.Decision.Text);
            if (!d.Executed)
            {
                Miss(d.Decision.Line, $"{text}: never ran");
                continue;
            }
            if (!(d.OutcomeTrue && d.OutcomeFalse))
            {
                Miss(d.Decision.Line, $"{text}: only {(d.OutcomeTrue ? "true" : "false")}");
            }
            foreach (var c in d.Conditions.Where(c => !c.Covered))
            {
                Miss(d.Decision.Line, $"MC/DC: {McDcReport.OneLine(c.Text)} not shown");
            }
        }
        foreach (var s in coverage.SelectMany(f => f.Switches).Where(s => s.NotTaken.Count > 0))
        {
            Miss(s.Switch.Line, $"{s.Switch.Kind}: not taken: {string.Join("; ", s.NotTaken)}");
        }
        foreach (var f in coverage.SelectMany(f => f.Functions))
        {
            if (!f.Entered)
            {
                Miss(f.Function.Line, $"{f.Function.Name}: never entered");
            }
            else if (f.ExitsNotTaken.Count > 0)
            {
                Miss(f.Function.Line, $"{f.Function.Name}: exits not taken: {string.Join(", ", f.ExitsNotTaken)}");
            }
        }

        var run = coverage.Sum(f => f.LinesRun.Count);
        Line(Invariant($"<h2 id=\"f{index}\">{E(file)} <span class=\"ratio\">lines {run}/{lines.Count}</span></h2>"));

        var source = parts.Select(p => p.Module.Map.Sources.TryGetValue(file, out var path) ? path : null).FirstOrDefault(p => p != null && File.Exists(p));
        if (source != null)
        {
            Line("<table class=\"source\"><tbody>");
            var text = File.ReadAllLines(source);
            for (var n = 1; n <= text.Length; n++)
            {
                var status = missed.ContainsKey(n) ? "partial" : counts.ContainsKey(n) ? "run" : lines.Contains(n) ? "notrun" : "";
                var title = missed.TryGetValue(n, out var what) ? $" title=\"{E(string.Join("\n", what))}\"" : "";
                var count = counts.TryGetValue(n, out var c) ? c.ToString("N0", CultureInfo.InvariantCulture) : lines.Contains(n) ? "0" : "";
                Line(Invariant($"<tr class=\"{status}\"{title}><td class=\"n\">{n}</td><td class=\"c\">{count}</td><td class=\"s\">{E(text[n - 1])}</td></tr>"));
            }
            Line("</tbody></table>");
        }
        else if (missed.Count > 0)
        {
            Line("<ul>");
            foreach (var (line, what) in missed.OrderBy(m => m.Key))
            {
                Line(Invariant($"<li>line {line}: {E(string.Join("; ", what))}</li>"));
            }
            Line("</ul>");
        }

        var decisions = coverage.SelectMany(f => f.Decisions).OrderBy(d => d.Decision.Line).ToList();
        if (decisions.Count > 0)
        {
            var shown = decisions.Count(d => d.Conditions.All(c => c.Covered));
            var open = shown < decisions.Count ? " open" : "";
            Line(Invariant($"<details{open}><summary>MC/DC: {shown}/{decisions.Count} decisions complete</summary>"));
            foreach (var d in decisions)
            {
                FormatDecision(sb, d);
            }
            Line("</details>");
        }
    }

    // a decision: its evaluations (c0.. and the outcome), then its conditions
    private static void FormatDecision(StringBuilder sb, DecisionResult d)
    {
        void Line(string text) => sb.Append(text).Append('\n');
        var n = d.Decision.Conditions.Count;
        var complete = d.Conditions.All(c => c.Covered);

        Line(Invariant($"<div class=\"decision\"><h3><span class=\"ratio\">line {d.Decision.Line}</span> <code>{E(McDcReport.OneLine(d.Decision.Text))}</code>") +
             $" <span class=\"{(complete ? "ok" : "gap")}\">{(complete ? "complete" : d.Executed ? "incomplete" : "never ran")}</span></h3>");
        if (d.Executed)
        {
            Line("<table class=\"evaluations\"><thead><tr>" + string.Concat(Enumerable.Range(0, n).Select(i => $"<th>c{i}</th>")) + "<th>Outcome</th></tr></thead><tbody>");
            var rows = d.Evaluations
                .Select(h => Enumerable.Range(0, n).Select(i => Value((h.Evaluated & (1u << i)) != 0 ? (h.Values & (1u << i)) != 0 : null)).Append(Value(h.Outcome)).ToList())
                .OrderBy(r => string.Concat(r), StringComparer.Ordinal);
            foreach (var r in rows)
            {
                Line("<tr>" + string.Concat(r.Select(c => $"<td>{c}</td>")) + "</tr>");
            }
            Line("</tbody></table>");
        }

        Line("<table class=\"conditions\"><thead><tr><th></th><th>Condition</th><th>Shown true</th><th>Shown false</th><th>MC/DC</th><th>Test to add</th></tr></thead><tbody>");
        foreach (var c in d.Conditions)
        {
            var status = c.Covered ? "shown" : c.ShownTrue && c.ShownFalse ? "no pair" : "not shown";
            var tests = c.Covered ? ""
                : c.Suggestions.Count == 0 ? "none can show it"
                : string.Join(", then ", c.Suggestions.Select(v => "<code>" + string.Join(" ", v.Select((x, i) => $"c{i}={Value(x)}")) + "</code>"));
            Line(Invariant($"<tr><td>c{c.Index}</td><td><code>{E(McDcReport.OneLine(c.Text))}</code></td><td>{Check(c.ShownTrue)}</td><td>{Check(c.ShownFalse)}</td>") +
                 $"<td class=\"{(c.Covered ? "ok" : "gap")}\">{status}</td><td>{tests}</td></tr>");
        }
        Line("</tbody></table></div>");
        if (d.Inconsistent.Count > 0)
        {
            Line(Invariant($"<p class=\"gap\">{d.Inconsistent.Count} recorded evaluations don't match the decision (ignored)</p>"));
        }
    }

    private static string Value(bool? value) => value == null ? "-" : value.Value ? "T" : "F";

    private static string Check(bool value) => value ? "✓" : "✗";

    private static void Row(StringBuilder sb, string name, int covered, int total, string note) =>
        sb.Append(Invariant($"<tr><td>{name}</td><td>{covered}</td><td>{total}</td><td>{Percent(covered, total)}</td><td>{E(note)}</td></tr>\n"));

    private static string Ratio(int covered, int total) => Invariant($"{covered}/{total} ({Percent(covered, total)})");

    private static string Percent(int covered, int total) =>
        (total == 0 ? 100.0 : 100.0 * covered / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private const string Style = @"
:root { --bg: #fff; --fg: #1f2328; --muted: #656d76; --line: #d0d7de; --run: #dafbe1; --notrun: #ffebe9; --partial: #fff8c5; }
@media (prefers-color-scheme: dark) {
  :root { --bg: #0d1117; --fg: #e6edf3; --muted: #8d96a0; --line: #30363d; --run: #12261e; --notrun: #3c1618; --partial: #3a2f0b; }
}
body { background: var(--bg); color: var(--fg); font: 14px/1.5 -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; margin: 0 auto; max-width: 1100px; padding: 16px; }
a { color: inherit; }
table { border-collapse: collapse; margin: 12px 0; }
th, td { border-bottom: 1px solid var(--line); padding: 2px 10px; text-align: left; }
.summary td:nth-child(n+2):nth-child(-n+4), .files td:nth-child(n+2) { text-align: right; }
.ratio { color: var(--muted); font-size: 14px; font-weight: normal; }
.source { width: 100%; font: 12px/1.45 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
.source td { border: 0; padding: 0 8px; white-space: pre; }
.source .n, .source .c { color: var(--muted); text-align: right; user-select: none; width: 1%; }
.run { background: var(--run); } .notrun { background: var(--notrun); } .partial { background: var(--partial); }
.source tr[title] { cursor: help; }
.decision h3 { font-size: 14px; margin: 16px 0 4px; }
.evaluations td, .evaluations th, .conditions td:nth-child(n+3):nth-child(-n+5), .conditions th:nth-child(n+3):nth-child(-n+5) { text-align: center; }
.evaluations td { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
.ok { color: #1a7f37; } .gap { color: #cf222e; font-weight: 600; }
@media (prefers-color-scheme: dark) { .ok { color: #3fb950; } .gap { color: #f85149; } }
";
}
