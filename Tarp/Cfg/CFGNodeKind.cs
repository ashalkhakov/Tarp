using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Tarp.Cfg;


[JsonConverter(typeof(JsonStringEnumMemberConverter))]
public enum CFGNodeKind
{
    [EnumMember(Value = "entry")]
    Entry,
    [EnumMember(Value = "exit")]
    Exit,
    [EnumMember(Value = "block")]
    Block,
    [EnumMember(Value = "branch")]
    Branch,
    [EnumMember(Value = "label")]
    Label,
    [EnumMember(Value = "jump")]
    Jump
}
