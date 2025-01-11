using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Tarp.Cfg;


[JsonConverter(typeof(JsonStringEnumMemberConverter))]
public enum CFGNodeKind
{
    // method entry block (no predecessors, one successor)
    [EnumMember(Value = "entry")]
    Entry,

    // method exit block (no successors, many predecessors)
    [EnumMember(Value = "exit")]
    Exit,

    // a basic block that jumps to the next instruction in the AST
    [EnumMember(Value = "block")]
    Block,

    // conditional branch: has two successors
    [EnumMember(Value = "branch")]
    Branch,

    // labelled block: can be targeted by a goto
    [EnumMember(Value = "label")]
    Label,

    // basic block that ends with a goto
    [EnumMember(Value = "jump")]
    Jump,

    // basic block that ends with a jump to either finally or one of the exception handlers
    [EnumMember(Value = "try")]
    Try,

    // basic block that can only be entered by raising an exception
    [EnumMember(Value = "exception_handler")]
    ExceptionHandler,

    [EnumMember(Value = "finally_handler")]
    FinallyHandler
}
