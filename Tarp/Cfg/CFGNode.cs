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

        Debug.Assert(Successors.Count <= MaxAllowedSuccessors(Kind), "Too many successors");

        switch (Kind)
        {
            case CFGNodeKind.Entry:
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                //Debug.Assert(Successors.Count > 0, "There should be successors");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
                Debug.Assert(Predecessors.Count == 0, "There should be no predecessors");
                break;
            case CFGNodeKind.Exit:
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count == 0, "Statements are not allowed");
                //Debug.Assert(Successors.Count == 0, "there should be no successors");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
                Debug.Assert(Predecessors.Count > 0, "There should be predecessors");
                break;
            case CFGNodeKind.Block:
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                //Debug.Assert(Successors.Count == 1, "There should be exactly one successor");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
                Debug.Assert(Predecessors.Count > 0, "There should be predecessors");
                break;
            case CFGNodeKind.Branch:
                Debug.Assert(Condition != null, "Condition is required");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                //Debug.Assert(Successors.Count == 2, "There should be exactly two successors");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
                Debug.Assert(Predecessors.Count > 0, "There should be predecessors");
                break;
            case CFGNodeKind.Label:
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                //Debug.Assert(Successors.Count == 1, "There should be exactly one successor");
                Debug.Assert(Label.HasValue, "Label is required");
                Debug.Assert(Predecessors.Count > 0, "There should be predecessors");
                break;
            case CFGNodeKind.Jump:
                Debug.Assert(Condition == null, "Condition is not allowed");
                Debug.Assert(Statements.Count >= 0, "Statements are allowed");
                //Debug.Assert(Successors.Count == 1, "There should be exactly one successor");
                Debug.Assert(!Label.HasValue, "Label is not allowed");
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
        if (Kind == CFGNodeKind.Entry || Kind == CFGNodeKind.Label)
        {
            return false;
        }
        Kind = CFGNodeKind.Branch;
        Condition = condition;
        Label = null;

        foreach (var n in successors)
        {
            n.predecessors.Remove(this);
        }
        successors.Clear();
        AddSuccessor(yes);
        AddSuccessor(nay);

        return true;
    }

    public bool ToJump(CFGNode succ)
    {
        if (Kind == CFGNodeKind.Entry)
        {
            return false;
        }
        Kind = CFGNodeKind.Jump;
        Condition = null;
        Label = null;

        OverrideSuccessors(succ);

        return true;
    }

    public void ToLabel(SyntaxToken identifier)
    {
        if (Kind == CFGNodeKind.Branch)
        {

        }
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
        int maxSuccessors = MaxAllowedSuccessors(node.Kind);
        if (successors.Count > maxSuccessors)
        {
            throw new Exception("Cannot add a successor");
        }
        successors.Add(node);
        node.predecessors.Add(this);
    }

    private static int MaxAllowedSuccessors(CFGNodeKind kind)
    {
        return kind == CFGNodeKind.Entry ? 1 :
            kind == CFGNodeKind.Exit ? 0 :
            kind == CFGNodeKind.Block ? 1 :
            kind == CFGNodeKind.Branch ? 2 :
            kind == CFGNodeKind.Label ? 1 :
            kind == CFGNodeKind.Jump ? 1 :
            0;
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
        if (Kind == CFGNodeKind.Branch)
        {
            throw new Exception("Cannot override succesors of conditional branch");
        }
        foreach (var n in successors)
        {
            n.predecessors.Remove(this);
        }
        successors.Clear();
        successors.Add(exitNode);
        exitNode.predecessors.Add(this);
    }
}
