using System.Text.Json.Serialization;

namespace Tarp.Types;

public class GCovJsonFile
{
    /// <summary>
    /// working directory where a compilation unit was compiled
    /// </summary>
    [JsonPropertyName("current_working_directory")]
    public string CurrentWorkingDirectory { get; set; }

    /// <summary>
    /// name of the data file (GCDA)
    /// </summary>
    [JsonPropertyName("data_file")]
    public string DataFile { get; set; }

    /// <summary>
    /// semantic version of the format
    /// </summary>
    [JsonPropertyName("format_version")]
    public string FormatVersion { get; set; } = "2";

    /// <summary>
    /// version of the GCC compiler
    /// </summary>
    [JsonPropertyName("gcc_version")]
    public string CompilerVersion { get; set; }

    [JsonPropertyName("files")]
    public GCovFile[] Files { get; set; }
}

public class GCovFile
{
    /// <summary>
    /// name of the source file
    /// </summary>
    [JsonPropertyName("file")]
    public string FileName { get; set; }

    [JsonPropertyName("functions")]
    public GCovFunction[] Functions { get; set; }

    [JsonPropertyName("lines")]
    public GCovLine[] Lines { get; set; }
}

public class GCovFunction
{
    /// <summary>
    /// number of blocks that are in the function
    /// </summary>
    [JsonPropertyName("blocks")]
    public int NumBlocks { get; set; }

    /// <summary>
    /// number of executed blocks of the function
    /// </summary>
    [JsonPropertyName("blocks_executed")]
    public int NumBlocksExecuted { get; set; }

    /// <summary>
    /// demangled name of the function
    /// </summary>
    [JsonPropertyName("demangled_name")]
    public string DemangledName { get; set; }

    /// <summary>
    /// column in the source file where the function ends
    /// </summary>
    [JsonPropertyName("end_column")]
    public int EndColumn { get; set; }

    /// <summary>
    /// line in the source file where the function ends
    /// </summary>
    [JsonPropertyName("end_line")]
    public int EndLine { get; set; }

    /// <summary>
    /// number of executions of the function
    /// </summary>
    [JsonPropertyName("execution_count")]
    public int ExecutionCount { get; set; }

    /// <summary>
    /// name of the function
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }

    /// <summary>
    /// column in the source file where the function begins
    /// </summary>
    [JsonPropertyName("start_column")]
    public int StartColumn { get; set; }

    /// <summary>
    /// line in the source file where the function begins
    /// </summary>
    [JsonPropertyName("start_line")]
    public int StartLine { get; set; }
}

public class GCovLine
{
    /// <summary>
    /// IDs of basic blocks that belong to the line
    /// </summary>
    [JsonPropertyName("block_ids")]
    public int[] BlockIds { get; set; }

    /// <summary>
    /// 
    /// </summary>
    [JsonPropertyName("branches")]
    public GCovBranch[] Branches { get; set; }

    [JsonPropertyName("calls")]
    public GCovCall[] Calls { get; set; }

    /// <summary>
    /// number of executions of the line
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("conditions")]
    public GCovCondition[] Conditions { get; set; }

    /// <summary>
    /// line number
    /// </summary>
    [JsonPropertyName("line_number")]
    public int LineNumber { get; set; }

    /// <summary>
    /// flag whether the line contains an unexecuted block (not all statements on the line are executed)
    /// </summary>
    [JsonPropertyName("unexecuted_block")]
    public bool UnexecutedBlock { get; set; }

    /// <summary>
    /// a name of a function this line belongs to (for a line with an inlined statements can be not set)
    /// </summary>
    [JsonPropertyName("function_name")]
    public string FunctionName { get; set; }
}

public class GCovBranch
{
    /// <summary>
    /// number of executions of the branch
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>
    /// ID of the basic block this branch jumps to
    /// </summary>
    [JsonPropertyName("destination_block_id")]
    public int DestinationBlockId { get; set; }

    /// <summary>
    /// true when the branch is a fall through branch
    /// </summary>
    [JsonPropertyName("fallthrough")]
    public bool Fallthrough { get; set; }

    /// <summary>
    /// ID of the basic block where this branch happens
    /// </summary>
    [JsonPropertyName("source_block_id")]
    public int SourceBlockId { get; set; }

    /// <summary>
    /// true when the branch is an exceptional branch
    /// </summary>
    [JsonPropertyName("throw")]
    public bool Throw { get; set; }
}

public class GCovCall
{
    /// <summary>
    /// ID of the basic block this calls continues after return
    /// </summary>
    [JsonPropertyName("destination_block_id")]
    public int DestinationBlockId { get; set; }

    /// <summary>
    /// number of times a function call returned (call count is equal to line::count)
    /// </summary>
    [JsonPropertyName("returned")]
    public int Returned { get; set; }

    /// <summary>
    /// ID of the basic block where this call happens
    /// </summary>
    [JsonPropertyName("source_block_id")]
    public int SourceBlockId { get; set; }
}

public class GCovCondition
{
    /// <summary>
    /// number of condition outcomes in this expression
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>
    /// number of covered condition outcomes in this expression
    /// </summary>
    [JsonPropertyName("covered")]
    public int Covered { get; set; }

    /// <summary>
    /// terms, by index, not seen as true in this expression
    /// </summary>
    [JsonPropertyName("not_covered_true")]
    public int[] NotCoveredTrue { get; set; }

    /// <summary>
    /// terms, by index, not seen as false in this expression
    /// </summary>
    [JsonPropertyName("not_covered_false")]
    public int[] NotCoveredFalse { get; set; }
}
