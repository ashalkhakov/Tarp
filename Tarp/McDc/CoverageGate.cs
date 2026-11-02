using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tarp.McDc;

/*
 * What's left out of the gate, one entry a line, as the reports name files and conditions (# starts a
 * comment):
 *
 *   Shop.Core/Legacy/*.cs                  whole files: * within a directory, ** across directories,
 *                                          ? one character
 *   Shop.Core/Orders.cs:68                 everything on a line: its lines, its decisions' outcomes and
 *                                          conditions, its branch points
 *   Shop.Core/Orders.cs:18 id.Length < 20  one condition of the decisions on a line (MC/DC only)
 *   Shop.Core/Orders.cs:68 [false]         one branch on a line (branches only): a decision's outcome,
 *                                          [true] or [false], or a branch point's way, as the text
 *                                          report names it after "not taken:" ([default:], [_],
 *                                          [no iterations], [catch (IOException)], ...)
 *
 * Only the gate leaves them out: the reports still show them.
 */
public sealed class CoverageExclusions
{
    public sealed record Entry(string Text, Regex File, int? Line, string? Condition, string? Branch);

    // path, then :line, then the condition after a space
    private static readonly Regex Syntax = new(@"^(?<path>.+?)(:(?<line>\d+)(\s+(?<condition>.+))?)?$", RegexOptions.CultureInvariant);

    private CoverageExclusions(List<Entry> entries) => Entries = entries;

    public static readonly CoverageExclusions None = new(new List<Entry>());

    public IReadOnlyList<Entry> Entries { get; }

    public static CoverageExclusions Parse(IEnumerable<string> lines)
    {
        var entries = new List<Entry>();
        foreach (var raw in lines)
        {
            var hash = raw.IndexOf('#', StringComparison.Ordinal);
            var text = (hash < 0 ? raw : raw[..hash]).Trim();
            if (text.Length == 0)
            {
                continue;
            }
            var m = Syntax.Match(text);
            var after = m.Groups["condition"].Success ? McDcReport.OneLine(m.Groups["condition"].Value) : null;
            var branch = after != null && after.StartsWith('[') && after.EndsWith(']') ? after[1..^1].Trim() : null;
            entries.Add(new Entry(text, Glob(m.Groups["path"].Value.Replace('\\', '/')),
                m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture) : null,
                branch == null ? after : null, branch));
        }
        return new CoverageExclusions(entries);
    }

    // the whole-file entries for a file, the whole-line entries for a line of it, and a condition's entries
    public IReadOnlyList<Entry> ForFile(string file) => Matching(file, e => e.Line == null);

    public IReadOnlyList<Entry> ForLine(string file, int line) => Matching(file, e => e.Line == line && e.Condition == null && e.Branch == null);

    public IReadOnlyList<Entry> ForBranch(string file, int line, string label) =>
        Matching(file, e => e.Line == line && e.Branch == McDcReport.OneLine(label));

    public IReadOnlyList<Entry> ForCondition(string file, int line, string condition) =>
        Matching(file, e => e.Line == line && e.Condition == McDcReport.OneLine(condition));

    public bool ExcludesFile(string file) => ForFile(file).Count > 0;

    private List<Entry> Matching(string file, Func<Entry, bool> where)
    {
        var path = file.Replace('\\', '/');
        return Entries.Where(e => where(e) && e.File.IsMatch(path)).ToList();
    }

    private static Regex Glob(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                sb.Append(slash ? "(.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else
            {
                sb.Append(pattern[i] switch { '*' => "[^/]*", '?' => "[^/]", var c => Regex.Escape(c.ToString()) });
            }
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
    }
}

// the minimum percentages; null: not checked
public sealed record CoverageThresholds(double? Lines = null, double? Branches = null, double? McDc = null)
{
    public bool Any => Lines != null || Branches != null || McDc != null;
}

/*
 * The coverage gate: fails when lines, branches or MC/DC (conditions shown, as in the reports) are
 * below their minimum, counting without what the exclusions leave out.
 *
 * As a ratchet, the exclusions should only shrink: an entry that leaves out no gap of a checked metric
 * (its code is covered now, or gone) is listed, and with strict, fails the gate.
 */
public static class CoverageGate
{
    public static (bool Passed, string Text) Check(IReadOnlyList<ModuleResult> modules, CoverageThresholds thresholds, CoverageExclusions exclusions,
        bool strict = false)
    {
        int lines = 0, linesRun = 0, branches = 0, taken = 0, conditions = 0, shown = 0;
        // the entries that left out a gap
        var needed = new HashSet<CoverageExclusions.Entry>();
        void Gap(IEnumerable<CoverageExclusions.Entry> entries, bool gap)
        {
            if (gap)
            {
                needed.UnionWith(entries);
            }
        }
        bool LineGap(FileCoverage f, int line) => thresholds.Lines != null && !f.LinesRun.Contains(line);
        bool DecisionGap(DecisionResult d) =>
            thresholds.Branches != null && !(d.OutcomeTrue && d.OutcomeFalse) || thresholds.McDc != null && d.Conditions.Any(c => !c.Covered);
        bool SwitchGap(SwitchCoverage s) => thresholds.Branches != null && s.Taken < s.Total;
        void Branch(FileCoverage f, int line, string label, bool wasTaken)
        {
            var forBranch = exclusions.ForBranch(f.File, line, label);
            if (forBranch.Count > 0)
            {
                Gap(forBranch, thresholds.Branches != null && !wasTaken);
                return;
            }
            branches++;
            taken += wasTaken ? 1 : 0;
        }

        foreach (var f in modules.SelectMany(FileCoverage.Of))
        {
            var forFile = exclusions.ForFile(f.File);
            if (forFile.Count > 0)
            {
                Gap(forFile, f.Lines.Any(l => LineGap(f, l)) || f.Decisions.Any(DecisionGap) || f.Switches.Any(SwitchGap));
                continue;
            }

            foreach (var line in f.Lines)
            {
                var forLine = exclusions.ForLine(f.File, line);
                if (forLine.Count > 0)
                {
                    Gap(forLine, LineGap(f, line));
                    continue;
                }
                lines++;
                linesRun += f.LinesRun.Contains(line) ? 1 : 0;
            }
            foreach (var d in f.Decisions)
            {
                var forLine = exclusions.ForLine(f.File, d.Decision.Line);
                if (forLine.Count > 0)
                {
                    Gap(forLine, DecisionGap(d));
                    continue;
                }
                foreach (var (outcome, wasTaken) in new[] { ("true", d.OutcomeTrue), ("false", d.OutcomeFalse) })
                {
                    Branch(f, d.Decision.Line, outcome, wasTaken);
                }
                foreach (var c in d.Conditions)
                {
                    var forCondition = exclusions.ForCondition(f.File, d.Decision.Line, c.Text);
                    if (forCondition.Count > 0)
                    {
                        Gap(forCondition, thresholds.McDc != null && !c.Covered);
                        continue;
                    }
                    conditions++;
                    shown += c.Covered ? 1 : 0;
                }
            }
            foreach (var s in f.Switches)
            {
                var forLine = exclusions.ForLine(f.File, s.Switch.Line);
                if (forLine.Count > 0)
                {
                    Gap(forLine, SwitchGap(s));
                    continue;
                }
                foreach (var (label, wasTaken) in s.Branches)
                {
                    Branch(f, s.Switch.Line, label, wasTaken);
                }
            }
        }

        var sb = new StringBuilder();
        var passed = true;
        sb.Append(CultureInfo.InvariantCulture, $"Tarp: coverage gate{(exclusions.Entries.Count > 0 ? $" ({exclusions.Entries.Count} exclusions)" : "")}\n");
        void Row(string name, int covered, int total, double? minimum)
        {
            if (minimum == null)
            {
                return;
            }
            var percent = total == 0 ? 100.0 : 100.0 * covered / total;
            var ok = percent >= minimum.Value;
            passed &= ok;
            sb.Append(string.Format(CultureInfo.InvariantCulture, "  {0,-9}{1,6}/{2,-6}{3,6:0.0}%  minimum {4}%  {5}\n",
                name, covered, total, percent, minimum.Value, ok ? "ok" : "FAILED"));
        }
        Row("Lines", linesRun, lines, thresholds.Lines);
        Row("Branches", taken, branches, thresholds.Branches);
        Row("MC/DC", shown, conditions, thresholds.McDc);

        var stale = exclusions.Entries.Where(e => !needed.Contains(e)).ToList();
        if (stale.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"  {(stale.Count == 1 ? "1 exclusion leaves" : $"{stale.Count} exclusions leave")} out no gap (covered now, or no such code){(strict ? ": FAILED, remove them" : "; remove them")}:\n");
            foreach (var e in stale)
            {
                sb.Append(CultureInfo.InvariantCulture, $"    {e.Text}\n");
            }
            passed &= !strict;
        }
        return (passed, sb.ToString());
    }
}
