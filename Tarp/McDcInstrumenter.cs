using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Tarp;

public class McDcInstrumenter : CSharpSyntaxRewriter
{
    private const string TarpCodeAnnotation = "tarpcode";

    public McDcInstrumenter() : base(false)
    {
        _currentCompilationUnit = new Stack<Location>();
        _counters = new();
        _counterIncreaseMethodName = SyntaxFactory.IdentifierName("TARPGEN.C.Inc"); // TODO: ensure it's fresh
        _prependStatements = new();
        _appendStatements = new();
    }

    readonly Stack<Location> _currentCompilationUnit;
    public class Counter
    {
        public int Id { get; set; }
        public Location SourceLocation { get; set; }
        public SyntaxToken Identifier { get; set; }
    }
    readonly List<Counter> _counters;
    readonly IdentifierNameSyntax _counterIncreaseMethodName;
    readonly Stack<StatementSyntax> _prependStatements;
    readonly Stack<StatementSyntax> _appendStatements;

    Counter AllocCounter(Location location)
    {
        var id = _counters.Count;
        // TODO: ensure the identifier is fresh
        var expr = SyntaxFactory.Literal(id);

        var c = new Counter
        {
            Id = id,
            SourceLocation = location,
            Identifier = expr
        };
        _counters.Add(c);
        return c;
    }

    public override SyntaxNode VisitCompilationUnit(CompilationUnitSyntax node)
    {
        Debug.Assert(node != null);

        _currentCompilationUnit.Push(node.GetLocation());
        var r = base.VisitCompilationUnit(node);
        _currentCompilationUnit.Pop();
        return r;
    }

    /*public override SyntaxNode VisitBlock(BlockSyntax node)
    {
        return base.VisitBlock(node);
    }*/

    private static BlockSyntax StatementSyntaxToBlock(StatementSyntax statement)
    {
        if (statement is BlockSyntax bs)
        {
            return bs;
        }
        else
        {
            var newBlock = SyntaxFactory.Block(statement);

            return newBlock;
        }
    }

    private ExpressionStatementSyntax IncrementCounter(Counter c)
    {
        var expr0 = SyntaxFactory.InvocationExpression(
            _counterIncreaseMethodName,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, c.Identifier)
                    )
                )
            )
        );
        var expr = SyntaxFactory.ExpressionStatement(expr0)
            .WithAdditionalAnnotations(new SyntaxAnnotation(TarpCodeAnnotation));

        return expr;
    }

    private BlockSyntax IncrementCounterAtBeginning(BlockSyntax block, Counter c)
    {
        var expr = IncrementCounter(c);

        return block.WithStatements(block.Statements.Insert(0, expr));
    }

    public override SyntaxNode VisitIfStatement(IfStatementSyntax node)
    {
        Debug.Assert(node != null);

        if (node.HasAnnotations(TarpCodeAnnotation))
        {
            // don't bother instrumenting our own code
            return node;
        }

        var cond = node.Condition;
        var thenLocation = node.Statement.GetLocation();

        // TODO: allocate or fetch a decision for [cond]
        var condCounter = AllocCounter(cond.GetLocation());

        var thenBlock = StatementSyntaxToBlock(node.Statement);

        thenBlock = IncrementCounterAtBeginning(thenBlock, condCounter);

        var newNode =
            node.WithStatement(thenBlock);

        if (node.Else != null)
        {
            var elseBlock = StatementSyntaxToBlock(node.Else.Statement);

            elseBlock = IncrementCounterAtBeginning(elseBlock, condCounter);

            newNode =
                newNode
                .WithElse(node.Else.WithStatement(elseBlock));
        }

        return base.VisitIfStatement(newNode);
    }

    public override SyntaxNode VisitForStatement(ForStatementSyntax node)
    {
        Debug.Assert(node != null);

        if (node.Condition != null)
        {
            var counter = AllocCounter(node.Condition.GetLocation());

            var loopBlock = StatementSyntaxToBlock(node.Statement);
            var instrumentedLoopBlock = IncrementCounterAtBeginning(loopBlock, counter);

            var newNode = base.VisitForStatement(
                node
                .WithStatement(instrumentedLoopBlock));

            _appendStatements.Push(IncrementCounter(counter));

            return newNode;
        }
        else
        {
            // no condition
            return base.VisitForStatement(node);
        }
    }

    public override SyntaxNode VisitWhileStatement(WhileStatementSyntax node)
    {
        Debug.Assert(node != null);

        // add a counter IN the loop and AFTER the loop
        var counter = AllocCounter(node.Condition.GetLocation());

        var loopBlock = StatementSyntaxToBlock(node.Statement);
        var instrumentedLoopBlock = IncrementCounterAtBeginning(loopBlock, counter);

        var whileSyntax = base.VisitWhileStatement(node.WithStatement(instrumentedLoopBlock));

        _appendStatements.Push(IncrementCounter(counter));

        return whileSyntax;
    }

    public override SyntaxNode VisitDoStatement(DoStatementSyntax node)
    {
        Debug.Assert(node != null);

        // bool first = true;
        // do{
        // if (!first) { inc(C); first = false; }
        // ....
        // } while(...);
        // inc(C);

        // add a counter IN the loop and AFTER the loop
        var counter = AllocCounter(node.Condition.GetLocation());

        // FIXME: make this fresh!!!
        var firstId = "__first" + counter.Identifier.Text;

        var firstIdentifier = SyntaxFactory.Identifier(firstId);

        var first =
            SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)
                    .WithTrailingTrivia(SyntaxFactory.SyntaxTrivia(SyntaxKind.WhitespaceTrivia, " "))))
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(firstIdentifier)
                        .WithInitializer(
                            SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.LiteralExpression(
                                    SyntaxKind.TrueLiteralExpression))))))
            .WithAdditionalAnnotations(new SyntaxAnnotation(TarpCodeAnnotation));

        _prependStatements.Push(first);

        var incrementCounter = IncrementCounter(counter);

        var ifFirstThenIncrement =
            SyntaxFactory.IfStatement(
                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName(firstId)),
                SyntaxFactory.Block(
                    incrementCounter,
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(firstId),
                            SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))))
            )
            .WithAdditionalAnnotations(new SyntaxAnnotation(TarpCodeAnnotation));


        var loopBlock = StatementSyntaxToBlock(node.Statement);
        var instrumentedLoopBlock =
            loopBlock.WithStatements(loopBlock.Statements.Insert(0, ifFirstThenIncrement));

        _appendStatements.Push(incrementCounter);

        return base.VisitDoStatement(node.WithStatement(instrumentedLoopBlock));
    }

    public override SyntaxNode VisitBlock(BlockSyntax node)
    {
        Debug.Assert(node != null);

        var startPrependSyntaxCount = _prependStatements.Count;
        var startAppendSyntaxCount = _appendStatements.Count;

        SyntaxToken openBraceToken = VisitToken(node.OpenBraceToken);
        
        var newStatements = new List<StatementSyntax>(node.Statements.Count);
        foreach (var statement in node.Statements)
        {
            var newStatement = Visit(statement);

            while (_prependStatements.Count > startPrependSyntaxCount)
            {
                var n = _prependStatements.Pop();
                newStatements.Add(n);
            }

            newStatements.Add((StatementSyntax)newStatement);
            
            while (_appendStatements.Count > startAppendSyntaxCount)
            {
                var n = _appendStatements.Pop();
                newStatements.Add(n);
            }
        }
        var sts = new SyntaxList<StatementSyntax>(newStatements);
        SyntaxToken closeBraceToken = VisitToken(node.CloseBraceToken);
        return node.Update(openBraceToken, sts, closeBraceToken);
    }

    public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        Debug.Assert(node != null);
        Debug.Assert(node.Body != null && node.ExpressionBody == null ||
            node.Body == null && node.ExpressionBody != null);

        if (node.Body != null)
        {
            var location = node.Body.GetLocation();

            var entryCounter = AllocCounter(location);

            var expr0 = SyntaxFactory.InvocationExpression(
                _counterIncreaseMethodName,
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, entryCounter.Identifier)
                        )
                    )
                )
            );

            var expr = SyntaxFactory.ExpressionStatement(expr0);

            var body = node.Body.WithStatements(
                node.Body.Statements.Insert(0, expr)
                );

            return base.VisitMethodDeclaration(node.WithBody(body));
        }
        else if (node.ExpressionBody != null)
        {
            var location = node.ExpressionBody.GetLocation();

            var entryCounter = AllocCounter(location);

            var expr0 = SyntaxFactory.InvocationExpression(
                _counterIncreaseMethodName,
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, entryCounter.Identifier)
                        )
                    )
                )
            );

            // rewrite: ... => E ---> ... {...;return E;}

            var expr = SyntaxFactory.ExpressionStatement(expr0);

            var exprBody = node.ExpressionBody;

            var ret = SyntaxFactory.ReturnStatement(
                exprBody.Expression
                    .WithLeadingTrivia(SyntaxFactory.SyntaxTrivia(SyntaxKind.WhitespaceTrivia, " ")));

            var newBody = SyntaxFactory.Block(expr, ret);
            var newNode = node
                .RemoveNode(node.ExpressionBody, SyntaxRemoveOptions.KeepNoTrivia)
                .WithBody(newBody);

            return base.VisitMethodDeclaration(newNode);
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        return base.VisitAnonymousMethodExpression(node);
    }
    
    public void Instrument(ControlFlowGraph cfg)
    {
        /*
         * all methods have 1 entry, 1 exit block, and possibly in-between blocks
         * 
         * we need to allocate a counter for each block
         */

        foreach (var block in cfg.Blocks)
        {
            Console.Write("Block kind: ");
            switch (block.Kind)
            {
                case BasicBlockKind.Entry:
                    Console.WriteLine("entry");
                    break;
                case BasicBlockKind.Exit:
                    Console.WriteLine("exit");
                    break;
                case BasicBlockKind.Block:
                    Console.WriteLine("block");
                    break;
            }
            Console.Write("Condition kind: ");
            switch (block.ConditionKind)
            {
                case ControlFlowConditionKind.None:
                    Console.WriteLine("none");
                    break;
                case ControlFlowConditionKind.WhenFalse:
                    Console.WriteLine("when false");
                    Console.WriteLine("Branch value: {0}", block.BranchValue.Syntax.Kind());
                    //            block.ConditionalSuccessor
                    break;
                case ControlFlowConditionKind.WhenTrue:
                    Console.WriteLine("when true");
                    Console.WriteLine("Branch value: {0}", block.BranchValue.Syntax.Kind());
                    //            block.ConditionalSuccessor
                    break;
            }
            // e.g. a branch will look like:
            // - conditionKind=WhenTrue,BranchValue=(a == 100),ConditionalSuccessor:L1,FallthroughSuccessor:L2
            //block.FallThroughSuccessor

            Console.WriteLine("Operations:");
            foreach (var operation in block.Operations)
            {
                var syntax = operation.Syntax;
                Console.WriteLine(syntax.Kind());
            }
        }
    }
}
