using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Tarp.McDc;

/*
 * Line, function and switch coverage, on top of the decisions (DecisionRewriter), in the same pass.
 *
 * A probe, global::TarpGen.McDc.P(id), runs right before every statement: a statement's lines count
 * as run once it has started. That keeps line coverage right when exceptions are thrown, with no
 * control flow graph: a call that throws doesn't make the statements after it look run.
 *
 * - A statement that isn't in a block (if (x) return;) gets a block, to make room for its probe.
 * - A compound statement's probe covers its header (if (...), while (...), switch (...)); its body
 *   has probes of its own.
 * - Functions (methods, constructors, accessors, operators, local functions, top-level statements):
 *   entry is the first probe in the body; exits are return and throw statements, and the end of the
 *   body when it can be reached (a probe there). Expression bodies become block bodies, to have
 *   somewhere to put the probe: "=> e" is "{ P(id); return e; }" (or "e;", or "throw ...;").
 * - A switch statement's branches are its sections; one with no default gets "default: P(id); break;",
 *   for "no case matched". Branches of decisions are their outcomes (see McDcReport).
 * - Lambdas: their block bodies get probes, but they aren't counted as functions; expression bodies
 *   aren't probed (their line counts with the statement they are in).
 *
 * Nothing here adds a line break, so line numbers (and #line) stay right.
 */
public sealed class CoverageRewriter : DecisionRewriter
{
    private const string ProbeMethod = "global::TarpGen.McDc.P";

    // the function whose body is being visited; null in a lambda, whose returns aren't the function's exits
    private readonly Stack<FunctionEntry?> _functions = new();
    private readonly Stack<SwitchEntry> _switches = new();
    // the probe that runs first for an original statement (for a block: the first one in it)
    private readonly Dictionary<StatementSyntax, int> _firstProbe = new();

    public CoverageRewriter(SemanticModel model, McDcMap map, string displayPath, IReadOnlyDictionary<int, string>? excluded = null)
        : base(model, map, displayPath, excluded)
    {
    }

    // statements that aren't in a block: if (x) return; -> if (x) { P(id); return; }
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var result = base.Visit(node);
        if (node is StatementSyntax original && original is not BlockSyntax && IsEmbedded(original) &&
            result is StatementSyntax rewritten && ShouldProbe(original))
        {
            var probe = ProbeStatement(ProbeFor(original));
            return Block(List(new[] { probe.WithTrailingTrivia(Space), rewritten.WithoutTrivia() }))
                .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(rewritten.GetLeadingTrivia()).WithTrailingTrivia(Space))
                .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(Space).WithTrailingTrivia(rewritten.GetTrailingTrivia()));
        }
        return result;
    }

    private static bool IsEmbedded(StatementSyntax s) => s.Parent switch
    {
        IfStatementSyntax p => p.Statement == s,
        ElseClauseSyntax => true,
        WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax => true,
        UsingStatementSyntax or LockStatementSyntax or FixedStatementSyntax => true,
        _ => false
    };

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var rewritten = (BlockSyntax)base.VisitBlock(node)!;
        if (IsExcludedFromCoverage(node))
        {
            return rewritten;
        }

        if (node.Statements.Count == 0)
        {
            if (IsFunctionBody(node))
            {
                // the function's end probe stands for it
                return rewritten;
            }
            var id = NewProbe(node, node.OpenBraceToken.Span, ProbeEntry.EmptyBlock);
            _firstProbe[node] = id;
            return rewritten.WithStatements(SingletonList(ProbeStatement(id).WithLeadingTrivia(Space).WithTrailingTrivia(Space)));
        }

        var statements = Probe(node.Statements, rewritten.Statements);
        var first = node.Statements.FirstOrDefault(_firstProbe.ContainsKey);
        if (first != null)
        {
            _firstProbe[node] = _firstProbe[first];
        }
        return rewritten.WithStatements(statements);
    }

    private static bool IsFunctionBody(BlockSyntax block) =>
        block.Parent is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax;

    // a probe before each statement of a list (a block's, or a switch section's)
    private SyntaxList<StatementSyntax> Probe(SyntaxList<StatementSyntax> original, SyntaxList<StatementSyntax> rewritten)
    {
        var result = new List<StatementSyntax>();
        for (var i = 0; i < original.Count; i++)
        {
            var o = original[i];
            var r = rewritten[i];
            if (!ShouldProbe(o))
            {
                result.Add(r);
                continue;
            }

            var probe = ProbeStatement(ProbeFor(o));
            if (r is LabeledStatementSyntax labeled)
            {
                // label: P(id); statement -- a goto runs the probe too
                result.Add(labeled.WithStatement(probe.WithLeadingTrivia(labeled.Statement.GetLeadingTrivia())));
                result.Add(labeled.Statement.WithLeadingTrivia(Space));
            }
            else
            {
                result.Add(probe.WithLeadingTrivia(r.GetLeadingTrivia()).WithTrailingTrivia(Space));
                result.Add(r.WithLeadingTrivia());
            }
        }
        return List(result);
    }

    private bool ShouldProbe(StatementSyntax s) => s switch
    {
        // not run where they are; a local function's body has probes of its own
        LocalFunctionStatementSyntax => false,
        LocalDeclarationStatementSyntax d when d.IsConst => false,
        // its statements have probes
        BlockSyntax => false,
        EmptyStatementSyntax => false,
        _ => !IsExcludedFromCoverage(s)
    };

    // the probe of a statement, covering its own lines: for a compound statement, its header
    private int ProbeFor(StatementSyntax original)
    {
        var id = NewProbe(original, OwnSpan(original), ProbeEntry.Statement);
        _firstProbe[original] = id;

        var exit = original is ReturnStatementSyntax or ThrowStatementSyntax ||
                   original is YieldStatementSyntax y && y.IsKind(SyntaxKind.YieldBreakStatement);
        if (exit && _functions.Count > 0 && _functions.Peek() is { } function)
        {
            function.Exits.Add(id);
        }
        return id;
    }

    private static TextSpan OwnSpan(StatementSyntax s) => s switch
    {
        IfStatementSyntax x => FromTo(x.IfKeyword, x.CloseParenToken),
        WhileStatementSyntax x => FromTo(x.WhileKeyword, x.CloseParenToken),
        ForStatementSyntax x => FromTo(x.ForKeyword, x.CloseParenToken),
        CommonForEachStatementSyntax x => FromTo(x.ForEachKeyword, x.CloseParenToken),
        DoStatementSyntax x => x.DoKeyword.Span,
        SwitchStatementSyntax x => FromTo(x.SwitchKeyword, x.CloseParenToken.IsKind(SyntaxKind.None) ? x.Expression.GetLastToken() : x.CloseParenToken),
        TryStatementSyntax x => x.TryKeyword.Span,
        UsingStatementSyntax x => FromTo(x.UsingKeyword, x.CloseParenToken),
        LockStatementSyntax x => FromTo(x.LockKeyword, x.CloseParenToken),
        FixedStatementSyntax x => FromTo(x.FixedKeyword, x.CloseParenToken),
        CheckedStatementSyntax x => x.Keyword.Span,
        UnsafeStatementSyntax x => x.UnsafeKeyword.Span,
        LabeledStatementSyntax x => OwnSpan(x.Statement),
        _ => s.Span
    };

    private static TextSpan FromTo(SyntaxToken first, SyntaxToken last) => TextSpan.FromBounds(first.SpanStart, last.Span.End);

    private int NewProbe(SyntaxNode original, TextSpan span, string kind)
    {
        var lines = original.SyntaxTree.GetLineSpan(span);
        var id = Map.Probes.Count;
        Map.Probes.Add(new ProbeEntry
        {
            Id = id,
            File = DisplayPath,
            Line = lines.StartLinePosition.Line + 1,
            EndLine = lines.EndLinePosition.Line + 1,
            Kind = kind
        });
        return id;
    }

    private static StatementSyntax ProbeStatement(int id) => ParseStatement($"{ProbeMethod}({id});");

    // switch statements: each section is a branch, and so is "no case matched"
    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        if (IsExcludedFromCoverage(node))
        {
            return base.VisitSwitchStatement(node);
        }

        var entry = new SwitchEntry { Id = Map.Switches.Count, File = DisplayPath, Line = Line(node) };
        Map.Switches.Add(entry);

        _switches.Push(entry);
        var rewritten = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;
        _switches.Pop();

        if (!node.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax)))
        {
            // no change to what the switch does: without a default, no match goes past the switch too
            var id = NewProbe(node, OwnSpan(node), ProbeEntry.NoCaseMatched);
            var section = SwitchSection(
                SingletonList<SwitchLabelSyntax>(DefaultSwitchLabel().WithTrailingTrivia(Space)),
                List(new[] { ProbeStatement(id).WithTrailingTrivia(Space), ParseStatement("break;").WithTrailingTrivia(Space) }));
            rewritten = rewritten.AddSections(section);
            entry.Labels.Add("no case matched");
            entry.Sections.Add(id);
        }
        return rewritten;
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var rewritten = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        if (IsExcludedFromCoverage(node))
        {
            return rewritten;
        }

        var statements = Probe(node.Statements, rewritten.Statements);
        if (_switches.Count > 0)
        {
            var first = node.Statements.FirstOrDefault(_firstProbe.ContainsKey);
            var entry = _switches.Peek();
            entry.Labels.Add(string.Join(" ", node.Labels.Select(l => l.ToString().Trim())));
            entry.Sections.Add(first != null ? _firstProbe[first] : -1);
        }
        return rewritten.WithStatements(statements);
    }

    // lambdas: probes in block bodies, but not functions of their own
    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) =>
        InLambda(() => base.VisitParenthesizedLambdaExpression(node));

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) =>
        InLambda(() => base.VisitSimpleLambdaExpression(node));

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) =>
        InLambda(() => base.VisitAnonymousMethodExpression(node));

    private SyntaxNode? InLambda(Func<SyntaxNode?> visit)
    {
        _functions.Push(null);
        var result = visit();
        _functions.Pop();
        return result;
    }

    // functions

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (!BeginFunction(node, node.Body, node.ExpressionBody, out var function))
        {
            return base.VisitMethodDeclaration(node);
        }
        var r = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        var body = FunctionBody(node.Body, node.ExpressionBody, r.Body, r.ExpressionBody, r.SemicolonToken, ReturnsValue(node), function);
        return r.WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        if (!BeginFunction(node, node.Body, node.ExpressionBody, out var function))
        {
            return base.VisitConstructorDeclaration(node);
        }
        var r = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        var body = FunctionBody(node.Body, node.ExpressionBody, r.Body, r.ExpressionBody, r.SemicolonToken, false, function);
        return r.WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node)
    {
        if (!BeginFunction(node, node.Body, node.ExpressionBody, out var function))
        {
            return base.VisitDestructorDeclaration(node);
        }
        var r = (DestructorDeclarationSyntax)base.VisitDestructorDeclaration(node)!;
        var body = FunctionBody(node.Body, node.ExpressionBody, r.Body, r.ExpressionBody, r.SemicolonToken, false, function);
        return r.WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        if (!BeginFunction(node, node.Body, node.ExpressionBody, out var function))
        {
            return base.VisitOperatorDeclaration(node);
        }
        var r = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;
        var body = FunctionBody(node.Body, node.ExpressionBody, r.Body, r.ExpressionBody, r.SemicolonToken, ReturnsValue(node), function);
        return r.WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        if (!BeginFunction(node, node.Body, node.ExpressionBody, out var function))
        {
            return base.VisitConversionOperatorDeclaration(node);
        }
        var r = (ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node)!;
        var body = FunctionBody(node.Body, node.ExpressionBody, r.Body, r.ExpressionBody, r.SemicolonToken, true, function);
        return r.WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
    {
        if (!BeginFunction(node, node.Body, node.ExpressionBody, out var function))
        {
            return base.VisitAccessorDeclaration(node);
        }
        var r = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
        var body = FunctionBody(node.Body, node.ExpressionBody, r.Body, r.ExpressionBody, r.SemicolonToken, ReturnsValue(node), function);
        return r.WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        if (!BeginFunction(node, node.Body, node.ExpressionBody, out var function))
        {
            return base.VisitLocalFunctionStatement(node);
        }
        var r = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        var body = FunctionBody(node.Body, node.ExpressionBody, r.Body, r.ExpressionBody, r.SemicolonToken, ReturnsValue(node), function);
        return r.WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
    }

    // int X => e;  ->  int X { get { P(id); return e; } }
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (node.ExpressionBody == null || !BeginFunction(node, null, node.ExpressionBody, out var function, ".get"))
        {
            return base.VisitPropertyDeclaration(node);
        }
        var r = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        var body = FunctionBody(null, node.ExpressionBody, null, r.ExpressionBody, r.SemicolonToken, true, function);
        return r.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(Getter(body, r.SemicolonToken));
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        if (node.ExpressionBody == null || !BeginFunction(node, null, node.ExpressionBody, out var function, ".get"))
        {
            return base.VisitIndexerDeclaration(node);
        }
        var r = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;
        var body = FunctionBody(null, node.ExpressionBody, null, r.ExpressionBody, r.SemicolonToken, true, function);
        return r.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(Getter(body, r.SemicolonToken));
    }

    private static AccessorListSyntax Getter(BlockSyntax body, SyntaxToken semicolon) =>
        AccessorList(SingletonList(
                AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, body.WithCloseBraceToken(body.CloseBraceToken.WithTrailingTrivia(Space)))
                    .WithKeyword(Token(SyntaxKind.GetKeyword).WithTrailingTrivia(Space))))
            .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(Space).WithTrailingTrivia(Space))
            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(semicolon.TrailingTrivia));

    // top-level statements: a function of their own
    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        if (!node.Members.Any(m => m is GlobalStatementSyntax))
        {
            return base.VisitCompilationUnit(node);
        }

        var first = node.Members.OfType<GlobalStatementSyntax>().First();
        var function = new FunctionEntry { Id = Map.Functions.Count, File = DisplayPath, Line = Line(first), Name = "<top-level statements>" };
        Map.Functions.Add(function);
        _functions.Push(function);

        var r = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
        var members = new List<MemberDeclarationSyntax>();
        for (var i = 0; i < node.Members.Count; i++)
        {
            if (node.Members[i] is GlobalStatementSyntax g && ShouldProbe(g.Statement))
            {
                var id = ProbeFor(g.Statement);
                if (function.Entry < 0)
                {
                    function.Entry = id;
                }
                members.Add(GlobalStatement(ProbeStatement(id)).WithLeadingTrivia(r.Members[i].GetLeadingTrivia()).WithTrailingTrivia(Space));
                members.Add(r.Members[i].WithLeadingTrivia());
            }
            else
            {
                members.Add(r.Members[i]);
            }
        }

        _functions.Pop();
        return r.WithMembers(List(members));
    }

    private bool BeginFunction(SyntaxNode node, BlockSyntax? body, ArrowExpressionClauseSyntax? arrow, out FunctionEntry function, string suffix = "")
    {
        function = null!;
        if (body == null && arrow == null || IsExcludedFromCoverage(node))
        {
            return false;
        }

        var symbol = Model.GetDeclaredSymbol(node);
        function = new FunctionEntry
        {
            Id = Map.Functions.Count,
            File = DisplayPath,
            Line = Line(node),
            Name = (symbol?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat) ?? node.ToString()) + suffix
        };
        Map.Functions.Add(function);
        _functions.Push(function);
        return true;
    }

    // the function's body, with its entry and exits; ends the function begun with BeginFunction
    private BlockSyntax FunctionBody(
        BlockSyntax? originalBody, ArrowExpressionClauseSyntax? originalArrow,
        BlockSyntax? body, ArrowExpressionClauseSyntax? arrow, SyntaxToken semicolon, bool returnsValue, FunctionEntry function)
    {
        _functions.Pop();

        if (arrow != null)
        {
            var id = NewProbe(originalArrow!, originalArrow!.Expression.Span, ProbeEntry.Statement);
            function.Entry = id;
            function.Exits.Add(id);

            var e = arrow.Expression;
            StatementSyntax statement = e is ThrowExpressionSyntax t
                ? ThrowStatement(Token(SyntaxKind.ThrowKeyword).WithTrailingTrivia(Space), t.Expression.WithoutLeadingTrivia(), Token(SyntaxKind.SemicolonToken))
                : returnsValue
                    ? ReturnStatement(Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(Space), e.WithoutLeadingTrivia(), Token(SyntaxKind.SemicolonToken))
                    : ExpressionStatement(e.WithoutLeadingTrivia());

            // { keeps what was after =>: a line break there stays a line break
            return Block(List(new[] { ProbeStatement(id).WithLeadingTrivia(e.GetLeadingTrivia()).WithTrailingTrivia(Space), statement.WithTrailingTrivia(Space) }))
                .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(arrow.ArrowToken.LeadingTrivia).WithTrailingTrivia(arrow.ArrowToken.TrailingTrivia))
                .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(semicolon.TrailingTrivia));
        }

        if (_firstProbe.TryGetValue(originalBody!, out var entry))
        {
            function.Entry = entry;
        }

        if (!EndIsReachable(originalBody!))
        {
            return body!;
        }

        var end = NewProbe(originalBody!, originalBody!.CloseBraceToken.Span, ProbeEntry.End);
        function.Exits.Add(end);
        if (function.Entry < 0)
        {
            function.Entry = end;
        }
        var probe = ProbeStatement(end).WithLeadingTrivia(body!.CloseBraceToken.LeadingTrivia).WithTrailingTrivia(Space);
        if (body.Statements.Count == 0)
        {
            probe = probe.WithLeadingTrivia(Space);
        }
        return body.WithStatements(body.Statements.Add(probe))
            .WithCloseBraceToken(body.CloseBraceToken.WithLeadingTrivia());
    }

    private bool EndIsReachable(BlockSyntax body)
    {
        if (body.Statements.Count == 0)
        {
            return true;
        }
        try
        {
            return Model.AnalyzeControlFlow(body).EndPointIsReachable;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private bool ReturnsValue(SyntaxNode node)
    {
        if (Model.GetDeclaredSymbol(node) is not IMethodSymbol method)
        {
            return true;
        }
        if (method.ReturnsVoid)
        {
            return false;
        }
        // async Task M() => await X(); is a statement, not a return
        return !(method.IsAsync && method.ReturnType is INamedTypeSymbol { Arity: 0 } type &&
                 type.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask");
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
