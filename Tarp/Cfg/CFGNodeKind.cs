using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Tarp.Cfg;


[JsonConverter(typeof(JsonStringEnumMemberConverter))]
public enum CFGNodeKind
{
    // method entry block
    [EnumMember(Value = "entry")]
    Entry,
    [EnumMember(Value = "exit")]
    // method exit block
    Exit,
    // basic block that jumps to the next instruction in the AST
    [EnumMember(Value = "block")]
    Block,
    // conditional branch
    [EnumMember(Value = "branch")]
    Branch,
    // labelled block
    [EnumMember(Value = "label")]
    Label,
    // basic block that jumps to a label
    [EnumMember(Value = "jump")]
    Jump,
    // an exception handler node encapsulates a try-catch-finally block
    [EnumMember(Value = "exception_handler")]
    ExceptionHandler
}
