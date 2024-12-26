using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using Tarp.Basics;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Tarp.Rewriting;

public class NfToProbed : CSharpSyntaxRewriter
{
    private readonly ProbeGenerator probeGenerator;
    private string currentClassOrStruct;
    private readonly IdentifierNameSyntax counterIncreaseMethodName;

    public NfToProbed(ProbeGenerator probeGenerator)
    {
        this.probeGenerator = probeGenerator;
        counterIncreaseMethodName = IdentifierName("TARPGEN.C.Inc"); // TODO: ensure it's fresh
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        currentClassOrStruct = node.Identifier.ValueText;
        return base.VisitClassDeclaration(node);
    }

    private ExpressionStatementSyntax IncrementCounter(int counterId)
    {
        var id = Literal(counterId);

        var expr0 = InvocationExpression(
            counterIncreaseMethodName,
            ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, id))))
        );
        var expr = ExpressionStatement(expr0);

        return expr;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        probeGenerator.StartMethod(node.GetLocation(), currentClassOrStruct, node.Identifier.Text);

        var block = node.Body;
        var openingBraceSpan = block.OpenBraceToken.Span;

        var entryProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Entry, openingBraceSpan);
        var entryProbe = IncrementCounter(entryProbeId);

        var statementsWithEntryCounter = node.Body.Statements
                    .Insert(0, entryProbe);

        var body = node.Body.WithStatements(
            statementsWithEntryCounter
            );
        var moddedNode = node.WithBody(body);

        return base.VisitMethodDeclaration(moddedNode);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var spanStart = node.SpanStart;
        var newStatements = new List<StatementSyntax>();

        // go over statements and find points where we need to insert block probes
        foreach (var statement in node.Statements)
        {
            if (statement is ReturnStatementSyntax rs)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Exit, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(rs));
            }
            else if (statement is GotoStatementSyntax gs)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(gs));
            }
            else if (statement is BreakStatementSyntax bs)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(bs));
            }
            else if (statement is WhileStatementSyntax ws)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(ws));
            }
            else if (statement is DoStatementSyntax ds)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(ds));
            }
            else if (statement is ForStatementSyntax fs)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(fs));
            }
            else if (statement is ForEachStatementSyntax fes)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(fes));
            }
            else if (statement is IfStatementSyntax ifs)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(ifs));
            }
            else if (statement is SwitchStatementSyntax ss)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(ss));
            }
            else
            {
                newStatements.Add((StatementSyntax)Visit(statement));
            }
        }

        if (node.Parent is WhileStatementSyntax
            || node.Parent is DoStatementSyntax
            || node.Parent is ForStatementSyntax
            || node.Parent is ForEachStatementSyntax
            || node.Parent is IfStatementSyntax
            || node.Parent is ElseClauseSyntax)
        {
            // put a probe at the very end of this block
            var span = GetSpanBefore(spanStart, newStatements.Last());
            var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Block, span);
            var exitProbe = IncrementCounter(exitProbeId);
            newStatements.Add(exitProbe);
        }

        var newNode = node
            .WithStatements(SyntaxList.Create([.. newStatements]));

        return newNode;
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var condition = node.Condition;
        var statement = node.Statement;
        var elseStatement = node.Else;

        var newNode = base.VisitIfStatement(node);
        return newNode;
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var newStatements = new List<StatementSyntax>(node.Statements.Count);
        var spanStart = node.SpanStart;

        foreach (var statement in node.Statements)
        {
            if (statement is ReturnStatementSyntax rs)
            {
                var span = GetSpanBefore(spanStart, statement);
                spanStart = statement.Span.End;

                var exitProbeId = probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Exit, span);
                var exitProbe = IncrementCounter(exitProbeId);

                newStatements.Add(exitProbe);
                newStatements.Add((StatementSyntax)Visit(rs));
            }
            else
            {
                var newStatement = Visit(statement);
                newStatements.Add((StatementSyntax)newStatement);
            }
        }
        var sts = new SyntaxList<StatementSyntax>(newStatements);

        var newSection = node.Update(node.Labels, sts);

        return newSection;
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var newCondition = (ExpressionSyntax)Visit(node.Condition);
        var newStatement = (StatementSyntax)Visit(node.Statement);

        // TODO: add probe at loop block end(s)

        var newResult = node
            .WithCondition(newCondition)
            .WithStatement(newStatement);

        return newResult;
    }

    private static Microsoft.CodeAnalysis.Text.TextSpan GetSpanBefore(int spanStart, StatementSyntax statement)
    {
        var spanEnd = statement.Span.End;
        var span = new Microsoft.CodeAnalysis.Text.TextSpan(spanStart, spanEnd);
        return span;
    }
}
