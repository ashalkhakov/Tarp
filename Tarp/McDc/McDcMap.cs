using System.Collections.Generic;
using System.Text.Json;

namespace Tarp.McDc;

// what the instrumenter did, written next to the instrumented sources and read by the report
public class McDcMap
{
    // file name suffix of maps in a results directory: <assembly>[.<target framework>].map.json
    public const string FileSuffix = ".map.json";

    public string ModuleId { get; set; } = "";
    public List<DecisionEntry> Decisions { get; set; } = new();
    public List<SkippedDecision> Skipped { get; set; } = new();
    // line, function and switch coverage (see CoverageRewriter)
    public List<ProbeEntry> Probes { get; set; } = new();
    public List<FunctionEntry> Functions { get; set; } = new();
    public List<SwitchEntry> Switches { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static McDcMap FromJson(string json) =>
        JsonSerializer.Deserialize<McDcMap>(json) ?? throw new JsonException("empty MC/DC map");
}

public class DecisionEntry
{
    public int Id { get; set; }
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    // position in the original file, identifies the decision across instrumentation passes
    public int SpanStart { get; set; }
    // the decision's source text
    public string Text { get; set; } = "";
    // its structure, with c0, c1, ... for the conditions, e.g. "(c0) == ((c1) || (c2))": the report
    // rebuilds the tree from it (see DecisionTree; maps without it: from the text)
    public string? Shape { get; set; }
    // a section of a switch statement with several case labels: its labels are its conditions, and its
    // evaluations come from which label matched (see SwitchEntry.LabelProbes and McDcReport)
    public int? SwitchId { get; set; }
    public int? SwitchSection { get; set; }
    public List<string> Conditions { get; set; } = new();
}

public class SkippedDecision
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public int SpanStart { get; set; }
    public string Text { get; set; } = "";
    public string Reason { get; set; } = "";
}

// a probe runs right before a statement: the statement's lines count as run once it has started
public class ProbeEntry
{
    public const string Statement = "statement";
    // the end of a function's body, reached by falling off it
    public const string End = "end";
    // an empty block, e.g. "else { }"
    public const string EmptyBlock = "empty-block";
    // the default section Tarp adds to a switch that has none: no case matched
    public const string NoCaseMatched = "no-case-matched";
    // a branch that isn't a line of its own, e.g. a side of ??
    public const string Branch = "branch";
    // a function left by an exception (the exception exits option): an exit, not a line
    public const string ExceptionExit = "exception-exit";

    public int Id { get; set; }
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int EndLine { get; set; }
    public string Kind { get; set; } = Statement;
}

public class FunctionEntry
{
    public int Id { get; set; }
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Name { get; set; } = "";
    // the probe that runs first in the function
    public int Entry { get; set; } = -1;
    // the probes of its exit points: return and throw statements, the end of the body, and (with the
    // exception exits option) an exception leaving it
    public List<int> Exits { get; set; } = new();
}

// a branch point other than a decision: a switch statement or expression, ??, ?., a foreach, or a try with catches
public class SwitchEntry
{
    public const string Statement = "switch";
    public const string Expression = "switch expression";
    public const string Coalesce = "??";
    public const string ConditionalAccess = "?.";
    public const string ForEach = "foreach";
    public const string Try = "try";

    public int Id { get; set; }
    public string Kind { get; set; } = Statement;
    public string File { get; set; } = "";
    public int Line { get; set; }
    // per section: its labels, and the probe of its first statement
    public List<string> Labels { get; set; } = new();
    public List<int> Sections { get; set; } = new();
    // switch statements, per section: the probe of each case label (in its when), run when that label
    // matched (no sections: the labels aren't probed, e.g. because of goto case); and the probe of
    // "no case matched" (-1: none, or it can't happen)
    public List<List<int>> LabelProbes { get; set; } = new();
    public int NoMatchProbe { get; set; } = -1;
}
