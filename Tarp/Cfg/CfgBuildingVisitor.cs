using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Tarp.Types;

namespace Tarp.Cfg;

public class CfgBuildingVisitor : CSharpSyntaxVisitor
{
    public CfgBuildingVisitor(MethodDeclarationSyntax method)
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
        IEnumerable<StatementSyntax>? defaultStatements = null;

        Types.LinkedListNode<(ExpressionSyntax Cond, IEnumerable<StatementSyntax> Statements)>? clauses = null;

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
                    defaultStatements = section.Statements.Where(s => !(s is BreakStatementSyntax));
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

            var statements = section.Statements.Where(s => !(s is BreakStatementSyntax));

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
    // TOOD: [goto case]/[goto default] support inside a switch (that's just goto though! very obscure feature)
    // TODO: goto
    // TODO: what about declarations inside a method? delegates, lambdas, inner functions

    public override void VisitGotoStatement(GotoStatementSyntax node)
    {
        throw new NotImplementedException();
    }

    public override void VisitTryStatement(TryStatementSyntax node)
    {
        /*
         * try { S }
         * catch (T1 e) { S1 }
         * ...
         * catch (Tn e) { Sn }
         * finally { Sf }
         * 
         * we probably need special edges for exceptions
         * basically:
         * execute the [try] as a new block. if everything is OK, goto [finally] or to the next statement after the [try-catch-finally].
         * if exception is thrown, then [catches] are tried in the order given, one by one. if you get inside the catch, you will then go to the [finally] or the next statement.
         * [finally] block is always the block after all the catches.
         * 
         * [throw] also needs to be taken into account.
         * 
         * ----
         * a special kind of branch at the end of a basic block?
         * a multiway kind of 'catch'
         * ----
         * we can just represent all 'catch'es as typical branching.
         * 'throw' inside a 'try' does a 'goto' into the 'catch' block. the 'catch' block contains multiple sub-blocks with implicit branching.
         * all of the 'catch' blocks will 'goto' the 'finally' block (if present), or to AFTER the whole statement.
         * the 'finally' block performs some translation and control goes to wherever it went.
         * ----
         * UPDATE 2024-01-21:
         * file:///C:/Users/User/Downloads/icsea_2013_2_20_10241.pdf
         * How Exception Handling Constructions are Tested: An Initial Investigation with Open Source Software
         * Auri Marcelo Rizzo Vincenzi et al
         *
         * no, simple branching will not suffice. a 'try' block may have additional 'exceptional exits'.
         * - basically, any basic block may have additional 'exceptional edges' which represent control flow when an exception is raised!
         *   - this is a wholly new way to branch! it's ORTHOGONAL to regular exits
         * - some basic blocks can only be reached through the 'exceptional edges', which makes them 'exception handlers'
         * - and some basic blocks can be reached through the 'finally edges', which are exits from exception handlers OR from the 'try' blocks
         * 
         * also regarding [finally]. there's some syntax sugar involved.
         * - in CLR there is only 'try-catch' and 'try-finally'.
         * - 'try-catch-finally' is 'try-catch' nested in 'try-finally'
         *   try {
         *      try { S1; } // the exceptional control flow of S1 MUST go through the [catch] statements below
         *      catch (E1) { S2; leave L0; } // this use of "leave" means: just goto L0 ^_^
         *      catch (E2) { S3; leave L0; }
         *      L0: leave L1; // this use of "leave" means: go to the finally block, execute Sf; then goto L1
         *   }
         *   finally { Sf; }
         *   L1: ...
         *   
         * - the 'catch' blocks end with a 'leave' to 
         * 
         * So, Roslyn does it this way. there are 'regions' on top of blocks. a method is a Try region.
         * there are also TryAndCatch and TryAndFinally regions (and a try-catch-finally is rewritten as above). regions may nest.
         * a TryAndCatch region has the following subregions:
         * - a Try region
         * - multiple Catch regions
         * a TryAndFinally region has the following subregions:
         * - a Try region
         * - a Finally region
         * I guess that regular control flow goes as follows between regions:
         * - Try is executed, then Finally may be executed, but then we go to the statement after the TryFinally
         * - Catches are never executed
         * but the exceptional control flow is meant to be quite convoluted...
         */
        throw new NotImplementedException();
    }

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
