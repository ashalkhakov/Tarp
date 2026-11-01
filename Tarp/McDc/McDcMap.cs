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
    // the decision's source text: the report rebuilds the tree from it (see DecisionTree)
    public string Text { get; set; } = "";
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
