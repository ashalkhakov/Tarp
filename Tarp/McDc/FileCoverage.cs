using System.Collections.Generic;
using System.Linq;

namespace Tarp.McDc;

public class FunctionCoverage
{
    public FunctionEntry Function { get; init; } = new();
    public bool Entered { get; init; }
    public int ExitsTaken { get; init; }
    // the exits that were never taken: "line 12", or "an exception" (the exception exits option)
    public List<string> ExitsNotTaken { get; init; } = new();
}

public class SwitchCoverage
{
    public SwitchEntry Switch { get; init; } = new();
    public int Taken { get; init; }
    public int Total { get; init; }
    public List<string> NotTaken { get; init; } = new();
}

/*
 * The coverage of one file of one module, from the probes that ran and the decisions' evaluations:
 * - lines: a line was run if a probe covering it ran (statements, empty blocks)
 * - branches: each decision's two outcomes, and each switch section (with "no case matched")
 * - functions: entered when their entry probe ran; exits are return/throw statements and the end
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

        foreach (var function in map.Functions)
        {
            var exits = function.Exits.Select(id => map.Probes[id]).ToList();
            For(function.File).Functions.Add(new FunctionCoverage
            {
                Function = function,
                Entered = function.Entry >= 0 && ran.Contains(function.Entry),
                ExitsTaken = exits.Count(p => ran.Contains(p.Id)),
                ExitsNotTaken = exits.Where(p => !ran.Contains(p.Id))
                    .Select(p => p.Kind == ProbeEntry.ExceptionExit ? "an exception" : $"line {p.Line}")
                    .ToList()
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
                NotTaken = sections.Where(x => !ran.Contains(x.probe)).Select(x => x.label).ToList()
            });
        }

        foreach (var d in module.Decisions)
        {
            For(d.Decision.File).Decisions.Add(d);
        }

        return files.Values;
    }
}
