using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        public static LinkedListNode<T>? Reverse<T>(LinkedListNode<T> list)
        {
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
    public record CfgBlockBranch(int Label) : CfgBlockLast;
    public record CfgBlockConditionalBranch(SyntaxNode Cond, int LabelIfSo, int LabelIfNot) : CfgBlockLast;
    public record CfgBlockReturn() : CfgBlockLast;

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
    /// - its sequence of instructions in the middle
    /// - its incoming arcs (either an Entry point or a Label)
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

            if (_blocks.Any(b => b.Last is CfgBlockExit))
            {
                EmitBaseNode(writer, "exit");
            }
            EmitBaseNode(writer, "return");

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
                else if (n.Last is CfgBlockReturn)
                {
                    writer.Write($"      {name} -> return;\n");
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

        public void Label(int label)
        {
            Debug.Assert(_focus != null);

            var last = _focus.Last;

            _focus.Last = new CfgBlockBranch(label);

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

        public void Branch()
        {
            // FIXME: is this like "brfalse" instruction???
        }
        
        public void CondBranch(SyntaxNode condition, int ifsoLabel, int ifNotLabel, int done)
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

            var nextBlock = new CfgBasicBlock(new CfgBlockLabel(done), last);
            nextBlock.Middle = tail;

            Unfocus();
            _focus = nextBlock;
        }

        public void Return(ExpressionSyntax? expression)
        {
            Debug.Assert(_focus != null);

            var last = _focus.Last; // TODO: what should we do with this one?

            _focus.Last = new CfgBlockReturn();
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
                    n.Last = new CfgBlockBranch(labelExit);
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

    public class CfgBuildingVisitor3 : CSharpSyntaxVisitor
    {
        public CfgBuildingVisitor3(MethodDeclarationSyntax method)
        {
            _graphBuilders = new Stack<CfgGraphBuilder>();
            _subgraphs = new();
        }
        readonly Stack<CfgGraphBuilder> _graphBuilders;
        readonly List<CfgGraphBuilder> _subgraphs;
        int _numLabels; // TODO: this must be per method, right?

        public CfgGraphBuilder GraphBuilder => _subgraphs.Last();

        public override void VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            Debug.Assert(_graphBuilders.Count > 0);

            _graphBuilders.Peek().Instruction(node);
        }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            Debug.Assert(_graphBuilders.Count > 0);

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
            _graphBuilders.Peek().Label(loopStart);

            _graphBuilders.Peek().CondBranch(node.Condition, loopBody, loopEnd, loopEnd);

            // translate the loop body
            var body = new CfgGraphBuilder();
            _graphBuilders.Push(body);
            Visit(node.Statement);
            _graphBuilders.Pop();
            body.Unfocus();
            //_graphBuilders.Peek().Label(loopEnd);
            _graphBuilders.Peek().Splice(loopBody, loopStart, body);
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

        public override void VisitLabeledStatement(LabeledStatementSyntax node)
        {
            Debug.Assert(_graphBuilders.Count > 0);

            var l = _numLabels++; // TODO: save the exact labels too?
            _graphBuilders.Peek().Label(l);
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
                builder.Return(null);
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
