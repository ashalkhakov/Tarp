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
 *   for "no case matched". A switch expression's are its arms: "when PT(id)" (or "when (c) && PT(id)")
 *   runs the probe when the arm is taken. A ??'s are its sides: PN(id, left) ?? PV(id, right).
 *   Branches of decisions are their outcomes (see McDcReport).
 * - Where there's no room for a statement, probes go in expressions, keeping what the compiler knows:
 *   do { } while (PF(id) && false || (c)); field and property initializers: = PV<Type>(id, value).
 * - Lambdas: probes in their bodies (an expression body becomes a block body, unless the lambda is
 *   an expression tree), but they aren't counted as functions.
 *
 * Nothing here adds a line break, so line numbers (and #line) stay right.
 */
public sealed class CoverageRewriter : DecisionRewriter
{
    private const string Runtime = "global::TarpGen.McDc";
    private const string ProbeMethod = Runtime + ".P";

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
            return Block(List(new[] { probe.WithTrailingTrivia(Space) }.Concat(WithForEachBranch(original, rewritten.WithoutTrivia()))))
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
                result.AddRange(WithForEachBranch(((LabeledStatementSyntax)o).Statement, labeled.Statement.WithLeadingTrivia(Space)));
            }
            else
            {
                result.Add(probe.WithLeadingTrivia(r.GetLeadingTrivia()).WithTrailingTrivia(Space));
                result.AddRange(WithForEachBranch(o, r.WithLeadingTrivia()));
            }
        }
        return List(result);
    }

    /*
     * foreach: "iterated" and "no iterations" branches
     *
     *   bool __tarpRan9 = false; foreach (...) { __tarpRan9 = true; ... } if (!__tarpRan9) P(9);
     *
     * all on the loop's lines. Other statements: as they are.
     */
    private IEnumerable<StatementSyntax> WithForEachBranch(StatementSyntax original, StatementSyntax rewritten)
    {
        if (original is not CommonForEachStatementSyntax loop || rewritten is not CommonForEachStatementSyntax r ||
            r.Statement is not BlockSyntax body || IsExcludedFromCoverage(original))
        {
            return new[] { rewritten };
        }

        var none = NewProbe(loop, OwnSpan(loop), ProbeEntry.Branch);
        var flag = "__tarpRan" + none;
        Map.Switches.Add(new SwitchEntry
        {
            Id = Map.Switches.Count,
            File = DisplayPath,
            Line = Line(loop),
            Kind = SwitchEntry.ForEach,
            Labels = { "iterated", "no iterations" },
            Sections = { _firstProbe.TryGetValue(loop.Statement, out var first) ? first : -1, none }
        });

        var set = ParseStatement($"{flag} = true;").WithTrailingTrivia(Space);
        body = body.Statements.Count == 0
            ? body.WithStatements(SingletonList(set.WithLeadingTrivia(Space)))
            : body.WithStatements(body.Statements
                .Replace(body.Statements[0], body.Statements[0].WithLeadingTrivia())
                .Insert(0, set.WithLeadingTrivia(body.Statements[0].GetLeadingTrivia())));

        return new[]
        {
            ParseStatement($"bool {flag} = false;").WithLeadingTrivia(r.GetLeadingTrivia()).WithTrailingTrivia(Space),
            r.WithStatement(body).WithLeadingTrivia().WithTrailingTrivia(Space),
            ParseStatement($"if (!{flag}) {ProbeMethod}({none});").WithTrailingTrivia(r.GetTrailingTrivia())
        };
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

        var guarded = CanProbeLabels(node);
        if (guarded)
        {
            rewritten = ProbeLabels(node, rewritten, entry);
        }

        // without a default, no match goes past the switch: a default that does the same changes nothing.
        // A switch that matches everything (switch (b) { case true: ... case false: ... }) has no such
        // branch, and the end can't be reached: a default that breaks would change that, so none is added;
        // with labels probed (guarded labels don't count for that), one that throws, which never runs
        var hasDefault = node.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
        if (!hasDefault && SwitchEndIsReachable(node))
        {
            var id = NewProbe(node, OwnSpan(node), ProbeEntry.NoCaseMatched);
            rewritten = rewritten.AddSections(DefaultSection(ProbeStatement(id), ParseStatement("break;")));
            entry.Labels.Add("no case matched");
            entry.Sections.Add(id);
            entry.NoMatchProbe = id;
        }
        else if (!hasDefault && guarded)
        {
            rewritten = rewritten.AddSections(DefaultSection(ParseStatement("throw new global::System.InvalidOperationException(\"Tarp: no case matched\");")));
        }
        else if (hasDefault && guarded)
        {
            // a default section of its own (CanProbeLabels): entered only when no case matched
            var defaultSection = node.Sections.First(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
            var first = defaultSection.Statements.FirstOrDefault(_firstProbe.ContainsKey);
            entry.NoMatchProbe = first != null ? _firstProbe[first] : -1;
        }
        return rewritten;
    }

    private static SwitchSectionSyntax DefaultSection(params StatementSyntax[] statements) =>
        SwitchSection(
            SingletonList<SwitchLabelSyntax>(DefaultSwitchLabel().WithTrailingTrivia(Space)),
            List(statements.Select(s => s.WithTrailingTrivia(Space))));

    private bool SwitchEndIsReachable(SwitchStatementSyntax node)
    {
        try
        {
            return Model.AnalyzeControlFlow(node)?.EndPointIsReachable ?? true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /*
     * Case labels as conditions (MC/DC over case labels): each case label gets a probe in its when,
     * run when the label matched. Not when the labels must stay as they are: goto case needs a
     * "case c:" label, and a section with both default and case labels wouldn't tell "no case matched"
     */
    private bool CanProbeLabels(SwitchStatementSyntax node) =>
        !node.DescendantNodes().OfType<GotoStatementSyntax>().Any(g => g.IsKind(SyntaxKind.GotoCaseStatement) &&
            g.Ancestors().OfType<SwitchStatementSyntax>().FirstOrDefault() == node) &&
        !node.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax) && s.Labels.Count > 1) &&
        !IsInExpressionTree(node);

    // case c:  ->  case c when PT(id):     case p when g:  ->  case p when (g) && PT(id):
    private SwitchStatementSyntax ProbeLabels(SwitchStatementSyntax original, SwitchStatementSyntax rewritten, SwitchEntry entry)
    {
        var sections = new List<SwitchSectionSyntax>();
        for (var s = 0; s < original.Sections.Count; s++)
        {
            var o = original.Sections[s];
            var r = rewritten.Sections[s];
            var probes = new List<int>();
            var labels = new List<SwitchLabelSyntax>();
            for (var l = 0; l < o.Labels.Count; l++)
            {
                if (o.Labels[l] is DefaultSwitchLabelSyntax)
                {
                    labels.Add(r.Labels[l]);
                    continue;
                }
                var id = NewProbe(o.Labels[l], o.Labels[l].Span, ProbeEntry.Branch);
                probes.Add(id);
                labels.Add(r.Labels[l] switch
                {
                    CaseSwitchLabelSyntax c => CasePatternSwitchLabel(
                            c.Keyword, ConstantPattern(c.Value.WithoutTrailingTrivia()),
                            WhenClause(ParseExpression($"{Runtime}.PT({id})")).WithWhenKeyword(Token(SyntaxKind.WhenKeyword).WithLeadingTrivia(Space).WithTrailingTrivia(Space)),
                            c.ColonToken),
                    CasePatternSwitchLabelSyntax { WhenClause: null } c => c
                        .WithPattern(c.Pattern.WithoutTrailingTrivia())
                        .WithWhenClause(WhenClause(ParseExpression($"{Runtime}.PT({id})")).WithWhenKeyword(Token(SyntaxKind.WhenKeyword).WithLeadingTrivia(Space).WithTrailingTrivia(Space))),
                    CasePatternSwitchLabelSyntax c => c.WithWhenClause(c.WhenClause!.WithCondition(Probed($"__tarpValue && {Runtime}.PT({id})", c.WhenClause.Condition))),
                    var other => other
                });
            }
            entry.LabelProbes.Add(probes);
            sections.Add(r.WithLabels(List(labels)));

            // a section with several case labels: a decision, its labels the conditions
            if (probes.Count > 1)
            {
                var lineSpan = o.GetLocation().GetLineSpan().StartLinePosition;
                var texts = o.Labels.Where(x => x is not DefaultSwitchLabelSyntax).Select(x => x.ToString().Trim().TrimEnd(':').TrimEnd()).ToList();
                Map.Decisions.Add(new DecisionEntry
                {
                    Id = Map.Decisions.Count,
                    File = DisplayPath,
                    Line = lineSpan.Line + 1,
                    Column = lineSpan.Character + 1,
                    SpanStart = o.SpanStart,
                    Text = string.Join(" ", o.Labels.Select(x => x.ToString().Trim())),
                    Shape = string.Join(" || ", texts.Select((_, i) => $"(c{i})")),
                    Conditions = texts,
                    SwitchId = entry.Id,
                    SwitchSection = s
                });
            }
        }
        return rewritten.WithSections(List(sections));
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

    /*
     * try with catches: each catch clause is a branch (its first statement ran), and so is "completed
     * without an exception" (a probe after the try block's last statement) when that end can be reached.
     * Not counted: which way a finally was entered, or a function left by an exception it didn't catch
     * (both would need handlers of Tarp's own around the code: see issue #9).
     */
    public override SyntaxNode? VisitTryStatement(TryStatementSyntax node)
    {
        var rewritten = (TryStatementSyntax)base.VisitTryStatement(node)!;
        if (node.Catches.Count == 0 || IsExcludedFromCoverage(node))
        {
            return rewritten;
        }

        var entry = new SwitchEntry { Id = Map.Switches.Count, File = DisplayPath, Line = Line(node), Kind = SwitchEntry.Try };
        Map.Switches.Add(entry);

        if (EndIsReachable(node.Block))
        {
            var id = NewProbe(node, node.Block.CloseBraceToken.Span, ProbeEntry.Branch);
            entry.Labels.Add("completed without an exception");
            entry.Sections.Add(id);
            rewritten = rewritten.WithBlock(Append(rewritten.Block, ProbeStatement(id)));
        }
        foreach (var c in node.Catches)
        {
            entry.Labels.Add("catch" + (c.Declaration != null ? " " + c.Declaration : "") + (c.Filter != null ? " " + c.Filter : ""));
            entry.Sections.Add(_firstProbe.TryGetValue(c.Block, out var first) ? first : -1);
        }
        return rewritten;
    }

    // a statement at the end of a block, on the line of its }
    private static BlockSyntax Append(BlockSyntax block, StatementSyntax statement)
    {
        var last = statement.WithLeadingTrivia(block.Statements.Count == 0 ? TriviaList(Space) : block.CloseBraceToken.LeadingTrivia).WithTrailingTrivia(Space);
        return block.WithStatements(block.Statements.Add(last)).WithCloseBraceToken(block.CloseBraceToken.WithLeadingTrivia());
    }

    // lambdas: probes in block bodies, but not functions of their own
    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) =>
        InLambda(node, () => base.VisitParenthesizedLambdaExpression(node));

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) =>
        InLambda(node, () => base.VisitSimpleLambdaExpression(node));

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) =>
        InLambda(node, () => base.VisitAnonymousMethodExpression(node));

    // x => e  ->  x => { P(id); return e; }
    private SyntaxNode? InLambda(AnonymousFunctionExpressionSyntax node, Func<SyntaxNode?> visit)
    {
        _functions.Push(null);
        var result = visit();
        _functions.Pop();

        if (node.ExpressionBody is not { } body || result is not AnonymousFunctionExpressionSyntax lambda || lambda.ExpressionBody is not { } rewritten ||
            IsExcludedFromCoverage(node) || IsInExpressionTree(node) || Model.GetSymbolInfo(node).Symbol is not IMethodSymbol method)
        {
            return result;
        }

        var id = NewProbe(node, body.Span, ProbeEntry.Statement);
        var block = Block(List(new[] { ProbeStatement(id).WithTrailingTrivia(Space), StatementFor(rewritten, ReturnsValue(method)).WithTrailingTrivia(Space) }))
            .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(rewritten.GetLeadingTrivia()).WithTrailingTrivia(Space))
            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(rewritten.GetTrailingTrivia()));
        return lambda.WithExpressionBody(null).WithBlock(block);
    }

    // the statement for an expression body: return e; or e; (or throw ...;)
    private static StatementSyntax StatementFor(ExpressionSyntax e, bool returnsValue) => e is ThrowExpressionSyntax t
        ? ThrowStatement(Token(SyntaxKind.ThrowKeyword).WithTrailingTrivia(Space), t.Expression.WithoutTrivia(), Token(SyntaxKind.SemicolonToken))
        : returnsValue
            ? ReturnStatement(Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(Space), e.WithoutTrivia(), Token(SyntaxKind.SemicolonToken))
            : ExpressionStatement(e.WithoutTrivia());

    // do { } while (c);  ->  do { } while (PF(id) && false || (c));  "&& false": what the compiler knows after c stays
    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var rewritten = (DoStatementSyntax)base.VisitDoStatement(node)!;
        if (IsExcludedFromCoverage(node))
        {
            return rewritten;
        }
        var id = NewProbe(node, FromTo(node.WhileKeyword, node.CloseParenToken), ProbeEntry.Statement);
        return rewritten.WithCondition(Probed($"({Runtime}.PF({id}) && false || __tarpValue)", rewritten.Condition));
    }

    // template with __tarpValue in it, with (value) for it
    private static ExpressionSyntax Probed(string template, ExpressionSyntax value)
    {
        var e = ParseExpression(template);
        var placeholder = e.DescendantNodes().OfType<IdentifierNameSyntax>().Single(n => n.Identifier.Text == "__tarpValue");
        return e.ReplaceNode(placeholder, ParenthesizedExpression(value.WithoutTrivia())).WithTriviaFrom(value);
    }

    // switch expressions: each arm is a branch, and its lines
    public override SyntaxNode? VisitSwitchExpression(SwitchExpressionSyntax node)
    {
        if (IsExcludedFromCoverage(node))
        {
            return base.VisitSwitchExpression(node);
        }

        var entry = new SwitchEntry { Id = Map.Switches.Count, File = DisplayPath, Line = Line(node), Kind = SwitchEntry.Expression };
        Map.Switches.Add(entry);

        var rewritten = (SwitchExpressionSyntax)base.VisitSwitchExpression(node)!;
        var arms = new List<SwitchExpressionArmSyntax>();
        for (var i = 0; i < node.Arms.Count; i++)
        {
            var o = node.Arms[i];
            var a = rewritten.Arms[i];
            var id = NewProbe(o, o.Span, ProbeEntry.Statement);
            entry.Labels.Add(o.Pattern + (o.WhenClause != null ? " " + o.WhenClause : ""));
            entry.Sections.Add(id);

            // the arm's lines are its own (its probe is the innermost), not those of the statement around it
            var when = a.WhenClause == null
                ? WhenClause(ParseExpression($"{Runtime}.PT({id})"))
                    .WithWhenKeyword(Token(SyntaxKind.WhenKeyword).WithLeadingTrivia(Space).WithTrailingTrivia(Space))
                    .WithTrailingTrivia(a.Pattern.GetTrailingTrivia())
                : a.WhenClause.WithCondition(Probed($"__tarpValue && {Runtime}.PT({id})", a.WhenClause.Condition));
            arms.Add(a.WhenClause == null ? a.WithPattern(a.Pattern.WithoutTrailingTrivia()).WithWhenClause(when) : a.WithWhenClause(when));
        }
        return rewritten.WithArms(SeparatedList(arms, rewritten.Arms.GetSeparators()));
    }

    // a ?? b  ->  PN<A>(id, a) ?? PV<B>(id2, b): left not null, right used
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var result = base.VisitBinaryExpression(node);
        if (!node.IsKind(SyntaxKind.CoalesceExpression) || result is not BinaryExpressionSyntax rewritten ||
            node.Right is ThrowExpressionSyntax || IsExcludedFromCoverage(node) || IsInExpressionTree(node) || !CanCallHere(node))
        {
            return result;
        }

        var left = TypeArgument(Model.GetTypeInfo(node.Left).Type);
        var right = TypeArgument(Model.GetTypeInfo(node.Right).ConvertedType);
        if (left == null || right == null)
        {
            return result;
        }

        var leftId = NewProbe(node.Left, node.Left.Span, ProbeEntry.Branch);
        var rightId = NewProbe(node.Right, node.Right.Span, ProbeEntry.Branch);
        Map.Switches.Add(new SwitchEntry
        {
            Id = Map.Switches.Count,
            File = DisplayPath,
            Line = Line(node),
            Kind = SwitchEntry.Coalesce,
            Labels = { "left side not null", "right side used" },
            Sections = { leftId, rightId }
        });

        return rewritten
            .WithLeft(Probed($"{Runtime}.PN<{left}>({leftId}, __tarpValue)", rewritten.Left))
            .WithRight(Probed($"{Runtime}.PV<{right}>({rightId}, __tarpValue)", rewritten.Right));
    }

    // field and event initializers: = PV<Type>(id, value), so that their lines count
    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var rewritten = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword) || m.IsKind(SyntaxKind.FixedKeyword)))
        {
            return rewritten;
        }
        return rewritten.WithDeclaration(ProbedVariables(node, node.Declaration, rewritten.Declaration));
    }

    public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        var rewritten = (EventFieldDeclarationSyntax)base.VisitEventFieldDeclaration(node)!;
        return rewritten.WithDeclaration(ProbedVariables(node, node.Declaration, rewritten.Declaration));
    }

    private VariableDeclarationSyntax ProbedVariables(MemberDeclarationSyntax member, VariableDeclarationSyntax original, VariableDeclarationSyntax rewritten)
    {
        if (IsExcludedFromCoverage(member))
        {
            return rewritten;
        }

        var variables = new List<VariableDeclaratorSyntax>();
        for (var i = 0; i < original.Variables.Count; i++)
        {
            var o = original.Variables[i];
            var v = rewritten.Variables[i];
            var value = o.Initializer != null && v.Initializer != null ? ProbedInitializer(o.Initializer, v.Initializer.Value, original.Type) : null;
            variables.Add(value != null ? v.WithInitializer(v.Initializer!.WithValue(value)) : v);
        }
        return rewritten.WithVariables(SeparatedList(variables, rewritten.Variables.GetSeparators()));
    }

    // PV<Type>(id, value), with the declared type (target-typed values keep working); an array
    // initializer { 1, 2 } is no argument, so it becomes new Type { 1, 2 }; null if it can't be probed
    private ExpressionSyntax? ProbedInitializer(EqualsValueClauseSyntax original, ExpressionSyntax value, TypeSyntax type)
    {
        if (TypeArgument(Model.GetTypeInfo(type).Type) == null)
        {
            return null;
        }
        if (value is InitializerExpressionSyntax arrayInitializer)
        {
            if (type is not ArrayTypeSyntax arrayType)
            {
                return null;
            }
            value = ArrayCreationExpression(Token(SyntaxKind.NewKeyword).WithTrailingTrivia(Space), arrayType.WithoutTrivia(), arrayInitializer.WithoutTrivia())
                .WithTriviaFrom(arrayInitializer);
        }
        var id = NewProbe(original, original.Value.Span, ProbeEntry.Statement);
        return Probed($"{Runtime}.PV<{type.WithoutTrivia()}>({id}, __tarpValue)", value);
    }

    // : base(a, ...), : this(a, ...), class C(...) : B(a, ...): PV<A>(id, a), so that their lines count
    public override SyntaxNode? VisitConstructorInitializer(ConstructorInitializerSyntax node)
    {
        var rewritten = (ConstructorInitializerSyntax)base.VisitConstructorInitializer(node)!;
        var arguments = ProbedArguments(node, node.ArgumentList, rewritten.ArgumentList);
        return arguments != null ? rewritten.WithArgumentList(arguments) : rewritten;
    }

    public override SyntaxNode? VisitPrimaryConstructorBaseType(PrimaryConstructorBaseTypeSyntax node)
    {
        var rewritten = (PrimaryConstructorBaseTypeSyntax)base.VisitPrimaryConstructorBaseType(node)!;
        var arguments = ProbedArguments(node, node.ArgumentList, rewritten.ArgumentList);
        return arguments != null ? rewritten.WithArgumentList(arguments) : rewritten;
    }

    // probes the first argument (it runs first); null when there's none, or it's passed by reference
    private ArgumentListSyntax? ProbedArguments(SyntaxNode original, ArgumentListSyntax originalArguments, ArgumentListSyntax rewritten)
    {
        if (originalArguments.Arguments.Count == 0 || IsExcludedFromCoverage(original))
        {
            return null;
        }
        var first = originalArguments.Arguments[0];
        var type = TypeArgument(Model.GetTypeInfo(first.Expression).ConvertedType);
        if (!first.RefKindKeyword.IsKind(SyntaxKind.None) || type == null)
        {
            return null;
        }
        var id = NewProbe(original, original.Span, ProbeEntry.Statement);
        var argument = rewritten.Arguments[0];
        return rewritten.WithArguments(rewritten.Arguments.Replace(argument,
            argument.WithExpression(Probed($"{Runtime}.PV<{type}>({id}, __tarpValue)", argument.Expression))));
    }

    // a?.b, a?[i]: PNN<A>(id, id2, a)?.b, "not null" and "null" branches (an inner ?. has no receiver of its own)
    public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        var result = base.VisitConditionalAccessExpression(node);
        if (result is not ConditionalAccessExpressionSyntax rewritten || node.Expression is MemberBindingExpressionSyntax or ElementBindingExpressionSyntax ||
            IsExcludedFromCoverage(node) || IsInExpressionTree(node) || !CanCallHere(node))
        {
            return result;
        }
        var type = TypeArgument(Model.GetTypeInfo(node.Expression).Type);
        if (type == null)
        {
            return result;
        }

        var notNull = NewProbe(node.Expression, node.Expression.Span, ProbeEntry.Branch);
        var isNull = NewProbe(node.Expression, node.Expression.Span, ProbeEntry.Branch);
        Map.Switches.Add(new SwitchEntry
        {
            Id = Map.Switches.Count,
            File = DisplayPath,
            Line = Line(node),
            Kind = SwitchEntry.ConditionalAccess,
            Labels = { "not null", "null" },
            Sections = { notNull, isNull }
        });
        return rewritten.WithExpression(Probed($"{Runtime}.PNN<{type}>({notNull}, {isNull}, __tarpValue)", rewritten.Expression));
    }

    // a type as a type argument: null when it can't be one, or can't be written (anonymous types)
    private static string? TypeArgument(ITypeSymbol? type)
    {
        static bool Ok(ITypeSymbol t) => t switch
        {
            IErrorTypeSymbol or IPointerTypeSymbol or IFunctionPointerTypeSymbol => false,
            { IsAnonymousType: true } or { IsRefLikeType: true } => false,
            IArrayTypeSymbol a => Ok(a.ElementType),
            INamedTypeSymbol n => n.TypeArguments.All(Ok),
            _ => true
        };

        if (type == null || !Ok(type))
        {
            return null;
        }
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
    }

    // no calls in attribute arguments, parameter defaults or constants
    private bool CanCallHere(ExpressionSyntax e) =>
        !Model.GetConstantValue(e).HasValue &&
        !e.Ancestors().Any(a => a is AttributeArgumentSyntax || a is EqualsValueClauseSyntax { Parent: ParameterSyntax } ||
                                a is LocalDeclarationStatementSyntax { IsConst: true } ||
                                a is FieldDeclarationSyntax f && f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)));

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
        if (node.Initializer != null)
        {
            // int X { get; } = e;  ->  = PV<int>(id, e);
            var initialized = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
            var value = IsExcludedFromCoverage(node) || initialized.Initializer == null
                ? null
                : ProbedInitializer(node.Initializer, initialized.Initializer.Value, node.Type);
            return value != null ? initialized.WithInitializer(initialized.Initializer!.WithValue(value)) : initialized;
        }
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
                var statement = ((GlobalStatementSyntax)r.Members[i]).Statement.WithLeadingTrivia();
                members.AddRange(WithForEachBranch(g.Statement, statement).Select(s => GlobalStatement(s)));
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
            var statement = StatementFor(e, returnsValue);

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
            return Model.AnalyzeControlFlow(body)?.EndPointIsReachable ?? true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private bool ReturnsValue(SyntaxNode node) =>
        Model.GetDeclaredSymbol(node) is not IMethodSymbol method || ReturnsValue(method);

    private static bool ReturnsValue(IMethodSymbol method)
    {
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
