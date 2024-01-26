using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Tarp.Types;

/*
 * build a model of software as a control-flow graph.
 * a control flow graph has nodes (basic blocks), edges (control transfers). some nodes are entry(start) and some are exit(final).
 * 
 * path: a sequence of nodes [n1,n2,...,nm]. (each pair of nodes is an edge)
 * length: the number of edges (a single node is a path of length 0)
 * subpath: a subsequence of nodes in path p is a subpath of p.
 * 
 * test path: a path that starts at an initial node and ends at a final node.
 * test paths represent the execution of test cases.
 * 
 * a test path p visits node n if n is in p.
 * a test path p visits edge e if e is in p.
 * a test path p tours subpath q if q is a subpath of p. (technically, each edge is also a subpath)
 * 
 * test requirements (TR): describe properties of test paths.
 * test criterion: rules that define test requirements.
 * satisfaction: given a set TR of test requirements for a criterion C, a set of tests T satisfies C on a graph iff
 *    for every test requirement in TR, there exists a test path in path(T) that meets test requirement tr.
 * require tests to visit or tour specific sets of nodes, edges, or subpaths.
 * 
 * criteria follows.
 * 
 * node coverage (NC): set TR of test requirements contains each reachable node in G.
 * edge coverage (EC): set TR of test requirements contains each reachable path of length up to 1, inclusive, in G.
 */

public abstract record CfgBlockFirst
{
}
// regular entry
public record CfgBlockEntry(): CfgBlockFirst;
// regular entry via GOTO
public record CfgBlockLabel(int Label): CfgBlockFirst;
// the start of a [finally] block (either regular or exception entry)
public record CfgBlockFinally() : CfgBlockFirst;
// the start of exception handling [catch (E)] block (only exception entry)
// NOTE: [LabelNext] represents the next [catch (E1)] block or none at all
public record CfgBlockCatch(SyntaxNode? Guard, int Label, int? LabelNext) : CfgBlockFirst;

public abstract record CfgBlockLast
{
}
// regular exit
public record CfgBlockExit() : CfgBlockLast;
// non-conditional branch (aka GOTO)
public record CfgBlockBranch(SyntaxNode? Instruction, int Label) : CfgBlockLast;
// conditional branch
public record CfgBlockConditionalBranch(SyntaxNode Cond, int LabelIfSo, int LabelIfNot) : CfgBlockLast;
// regular return from a method
public record CfgBlockReturn(SyntaxNode? Expression) : CfgBlockLast;
// exceptional exit from a block
public record CfgBlockThrow(SyntaxNode? Expression, int LabelCatch) : CfgBlockLast;

public class CfgBasicBlock
{
    public CfgBasicBlock(CfgBlockFirst first, CfgBlockLast last)
    {
        First = first;
        Middle = null;
        Last = last;
    }

    public int Id { get; set; }

    public CfgBlockFirst First { get; set; }
    public LinkedListNode<SyntaxNode>? Middle { get; set; }
    
    public CfgBlockLast Last { get; set; }
}

/// <summary>
/// The design is based on https://plv.mpi-sws.org/plerg/papers/ramsey-cfg-zipper-2up.pdf
/// </summary>
/// <remarks
/// A block is represented by
/// - its incoming arcs (either an Entry point or a Label)
/// - its sequence of instructions in the middle (where no control flow branching is present)
/// - its outgoing arcs (either an Exit point, a Conditional Branch, an Unconditional Branch, or a Return)
/// 
/// Since blocks don't have identity we can easily splice the subgraphs.
/// 
/// Each subgraph (represented by the `CfgGraphBuilder`) can be focussed or not.
/// - if the subgraph is focussed, you can make modifications around the focus point
/// - but as you 'commit' your changes, you must drop the focus (by adding it to the block collection)
/// </remarks>
public class CfgGraphBuilder
{
    public CfgGraphBuilder()
    {
        _blocks = new HashSet<CfgBasicBlock>();
        _focus = new CfgBasicBlock(new CfgBlockEntry(), new CfgBlockExit());
    }

    private readonly HashSet<CfgBasicBlock> _blocks;
    private CfgBasicBlock? _focus;

    static void EmitBaseNode(StringWriter writer, string name)
    {
        writer.Write($"    {name} [ shape = \"box\"\n" +
                    $"             fontname = \"Monospace\"\n" +
                    $"             label = \"<{name}>\\l");
        writer.Write("\" ];\n");
    }

    public string EmitCFG()
    {
        Debug.Assert(_focus == null);

        using var writer = new StringWriter();

        writer.Write("digraph G\n{\n");

        EmitBaseNode(writer, "exit");

        var id = 0;
        foreach (var n in _blocks)
        {
            id++;

            string? name;
            if (n.First is CfgBlockLabel l)
            {
                name = $"lab{l.Label}";
            }
            else if (n.First is CfgBlockEntry)
            {
                name = "entry";
            }
            else
            {
                name = $"n{id}";
            }

            writer.Write($"    {name} [ shape = \"box\"\n" +
                        $"             fontname = \"Monospace\"\n" +
                        $"             label = \"<{name}>\\l");

            var j = 0;
            var mid = n.Middle;
            for (; mid != null; mid = mid.Next, j++)
            {
                writer.Write($"{j}:\t{mid.Value}");
                writer.Write("\\l");
            }
            writer.Write("\" ];\n");

            if (n.Last is CfgBlockExit)
            {
                writer.Write($"      {name} -> exit;\n");
            }
            else if (n.Last is CfgBlockBranch br)
            {
                writer.Write($"      {name} -> lab{br.Label};\n");
            }
            else if (n.Last is CfgBlockConditionalBranch cbr)
            {
                writer.Write($"      {name} -> lab{cbr.LabelIfSo} [label = \"{cbr.Cond}\"];\n");
                writer.Write($"      {name} -> lab{cbr.LabelIfNot} [label = \"!({cbr.Cond})\"];\n");
            }
            else if (n.Last is CfgBlockReturn ret)
            {
                // NOTE: a return block goes to the exit also
                writer.Write($"      {name} -> exit [label = \"{ret.Expression}\"];\n");
            }
        }
        writer.Write("}\n");

        return writer.ToString();
    }

    public void Instruction(SyntaxNode node)
    {
        Debug.Assert(_focus != null);

        var middle = _focus.Middle;
        DoublyLinkedList.InsertAfter(ref middle, node);
        _focus.Middle = middle;
    }

    /// <summary>
    /// E I1 . I2 X ==> E I1 . goto L ; L: I1 X
    /// where
    /// E is entry(either Entry or L:)
    /// X is exit(either Exit, goto L or Return E)
    /// I is instruction(s)
    /// . is the focus point
    /// </summary>
    /// <param name="label"></param>
    /// <param name="token"></param>
    public void Label(int label, SyntaxToken token)
    {
        Debug.Assert(_focus != null);

        var last = _focus.Last;

        _focus.Last = new CfgBlockBranch(null, label);

        var middle = _focus.Middle;
        DoublyLinkedList.Split(ref middle, out var tail);
        _focus.Middle = middle;

        var nextBlock = new CfgBasicBlock(new CfgBlockLabel(label), last);
        nextBlock.Middle = tail;

        Unfocus();
        _focus = nextBlock;
    }

    void StampBlock(CfgBasicBlock block)
    {
        _blocks.Add(block);
    }

    public void Branch(SyntaxNode instruction, int label)
    {
        // FIXME: same as [Label]. remove?

        Debug.Assert(_focus != null);

        var last = _focus.Last;

        _focus.Last = new CfgBlockBranch(instruction, label);

        var middle = _focus.Middle;
        DoublyLinkedList.Split(ref middle, out var tail);
        _focus.Middle = middle;

        var nextBlock = new CfgBasicBlock(new CfgBlockLabel(label), last);
        nextBlock.Middle = tail;

        Unfocus();
        _focus = nextBlock;
    }

    /// <summary>
    /// CondBranch(C, L1, L2, L3):
    ///   E I1 . I2 X ==> E I1 Cond(C,L1,L2) ; L3: I2 X
    /// where
    /// E is entry(either Entry or L:)
    /// X is exit(either Exit, goto L or Return E)
    /// I is instruction(s)
    /// . is the focus point
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="ifsoLabel"></param>
    /// <param name="ifNotLabel"></param>
    /// <param name="done"></param>
    public void CondBranch(SyntaxNode condition, int ifsoLabel, int ifNotLabel, int? done)
    {
        Debug.Assert(_focus != null);

        // [condition] is the the condition. okay.
        // [ifsoLabel]: the start of the THEN statement block.
        // [ifNotLabel]: the start of the ELSE statement block.

        // to insert a conditional branch,
        // split the focused block in two.
        // - the tail, which loses the focus, is inserted into [_blocks] (NO LABELS??? EH?)
        // - the head, which keeps the focus, is followed by a conditional branch to [ifsoLabel] or [ifNotLabel]
        //   depending on [condition]

        var last = _focus.Last;

        _focus.Last = new CfgBlockConditionalBranch(condition, ifsoLabel, ifNotLabel);

        var middle = _focus.Middle;
        DoublyLinkedList.Split(ref middle, out var tail);
        _focus.Middle = middle;

        if (done != null)
        {
            var nextBlock = new CfgBasicBlock(new CfgBlockLabel(done.Value), last);
            nextBlock.Middle = tail;

            Unfocus();
            _focus = nextBlock;
        }
        else
        {
            Debug.Assert(tail == null);
            Unfocus();
            _focus = null;
        }
    }

    public void Return(ExpressionSyntax? expression)
    {
        Debug.Assert(_focus != null);

        var last = _focus.Last; // TODO: what should we do with this one?

        _focus.Last = new CfgBlockReturn(expression);
    }

    public void Zip()
    {
        Debug.Assert(_focus != null);
        var middle = _focus.Middle;
        while (middle != null && middle.Prev != null)
        {
            middle = middle.Prev;
        }
        _focus.Middle = middle;
    }

    public void Unzip(CfgBasicBlock block)
    {
        Debug.Assert(block != null);
        Debug.Assert(_focus == null);

        _focus = block;
    }

    public void Unfocus()
    {
        Debug.Assert(_focus != null);
        Zip();
        StampBlock(_focus);
        _focus = null;
    }

    public void Splice(int labelEntry, int labelExit, CfgGraphBuilder g)
    {
        Debug.Assert(g._focus == null);

        foreach (var n in g._blocks)
        {
            if (n.First is CfgBlockEntry)
            {
                n.First = new CfgBlockLabel(labelEntry);
            }
            if (n.Last is CfgBlockExit)
            {
                n.Last = new CfgBlockBranch(null, labelExit);
            }
            _blocks.Add(n);
        }
    }

    public void Splice2(int labelEntry, int labelExit, CfgGraphBuilder g)
    {
        Debug.Assert(g._focus == null);
        Debug.Assert(_focus != null);

        if (_focus.First is CfgBlockLabel l && l.Label == labelEntry)
        {
            // labelEntry: I1 . I2 X
            // g = Entry I Exit
            // -->
            // labelExit: I1 . I2 X
            // g = LabelEntry: I goto LabelExit
            _focus.First = new CfgBlockLabel(labelExit);
        }

        foreach (var n in g._blocks)
        {
            if (n.First is CfgBlockEntry)
            {
                n.First = new CfgBlockLabel(labelEntry);
            }
            if (n.Last is CfgBlockExit)
            {
                n.Last = new CfgBlockBranch(null, labelExit);
            }
            _blocks.Add(n);
        }
    }

    public void SpliceHead(CfgGraphBuilder g)
    {
        Debug.Assert(_focus != null);
        Debug.Assert(g != null);

        // if g is without an exit, halt
        //
        // (g',h') = SpliceHead(h, g):
        // - the entry node of [g] is joined to h,
        // and the nodes leading up to g's exit
        // become a new head h', which can be used for further splicing
        // and g' is the new subgraph such that
        // - h goes to g', g' goes to h'
        // - h' is the new focus
        CfgBasicBlock? entryNode = null;
        CfgBasicBlock? exitNode = null;

        foreach (var b in g._blocks)
        {
            if (b.First is CfgBlockEntry entry)
            {
                Debug.Assert(entryNode == null);
                entryNode = b;
            }
            if (b.Last is CfgBlockExit exit || b.Last is CfgBlockReturn ret)
            {
                Debug.Assert(exitNode == null);
                exitNode = b;
            }
        }

        Debug.Assert(entryNode != null);
        Debug.Assert(exitNode != null && exitNode.Last is CfgBlockExit);
        

    }
    public void SpliceTail(CfgGraphBuilder g)
    {
        // if this graph is without an exit, halt
    }
    public void SpliceHeadOnly(CfgGraphBuilder g)
    {
        // 
    }

    public CfgBasicBlock ReplaceEntryByLabel()
    {
        Debug.Assert(_focus == null);
        // find the entry node of a graph and remove it,
        // leaving a tail leading into the rest of the graph

        CfgBasicBlock? blockWithEntry = null;
        foreach (var block in _blocks)
        {
            if (block.First is CfgBlockEntry e)
            {
                blockWithEntry = block;
                break;
            }
        }
        Debug.Assert(blockWithEntry != null);
        _blocks.Remove(blockWithEntry);

        return blockWithEntry;
    }

    public CfgBasicBlock RemoveEntry()
    {
        Debug.Assert(_focus == null);
        // find the entry node of a graph and remove it,
        // leaving a tail leading into the rest of the graph

        CfgBasicBlock? blockWithEntry = null;
        foreach (var block in _blocks)
        {
            if (block.First is CfgBlockEntry e)
            {
                blockWithEntry = block;
                break;
            }
        }
        Debug.Assert(blockWithEntry != null);
        _blocks.Remove(blockWithEntry);

        return blockWithEntry;
    }
    public void SpliceFocusEntry(CfgGraphBuilder g)
    {
        // splice a single-entry, single-exit graph g
        // into the edge at the current focus.
        // the new focus is at the entry edge of the spliced-in graph.
    }
    public void SpliceFocusExit(CfgGraphBuilder g)
    {
        // splice a single-entry, single-exit graph g
        // into the edge at the current focus.
        // the new focus is at the exit edge of the spliced-in graph.
    }

    public void GotoStart()
    {
        Debug.Assert(_focus != null);

        var n = _focus.Middle;
        while (n != null && n.Prev != null)
        {
            n = n.Prev;
        }
        _focus.Middle = n;
    }

    public void GotoEnd()
    {
        Debug.Assert(_focus != null);

        var n = _focus.Middle;
        while (n != null && n.Next != null)
        {
            n = n.Next;
        }
        _focus.Middle = n;
    }
}

/*
public class CfgBuildingVisitor2 : CSharpSyntaxVisitor
{
    readonly List<CfgNode> _nodes = new();

    CfgNode _startNode;
    CfgNode _endNode;
    readonly Stack<CfgFragment> _stack = new();

    public IReadOnlyCollection<CfgNode> CfgNodes => _nodes;

    public string EmitCFG()
    {
        using var writer = new StringWriter();

        writer.Write("digraph G\n{\n");
        foreach (var n in _nodes)
        {
            writer.Write($"    n{n.Id} [ shape = \"box\"\n" +
                        $"             fontname = \"Monospace\"\n" +
                        $"             label = \"<{n.Id}>\\l");

            var j = 0;
            foreach (var i in n.Nodes)
            {
                writer.Write($"{j}:\t{i.ToString()}");
                writer.Write("\\l");
                j++;
            }
            writer.Write("\" ];\n");

            foreach (var o in n.Outgoing)
            {
                writer.Write($"      n{n.Id} -> n{o.Destination.Id};\n");
            }
        }
        writer.Write("}\n");

        return writer.ToString();
    }

    CfgNode AllocateNode()
    {
        var id = _nodes.Count;
        var node = new CfgNode
        {
            Id = id,
            Incoming = new(),
            Outgoing = new(),
            Nodes = new()
        };
        _nodes.Add(node);
        return node;
    }

    public override void VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var frag = _stack.Peek();

        // OR: make a new node, make it the new EXIT node of the subgraph
        frag.Entry.Parent.Nodes.Add(node);
    }

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        var prev = _stack.Pop();

        var thenBlock = AllocateNode();
        _stack.Push(thenBlock);
        Visit(node.Statement);
        _stack.Pop();

        CfgNode? elseblock = null;
        if (node.Else != null)
        {
            elseblock = AllocateNode();
            _stack.Push(elseblock);
            Visit(node.Else.Statement);
            _stack.Pop();
        }

        prev.Outgoing.Add(new CfgBranch { Destination = thenBlock, Kind = CfgBranchKind.TrueBranch, OpCode = node.Condition });
        thenBlock.Incoming.Add(prev);

        if (elseblock != null)
        {
            prev.Outgoing.Add(new CfgBranch { Destination = elseblock, Kind = CfgBranchKind.FalseBranch, OpCode = node.Condition });
            elseblock.Incoming.Add(prev);
        }

        var newblock = AllocateNode();
        LinkNodes(thenBlock, newblock);
        if (elseblock != null)
        {
            LinkNodes(elseblock, newblock);
        }
        else
        {
            prev.Outgoing.Add(new CfgBranch { Destination = newblock, Kind = CfgBranchKind.FalseBranch, OpCode = node.Condition });
            newblock.Incoming.Add(prev);
        }
        _stack.Push(newblock);
    }

    public override void VisitWhileStatement(WhileStatementSyntax node)
    {
        var prev = _stack.Pop();

        // allocate the new entry and exit nodes

        var thenBlock = AllocateNode();

        var newblock = AllocateNode();
        var start = _startNode;
        _startNode = prev;
        var end = _endNode;
        _endNode = end;

        _stack.Push(thenBlock);
        Visit(node.Statement);
        _stack.Pop();

        _startNode = start;
        _endNode = end;

        // FIXME: handling continue and break inside the loop? we need an environment for that. 2 destinations: continue is the start of the loop. break is the end of the loop.
        // - so a loop has its own [start] and [end] nodes:
        //  - start: the real start of the loop (here it's PREV)
        //  - end: the first node after the loop (here it's NEWBLOCK)
        // FIXME: handling gotos and labels? each statement after a label is a leader...

        prev.Outgoing.Add(new CfgBranch { Destination = thenBlock, Kind = CfgBranchKind.TrueBranch, OpCode = node.Condition });
        thenBlock.Incoming.Add(prev);

        prev.Outgoing.Add(new CfgBranch { Destination = newblock, Kind = CfgBranchKind.FalseBranch, OpCode = node.Condition });
        newblock.Incoming.Add(prev);

        _stack.Push(newblock);
    }

    public override void VisitBreakStatement(BreakStatementSyntax node)
    {
        var p = _stack.Pop();

        LinkNodes(p, _endNode);

        var n = AllocateNode();

        _stack.Push(n);
    }

    public override void VisitContinueStatement(ContinueStatementSyntax node)
    {
        var p = _stack.Pop();

        LinkNodes(p, _startNode);

        var n = AllocateNode();

        _stack.Push(n);
    }

    public override void VisitBlock(BlockSyntax node)
    {
        var i = 0;
        while (i < node.Statements.Count)
        {
            Visit(node.Statements[i]);
            i++;
        }
    }

    public override void VisitReturnStatement(ReturnStatementSyntax node)
    {
        var p = _stack.Pop();

        LinkNodes(p, _endNode);

        _stack.Push(AllocateNode());
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        _startNode = AllocateNode();
        _endNode = AllocateNode();

        if (node.Body == null || node.Body.Statements.Count == 0)
        {
            // TODO: handle expression body
            LinkNodes(_startNode, _endNode);
        }
        else
        {
            var n = AllocateNode();

            LinkNodes(_startNode, n);

            var newEntry = new CfgBranch { Parent = n, Destination = null, Kind = CfgBranchKind.Default, OpCode = null };
            var newExit = new CfgBranch { Parent = n, Destination = null, Kind = CfgBranchKind.Default, OpCode = null };
            
            _stack.Push(
                new CfgFragment
                {
                    Entry = newEntry,
                    Exit = newExit
                });

            Visit(node.Body);
            var p = _stack.Pop();
            if (p != _endNode)
            {
                if (p.Nodes.Count == 0)
                {
                    _nodes.RemoveAt(_nodes.Count - 1);
                }
                else
                {
                    LinkNodes(p, _endNode);
                }
            }
        }
    }

    private void LinkNodes(CfgNode pred, CfgNode node)
    {
        pred.Outgoing.Add(new CfgBranch { Destination = node, Kind = CfgBranchKind.Default, OpCode = null });
        node.Incoming.Add(pred);
    }
}*/

public record CfgBuilderLoopInfo(int LoopStartLabel, int LoopEndLabel);

public class CfgBuildingVisitor3 : CSharpSyntaxVisitor
{
    public CfgBuildingVisitor3(MethodDeclarationSyntax method)
    {
        _graphBuilders = new Stack<CfgGraphBuilder>();
        _subgraphs = new();
        _continueLabels = new();
        _breakLabels = new();
    }
    readonly Stack<CfgGraphBuilder> _graphBuilders;
    readonly List<CfgGraphBuilder> _subgraphs;
    int _numLabels; // TODO: this must be per method, right?
    // TODO: this must be per method, right?
    readonly Stack<int> _continueLabels;
    readonly Stack<int> _breakLabels;

    public CfgGraphBuilder GraphBuilder => _subgraphs.Last();

    public override void VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        _graphBuilders.Peek().Instruction(node);
    }

    public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        _graphBuilders.Peek().Instruction(node);
    }

    public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        _graphBuilders.Peek().Instruction(node);
    }

    private void TranslateIfStatement(ExpressionSyntax condition, SyntaxList<StatementSyntax> statements, int entry, int ifso, int ifno, int done)
    {
        // finish the current node

        // translate [then]
        var so = new CfgGraphBuilder();
        _graphBuilders.Push(so);
        _graphBuilders.Peek().CondBranch(condition, ifso, ifno, ifso);
        {
            foreach (var statement in statements)
            {
                Visit(statement);
            }
        }
        _graphBuilders.Pop();
        so.Unfocus();
        _graphBuilders.Peek().Splice2(entry, ifno, so);

        // translate [else]
        /*
        var no = new CfgGraphBuilder();
        _graphBuilders.Push(no);
        Visit(node.Else.Statement);
        _graphBuilders.Pop();
        no.Unfocus();
        _graphBuilders.Peek().Splice(ifno, done, no);
        */

        // what about the focus? it's currently at the DONE label
        // we want to replace it somehow... just unfocus it.
        

        //_graphBuilders.Peek().Label(ifno, SyntaxFactory.Token(SyntaxKind.CaseKeyword));
    }

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        if (node.Else != null)
        {
            // allocate new labels
            var done = _numLabels++;
            var ifso = _numLabels++;
            var ifno = _numLabels++;

            // finish the current node
            _graphBuilders.Peek().CondBranch(node.Condition, ifso, ifno, done);

            // translate [then]
            var so = new CfgGraphBuilder();
            _graphBuilders.Push(so);
            Visit(node.Statement);
            _graphBuilders.Pop();
            so.Unfocus();
            _graphBuilders.Peek().Splice(ifso, done, so);

            // translate [else]
            var no = new CfgGraphBuilder();
            _graphBuilders.Push(no);
            Visit(node.Else.Statement);
            _graphBuilders.Pop();
            no.Unfocus();
            _graphBuilders.Peek().Splice(ifno, done, no);
        }
        else
        {
            // allocate new labels
            var done = _numLabels++;
            var ifso = _numLabels++;

            // finish the current node
            _graphBuilders.Peek().CondBranch(node.Condition, ifso, done, done);

            // translate [then]
            var so = new CfgGraphBuilder();
            _graphBuilders.Push(so);
            Visit(node.Statement);
            _graphBuilders.Pop();
            so.Unfocus();
            _graphBuilders.Peek().Splice(ifso, done, so);
        }
    }

    public override void VisitWhileStatement(WhileStatementSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        /*
         * Lstart:
         * if (P) goto Lbody;
         * else goto Lend;
         * Lbody:
         * ...
         * goto Lstart;
         * Lend:
         */

        // allocate new labels
        var loopStart = _numLabels++;
        var loopEnd = _numLabels++;
        var loopBody = _numLabels++;

        // finish the current node
        _graphBuilders.Peek().Label(loopStart, node.WhileKeyword);

        _graphBuilders.Peek().CondBranch(node.Condition, loopBody, loopEnd, loopEnd);

        // translate the loop body
        _continueLabels.Push(loopStart);
        _breakLabels.Push(loopEnd);
        var body = new CfgGraphBuilder();
        _graphBuilders.Push(body);
        Visit(node.Statement);
        _graphBuilders.Pop();
        _continueLabels.Pop();
        _breakLabels.Pop();
        body.Unfocus();
        _graphBuilders.Peek().Splice2(loopBody, loopStart, body);
    }

    public override void VisitDoStatement(DoStatementSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        /*
         * Lbody:
         * ...
         * Lcond:
         * if (P) goto Lbody;
         * else goto Lend;
         * Lend:
         */
        
        // allocate new labels
        var loopBody = _numLabels++;
        var loopCond = _numLabels++;
        var loopEnd = _numLabels++;

        // finish the current node
        _graphBuilders.Peek().Label(loopBody, node.DoKeyword);

        // translate the loop body
        _continueLabels.Push(loopBody);
        _breakLabels.Push(loopEnd);
        var body = new CfgGraphBuilder();
        _graphBuilders.Push(body);
        Visit(node.Statement);
        _graphBuilders.Pop();
        _continueLabels.Pop();
        _breakLabels.Pop();
        body.Unfocus();
        _graphBuilders.Peek().Splice2(loopBody, loopCond, body);
        _graphBuilders.Peek().CondBranch(node.Condition, loopBody, loopEnd, loopEnd);
    }

    public override void VisitForStatement(ForStatementSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        /*
         * Declaration;
         * for (Initializers; Condition; Incrementors) { S }
         * 
         * ==>
         * 
         * Declaration;
         * Initializers;
         * Lstart:
         * if Condition then Lbody else Lend;
         * Lbody:
         *  S; goto Lincr;
         * Lincr:
         *  Incrementors;
         *  goto Lstart;
         * Lend:
         */

        var Lstart = _numLabels++;
        var Lbody = _numLabels++;
        var Lincr = _numLabels++;
        var Lend = _numLabels++;

        Visit(node.Declaration);
        foreach (var n in node.Initializers)
        {
            Visit(n);
        }

        // finish the current node
        _graphBuilders.Peek().Label(Lstart, node.ForKeyword);

        _graphBuilders.Peek().CondBranch(node.Condition, Lbody, Lend, Lend);

        // translate the loop body
        _continueLabels.Push(Lbody);
        _breakLabels.Push(Lend);
        var body = new CfgGraphBuilder();
        _graphBuilders.Push(body);
        Visit(node.Statement);
        _graphBuilders.Pop();
        _continueLabels.Pop();
        _breakLabels.Pop();
        body.Unfocus();
        _graphBuilders.Peek().Splice2(Lbody, Lincr, body);

        // translate the incrementors block
        var incr = new CfgGraphBuilder();
        _graphBuilders.Push(incr);
        foreach (var n in node.Incrementors)
        {
            Visit(n);
        }
        _graphBuilders.Pop();
        incr.Unfocus();
        _graphBuilders.Peek().Splice2(Lincr, Lstart, incr);
    }

    /*
    public override void VisitForEachStatement(ForEachStatementSyntax node)
    {
        // TODO: how to handle this?
        //   foreach (var x in E) S
        // we are only interested in S. has it been entered and exited at least once?
        base.VisitForEachStatement(node);
    }*/

    public override void VisitBlock(BlockSyntax node)
    {
        var i = 0;
        while (i < node.Statements.Count)
        {
            Visit(node.Statements[i]);
            i++;
        }
    }

    public override void VisitBreakStatement(BreakStatementSyntax node)
    {
        Debug.Assert(_breakLabels.Count > 0);
        Debug.Assert(_graphBuilders.Count > 0);

        var breakLabel = _breakLabels.Peek();

        _graphBuilders.Peek().Branch(node, breakLabel);
    }

    public override void VisitContinueStatement(ContinueStatementSyntax node)
    {
        Debug.Assert(_continueLabels.Count > 0);
        Debug.Assert(_graphBuilders.Count > 0);

        var continueLabel = _continueLabels.Peek();

        _graphBuilders.Peek().Branch(node, continueLabel);
    }

    public override void VisitSwitchStatement(SwitchStatementSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        /*
         * switch (E) {
         * case C1:
         * case C2:
         * ...
         * case Cn:
         *  // E == C1 || E == C2 || ... || E == Cn
         *  // entry into this block
         *  // NOTE: but also! E is only evaluated ONCE!
         *  // e.g. a temporary needs to be created. tmp = E; if (tmp == C1 || tmp == C2 || ... || tmp == Cn) S1 else if (tmp == Cm) ...
         *  S1;
         *  break;
         * case Cm:
         *  // E == Cm
         *  // entry into this block
         *  S2;
         *  break;
         * default:
         *  // negation of all of the above predicates...
         *  // entry into this block
         *  break;
         * }
         *
         * see also: http://www.ada-europe.org/conference2014/presentations/MCDC%20Case%20Statements.pdf
         * they are handling it EXACTLY as expected. so, rewrite to consecutive if-then-else:
         * 
         * if (E == C1 || E == C2 || ... || E == Cn) {S1;}
         * else if (E == Cm) {S2;}
         * else {}
         * 
         * also, don't care about multiple evaluations of E. the executable code will still be the same,
         * we just want to mark it for ourselves when a specific condition has been evaluated.
         * e.g. if we get into S1, then we know that tmp=E;tmp==C1||tmp==C2||tmp==C3 must have been evaluated.
         * 
         * OR, do care about multiple evaluations. then simply introduce fresh temporaries.
         * OR, just disallow for E to be anything but a variable in the source program. make programmers rewrite it. probably the best solution.
         * OR, let programmers know that if E contains side-effects, instrumentation WILL change the behavior of their programs. woops.
         * 
         * TODO: support default-less switch (in this case, instead of a default, you just go directly to [done]).
         */

        DefaultSwitchLabelSyntax? defaultLabel = null;
        IEnumerable<StatementSyntax>? defaultStatements = null;

        LinkedListNode<(ExpressionSyntax Cond, IEnumerable<StatementSyntax> Statements)>? clauses = null;

        foreach (var section in node.Sections)
        {
            var operands = new List<ExpressionSyntax>();

            foreach (var label in section.Labels)
            {
                if (label is DefaultSwitchLabelSyntax d)
                {
                    if (defaultLabel != null)
                        throw new Exception("Multiple default labels in a switch");
                    defaultLabel = d;
                    defaultStatements = section.Statements.Where(s => !(s is BreakStatementSyntax));
                    continue;
                }

                var labelExpr = (CaseSwitchLabelSyntax)label;
                operands.Add(labelExpr.Value);
            }

            var expression = operands
                .Select(e =>
                    SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, node.Expression, e))
                .Aggregate((e1, e2) => 
                    SyntaxFactory.BinaryExpression(SyntaxKind.LogicalOrExpression, e1, e2));
            
            var statements = section.Statements.Where(s => !(s is BreakStatementSyntax));

            DoublyLinkedList.InsertAfter(ref clauses, (expression, statements));
        }

        // clauses -> branches
        // - default is the last one

        // rewind
        StatementSyntax defBlock = SyntaxFactory.Block(defaultStatements);

        while (clauses != null)
        {
            defBlock =
                SyntaxFactory.IfStatement(
                clauses.Value.Cond,
                SyntaxFactory.Block(clauses.Value.Statements),
                SyntaxFactory.ElseClause(defBlock))
                .WithLeadingTrivia(SyntaxFactory.Whitespace(" "));
            if (clauses.Prev == null)
            {
                break;
            }
            clauses = clauses.Prev;
        }

        // TODO: make a separate pass for [switch] rewrites?
        Visit(defBlock);
    }

    // TODO: more statement kinds?
    // TOOD: [goto case]/[goto default] support inside a switch (that's just goto though! very obscure feature)
    // TODO: goto
    // TODO: what about declarations inside a method? delegates, lambdas, inner functions

    public override void VisitGotoStatement(GotoStatementSyntax node)
    {
        throw new NotImplementedException();
    }

    public override void VisitTryStatement(TryStatementSyntax node)
    {
        /*
         * try { S }
         * catch (T1 e) { S1 }
         * ...
         * catch (Tn e) { Sn }
         * finally { Sf }
         * 
         * we probably need special edges for exceptions
         * basically:
         * execute the [try] as a new block. if everything is OK, goto [finally] or to the next statement after the [try-catch-finally].
         * if exception is thrown, then [catches] are tried in the order given, one by one. if you get inside the catch, you will then go to the [finally] or the next statement.
         * [finally] block is always the block after all the catches.
         * 
         * [throw] also needs to be taken into account.
         * 
         * ----
         * a special kind of branch at the end of a basic block?
         * a multiway kind of 'catch'
         * ----
         * we can just represent all 'catch'es as typical branching.
         * 'throw' inside a 'try' does a 'goto' into the 'catch' block. the 'catch' block contains multiple sub-blocks with implicit branching.
         * all of the 'catch' blocks will 'goto' the 'finally' block (if present), or to AFTER the whole statement.
         * the 'finally' block performs some translation and control goes to wherever it went.
         * ----
         * UPDATE 2024-01-21:
         * file:///C:/Users/User/Downloads/icsea_2013_2_20_10241.pdf
         * How Exception Handling Constructions are Tested: An Initial Investigation with Open Source Software
         * Auri Marcelo Rizzo Vincenzi et al
         *
         * no, simple branching will not suffice. a 'try' block may have additional 'exceptional exits'.
         * - basically, any basic block may have additional 'exceptional edges' which represent control flow when an exception is raised!
         *   - this is a wholly new way to branch! it's ORTHOGONAL to regular exits
         * - some basic blocks can only be reached through the 'exceptional edges', which makes them 'exception handlers'
         * - and some basic blocks can be reached through the 'finally edges', which are exits from exception handlers OR from the 'try' blocks
         * 
         * also regarding [finally]. there's some syntax sugar involved.
         * - in CLR there is only 'try-catch' and 'try-finally'.
         * - 'try-catch-finally' is 'try-catch' nested in 'try-finally'
         *   try {
         *      try { S1; } // the exceptional control flow of S1 MUST go through the [catch] statements below
         *      catch (E1) { S2; leave L0; } // this use of "leave" means: just goto L0 ^_^
         *      catch (E2) { S3; leave L0; }
         *      L0: leave L1; // this use of "leave" means: go to the finally block, execute Sf; then goto L1
         *   }
         *   finally { Sf; }
         *   L1: ...
         *   
         * - the 'catch' blocks end with a 'leave' to 
         * 
         * So, Roslyn does it this way. there are 'regions' on top of blocks. a method is a Try region.
         * there are also TryAndCatch and TryAndFinally regions (and a try-catch-finally is rewritten as above). regions may nest.
         * a TryAndCatch region has the following subregions:
         * - a Try region
         * - multiple Catch regions
         * a TryAndFinally region has the following subregions:
         * - a Try region
         * - a Finally region
         * I guess that regular control flow goes as follows between regions:
         * - Try is executed, then Finally may be executed, but then we go to the statement after the TryFinally
         * - Catches are never executed
         * but the exceptional control flow is meant to be quite convoluted...
         */
        throw new NotImplementedException();
    }

    public override void VisitLabeledStatement(LabeledStatementSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        var l = _numLabels++; // TODO: save the exact labels too?
        _graphBuilders.Peek().Label(l, node.Identifier);
        Visit(node.Statement);
    }

    public override void VisitReturnStatement(ReturnStatementSyntax node)
    {
        Debug.Assert(_graphBuilders.Count > 0);

        _graphBuilders.Peek().Return(node.Expression);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var builder = new CfgGraphBuilder();

        _graphBuilders.Push(builder);

        if (node.Body == null || node.Body.Statements.Count == 0)
        {
            builder.Return(null);
        }
        else
        {
            Visit(node.Body);
        }
        builder.Unfocus();

        _graphBuilders.Pop();

        _subgraphs.Add(builder);
    }
}
