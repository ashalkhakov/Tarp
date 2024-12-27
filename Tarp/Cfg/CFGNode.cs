using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Tarp.Cfg;

public class CFGNode(int id, CFGNodeKind kind)
{
    public int Id { get; set; } = id;

    public CFGNodeKind Kind { get; set; } = kind;

    public SyntaxNode? Condition { get; set; }

    public SyntaxToken? Label { get; set; }

    public void SanityCheck()
    {
        Debug.Assert(Id >= 0);

        switch (Kind)
        {
            case CFGNodeKind.Entry:
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                Debug.Assert(Successors.Count > 0, "There should be successors");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
                Debug.Assert(Predecessors.Count == 0, "There should be no predecessors");
                break;
            case CFGNodeKind.Exit:
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count == 0, "Statements are not allowed");
                Debug.Assert(Successors.Count == 0, "there should be no successors");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
                Debug.Assert(Predecessors.Count > 0, "There should be predecessors");
                break;
            case CFGNodeKind.Block:
                /*
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                Debug.Assert(Successors.Count == 1, "There should be exactly one successor");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
                Debug.Assert(Predecessors.Count > 0, "There should be predecessors");*/
                break;
            case CFGNodeKind.Branch:
                Debug.Assert(Condition != null, "Condition is required");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                Debug.Assert(Successors.Count == 2, "There should be exactly two successors");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
                Debug.Assert(Predecessors.Count > 0, "There should be predecessors");
                break;
            case CFGNodeKind.Label:
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                Debug.Assert(Successors.Count == 1, "There should be exactly one successor");
                Debug.Assert(Label.HasValue, "Label is required");
                Debug.Assert(Predecessors.Count > 0, "There should be predecessors");
                break;
            default:
                Debug.Assert(false);
                break;
        }

        // connectedness checks
        foreach (var succ in Successors)
        {
            Debug.Assert(succ.Predecessors.Contains(this), "A successor node must contain this node as predecessor");
        }
    }

    public bool ToBranch(SyntaxNode? condition, CFGNode yes, CFGNode nay)
    {
        if (Kind == CFGNodeKind.Entry)
        {
            return false;
        }
        Kind = CFGNodeKind.Branch;
        Condition = condition;

        foreach (var n in successors)
        {
            n.predecessors.Remove(this);
        }
        successors.Clear();
        AddSuccessor(yes);
        AddSuccessor(nay);

        return true;
    }

    public void ToLabel(SyntaxToken identifier)
    {
        Kind = CFGNodeKind.Label;
        Label = identifier;
    }

    private List<SyntaxNode> statements = new();

    public IReadOnlyCollection<SyntaxNode> Statements { get => statements; }

    private List<CFGNode> predecessors = new();

    public IReadOnlyCollection<CFGNode> Predecessors { get => predecessors; }

    private List<CFGNode> successors = new();

    public IReadOnlyCollection<CFGNode> Successors { get => successors; }

    public void AddSuccessor(CFGNode node)
    {
        successors.Add(node);
        node.predecessors.Add(this);
    }

    public void AddStatement(SyntaxNode node)
    {
        statements.Add(node);
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

    public void OverrideSuccessors(CFGNode exitNode)
    {
        foreach (var n in successors)
        {
            n.predecessors.Remove(this);
        }
        successors.Clear();
        successors.Add(exitNode);
        exitNode.predecessors.Add(this);
    }
}
