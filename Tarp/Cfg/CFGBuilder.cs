using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using static System.Net.Mime.MediaTypeNames;


namespace Tarp.Cfg;

public class CFGBuilder : CSharpSyntaxVisitor<CFGNode>
{
    private readonly ControlFlowGraph _cfg = new ControlFlowGraph();
    private CFGNode _currentNode;
    private Stack<(CFGNode BreakNode, CFGNode ContinueNode)> _loopContinuations = new();
    private Dictionary<string, CFGNode> _labelledBlocks = new();

    public ControlFlowGraph Build(SyntaxNode root)
    {
        _currentNode = _cfg.EntryNode;

        Visit(root);

        return _cfg;
    }

    public override CFGNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        // prepare a block for each labelled statement
        foreach (var statement in node.Body.DescendantNodes().OfType<LabeledStatementSyntax>())
        {
            var identifier = statement.Identifier;
            var block = _cfg.AddNewNode(CFGNodeKind.Label);

            _labelledBlocks.Add((string)identifier.Value, block);
        }

        // Visit method body
        Visit(node.Body);

        if (_currentNode.Kind != CFGNodeKind.Exit && _currentNode.Successors.Count == 0)
        {
            _currentNode.AddSuccessor(_cfg.ExitNode);
        }

        _cfg.DeleteUnreferencedNodes();

        return _currentNode;
    }

    public override CFGNode VisitBlock(BlockSyntax node)
    {
        foreach (var st in node.Statements)
        {
            Visit(st);
        }

        return _currentNode;
    }

    public override CFGNode VisitIfStatement(IfStatementSyntax node)
    {
        if (node.Else == null)
        {
            var kont = _currentNode.Successors.FirstOrDefault();
            var ifExit = /*_currentNode.Successors.FirstOrDefault() ??*/ _cfg.AddNewNode(CFGNodeKind.Block);
            var ifBody = _cfg.AddNewNode(CFGNodeKind.Block);

            if (!_currentNode.ToBranch(node.Condition, ifBody, ifExit))
            {
                var newNode = _cfg.AddNewNode(CFGNodeKind.Branch);
                _currentNode.AddSuccessor(newNode);
                newNode.ToBranch(node.Condition, ifBody, ifExit);
            }

            ifBody.AddSuccessor(ifExit);

            _currentNode = ifBody;

            Visit(node.Statement);

            _currentNode = ifExit;
            if (_currentNode.Kind == CFGNodeKind.Block && _currentNode.Successors.Count == 0 && kont != null)
            {
                _currentNode.AddSuccessor(kont);
            }

            return _currentNode;
        }
        else
        {
            var kont = _currentNode.Successors.FirstOrDefault();
            var ifExit = /*_currentNode.Successors.FirstOrDefault() ??*/ _cfg.AddNewNode(CFGNodeKind.Block);
            var ifBody = _cfg.AddNewNode(CFGNodeKind.Block);
            var elseBody = _cfg.AddNewNode(CFGNodeKind.Block);

            if (!_currentNode.ToBranch(node.Condition, ifBody, elseBody))
            {
                var ifBranch = _cfg.AddNewNode(CFGNodeKind.Branch);
                ifBranch.ToBranch(node.Condition, ifBody, elseBody);
                _currentNode.AddSuccessor(ifBranch);
            }

            ifBody.AddSuccessor(ifExit);
            elseBody.AddSuccessor(ifExit);

            _currentNode = ifBody;

            Visit(node.Statement);

            _currentNode = elseBody;

            Visit(node.Else);

            _currentNode = ifExit;
            if (_currentNode.Kind == CFGNodeKind.Block && _currentNode.Successors.Count == 0 && kont != null)
            {
                _currentNode.AddSuccessor(kont);
            }

            return _currentNode;
        }
    }

    public override CFGNode? VisitElseClause(ElseClauseSyntax node)
    {
        Visit(node.Statement);

        return _currentNode;
    }

    public override CFGNode VisitWhileStatement(WhileStatementSyntax node)
    {
        var loopEntry = _cfg.AddNewNode(CFGNodeKind.Block);
        var loopExit = _currentNode.Successors.FirstOrDefault() ?? _cfg.AddNewNode(CFGNodeKind.Block);
        var loopBody = _cfg.AddNewNode(CFGNodeKind.Block);

        _currentNode.AddSuccessor(loopEntry);

        loopEntry.ToBranch(node.Condition, loopBody, loopExit);

        _currentNode = loopBody;
        _loopContinuations.Push((loopExit, loopEntry));

        Visit(node.Statement);

        _loopContinuations.Pop();
        if (_currentNode.Kind == CFGNodeKind.Block && _currentNode.Successors.Count == 0)
        {
            _currentNode.AddSuccessor(loopEntry);
        }

        _currentNode = loopExit;

        return _currentNode;
    }

    public override CFGNode? VisitDoStatement(DoStatementSyntax node)
    {
        var loopEntry = _currentNode;
        var loopExit = _currentNode.Successors.FirstOrDefault() ?? _cfg.AddNewNode(CFGNodeKind.Block);
        var loopBody = _cfg.AddNewNode(CFGNodeKind.Block);
        var loopCheck = _cfg.AddNewNode(CFGNodeKind.Block);

        loopEntry.OverrideSuccessors(loopBody);
        loopCheck.ToBranch(node.Condition, loopBody, loopExit);

        _currentNode = loopBody;
        _loopContinuations.Push((loopExit, loopCheck));

        Visit(node.Statement);

        if (_currentNode.Kind == CFGNodeKind.Block && _currentNode.Successors.Count == 0)
        {
            _currentNode.AddSuccessor(loopCheck);
        }

        _loopContinuations.Pop();
        _currentNode = loopExit;

        return _currentNode;
    }

    public override CFGNode? VisitForStatement(ForStatementSyntax node)
    {
        var loopInit = _currentNode;
        var loopEntry = _cfg.AddNewNode(CFGNodeKind.Block);
        var loopExit = _currentNode.Successors.FirstOrDefault() ?? _cfg.AddNewNode(CFGNodeKind.Block);
        var loopBody = _cfg.AddNewNode(CFGNodeKind.Block);
        var loopIncrement = node.Incrementors.Count == 0 ? null : _cfg.AddNewNode(CFGNodeKind.Block);

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

        _currentNode = loopBody;

        _loopContinuations.Push((loopExit, loopIncrement));

        Visit(node.Statement);

        _loopContinuations.Pop();

        if (loopIncrement != null)
        {
            foreach (var incrementor in node.Incrementors)
            {
                loopIncrement.AddStatement(incrementor);
            }
            if (_currentNode.Kind == CFGNodeKind.Block && _currentNode.Successors.Count == 0)
            {
                _currentNode.AddSuccessor(loopIncrement);
            }
        }
        else
        {
            if (_currentNode.Kind == CFGNodeKind.Block && _currentNode.Successors.Count == 0)
            {
                _currentNode.AddSuccessor(loopEntry);
            }
        }

        _currentNode = loopExit;

        return _currentNode;
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

        if (!_labelledBlocks.TryGetValue((string)identifier.Value, out var labelEntry))
        {
            throw new System.Exception("Labeled statement has no block prepared for it");
        }

        if (labelEntry.Kind != CFGNodeKind.Label || labelEntry.Successors.Count > 0)
        {
            throw new System.Exception("Block for labeled statement has already been processed");
        }

        // don't override the successors (it could be a e.g. return statement)
        if (_currentNode.Successors.Count == 0)
        {
            _currentNode.AddSuccessor(labelEntry);
        }

        labelEntry.ToLabel(identifier);

        _currentNode = labelEntry;
        Visit(statement);

        return _currentNode;
    }

    public override CFGNode? VisitGotoStatement(GotoStatementSyntax node)
    {
        if (node.CaseOrDefaultKeyword.Value != null)
        {
            throw new System.NotImplementedException("goto case / default not implemented yet");
        }

        var label = (node.Expression as IdentifierNameSyntax).Identifier;

        if (!_labelledBlocks.TryGetValue((string)label.Value, out var dest))
        {
            throw new System.Exception($"Label is not declared {label.Value}");
        }

        var jump = _cfg.AddNewNode(CFGNodeKind.Jump);

        _currentNode.OverrideSuccessors(jump);

        jump.ToJump(dest);
        _currentNode = jump;

        return jump;
    }

    public override CFGNode? VisitBreakStatement(BreakStatementSyntax node)
    {
        var e = _loopContinuations.Peek();
        var jump = _cfg.AddNewNode(CFGNodeKind.Jump);

        _currentNode.OverrideSuccessors(jump);

        jump.ToJump(e.BreakNode);
        _currentNode = jump;

        return jump;
    }

    public override CFGNode? VisitContinueStatement(ContinueStatementSyntax node)
    {
        var e = _loopContinuations.Peek();
        var jump = _cfg.AddNewNode(CFGNodeKind.Jump);

        _currentNode.OverrideSuccessors(jump);

        jump.ToJump(e.ContinueNode);
        _currentNode = jump;

        return jump;
    }

    public override CFGNode VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        AddSyntaxNode(node);
        return _currentNode;
    }

    public override CFGNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        AddSyntaxNode(node);
        return _currentNode;
    }

    public override CFGNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        _currentNode.AddStatement(node);
        _currentNode.OverrideSuccessors(_cfg.ExitNode);

        return _currentNode;
    }

    public void AddSyntaxNode(StatementSyntax node)
    {
        if (_currentNode.Kind == CFGNodeKind.Entry)
        {
            var newNode = _cfg.AddNewNode(CFGNodeKind.Block);
            _currentNode.AddSuccessor(newNode);
            _currentNode = newNode;
            newNode.AddStatement(node);
            return;
        }

        var (isExit, cond) = IsBlockExitPoint(node);
        if (isExit)
        {
            if (cond != null)
            {
                var newNode = _cfg.AddNewNode(CFGNodeKind.Block);
                _currentNode.Kind = CFGNodeKind.Branch;
                _currentNode.Condition = cond;
                _currentNode.AddSuccessor(newNode);
                _currentNode = newNode;
            }
            else
            {
                _currentNode.AddSuccessor(_cfg.ExitNode);
                var newNode = _cfg.AddNewNode(CFGNodeKind.Block);
                _currentNode = newNode;
            }
        }
        else
        {
            _currentNode.AddStatement(node);
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
}
