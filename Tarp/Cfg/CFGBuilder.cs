using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace Tarp.Cfg;

/// <summary>
/// A CFG fragment is two nodes:
/// - entry node (without predecessors)
/// - exit node (without successors)
/// 
/// (It may be the same node.)
///
/// In the case of jumps (return, throw, continue, break, goto),
/// the exit node may not be reachable from the entry node.
/// </summary>
public class CFGFragment
{
    public CFGNode Start { get; set; }
    public CFGNode End { get; set; }
}

public class CFGBuilder : CSharpSyntaxVisitor<CFGFragment>
{
    private readonly ControlFlowGraph cfg = new ControlFlowGraph();
    private Stack<(CFGNode BreakNode, CFGNode ContinueNode)> loopContinuations = new();
    private Dictionary<string, CFGNode> labelledBlocks = new();
    private CFGRegion region;
    private List<CFGNode> exceptionHandlers = new();

    public ControlFlowGraph Build(SyntaxNode root)
    {
        region = cfg.Region;

        var fragment = Visit(root);

        cfg.EntryNode.AddSuccessor(fragment.Start);
        fragment.End.AddSuccessor(cfg.ExitNode);

        return cfg;
    }

    public override CFGFragment VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        // prepare a block for each labelled statement
        foreach (var statement in node.Body.DescendantNodes().OfType<LabeledStatementSyntax>())
        {
            var identifier = statement.Identifier;
            var block = cfg.AddNewNode(CFGNodeKind.Label);

            labelledBlocks.Add((string)identifier.Value, block);
        }

        // all exceptions will by default go to the exit node
        // TODO: ensure that edges are marked as exceptional or some such?
        exceptionHandlers.Add(cfg.ExitNode);

        // Visit method body
        var frag = Visit(node.Body);

        exceptionHandlers.RemoveAt(exceptionHandlers.Count - 1);

        return frag;
    }

    public override CFGFragment VisitBlock(BlockSyntax node)
    {
        if (node.Statements.Count == 0)
        {
            var n = cfg.AddNewNode(CFGNodeKind.Block);

            return new CFGFragment
            {
                Start = n,
                End = n
            };
        }

        var frag = Visit(node.Statements[0]);
        for (var i = 1; i < node.Statements.Count; i++)
        {
            var next = Visit(node.Statements[i]);

            frag.End.AddSuccessor(next.Start);
            frag.End = next.End;
        }

        return frag;
    }

    public override CFGFragment VisitIfStatement(IfStatementSyntax node)
    {
        var cond = cfg.AddNewNode(CFGNodeKind.Branch);
        var skip = cfg.AddNewNode(CFGNodeKind.Block);

        if (node.Else != null)
        {
            var c1 = Visit(node.Statement);
            var c2 = Visit(node.Else);

            cond.Condition = node.Condition;
            cond.AddSuccessor(c1.Start);
            cond.AddSuccessor(c2.Start);

            c1.End.AddSuccessor(skip);
            c2.End.AddSuccessor(skip);

            return new CFGFragment
            {
                Start = cond,
                End = skip
            };
        }
        else
        {
            var c1 = Visit(node.Statement);

            cond.Condition = node.Condition;
            cond.AddSuccessor(c1.Start);
            cond.AddSuccessor(skip);

            c1.End.AddSuccessor(skip);

            return new CFGFragment
            {
                Start = cond,
                End = skip
            };
        }
    }

    public override CFGFragment VisitElseClause(ElseClauseSyntax node)
    {
        return Visit(node.Statement);
    }

    public override CFGFragment VisitSwitchStatement(SwitchStatementSyntax node)
    {
        throw new System.NotImplementedException();
    }

    public override CFGFragment VisitWhileStatement(WhileStatementSyntax node)
    {
        var loopEntry = cfg.AddNewNode(CFGNodeKind.Branch);
        var skip = cfg.AddNewNode(CFGNodeKind.Block);

        loopContinuations.Push((skip, loopEntry));

        var loopBody = Visit(node.Statement);

        loopContinuations.Pop();

        loopEntry.Condition = node.Condition;
        loopEntry.AddSuccessor(loopBody.Start);
        loopEntry.AddSuccessor(skip);

        loopBody.End.AddSuccessor(loopEntry);

        return new CFGFragment
        {
            Start = loopEntry,
            End = skip
        };
    }

    public override CFGFragment VisitDoStatement(DoStatementSyntax node)
    {
        var loopExit = cfg.AddNewNode(CFGNodeKind.Block);
        var loopCheck = cfg.AddNewNode(CFGNodeKind.Block);

        loopContinuations.Push((loopExit, loopCheck));

        var loopBody = Visit(node.Statement);

        loopCheck.ToBranch(node.Condition, loopBody.Start, loopExit);

        loopContinuations.Pop();

        loopBody.End.AddSuccessor(loopCheck);

        return new CFGFragment
        {
            Start = loopBody.Start,
            End = loopExit
        };
    }

    public override CFGFragment VisitForStatement(ForStatementSyntax node)
    {
        var loopInit = node.Declaration != null || node.Initializers.Count > 0 ? cfg.AddNewNode(CFGNodeKind.Block) : null;
        var loopEntry = cfg.AddNewNode(CFGNodeKind.Block);
        var loopExit = cfg.AddNewNode(CFGNodeKind.Block);
        var loopIncrement = node.Incrementors.Count == 0 ? null : cfg.AddNewNode(CFGNodeKind.Block);

        loopContinuations.Push((loopExit, loopIncrement ?? loopEntry));

        var loopBody = Visit(node.Statement);

        loopContinuations.Pop();

        if (node.Declaration != null)
        {
            loopInit.AddStatement(node.Declaration);
        }

        if (node.Initializers.Count > 0)
        {
            foreach (var init in node.Initializers)
            {
                loopInit.AddStatement(init);
            }
        }

        if (loopInit != null)
        {
            loopInit.AddSuccessor(loopEntry);
        }

        if (node.Condition != null)
        {
            loopEntry.ToBranch(node.Condition, loopBody.Start, loopExit);
        }
        else
        {
            loopEntry.ToJump(loopBody.Start);
        }

        if (loopIncrement != null)
        {
            foreach (var incrementor in node.Incrementors)
            {
                loopIncrement.AddStatement(incrementor);
            }

            loopBody.End.AddSuccessor(loopIncrement);
            loopIncrement.AddSuccessor(loopEntry);
        }
        else
        {
            loopBody.End.AddSuccessor(loopEntry);
        }

        return new CFGFragment
        {
            Start = loopInit ?? loopEntry,
            End = loopExit
        };
    }

#if false
    public override CFGNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        // foreach (var r in E) { ... }
        // - kinda like a while... but it tests E's condition every time

        return base.VisitForEachStatement(node);
    }
#endif
    public override CFGFragment VisitLabeledStatement(LabeledStatementSyntax node)
    {
        var identifier = node.Identifier;
        var statement = node.Statement;

        if (!labelledBlocks.TryGetValue((string)identifier.Value, out var labelEntry))
        {
            throw new CFGBuilderException("Labeled statement has no block prepared for it", node.GetLocation());
        }

        if (labelEntry.Kind != CFGNodeKind.Label || labelEntry.Successors.Count > 0)
        {
            throw new CFGBuilderException("Block for labeled statement has already been processed", node.GetLocation());
        }

        labelEntry.ToLabel(identifier);

        var next = Visit(statement);

        labelEntry.AddSuccessor(next.Start);

        return new CFGFragment
        {
            Start = labelEntry,
            End = next.End
        };
    }

    public override CFGFragment VisitGotoStatement(GotoStatementSyntax node)
    {
        if (node.CaseOrDefaultKeyword.Value != null)
        {
            throw new CFGBuilderException("goto case / default not implemented yet", node.GetLocation());
        }

        var label = (node.Expression as IdentifierNameSyntax).Identifier;

        if (!labelledBlocks.TryGetValue((string)label.Value, out var dest))
        {
            throw new CFGBuilderException("Label is not declared", node.GetLocation());
        }

        var jump = cfg.AddNewNode(CFGNodeKind.Jump);

        jump.ToJump(dest);

        return new CFGFragment
        {
            Start = jump,
            End = jump
        };
    }

    public override CFGFragment VisitBreakStatement(BreakStatementSyntax node)
    {
        var e = loopContinuations.Peek();
        var jump = cfg.AddNewNode(CFGNodeKind.Jump);

        jump.ToJump(e.BreakNode);

        return new CFGFragment
        {
            Start = jump,
            End = jump
        };
    }

    public override CFGFragment VisitContinueStatement(ContinueStatementSyntax node)
    {
        var e = loopContinuations.Peek();
        var jump = cfg.AddNewNode(CFGNodeKind.Jump);

        jump.ToJump(e.ContinueNode);

        return new CFGFragment
        {
            Start = jump,
            End = jump
        };
    }

    public override CFGFragment VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var n = cfg.AddNewNode(CFGNodeKind.Block);
        n.AddStatement(node);
        return new CFGFragment
        {
            Start = n,
            End = n
        };
    }

    public override CFGFragment VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var n = cfg.AddNewNode(CFGNodeKind.Block);
        n.AddStatement(node);
        return new CFGFragment
        {
            Start = n,
            End = n
        };
    }

    public override CFGFragment VisitReturnStatement(ReturnStatementSyntax node)
    {
        var n1 = cfg.AddNewNode(CFGNodeKind.Block);
        n1.AddStatement(node);
        n1.AddSuccessor(cfg.ExitNode);

        return new CFGFragment
        {
            Start = n1,
            End = n1
        };
    }

#if false
    public void AddSyntaxNode(StatementSyntax node)
    {
        if (currentNode.Kind == CFGNodeKind.Entry)
        {
            var newNode = cfg.AddNewNode(CFGNodeKind.Block);
            currentNode.AddSuccessor(newNode);
            currentNode = newNode;
            newNode.AddStatement(node);
            return;
        }

        var (isExit, cond) = IsBlockExitPoint(node);
        if (isExit)
        {
            if (cond != null)
            {
                var newNode = cfg.AddNewNode(CFGNodeKind.Block);
                currentNode.Kind = CFGNodeKind.Branch;
                currentNode.Condition = cond;
                currentNode.AddSuccessor(newNode);
                currentNode = newNode;
            }
            else
            {
                currentNode.AddSuccessor(cfg.ExitNode);
                var newNode = cfg.AddNewNode(CFGNodeKind.Block);
                currentNode = newNode;
            }
        }
        else
        {
            currentNode.AddStatement(node);
        }
    }

    private (bool, ExpressionSyntax?) IsBlockExitPoint(StatementSyntax statement)
    {
        // Determine if a statement is a block exit point
        if (statement is IfStatementSyntax i)
        {
            return (true, i.Condition);
        }
        if (statement is WhileStatementSyntax w)
        {
            return (true, w.Condition);
        }
        if (statement is ForStatementSyntax f)
        {
            return (true, f.Condition);
        }
        //if (statement is SwitchStatementSyntax s)
        //{
        //    return (true, null); // TODO: cook something up? probably need to rewrite switches to if/then/else's...
        //}
        if (statement is LabeledStatementSyntax)
        {
            return (true, null);
        }
        if (statement is ReturnStatementSyntax)
        {
            return (true, null);
        }
        if (statement is BreakStatementSyntax)
        {
            return (true, null);
        }
        if (statement is ContinueStatementSyntax)
        {
            return (true, null);
        }
        return (false, null);
    }

    public override CFGNode? VisitThrowExpression(ThrowExpressionSyntax node)
    {
        var jump = cfg.AddNewNode(CFGNodeKind.Jump);

        currentNode.OverrideSuccessors(jump);

        CFGNode finallyNode = null;

        for (var i = exceptionHandlers.Count - 1; i >= 0; i--)
        {
            finallyNode = exceptionHandlers[i];
            if (finallyNode != null)
            {
                break;
            }
        }

        if (finallyNode == null)
        {
            throw new CFGBuilderException("Unable to find exception handling node", node.GetLocation());
        }

        jump.ToJump(finallyNode);
        currentNode = jump;

        return jump;
    }

    public override CFGNode? VisitThrowStatement(ThrowStatementSyntax node)
    {
        var jump = cfg.AddNewNode(CFGNodeKind.Jump);

        currentNode.OverrideSuccessors(jump);

        CFGNode finallyNode = null;
        
        for (var i = exceptionHandlers.Count - 1; i >= 0; i--)
        {
            finallyNode = exceptionHandlers[i];
            if (finallyNode != null)
            {
                break;
            }
        }

        if (finallyNode == null)
        {
            throw new CFGBuilderException("Unable to find exception handling node", node.GetLocation());
        }

        jump.ToJump(finallyNode);
        currentNode = jump;

        return jump;
    }

    public override CFGNode? VisitTryStatement(TryStatementSyntax node)
    {
        var kont = currentNode.Successors.FirstOrDefault();
        var tryBody = cfg.AddNewNode(CFGNodeKind.Try);
        var tryExit = currentNode.Successors.FirstOrDefault() ?? cfg.AddNewNode(CFGNodeKind.Block);
        var finallyNodeKind = node.Finally != null ? CFGNodeKind.FinallyHandler : CFGNodeKind.Block;
        var finallyNode = cfg.AddNewNode(finallyNodeKind);

        if (!currentNode.ToJump(tryBody))
        {
            currentNode.OverrideSuccessors(tryBody);
        }
        finallyNode.OverrideSuccessors(tryExit);

        var catchNodes = node.Catches.Select(c => cfg.AddNewNode(CFGNodeKind.ExceptionHandler)).ToArray();
        for (var i = 0; i < node.Catches.Count; i++)
        {
            var catchBody = catchNodes[i];

            catchBody.OverrideSuccessors(finallyNode);
        }
        tryBody.ToTry(finallyNode, catchNodes);

        exceptionHandlers.Add(tryBody);

        currentNode = tryBody;
        Visit(node.Block);

        exceptionHandlers.RemoveAt(exceptionHandlers.Count - 1);
        
        exceptionHandlers.Add(finallyNode);

        for (var i = 0; i < node.Catches.Count; i++)
        {
            var catchBody = catchNodes[i];
            var catchClause = node.Catches[i];

            currentNode = catchBody;
            currentNode.Condition = catchClause.Declaration;
            catchBody.OverrideSuccessors(finallyNode);

            Visit(catchClause.Block);
        }

        exceptionHandlers.RemoveAt(exceptionHandlers.Count - 1);

        if (node.Finally != null)
        {
            currentNode = finallyNode;
            Visit(node.Finally.Block);
        }

        currentNode = tryExit;
        if (currentNode.Kind == CFGNodeKind.Block && currentNode.Successors.Count == 0 && kont != null)
        {
            currentNode.AddSuccessor(kont);
        }

        return currentNode;
    }
#endif
}
