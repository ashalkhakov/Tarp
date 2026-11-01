using System.Collections.Generic;
using System.Linq;

namespace Tarp.McDc;

public class FunctionCoverage
{
    public FunctionEntry Function { get; init; } = new();
    public bool Entered { get; init; }
    public int ExitsTaken { get; init; }
    // lines of the exits that were never taken
    public List<int> ExitsNotTaken { get; init; } = new();
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

        foreach (var p in map.Probes.Where(p => p.Kind is ProbeEntry.Statement or ProbeEntry.EmptyBlock))
        {
            var f = For(p.File);
            for (var line = p.Line; line <= p.EndLine; line++)
            {
                f.Lines.Add(line);
                if (ran.Contains(p.Id))
                {
                    f.LinesRun.Add(line);
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
                ExitsNotTaken = exits.Where(p => !ran.Contains(p.Id)).Select(p => p.Line).ToList()
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
