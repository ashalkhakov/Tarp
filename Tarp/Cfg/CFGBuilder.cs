using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;


namespace Tarp.Cfg;

public class CFGBuilder : CSharpSyntaxVisitor<CFGNode>
{
    private readonly ControlFlowGraph _cfg = new ControlFlowGraph();
    private CFGNode _currentNode;

    public ControlFlowGraph Build(SyntaxNode root)
    {
        _currentNode = _cfg.EntryNode;

        Visit(root);

        return _cfg;
    }

    public override CFGNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
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
            var ifExit = _currentNode.Successors.FirstOrDefault() ?? _cfg.AddNewNode(CFGNodeKind.Block);
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

            return _currentNode;
        }
        else
        {
            var ifExit = _currentNode.Successors.FirstOrDefault() ?? _cfg.AddNewNode(CFGNodeKind.Block);
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
        var loopEntry = _currentNode;
        var loopExit = _cfg.AddNewNode(CFGNodeKind.Block);
        var loopBody = _cfg.AddNewNode(CFGNodeKind.Block);

        loopEntry.Kind = CFGNodeKind.Branch;

        loopEntry.ToBranch(node.Condition, loopBody, loopExit);

        loopBody.AddSuccessor(loopEntry);

        _currentNode = loopBody;

        Visit(node.Statement);

        _currentNode = loopExit;

        return _currentNode;
    }

    public override CFGNode? VisitLabeledStatement(LabeledStatementSyntax node)
    {
        var labelEntry = _cfg.AddNewNode(CFGNodeKind.Label);

        var identifier = node.Identifier;
        var statement = node.Statement;

        _currentNode.AddSuccessor(labelEntry);

        labelEntry.ToLabel(identifier);

        _currentNode = labelEntry;
        Visit(statement);

        return _currentNode;
    }

    public override CFGNode? VisitGotoStatement(GotoStatementSyntax node)
    {
        throw new System.NotImplementedException("goto not implemented yet");
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

    // Implement other statement visitors (e.g., for, foreach, switch, etc.) similarly

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
        return (false, null);
    }
}
