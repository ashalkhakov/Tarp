using Microsoft.CodeAnalysis;

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
