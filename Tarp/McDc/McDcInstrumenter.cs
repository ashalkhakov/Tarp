using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Tarp.McDc;

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
        _blockCounters = new();
    }

    readonly Stack<Location> _currentCompilationUnit;
    public enum CounterKind : int
    {
        Entry = 0, // for entry points into the block or the method
        Exit = 1, // for exit points from the block or the method
        DecisionFalse = 2, // for a decision is false
        DecisionTrue = 3, // for a decision is true
        Any = 4
    }
    public class Counter
    {
        public int Id { get; set; }
        public Location SourceLocation { get; set; }
        public SyntaxToken Identifier { get; set; }
        public CounterKind Kind { get; set; } = CounterKind.Entry;
    }
    readonly List<Counter> _counters;
    readonly Stack<Counter> _blockCounters;
    readonly IdentifierNameSyntax _counterIncreaseMethodName;
    readonly Stack<StatementSyntax> _prependStatements;
    readonly Stack<StatementSyntax> _appendStatements;

    /*
     * TODO: block exits?
     * - maintain a stack of blocks as you descend down the tree
     * - [break] and [continue] should work with the nearest enclosing block
     * - [goto] is not constrained though... so need a list of labels somewhere...
     * - [return] should work with the root block (i.e. the method itself)
     * 
     * also, if you have e.g.
     * if (E) { return 1; }
     * // mark entry into this sub-block
     * return 2;
     * 
     * i.e. a control transfer inside a sub-tree, you must also mark the entry into the 'next' statement
     * 
     * what kind of counters do we want?
     * - method entry and exit
     *   - at first statement
     *   - at all [return]s from this method (only the direct ones of course!)
     * - for each decision, we want a decision counter
     * - for each branch, we want a counter
     *   - e.g. if (E) { S1; } else { S2 } --> if (E) { inc(<E is true>); S1 } else { inc(<E is false>); S2 }
     *   - similarly for loops
     * - for successive statements without control transfers, we want a counter
     */

    Counter AllocCounter(Location location, CounterKind counterKind)
    {
        var id = _counters.Count;
        // TODO: ensure the identifier is fresh
        var expr = SyntaxFactory.Literal(id);

        var c = new Counter
        {
            Id = id,
            SourceLocation = location,
            Identifier = expr,
            Kind = counterKind
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
                SyntaxFactory.SeparatedList(
                    new[] {
                        // NOTE: remove this. it's here to show what location this counter maps to.
                        SyntaxFactory.Argument(
                            SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(c.SourceLocation.ToString()))
                        ),

                        SyntaxFactory.Argument(
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, c.Identifier)
                        )
                    }
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

    public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node)
    {
        Debug.Assert(node != null);

        var exitCounter = _blockCounters.Peek();

        _prependStatements.Push(IncrementCounter(exitCounter));

        return base.VisitReturnStatement(node);
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

        // TODO: if either of the blocks contain control transfers,
        // do we also mark the statement following this "if"?

        // TODO: allocate or fetch a decision for [cond]
        var condCounterTrue = AllocCounter(cond.GetLocation(), CounterKind.DecisionTrue);
        var condCounterFalse = AllocCounter(cond.GetLocation(), CounterKind.DecisionFalse);

        var thenBlock = StatementSyntaxToBlock(node.Statement);

        thenBlock = IncrementCounterAtBeginning(thenBlock, condCounterTrue);

        var newNode =
            node.WithStatement(thenBlock);

        if (node.Else != null)
        {
            // this is the "else" decision
            var elseBlock = StatementSyntaxToBlock(node.Else.Statement);

            elseBlock = IncrementCounterAtBeginning(elseBlock, condCounterFalse);

            newNode =
                newNode
                .WithElse(node.Else.WithStatement(elseBlock));
        }
        else
        {
            // this is the "else" decision
            _appendStatements.Push(IncrementCounter(condCounterFalse));
        }

        return base.VisitIfStatement(newNode);
    }

    public override SyntaxNode VisitSwitchStatement(SwitchStatementSyntax node)
    {
        Debug.Assert(node != null);
        Debug.Assert(node.Sections.Count >= 0);

        // TODO: allocate or fetch a decision for [Expression]
        /*
         * switch (E) {
         * case C1:
         * case C2:
         * ...
         * case Cn:
         *  // E == C1 || E == C2 || ... || E == Cn
         *  // entry into this block
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
         */

        var expressionCounter = AllocCounter(node.Expression.GetLocation(), CounterKind.DecisionFalse);

        SyntaxToken switchKeyword = VisitToken(node.SwitchKeyword);
        SyntaxToken openParenToken = VisitToken(node.OpenParenToken);

        ExpressionSyntax expression = (ExpressionSyntax)Visit(node.Expression);

        SyntaxToken closeParenToken = VisitToken(node.CloseParenToken);
        SyntaxToken openBraceToken = VisitToken(node.OpenBraceToken);

        List<SwitchSectionSyntax> sections = new();
        foreach (var section in node.Sections)
        {
            var labels = section.Labels;

            // TODO: what kind of counters do we need for a [switch]?
            var c = AllocCounter(section.GetLocation(), CounterKind.Any);
            var expr = IncrementCounter(c);

            var newStatements = new List<StatementSyntax>(section.Statements.Count);
            newStatements.Add(expr);

            foreach (var statement in section.Statements)
            {
                var newStatement = Visit(statement);

                newStatements.Add((StatementSyntax)newStatement);
            }
            var sts = new SyntaxList<StatementSyntax>(newStatements);

            var newSection = section.Update(labels, sts);
            sections.Add(newSection);
        }
        var newSections = new SyntaxList<SwitchSectionSyntax>(sections);

        _appendStatements.Push(IncrementCounter(expressionCounter));

        SyntaxToken closeBraceToken = VisitToken(node.CloseBraceToken);
        return node.Update(switchKeyword, openParenToken, expression, closeParenToken, openBraceToken, newSections, closeBraceToken);
    }

    public override SyntaxNode VisitForStatement(ForStatementSyntax node)
    {
        Debug.Assert(node != null);

        // TODO: we must always ensure that [for] has a proper { block }

        if (node.Condition != null)
        {
            var counterTrue = AllocCounter(node.Condition.GetLocation(), CounterKind.DecisionTrue);
            var counterFalse = AllocCounter(node.Condition.GetLocation(), CounterKind.DecisionFalse);

            var loopBlock = StatementSyntaxToBlock(node.Statement);
            var instrumentedLoopBlock = IncrementCounterAtBeginning(loopBlock, counterTrue);

            var newNode = base.VisitForStatement(
                node
                .WithStatement(instrumentedLoopBlock));

            _appendStatements.Push(IncrementCounter(counterFalse));

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

        // TODO: we must always ensure that [for] has a proper { block }

        // add a counter IN the loop and AFTER the loop
        var counterTrue = AllocCounter(node.Condition.GetLocation(), CounterKind.DecisionTrue);
        var counterFalse = AllocCounter(node.Condition.GetLocation(), CounterKind.DecisionFalse);

        var loopBlock = StatementSyntaxToBlock(node.Statement);
        var instrumentedLoopBlock = IncrementCounterAtBeginning(loopBlock, counterTrue);

        var whileSyntax = base.VisitWhileStatement(node.WithStatement(instrumentedLoopBlock));

        _appendStatements.Push(IncrementCounter(counterFalse));

        return whileSyntax;
    }

    public override SyntaxNode VisitDoStatement(DoStatementSyntax node)
    {
        Debug.Assert(node != null);

        // TODO: we must always ensure that [for] has a proper { block }

        // bool first = true;
        // do{
        // if (!first) { inc(C); first = false; }
        // ....
        // } while(...);
        // inc(C);

        // TODO: verify this

        // add a counter IN the loop and AFTER the loop
        var counterTrue = AllocCounter(node.Condition.GetLocation(), CounterKind.DecisionTrue);
        var counterFalse = AllocCounter(node.Condition.GetLocation(), CounterKind.DecisionFalse);

        // FIXME: make this fresh!!!
        var firstId = "__first" + counterTrue.Identifier.Text;

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

        var incrementCounter = IncrementCounter(counterTrue);

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

        // TODO: a counter for EXIT from the method?
        // - all [returns] inside the method
        // - the last statement of the method
        //   - e.g. if it's [return E], then have to rewrite it as [tmp = E; Inc(C); return tmp;]
        // TODO: a counter for exit from loops? what about goto's and labels?

        if (node.Body != null)
        {
            var location = node.Body.GetLocation();

            var entryCounter = AllocCounter(location, CounterKind.Entry);
            _blockCounters.Push(entryCounter);

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

            var statementsWithEntryCounter = node.Body.Statements
                        .Insert(0, expr);

            var body = node.Body.WithStatements(
                statementsWithEntryCounter
                );

            return base.VisitMethodDeclaration(node.WithBody(body));
        }
        else if (node.ExpressionBody != null)
        {
            var location = node.ExpressionBody.GetLocation();

            var entryCounter = AllocCounter(location, CounterKind.Entry);
            _blockCounters.Push(entryCounter);

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

    // TODO: according to ECT https://en.wikipedia.org/wiki/Elementary_comparison_testing
    // - and https://www.cs.auckland.ac.nz/~ewan/essays/nlip.html
    // we need to get a flowgraph for a method
    // and then we can enumerate all linearly independent paths through it.
    //
    // need our own CFGs...
    // - build off whatever MS has but! at the source level. e.g. keep all branching at the source level. don't break up A && B into 2 branches.
    // - keep the links to the source level (e.g. each basic block is a region of source code + the AST)
    //
    // basic block: the maximal sequence of operations without control flow. only 1 entry and 1 exit points.
    // - if,goto,return,label: these end a basic block
    // edge: there is an edge between blocks b1 and b2 if b2 may be executed immediately after b1.
    // two special nodes: entry (no in-edges) and exit (no out-edges)
    // all other nodes have at least one in-edge and one out-edge
    // 
    // TODO: what should we do about dead code?

    public void Instrument(ControlFlowGraph cfg)
    {
        /*
         * all methods have 1 entry, 1 exit block, and possibly in-between blocks
         * 
         * we need to allocate a counter for each block.
         * all the conditional jumps are due to decisions.
         * e.g. A&&B -->
         *    2 nodes
         *      ifz A then goto LE
         *      ifz B then goto L2
         *      ...
         *      goto ...
         *      goto L1
         *      L0:
         *      ...
         *      LE:
         *      ...
         * so we may need to coalesce multiple nodes somehow into decisions AS THEY ARE IN THE AST.
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
