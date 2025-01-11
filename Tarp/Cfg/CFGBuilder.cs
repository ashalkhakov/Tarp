using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace Tarp.Cfg;

public class CFGBuilder : CSharpSyntaxVisitor<CFGNode>
{
    private readonly ControlFlowGraph cfg = new ControlFlowGraph();
    private CFGNode currentNode;
    private Stack<(CFGNode BreakNode, CFGNode ContinueNode)> loopContinuations = new();
    private Dictionary<string, CFGNode> labelledBlocks = new();
    private CFGRegion region;
    private List<CFGNode> exceptionHandlers = new();

    public ControlFlowGraph Build(SyntaxNode root)
    {
        currentNode = cfg.EntryNode;
        region = cfg.Region;

        Visit(root);

        return cfg;
    }

    public override CFGNode VisitMethodDeclaration(MethodDeclarationSyntax node)
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
        Visit(node.Body);

        exceptionHandlers.RemoveAt(exceptionHandlers.Count - 1);

        if (currentNode.Kind != CFGNodeKind.Exit && currentNode.Successors.Count == 0)
        {
            currentNode.AddSuccessor(cfg.ExitNode);
        }

        cfg.DeleteUnreferencedNodes();

        return currentNode;
    }

    public override CFGNode VisitBlock(BlockSyntax node)
    {
        foreach (var st in node.Statements)
        {
            Visit(st);
        }

        return currentNode;
    }

    public override CFGNode VisitIfStatement(IfStatementSyntax node)
    {
        if (node.Else == null)
        {
            var kont = currentNode.Successors.FirstOrDefault();
            var ifExit = cfg.AddNewNode(CFGNodeKind.Block);
            var ifBody = cfg.AddNewNode(CFGNodeKind.Block);

            if (!currentNode.ToBranch(node.Condition, ifBody, ifExit))
            {
                var newNode = cfg.AddNewNode(CFGNodeKind.Branch);
                currentNode.AddSuccessor(newNode);
                newNode.ToBranch(node.Condition, ifBody, ifExit);
            }

            ifBody.AddSuccessor(ifExit);

            currentNode = ifBody;

            Visit(node.Statement);

            currentNode = ifExit;
            if (currentNode.Kind == CFGNodeKind.Block && currentNode.Successors.Count == 0 && kont != null)
            {
                currentNode.AddSuccessor(kont);
            }

            return currentNode;
        }
        else
        {
            var kont = currentNode.Successors.FirstOrDefault();
            var ifExit = cfg.AddNewNode(CFGNodeKind.Block);
            var ifBody = cfg.AddNewNode(CFGNodeKind.Block);
            var elseBody = cfg.AddNewNode(CFGNodeKind.Block);

            if (!currentNode.ToBranch(node.Condition, ifBody, elseBody))
            {
                var ifBranch = cfg.AddNewNode(CFGNodeKind.Branch);
                ifBranch.ToBranch(node.Condition, ifBody, elseBody);
                currentNode.AddSuccessor(ifBranch);
            }

            ifBody.AddSuccessor(ifExit);
            elseBody.AddSuccessor(ifExit);

            currentNode = ifBody;

            Visit(node.Statement);

            currentNode = elseBody;

            Visit(node.Else);

            currentNode = ifExit;
            if (currentNode.Kind == CFGNodeKind.Block && currentNode.Successors.Count == 0 && kont != null)
            {
                currentNode.AddSuccessor(kont);
            }

            return currentNode;
        }
    }

    public override CFGNode? VisitElseClause(ElseClauseSyntax node)
    {
        Visit(node.Statement);

        return currentNode;
    }

    public override CFGNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        throw new System.NotImplementedException();
    }

    public override CFGNode VisitWhileStatement(WhileStatementSyntax node)
    {
        var loopEntry = cfg.AddNewNode(CFGNodeKind.Block);
        var loopExit = currentNode.Successors.FirstOrDefault() ?? cfg.AddNewNode(CFGNodeKind.Block);
        var loopBody = cfg.AddNewNode(CFGNodeKind.Block);

        currentNode.AddSuccessor(loopEntry);

        loopEntry.ToBranch(node.Condition, loopBody, loopExit);

        currentNode = loopBody;
        loopContinuations.Push((loopExit, loopEntry));

        Visit(node.Statement);

        loopContinuations.Pop();
        if (currentNode.Kind == CFGNodeKind.Block && currentNode.Successors.Count == 0)
        {
            currentNode.AddSuccessor(loopEntry);
        }

        currentNode = loopExit;

        return currentNode;
    }

    public override CFGNode? VisitDoStatement(DoStatementSyntax node)
    {
        var loopEntry = currentNode;
        var loopExit = currentNode.Successors.FirstOrDefault() ?? cfg.AddNewNode(CFGNodeKind.Block);
        var loopBody = cfg.AddNewNode(CFGNodeKind.Block);
        var loopCheck = cfg.AddNewNode(CFGNodeKind.Block);

        loopEntry.OverrideSuccessors(loopBody);
        loopCheck.ToBranch(node.Condition, loopBody, loopExit);

        currentNode = loopBody;
        loopContinuations.Push((loopExit, loopCheck));

        Visit(node.Statement);

        if (currentNode.Kind == CFGNodeKind.Block && currentNode.Successors.Count == 0)
        {
            currentNode.AddSuccessor(loopCheck);
        }

        loopContinuations.Pop();
        currentNode = loopExit;

        return currentNode;
    }

    public override CFGNode? VisitForStatement(ForStatementSyntax node)
    {
        var loopInit = currentNode;
        var loopEntry = cfg.AddNewNode(CFGNodeKind.Block);
        var loopExit = currentNode.Successors.FirstOrDefault() ?? cfg.AddNewNode(CFGNodeKind.Block);
        var loopBody = cfg.AddNewNode(CFGNodeKind.Block);
        var loopIncrement = node.Incrementors.Count == 0 ? null : cfg.AddNewNode(CFGNodeKind.Block);

        if (node.Declaration != null)
        {
            loopInit.AddStatement(node.Declaration);
        }

        foreach (var init in node.Initializers)
        {
            loopInit.AddStatement(init);
        }

        loopInit.OverrideSuccessors(loopEntry);

        if (node.Condition != null)
        {
            loopEntry.ToBranch(node.Condition, loopBody, loopExit);
        }
        else
        {
            loopEntry.ToJump(loopBody);
        }

        if (loopIncrement != null)
        {
            loopBody.AddSuccessor(loopIncrement);
            loopIncrement.AddSuccessor(loopEntry);
        }
        else
        {
            loopBody.AddSuccessor(loopEntry);
        }

        currentNode = loopBody;

        loopContinuations.Push((loopExit, loopIncrement));

        Visit(node.Statement);

        loopContinuations.Pop();

        if (loopIncrement != null)
        {
            foreach (var incrementor in node.Incrementors)
            {
                loopIncrement.AddStatement(incrementor);
            }
            if (currentNode.Kind == CFGNodeKind.Block && currentNode.Successors.Count == 0)
            {
                currentNode.AddSuccessor(loopIncrement);
            }
        }
        else
        {
            if (currentNode.Kind == CFGNodeKind.Block && currentNode.Successors.Count == 0)
            {
                currentNode.AddSuccessor(loopEntry);
            }
        }

        currentNode = loopExit;

        return currentNode;
    }

    public override CFGNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        // foreach (var r in E) { ... }
        // - kinda like a while... but it tests E's condition every time

        return base.VisitForEachStatement(node);
    }

    public override CFGNode? VisitLabeledStatement(LabeledStatementSyntax node)
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

        // don't override the successors (it could be a e.g. return statement)
        if (currentNode.Successors.Count == 0)
        {
            currentNode.AddSuccessor(labelEntry);
        }

        labelEntry.ToLabel(identifier);

        currentNode = labelEntry;
        Visit(statement);

        return currentNode;
    }

    public override CFGNode? VisitGotoStatement(GotoStatementSyntax node)
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

        currentNode.OverrideSuccessors(jump);

        jump.ToJump(dest);
        currentNode = jump;

        return jump;
    }

    public override CFGNode? VisitBreakStatement(BreakStatementSyntax node)
    {
        var e = loopContinuations.Peek();
        var jump = cfg.AddNewNode(CFGNodeKind.Jump);

        currentNode.OverrideSuccessors(jump);

        jump.ToJump(e.BreakNode);
        currentNode = jump;

        return jump;
    }

    public override CFGNode? VisitContinueStatement(ContinueStatementSyntax node)
    {
        var e = loopContinuations.Peek();
        var jump = cfg.AddNewNode(CFGNodeKind.Jump);

        currentNode.OverrideSuccessors(jump);

        jump.ToJump(e.ContinueNode);
        currentNode = jump;

        return jump;
    }

    public override CFGNode VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        AddSyntaxNode(node);
        return currentNode;
    }

    public override CFGNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        AddSyntaxNode(node);
        return currentNode;
    }

    public override CFGNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        currentNode.AddStatement(node);
        currentNode.OverrideSuccessors(cfg.ExitNode);

        return currentNode;
    }

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
}
