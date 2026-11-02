using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Tarp.McDc;

/*
 * An lcov tracefile (lcov's geninfo(1) and its tracefile format; the format gcov data is turned into):
 * genhtml, Codecov, Coveralls, VS Code's Coverage Gutters and ReportGenerator read it. Per source file:
 *
 *   FN/FNDA, FNF/FNH       functions, and how many times each was entered
 *   BRDA, BRF/BRH          branches: each decision's two outcomes (taken: 1 or 0, "-" when the decision
 *                          never ran), and each branch point's ways (switch sections and arms, ??, ?.,
 *                          foreach, catch, finally; taken: how many times)
 *   MCDC, MCF/MCH          MC/DC (lcov 2.x): per condition and sense (t, f), 1 when shown to affect
 *                          its decision with that value (Tarp's masking MC/DC)
 *   DA, LF/LH              lines, and how many times each ran
 */
public static class LcovReport
{
    public static string Format(IReadOnlyList<ModuleResult> modules)
    {
        var sb = new StringBuilder();
        foreach (var module in modules)
        {
            foreach (var file in FileCoverage.Of(module).OrderBy(f => f.File, System.StringComparer.Ordinal))
            {
                FormatFile(sb, module, file);
            }
        }
        return sb.ToString();
    }

    private static void FormatFile(StringBuilder sb, ModuleResult module, FileCoverage file)
    {
        long Count(int probe) => module.Counts.TryGetValue(probe, out var c) ? c : 0;
        void Line(string text) => sb.Append(text).Append('\n');

        Line("TN:");
        Line("SF:" + (module.Map.Sources.TryGetValue(file.File, out var path) ? path : file.File));

        foreach (var f in file.Functions.OrderBy(f => f.Function.Line))
        {
            Line(Invariant($"FN:{f.Function.Line},{f.Function.Name}"));
        }
        foreach (var f in file.Functions.OrderBy(f => f.Function.Line))
        {
            Line(Invariant($"FNDA:{(f.Function.Entry >= 0 ? Count(f.Function.Entry) : 0)},{f.Function.Name}"));
        }
        Line(Invariant($"FNF:{file.Functions.Count}"));
        Line(Invariant($"FNH:{file.Functions.Count(f => f.Entered)}"));

        // blocks: numbered per line, in the order decisions and branch points appear
        var blocks = new Dictionary<int, int>();
        int Block(int line) => blocks[line] = blocks.TryGetValue(line, out var b) ? b + 1 : 0;
        var found = 0;
        var hit = 0;
        foreach (var d in file.Decisions.OrderBy(d => d.Decision.Line).ThenBy(d => d.Decision.Column))
        {
            var block = Block(d.Decision.Line);
            Line(Invariant($"BRDA:{d.Decision.Line},{block},0,{(d.Executed ? (d.OutcomeTrue ? "1" : "0") : "-")}"));
            Line(Invariant($"BRDA:{d.Decision.Line},{block},1,{(d.Executed ? (d.OutcomeFalse ? "1" : "0") : "-")}"));
            found += 2;
            hit += (d.OutcomeTrue ? 1 : 0) + (d.OutcomeFalse ? 1 : 0);
        }
        foreach (var s in file.Switches.OrderBy(s => s.Switch.Line))
        {
            var block = Block(s.Switch.Line);
            var branch = 0;
            foreach (var probe in s.Switch.Sections.Where(p => p >= 0))
            {
                Line(Invariant($"BRDA:{s.Switch.Line},{block},{branch++},{Count(probe)}"));
                found++;
                hit += Count(probe) > 0 ? 1 : 0;
            }
        }
        Line(Invariant($"BRF:{found}"));
        Line(Invariant($"BRH:{hit}"));

        var mcdcFound = 0;
        var mcdcHit = 0;
        foreach (var d in file.Decisions.OrderBy(d => d.Decision.Line).ThenBy(d => d.Decision.Column))
        {
            var n = d.Conditions.Count;
            foreach (var c in d.Conditions)
            {
                var expression = McDcReport.OneLine(c.Text);
                var t = c.ShownTrue && c.Paired;
                var f = c.ShownFalse && c.Paired;
                Line(Invariant($"MCDC:{d.Decision.Line},{n},t,{(t ? 1 : 0)},{c.Index},{expression}"));
                Line(Invariant($"MCDC:{d.Decision.Line},{n},f,{(f ? 1 : 0)},{c.Index},{expression}"));
                mcdcFound += 2;
                mcdcHit += (t ? 1 : 0) + (f ? 1 : 0);
            }
        }
        Line(Invariant($"MCF:{mcdcFound}"));
        Line(Invariant($"MCH:{mcdcHit}"));

        foreach (var line in file.Lines.OrderBy(l => l))
        {
            Line(Invariant($"DA:{line},{(file.LineCounts.TryGetValue(line, out var c) ? c : 0)}"));
        }
        Line(Invariant($"LF:{file.Lines.Count}"));
        Line(Invariant($"LH:{file.LinesRun.Count}"));
        Line("end_of_record");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
