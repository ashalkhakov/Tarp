using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;


namespace Tarp.Cfg;

public class CFGBuilder : CSharpSyntaxVisitor<CFGNode>
{
    private readonly ControlFlowGraph _cfg = new ControlFlowGraph();
    private CFGNode _currentNode;

    public ControlFlowGraph Build(SyntaxNode root)
    {
        _cfg.Exit = _cfg.AddNewNode(CFGNode.CFGNodeKind.Exit);
        _cfg.Entry = _cfg.AddNewNode(CFGNode.CFGNodeKind.Entry);

        _currentNode = _cfg.Entry;

        Visit(root);

        return _cfg;
    }

    public override CFGNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        // Visit method body
        Visit(node.Body);

        if (_currentNode.Kind != CFGNode.CFGNodeKind.Exit && _currentNode.Successors.Count == 0)
        {
            _currentNode.Successors.Add(_cfg.Exit);
        }

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
            var ifBranch = _cfg.AddNewNode(CFGNode.CFGNodeKind.Branch);
            var ifExit = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);
            var ifBody = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);

            _currentNode.Successors.Add(ifBranch);

            ifBranch.Condition = node.Condition;
            ifBranch.Successors.Add(ifBody); // if true
            ifBranch.Successors.Add(ifExit); // if false

            ifBody.Successors.Add(ifExit);

            _currentNode = ifBody;

            Visit(node.Statement);

            _currentNode = ifExit;

            return _currentNode;
        }
        else
        {
            var ifBranch = _cfg.AddNewNode(CFGNode.CFGNodeKind.Branch);
            var ifExit = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);
            var ifBody = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);
            var elseBody = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);

            _currentNode.Successors.Add(ifBranch);

            ifBranch.Condition = node.Condition;
            ifBranch.Successors.Add(ifBody); // if true
            ifBranch.Successors.Add(elseBody); // if false

            ifBody.Successors.Add(ifExit);
            elseBody.Successors.Add(ifExit);

            _currentNode = ifBody;

            Visit(node.Statement);

            _currentNode = elseBody;

            Visit(node.Statement);

            _currentNode = ifExit;

            return _currentNode;
        }
    }

    public override CFGNode VisitWhileStatement(WhileStatementSyntax node)
    {
        var loopEntry = _cfg.AddNewNode(CFGNode.CFGNodeKind.Branch);
        var loopExit = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);
        var loopBody = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);

        _currentNode.Successors.Add(loopEntry);

        loopEntry.Condition = node.Condition;
        loopEntry.Successors.Add(loopBody); // if true
        loopEntry.Successors.Add(loopExit); // if false

        loopBody.Successors.Add(loopEntry);

        _currentNode = loopBody;

        Visit(node.Statement);

        _currentNode = loopExit;

        return _currentNode;
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

    // Implement other statement visitors (e.g., for, foreach, switch, etc.) similarly

    public void AddSyntaxNode(StatementSyntax node)
    {
        var (isExit, cond) = IsBlockExitPoint(node);
        if (isExit)
        {
            if (cond != null)
            {
                _currentNode.Kind = CFGNode.CFGNodeKind.Branch;
                _currentNode.Condition = cond;
                var newNode = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);
                _currentNode.Successors.Add(newNode);
                _currentNode = newNode;
            }
            else
            {
                _currentNode.Successors.Add(_cfg.Exit);
                var newNode = _cfg.AddNewNode(CFGNode.CFGNodeKind.Block);
                _currentNode = newNode;
            }
        }
        else
        {
            _currentNode.Statements.Add(node);
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
        if (statement is ReturnStatementSyntax)
        {
            return (true, null);
        }
        return (false, null);
    }
}
