using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tarp.McDc;

/*
 * What's left out of the gate, one entry a line, as the reports name files (# starts a comment):
 *
 *   Shop.Core/Legacy/*.cs     whole files: * within a directory, ** across directories, ? one character
 *   Shop.Core/Orders.cs:68    everything on a line: its lines, its decisions' outcomes and conditions,
 *                             its branch points
 *
 * Only the gate leaves them out: the reports still show them.
 */
public sealed class CoverageExclusions
{
    private sealed record Entry(string Text, Regex File, int? Line)
    {
        public bool Used { get; set; }
    }

    private readonly List<Entry> _entries;

    private CoverageExclusions(List<Entry> entries) => _entries = entries;

    public static readonly CoverageExclusions None = new(new List<Entry>());

    public int Count => _entries.Count;

    // the entries that matched nothing: stale, once their code has been fixed or moved
    public IEnumerable<string> Unused => _entries.Where(e => !e.Used).Select(e => e.Text);

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
            var colon = text.LastIndexOf(':');
            int? line = null;
            var path = text;
            if (colon > 0 && int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                line = n;
                path = text[..colon];
            }
            entries.Add(new Entry(text, Glob(path.Replace('\\', '/')), line));
        }
        return new CoverageExclusions(entries);
    }

    public bool ExcludesFile(string file) => Match(file, null);

    public bool ExcludesLine(string file, int line) => Match(file, line);

    private bool Match(string file, int? line)
    {
        var path = file.Replace('\\', '/');
        var matched = false;
        foreach (var e in _entries.Where(e => (e.Line == null || e.Line == line) && e.File.IsMatch(path)))
        {
            e.Used = true;
            matched = true;
        }
        return matched;
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
 */
public static class CoverageGate
{
    public static (bool Passed, string Text) Check(IReadOnlyList<ModuleResult> modules, CoverageThresholds thresholds, CoverageExclusions exclusions)
    {
        int lines = 0, linesRun = 0, branches = 0, taken = 0, conditions = 0, shown = 0;
        foreach (var f in modules.SelectMany(FileCoverage.Of))
        {
            if (exclusions.ExcludesFile(f.File))
            {
                continue;
            }
            bool Kept(int line) => !exclusions.ExcludesLine(f.File, line);

            var kept = f.Lines.Where(Kept).ToList();
            lines += kept.Count;
            linesRun += kept.Count(f.LinesRun.Contains);
            foreach (var d in f.Decisions.Where(d => Kept(d.Decision.Line)))
            {
                branches += 2;
                taken += (d.OutcomeTrue ? 1 : 0) + (d.OutcomeFalse ? 1 : 0);
                conditions += d.Conditions.Count;
                shown += d.Conditions.Count(c => c.Covered);
            }
            foreach (var s in f.Switches.Where(s => Kept(s.Switch.Line)))
            {
                branches += s.Total;
                taken += s.Taken;
            }
        }

        var sb = new StringBuilder();
        var passed = true;
        sb.Append(CultureInfo.InvariantCulture, $"Tarp: coverage gate{(exclusions.Count > 0 ? $" ({exclusions.Count} exclusions)" : "")}\n");
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
        foreach (var e in exclusions.Unused)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  exclusion matches nothing: {e}\n");
        }
        return (passed, sb.ToString());
    }
}
