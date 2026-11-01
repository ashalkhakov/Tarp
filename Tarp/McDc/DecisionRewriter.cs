using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using Tarp.Types;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Tarp.McDc;

/*
 * Rewrites every condition of every decision, e.g. the conditions of
 *
 *   o is string s && s.Length > 0
 *
 * into (one line each in the output)
 *
 *   ((o is string s) && global::TarpGen.McDc.Yes0(out var __tarp17, 17, true, 0) || global::TarpGen.McDc.No0(out __tarp17, 17, false, 1) && false)
 *   ((s.Length > 0) && global::TarpGen.McDc.Yes(ref __tarp17, 17, 1, true, 2) || global::TarpGen.McDc.No(ref __tarp17, 17, 1, false, 1) && false)
 *
 * Yes returns true and No returns false, so each condition keeps its value; both record it in the
 * evaluation's frame, the local __tarp17 (declared by the first condition's marker, which is always
 * evaluated first). The marker whose value completes the decision records the evaluation, with the
 * outcome, which is known statically (McDcFunctions.DecisionOutcome): "done" is 0 if the value doesn't
 * complete the decision, else 1 + the outcome.
 *
 * Nothing wraps the decision, and "&& false" tells the compiler that the second branch never makes the
 * condition true: so the compiler still knows what each condition guarantees, e.g. that s is assigned
 * when "o is string s" is true. For a condition whose pattern variables are assigned when it's false
 * (o is not string s), the mirror image keeps that instead:
 *
 *   !(!(o is not string s) && Yes0(out var __tarp17, 17, false, ...) || No0(out __tarp17, 17, true, ...) && false)
 *
 * The frame being a local, an evaluation can span an await, threads, and recursive calls.
 *
 * Decisions are:
 * - maximal &&/|| expressions (anywhere, including nested in a condition, e.g. in a lambda)
 * - the controlling expression of if/while/do/for/?:, even with a single condition
 *
 * Decisions we can't instrument without changing the program's meaning (or making it not compile) are
 * left alone and listed in the map as skipped, with the reason.
 */
public class DecisionRewriter : CSharpSyntaxRewriter
{
    // the runtime's bit vectors and McDcFunctions' masks (2 bits per condition in an int)
    public const int MaxConditions = 16;

    private const string Runtime = "global::TarpGen.McDc";

    private readonly SemanticModel _model;
    private readonly McDcMap _map;
    private readonly string _displayPath;

    protected SemanticModel Model => _model;
    protected McDcMap Map => _map;
    protected string DisplayPath => _displayPath;
    // decisions not to instrument, by the start of their span, with the reason
    private readonly IReadOnlyDictionary<int, string> _excluded;

    public DecisionRewriter(SemanticModel model, McDcMap map, string displayPath, IReadOnlyDictionary<int, string>? excluded = null)
        : base(visitIntoStructuredTrivia: false)
    {
        _model = model;
        _map = map;
        _displayPath = displayPath;
        _excluded = excluded ?? new Dictionary<int, string>();
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (node is ExpressionSyntax e && IsDecisionRoot(e) && !IsExcludedFromCoverage(e))
        {
            return InstrumentDecision(e);
        }
        return base.Visit(node);
    }

    private readonly Dictionary<SyntaxNode, bool> _excludedMembers = new();

    // [ExcludeFromCodeCoverage] on the member, accessor, local function or type the node is in (or is)
    protected bool IsExcludedFromCoverage(SyntaxNode e)
    {
        foreach (var a in e.AncestorsAndSelf())
        {
            if (a is not (MemberDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }
            if (!_excludedMembers.TryGetValue(a, out var excluded))
            {
                var symbol = _model.GetDeclaredSymbol(a);
                excluded = symbol != null && symbol.GetAttributes().Any(x =>
                    x.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute");
                _excludedMembers[a] = excluded;
            }
            if (excluded)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsDecisionRoot(ExpressionSyntax e)
    {
        if (DecisionTree.IsStructural(e) && DecisionTree.ContainsLogical(e))
        {
            // the topmost node of the tree; the inner ones are handled together with it
            return !(e.Parent is ExpressionSyntax p && DecisionTree.IsStructural(p));
        }
        return IsControllingExpression(e);
    }

    private static bool IsControllingExpression(ExpressionSyntax e) => e.Parent switch
    {
        IfStatementSyntax s => s.Condition == e,
        WhileStatementSyntax s => s.Condition == e,
        DoStatementSyntax s => s.Condition == e,
        ForStatementSyntax s => s.Condition == e,
        ConditionalExpressionSyntax c => c.Condition == e,
        // case ... when c:, arm when c =>, catch (...) when (c)
        WhenClauseSyntax w => w.Condition == e,
        CatchFilterClauseSyntax f => f.FilterExpression == e,
        _ => false
    };

    private ExpressionSyntax InstrumentDecision(ExpressionSyntax e)
    {
        var conditions = new List<ExpressionSyntax>();
        var tree = DecisionTree.Build(e, conditions);

        var lineSpan = e.GetLocation().GetLineSpan().StartLinePosition;
        var reason = _excluded.TryGetValue(e.SpanStart, out var excludedReason) ? excludedReason : WhyNotInstrumentable(e, conditions);
        if (reason != null)
        {
            _map.Skipped.Add(new SkippedDecision
            {
                File = _displayPath,
                Line = lineSpan.Line + 1,
                Column = lineSpan.Character + 1,
                SpanStart = e.SpanStart,
                Text = e.ToString(),
                Reason = reason
            });
            var index0 = 0;
            return Rewrite(e, null, null, ref index0);
        }

        var id = _map.Decisions.Count;
        _map.Decisions.Add(new DecisionEntry
        {
            Id = id,
            File = _displayPath,
            Line = lineSpan.Line + 1,
            Column = lineSpan.Character + 1,
            SpanStart = e.SpanStart,
            Text = e.ToString(),
            Conditions = conditions.Select(c => c.ToString()).ToList()
        });

        var markers = new McDcFunctions().CreateMarkers(tree);
        var index = 0;
        return Rewrite(e, id, markers, ref index);
    }

    // rewrites the structure of the decision, adding markers to each condition (unless id is null)
    private ExpressionSyntax Rewrite(ExpressionSyntax e, int? id, IReadOnlyList<MarkerFunction>? markers, ref int index)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return p.WithExpression(Rewrite(p.Expression, id, markers, ref index));

            case BinaryExpressionSyntax b when DecisionTree.IsLogical(b):
                {
                    var l = Rewrite(b.Left, id, markers, ref index);
                    var r = Rewrite(b.Right, id, markers, ref index);
                    return b.WithLeft(l).WithRight(r);
                }

            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                return u.WithOperand(Rewrite(u.Operand, id, markers, ref index));

            default:
                {
                    // conditions may contain decisions of their own (e.g. in a lambda), so visit them too
                    var condition = (ExpressionSyntax)base.Visit(e)!;
                    if (id == null)
                    {
                        return condition;
                    }

                    var i = index++;
                    return Mark(e, condition, id.Value, i, markers![i]);
                }
        }
    }

    // condition: the original, to look at; rewritten: what goes into the output
    private static ExpressionSyntax Mark(ExpressionSyntax condition, ExpressionSyntax rewritten, int id, int i, MarkerFunction marker)
    {
        var frame = "__tarp" + id;

        string Call(string name, bool value, bool declare)
        {
            var outcome = McDcFunctions.DecisionOutcome(marker.Path, value);
            var done = outcome == null ? 0 : outcome.Value ? 2 : 1;
            var v = value ? "true" : "false";
            return i == 0
                ? $"{Runtime}.{name}0(out {(declare ? "var " : "")}{frame}, {id}, {v}, {done})"
                : $"{Runtime}.{name}(ref {frame}, {id}, {i}, {v}, {done})";
        }

        var template = AssignsWhenFalse(condition)
            ? $"!(!__tarpCondition && {Call("Yes", false, true)} || {Call("No", true, false)} && false)"
            : $"(__tarpCondition && {Call("Yes", true, true)} || {Call("No", false, false)} && false)";

        var marked = ParseExpression(template);
        var placeholder = marked.DescendantNodes().OfType<IdentifierNameSyntax>().Single(n => n.Identifier.Text == "__tarpCondition");
        return marked
            .ReplaceNode(placeholder, ParenthesizedExpression(rewritten.WithoutTrivia()))
            .WithTriviaFrom(rewritten);
    }

    // o is not string s: s is assigned when the condition is false
    private static bool AssignsWhenFalse(ExpressionSyntax condition) =>
        condition is IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax not } &&
        not.IsKind(SyntaxKind.NotPattern) &&
        DescendantsOutsideNestedFunctions(not).Any(n => n is SingleVariableDesignationSyntax);

    private string? WhyNotInstrumentable(ExpressionSyntax e, List<ExpressionSyntax> conditions)
    {
        if (conditions.Count > MaxConditions)
        {
            return $"more than {MaxConditions} conditions";
        }

        // the markers declare the frame with "out var" in any expression, which needs C# 7.3
        if (e.SyntaxTree.Options is CSharpParseOptions { LanguageVersion: < LanguageVersion.CSharp7_3 })
        {
            return "needs C# 7.3 or later";
        }

        if (_model.GetConstantValue(e).HasValue)
        {
            return "constant decision";
        }

        // && / || / ! must be the built-in operators on bool, and every condition must convert to bool
        foreach (var n in e.DescendantNodesAndSelf(n => n == e || n is ExpressionSyntax x && DecisionTree.IsStructural(x)))
        {
            if (n is ExpressionSyntax x && DecisionTree.IsStructural(x) && x is not ParenthesizedExpressionSyntax &&
                _model.GetTypeInfo(x).Type?.SpecialType != SpecialType.System_Boolean)
            {
                return "not a bool expression";
            }
        }
        foreach (var c in conditions)
        {
            // also skips dynamic conditions: their converted type is dynamic
            if (_model.GetTypeInfo(c).ConvertedType?.SpecialType != SpecialType.System_Boolean)
            {
                return "a condition is not a bool";
            }
            if (_model.GetConstantValue(c).HasValue)
            {
                // also, wrapping it would change reachability, e.g. while (true || x) { }
                return "constant condition";
            }
        }

        foreach (var a in e.Ancestors())
        {
            if (a is QueryExpressionSyntax)
            {
                return "in a query expression";
            }
            if (a is AnonymousFunctionExpressionSyntax f && IsExpressionTree(f))
            {
                return "in an expression tree lambda";
            }
        }

        return null;
    }

    // in a lambda that becomes an expression tree, or a query (which may be one): no calls to the runtime there
    protected bool IsInExpressionTree(SyntaxNode node) =>
        node.AncestorsAndSelf().Any(a => a is QueryExpressionSyntax || a is AnonymousFunctionExpressionSyntax f && IsExpressionTree(f));

    protected bool IsExpressionTree(AnonymousFunctionExpressionSyntax f)
    {
        var type = _model.GetTypeInfo(f).ConvertedType;
        return type?.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>";
    }

    private static IEnumerable<SyntaxNode> DescendantsOutsideNestedFunctions(SyntaxNode node) =>
        node.DescendantNodesAndSelf(n => n == node || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

}
