using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;

namespace Tarp.Cfg;

public class CFGNode(int id, CFGNodeKind kind)
{
    public int Id { get; set; } = id;

    public CFGNodeKind Kind { get; set; } = kind;

    public SyntaxNode? Condition { get; set; }

    public void SanityCheck()
    {
        Debug.Assert(Id >= 0);

        switch (Kind)
        {
            case CFGNodeKind.Entry:
                Debug.Assert(Kind == CFGNodeKind.Entry && Condition == null && Statements.Count >= 0 && Successors.Count >= 0);
                break;
            case CFGNodeKind.Exit:
                Debug.Assert(Condition == null && Statements.Count == 0 && Successors.Count == 0);
                break;
            case CFGNodeKind.Block:
                Debug.Assert(Condition == null && Statements.Count >= 0 && Successors.Count == 1);
                break;
            case CFGNodeKind.Branch:
                Debug.Assert(Condition != null && Statements.Count >= 0 && Successors.Count == 2);
                break;
            default:
                Debug.Assert(false);
                break;
        }
    }

    public void ToBranch(SyntaxNode? condition, CFGNode yes, CFGNode nay)
    {
        Kind = CFGNodeKind.Branch;
        Condition = condition;
        successors.Add(yes);
        successors.Add(nay);
    }

    private List<SyntaxNode> _statements = new();

    public IReadOnlyCollection<SyntaxNode> Statements { get => _statements; }

    private List<CFGNode> successors = new();

    public IReadOnlyCollection<CFGNode> Successors { get => successors; }

    public void AddSuccessor(CFGNode node)
    {
        successors.Add(node);
    }

    public void AddStatement(SyntaxNode node)
    {
        _statements.Add(node);
    }

    public override string ToString()
    {
        var successors = string.Join(",", Successors.Select(s => s.Id.ToString()));
        if (Kind == CFGNodeKind.Branch)
        {
            return $"{Id}: {Kind} {Condition}. Successors {successors}";
        }
        return $"{Id}: {Kind} {string.Join("\r\n", Statements.Select(s => s.ToString()))}. Successors: {successors}";
    }
}
