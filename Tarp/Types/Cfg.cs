using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace Tarp.Types
{
    /*
     * some references
     * https://github.com/ashalkhakov/qvm2wasm/blob/master/ir.c
     * 
     * https://www.cs.auckland.ac.nz/~ewan/essays/nlip.html
     * https://www.eecs.yorku.ca/course_archive/2011-12/W/4313/slides/11-Paths.pdf
     * https://ethz.ch/content/dam/ethz/special-interest/infk/inst-cs/lst-dam/documents/Education/Classes/Spring2018/210_Compiler_Design/Slides/w10_02_dataflow-2_18.pdf
     * https://en.wikipedia.org/wiki/Decision-to-decision_path
     * https://en.wikipedia.org/wiki/Elementary_comparison_testing
     * http://www.semdesigns.com/Company/Publications/TestCoverage.pdf (follows a syntax-directed approach, we will be making a CFG first)
     * 
     * https://www.stridewiki.com/index.php/Source_Instrumentation_Overview
     * 
     * for doing edits to the input AST, use this:
     * https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.editing.syntaxeditor?view=roslyn-dotnet-4.7.0
     * - this is probably the easiest way to have handles into the tree that are preserved between edits
     */
    public enum CfgBranchKind { TrueBranch = 0, FalseBranch = 1, Default = 2 }

    public class CfgBranch
    {
        public SyntaxNode? OpCode { get; set; }
        public CfgNode Parent { get; set; }
        public CfgNode Destination { get; set; }
        public CfgBranchKind Kind { get; set; }
    }
    public class CfgNode
    {
        // id of the node (always increasing)
        public int Id { get; set; }

        // source code statements
        public int Start { get; set; }
        public int Count { get; set; }
        public List<SyntaxNode> Nodes { get; set; }

        public List<CfgNode> Incoming { get; set; }
        public List<CfgBranch> Outgoing { get; set; }
    }
    public class Cfg
    {
        public SyntaxNode SourceNode { get; set; }
        public CfgNode StartNode { get; set; } // NOTE: same as [nodes]!
        public CfgNode EndNode { get; set; } // NOTE: this is the last node in [nodes]!
        public LinkedListNode<CfgNode> Nodes { get; set; }

        public static void Print(Cfg cfg)
        {

        }

        public static Cfg Build(MethodDeclarationSyntax method)
        {
            var visitor = new CfgBuildingVisitor(method);

            visitor.Visit(method);

            visitor.GetBlocks();

            return null;
        }
    }

    public sealed class LinkedListNode<T>
    {
        public LinkedListNode(T value)
        {
            _value = value;
        }
        private T _value;
        public LinkedListNode<T>? Next { get; set; }
        public LinkedListNode<T>? Prev { get; set; }
        public T Value { get => _value; set => _value = value; }
    }

    public static class DoublyLinkedList
    {
        public static int Length<T>(LinkedListNode<T>? list)
        {
            var head = list;
            int len = 0;

            while (head != null)
            {
                len++;
                head = head.Next;
            }

            return len;
        }

        public static void Split<T>(ref LinkedListNode<T>? list, out LinkedListNode<T?> tail)
        {
            if (list == null || list.Next == null)
            {
                tail = null;
                return;
            }
            tail = list.Next;

            tail.Prev = null;
            list.Next = null;
        }

        public static void InsertBefore<T>(ref LinkedListNode<T>? list, T item)
        {
            var newNode = new LinkedListNode<T>(item);

            if (list == null)
            {
                list = newNode;
                return;
            }

            newNode.Prev = list.Prev;
            list.Prev = newNode;
            newNode.Next = list;

            if (newNode.Prev != null)
            {
                newNode.Prev.Next = newNode;
            }
            list = newNode;
        }

        public static void InsertAfter<T>(ref LinkedListNode<T>? list, T item)
        {
            var newNode = new LinkedListNode<T>(item);

            if (list == null)
            {
                list = newNode;
                return;
            }

            newNode.Next = list.Next;
            list.Next = newNode;
            newNode.Prev = list;
            if (newNode.Next != null)
            {
                newNode.Next.Prev = newNode;
            }
            list = newNode;
        }

        public static void AppendToEnd<T>(ref LinkedListNode<T>? list, T item)
        {
            if (list == null)
            {
                var tmp = new LinkedListNode<T>(item);
                tmp.Next = null;
                list = tmp;
            }
            else
            {
                var head = list;
                while (head.Next != null)
                    head = head.Next;
                var tmp = new LinkedListNode<T>(item);
                tmp.Next = null;
                head.Next = tmp;
            }
        }

        public static LinkedListNode<T>? Reverse<T>(LinkedListNode<T>? list)
        {
            if (list == null)
            {
                return null;
            }

            LinkedListNode<T>? prev = null;
            var current = list;
            LinkedListNode<T>? next;
            while (current != null)
            {
                next = current.Next;
                current.Next = prev;
                prev = current;
                current = next;
            }
            return prev;
        }
    }

    public abstract record CfgBlockFirst
    {
    }
    public record CfgBlockEntry(): CfgBlockFirst;
    public record CfgBlockLabel(int Label): CfgBlockFirst;
    public abstract record CfgBlockLast
    {
    }
    public record CfgBlockExit() : CfgBlockLast;
    public record CfgBlockBranch(SyntaxNode? Instruction, int Label) : CfgBlockLast;
    public record CfgBlockConditionalBranch(SyntaxNode Cond, int LabelIfSo, int LabelIfNot) : CfgBlockLast;
    public record CfgBlockReturn(SyntaxNode? Expression) : CfgBlockLast;

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
     * 
     * subgraph:
     *     entry
     *     ...
     *     exit
     * 
     */
    public class CfgFragment
    {
        public CfgBranch Entry { get; set; }
        public CfgBranch Exit { get; set; }
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
            SyntaxList<StatementSyntax>? defaultStatements = null;

            LinkedListNode<(ExpressionSyntax Cond, SyntaxList<StatementSyntax> Statements)>? clauses = null;

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
                        defaultStatements = section.Statements;
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
                
                var statements = section.Statements;

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
        // TODO: what about declarations inside a method? delegates, lambdas, inner functions

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
    
    public class CfgBuildingVisitor : CSharpSyntaxVisitor
    {
        public CfgBuildingVisitor(MethodDeclarationSyntax method)
        {
            // Identify leaders: first statement of a basic block
            // In program order, construct a block by appending subsequent statements up to, but not including, the next leader

            // Identifying leaders
            // - first statement
            // - explicit target of any conditional or unconditional branch
            // - implicit target of any branch
            _leaders = null;

            var body = method.Body;
            var firstChild = method.Body;

            _leaders = intlist_insert_sorted(_leaders, method.Body.ChildNodes().First());
            _leaders = intlist_insert_sorted(_leaders, method.Body); // TODO: this is the wrong location!

            // TODO: this is a behavior in a statement block. descend into subtrees also.
        }



        public void GetBlocks()
        {
            Debug.Assert(_leaders != null);
            var numBlocks = DoublyLinkedList.Length(_leaders);
            Debug.Assert(numBlocks > 1); // enter and exit, at the very least

            var blocks = new CfgNode[numBlocks];

            // create all basic blocks
            var leader = _leaders;
            for (int i = 0; i < numBlocks; i++)
            {
                var next = leader.Next;
                /*
                var beg = leader.Value;
                var end = ((next != null) ? next.Value : offset + length);
                int len = end - beg;
                node = (cfgnode_t*)malloc(sizeof(cfgnode_t));
                node->num = i;
                node->start = beg;
                node->length = len;
                node->outgoing = NULL;
                node->incoming = NULL;
                node->opStackOutputDepth = -1; // unset

                blocks[i] = node;*/
                leader = next;

            }
        }

        private LinkedListNode<SyntaxNode>? _leaders;

        private static LinkedListNode<SyntaxNode> intlist_insert_sorted(LinkedListNode<SyntaxNode>? head, SyntaxNode num)
        {
            LinkedListNode<SyntaxNode> current;

            // end of list?
            if (head == null || head.Value.SpanStart >= num.SpanStart)
            {
                var node = new LinkedListNode<SyntaxNode>(num);
                node.Next = head;
                head = node;
            }
            else
            {
                // locate the node before the point of insertion
                current = head;
                while (current.Next != null && current.Next.Value.SpanStart < num.SpanStart)
                {
                    current = current.Next;
                }
                if (current.Value.SpanStart != num.SpanStart)
                {
                    var node = new LinkedListNode<SyntaxNode>(num);
                    node.Next = current.Next;
                    current.Next = node;
                }
            }
            return head;
        }

        public override void VisitBlock(BlockSyntax node)
        {
            var n = node.Statements.Count;
            for (var i = 0; i < n; i++)
            {
                var stmt = node.Statements[i];
                if (stmt is ReturnStatementSyntax && i + 1 < n)
                {
                    // next statement is a leader
                    _leaders = intlist_insert_sorted(_leaders, node.Statements[i + 1]);
                }
                else if (stmt is IfStatementSyntax cond)
                {
                    _leaders = intlist_insert_sorted(_leaders, cond.Statement);

                    base.Visit(cond.Statement);

                    if (cond.Else != null)
                    {
                        _leaders = intlist_insert_sorted(_leaders, cond.Else);
                        base.Visit(cond.Else);
                    }
                }
                else if (stmt is SwitchStatementSyntax switchStatement)
                {
                    foreach (var section in switchStatement.Sections)
                    {
                        if (section.Statements.Count == 0)
                        {
                            continue;
                        }
                        _leaders = intlist_insert_sorted(_leaders, section.Statements[0]);
                        base.Visit(section);
                    }

                    if (i + 1 < n)
                    {
                        _leaders = intlist_insert_sorted(_leaders, node.Statements[i + 1]);
                    }
                }
                else if (stmt is ForStatementSyntax loop)
                {
                    _leaders = intlist_insert_sorted(_leaders, loop.Statement);

                    base.Visit(loop.Statement);

                    if (i + 1 < n)
                    {
                        _leaders = intlist_insert_sorted(_leaders, node.Statements[i + 1]);
                    }
                }
                else if (stmt is WhileStatementSyntax whileLoop)
                {
                    _leaders = intlist_insert_sorted(_leaders, whileLoop.Statement);

                    base.Visit(whileLoop.Statement);

                    if (i + 1 < n)
                    {
                        _leaders = intlist_insert_sorted(_leaders, node.Statements[i + 1]);
                    }
                }
                else if (stmt is DoStatementSyntax doLoop)
                {
                    _leaders = intlist_insert_sorted(_leaders, doLoop.Statement);

                    base.Visit(doLoop.Statement);

                    if (i + 1 < n)
                    {
                        _leaders = intlist_insert_sorted(_leaders, node.Statements[i + 1]);
                    }
                }
                else if (stmt is TryStatementSyntax tryStatement)
                {
                    _leaders = intlist_insert_sorted(_leaders, tryStatement.Block);
                    base.Visit(tryStatement.Block);

                    foreach (var c in tryStatement.Catches)
                    {
                        _leaders = intlist_insert_sorted(_leaders, c.Block);
                        base.Visit(c.Block);
                    }

                    if (i + 1 < n)
                    {
                        _leaders = intlist_insert_sorted(_leaders, node.Statements[i + 1]);
                    }
                }
            }
        }
    }
}
