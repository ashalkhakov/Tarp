using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Tarp;

/*
 * The idea of this rewriter is to ensure some invariants over source code that we will use to our advantage later.
 * 
 * - ensure all conditionals and loops use { blocks }
 * - rewrite all expression-bodied methods to { return ExpressionBody; }
 * - rewrite all return E into tmp = E; return tmp
 * 
 * This is will be our "normal form".
 */
public class SourceToNf : CSharpSyntaxRewriter
{
    private FreshNameGenerator freshNameGenerator;

    public SourceToNf(FreshNameGenerator freshNameGenerator) : base(false)
    {
        this.freshNameGenerator = freshNameGenerator;
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

    public override SyntaxNode VisitIfStatement(IfStatementSyntax node)
    {
        Debug.Assert(node != null);

        var thenBlock = StatementSyntaxToBlock(node.Statement);

        var newNode = node.WithStatement(thenBlock);

        if (node.Else != null)
        {
            // this is the "else" decision
            var elseBlock = StatementSyntaxToBlock(node.Else.Statement);

            newNode =
                newNode
                .WithElse(node.Else.WithStatement(elseBlock));
        }

        return base.VisitIfStatement(newNode);
    }

    public override SyntaxNode VisitForStatement(ForStatementSyntax node)
    {
        Debug.Assert(node != null);

        var loopBlock = StatementSyntaxToBlock(node.Statement);

        var newNode = base.VisitForStatement(
            node
            .WithStatement(loopBlock));

        return newNode;
    }

    public override SyntaxNode VisitWhileStatement(WhileStatementSyntax node)
    {
        Debug.Assert(node != null);

        var loopBlock = StatementSyntaxToBlock(node.Statement);

        var whileSyntax = base.VisitWhileStatement(node.WithStatement(loopBlock));

        return whileSyntax;
    }

    public override SyntaxNode VisitDoStatement(DoStatementSyntax node)
    {
        Debug.Assert(node != null);

        var loopBlock = StatementSyntaxToBlock(node.Statement);

        return base.VisitDoStatement(node.WithStatement(loopBlock));
    }

    public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        Debug.Assert(node != null);
        Debug.Assert(node.Body != null && node.ExpressionBody == null ||
            node.Body == null && node.ExpressionBody != null);

        if (node.Body != null)
        {
            return base.VisitMethodDeclaration(node);
        }
        else if (node.ExpressionBody != null)
        {
            // rewrite: ... => E ---> ... {...;return E;}

            var exprBody = node.ExpressionBody;

            var ret = SyntaxFactory.ReturnStatement(
                exprBody.Expression
                    .WithLeadingTrivia(SyntaxFactory.SyntaxTrivia(SyntaxKind.WhitespaceTrivia, " ")));

            var newBody = SyntaxFactory.Block(ret);
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

    public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression != null && !(node.Expression is IdentifierNameSyntax) && !(node.Expression is LiteralExpressionSyntax))
        {
            var name = this.freshNameGenerator.GetFreshName();

            var location = node.GetLocation();
            var span = location.GetMappedLineSpan();

            var lineDirective =
                LineSpanDirectiveTrivia(
                                LineDirectivePosition(
                                    Literal(span.StartLinePosition.Line),
                                    Literal(span.StartLinePosition.Character)),
                                LineDirectivePosition(
                                    Literal(span.EndLinePosition.Line),
                                    Literal(span.EndLinePosition.Character)),
                                Literal(span.Path),
                                true)
                .WithLeadingTrivia(LineFeed)
                .WithTrailingTrivia(LineFeed);

            var leadingTrivia =
                node.ReturnKeyword.LeadingTrivia
                    .Insert(0, Trivia(lineDirective));

            var declarationStatement =
            LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName(
                        Identifier(
                            TriviaList(),
                            SyntaxKind.VarKeyword,
                            "var",
                            "var",
                            TriviaList()))
                    .WithTrailingTrivia(SyntaxTrivia(SyntaxKind.WhitespaceTrivia, " ")))
                .WithVariables(
                    SingletonSeparatedList<VariableDeclaratorSyntax>(
                        VariableDeclarator(
                            Identifier(name))
                        .WithInitializer(
                            EqualsValueClause(node.Expression)))))
            .WithLeadingTrivia(leadingTrivia);
            var newReturnStatemnt =
                ReturnStatement(
                    node.ReturnKeyword.WithoutTrivia(),
                    IdentifierName(name).WithLeadingTrivia(SyntaxTrivia(SyntaxKind.WhitespaceTrivia, " ")),
                    node.SemicolonToken);

            return Block(List<StatementSyntax>([declarationStatement, newReturnStatemnt]));
        }

        return base.VisitReturnStatement(node);
    }
}
