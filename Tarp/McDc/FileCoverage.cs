using System.Collections.Generic;
using System.Linq;

namespace Tarp.McDc;

public class FunctionCoverage
{
    public FunctionEntry Function { get; init; } = new();
    public bool Entered { get; init; }
    // return/throw statements and the end: how many, how many taken, and those never taken ("line 12")
    public int Exits { get; init; }
    public int ExitsTaken { get; init; }
    public List<string> ExitsNotTaken { get; init; } = new();
    // whether an exception left it (the exception exits option; null without it, and for iterators): a
    // metric of its own, not a gap, as most functions are never left by one in tests
    public bool? LeftByException { get; init; }
    // its lines (not those of local functions in it), and those of them that ran
    public HashSet<int> Lines { get; init; } = new();
    public HashSet<int> LinesRun { get; init; } = new();
}

public class SwitchCoverage
{
    public SwitchEntry Switch { get; init; } = new();
    public int Taken { get; init; }
    public int Total { get; init; }
    public List<string> NotTaken { get; init; } = new();
    // each branch's label (a case's labels, an arm's pattern, "no iterations", ...) and whether it was taken
    public List<(string Label, bool Taken)> Branches { get; init; } = new();
}

/*
 * The coverage of one file of one module, from the probes that ran and the decisions' evaluations:
 * - lines: a line was run if a probe covering it ran (statements, empty blocks)
 * - branches: each decision's two outcomes, and each switch section (with "no case matched")
 * - functions: entered when their entry probe ran; exits are return/throw statements and the end, and
 *   apart from them, whether an exception left the function
 */
public class FileCoverage
{
    public string File { get; init; } = "";
    public HashSet<int> Lines { get; } = new();
    public HashSet<int> LinesRun { get; } = new();
    // how many times each line ran: the most of the innermost probes on it
    public Dictionary<int, long> LineCounts { get; } = new();
    public List<FunctionCoverage> Functions { get; } = new();
    public List<SwitchCoverage> Switches { get; } = new();
    public List<DecisionResult> Decisions { get; } = new();

    public int Branches => Decisions.Count * 2 + Switches.Sum(s => s.Total);

    public int BranchesTaken =>
        Decisions.Sum(d => (d.OutcomeTrue ? 1 : 0) + (d.OutcomeFalse ? 1 : 0)) + Switches.Sum(s => s.Taken);

    public static IEnumerable<FileCoverage> Of(ModuleResult module)
    {
        var map = module.Map;
        var ran = module.Probes;
        var files = new Dictionary<string, FileCoverage>();
        FileCoverage For(string file) => files.TryGetValue(file, out var f) ? f : files[file] = new FileCoverage { File = file };

        // a line belongs to the innermost probes covering it: the lines of an arm of a switch expression,
        // or of a lambda's body, are run when they ran, not when the statement around them started
        foreach (var file in map.Probes.Where(p => p.Kind is ProbeEntry.Statement or ProbeEntry.EmptyBlock).GroupBy(p => p.File))
        {
            var f = For(file.Key);
            foreach (var line in file.SelectMany(p => Enumerable.Range(p.Line, p.EndLine - p.Line + 1)).Distinct())
            {
                var covering = file.Where(p => p.Line <= line && line <= p.EndLine).ToList();
                var innermost = covering.Min(p => p.EndLine - p.Line);
                f.Lines.Add(line);
                var count = covering.Where(p => p.EndLine - p.Line == innermost && ran.Contains(p.Id))
                    .Select(p => module.Counts.TryGetValue(p.Id, out var c) ? c : 1)
                    .DefaultIfEmpty(0)
                    .Max();
                if (count > 0)
                {
                    f.LinesRun.Add(line);
                    f.LineCounts[line] = count;
                }
            }
        }

        var functionLines = map.Probes
            .Where(p => p.Function >= 0 && p.Kind is ProbeEntry.Statement or ProbeEntry.EmptyBlock)
            .GroupBy(p => p.Function)
            .ToDictionary(g => g.Key, g => g.SelectMany(p => Enumerable.Range(p.Line, p.EndLine - p.Line + 1)).ToHashSet());
        foreach (var function in map.Functions)
        {
            var exits = function.Exits.Select(id => map.Probes[id]).Where(p => p.Kind != ProbeEntry.ExceptionExit).ToList();
            var exception = function.Exits.Select(id => map.Probes[id]).FirstOrDefault(p => p.Kind == ProbeEntry.ExceptionExit);
            var file = For(function.File);
            var lines = functionLines.TryGetValue(function.Id, out var l) ? l : new HashSet<int>();
            file.Functions.Add(new FunctionCoverage
            {
                Lines = lines,
                LinesRun = lines.Where(file.LinesRun.Contains).ToHashSet(),
                Function = function,
                Entered = function.Entry >= 0 && ran.Contains(function.Entry),
                Exits = exits.Count,
                ExitsTaken = exits.Count(p => ran.Contains(p.Id)),
                ExitsNotTaken = exits.Where(p => !ran.Contains(p.Id)).Select(p => $"line {p.Line}").ToList(),
                LeftByException = exception == null ? null : ran.Contains(exception.Id)
            });
        }

        foreach (var s in map.Switches)
        {
            var sections = s.Sections.Select((probe, i) => (probe, label: s.Labels[i])).Where(x => x.probe >= 0).ToList();
            For(s.File).Switches.Add(new SwitchCoverage
            {
                Switch = s,
                Total = sections.Count,
                Taken = sections.Count(x => ran.Contains(x.probe)),
                NotTaken = sections.Where(x => !ran.Contains(x.probe)).Select(x => x.label).ToList(),
                Branches = sections.Select(x => (x.label, ran.Contains(x.probe))).ToList()
            });
        }

        foreach (var d in module.Decisions)
        {
            For(d.Decision.File).Decisions.Add(d);
        }

        return files.Values;
    }
}
