using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Tarp.Types;

namespace Tarp;

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

    public IReadOnlyCollection<CfgBasicBlock> Blocks => _blocks;

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
