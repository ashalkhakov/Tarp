using ICSharpCode.Decompiler.IL;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Tarp;

public class NfToProbed : CSharpSyntaxRewriter
{
    private readonly ProbeGenerator probeGenerator;
    private string currentClassOrStruct;
    private readonly IdentifierNameSyntax counterIncreaseMethodName;

    public NfToProbed(ProbeGenerator probeGenerator)
    {
        this.probeGenerator = probeGenerator;
        this.counterIncreaseMethodName = IdentifierName("TARPGEN.C.Inc"); // TODO: ensure it's fresh
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
            this.counterIncreaseMethodName,
            ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, id))))
        );
        var expr = ExpressionStatement(expr0);

        return expr;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        this.probeGenerator.StartMethod(node.GetLocation(), currentClassOrStruct, node.Identifier.Text);

        var block = node.Body;
        var openingBraceSpan = block.OpenBraceToken.Span;

        var entryProbeId = this.probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Entry, openingBraceSpan);
        var entryProbe = IncrementCounter(entryProbeId);

        var statementsWithEntryCounter = node.Body.Statements
                    .Insert(0, entryProbe);

        var body = node.Body.WithStatements(
            statementsWithEntryCounter
            );
        var moddedNode = node.WithBody(body);

        return base.VisitMethodDeclaration(moddedNode);
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var condition = node.Condition;
        var thenNode = node.Statement;
        var elseNode = node.Else;

        return base.VisitIfStatement(node);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        // add an exit probe just before the return
        if (node.Statements.Count > 0 && node.Statements.Last() is ReturnStatementSyntax rs)
        {
            var exitProbeId = this.probeGenerator.AddProbe(ProbeGenerator.ProbeKind.Exit, node.FullSpan);
            var exitProbe = IncrementCounter(exitProbeId);

            var newNode = node.InsertNodesBefore(rs, new[] { exitProbe });
            node = newNode;
        }

        return base.VisitBlock(node);
    }
}
