using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
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

    /*
     * What's an operator of a decision (DO-178C: any operator whose operands and result are bools), and
     * what's a condition: && || !, and on bools & | ^ == != and ?:. An "is" with and/or/not patterns
     * is an operator too: its primitive patterns are conditions (see RewritePattern).
     */
    private enum Operator { None, Parentheses, Logical, Not, BoolBinary, BoolConditional, Patterns }

    private Operator OperatorOf(ExpressionSyntax e) => e switch
    {
        ParenthesizedExpressionSyntax => Operator.Parentheses,
        BinaryExpressionSyntax b when DecisionTree.IsLogical(b) => Operator.Logical,
        PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression) => Operator.Not,
        // with a constant side (x == true, a ? b : false), a condition (or a decision on c) as before
        BinaryExpressionSyntax b when DecisionTree.IsBoolOperatorKind(b.Kind()) && IsBool(b.Left) && IsBool(b.Right) &&
                                      !IsConstant(b.Left) && !IsConstant(b.Right) => Operator.BoolBinary,
        ConditionalExpressionSyntax c when IsBool(c.WhenTrue) && IsBool(c.WhenFalse) &&
                                           !IsConstant(c.WhenTrue) && !IsConstant(c.WhenFalse) => Operator.BoolConditional,
        IsPatternExpressionSyntax p when CanDecompose(p) => Operator.Patterns,
        _ => Operator.None
    };

    private bool IsBool(ExpressionSyntax e) => _model.GetTypeInfo(e).Type?.SpecialType == SpecialType.System_Boolean;

    private bool IsConstant(ExpressionSyntax e) => _model.GetConstantValue(e).HasValue;

    // an operator other than parentheses and ! in the tree
    private bool HasOperator(ExpressionSyntax e) => OperatorOf(e) switch
    {
        Operator.None => false,
        Operator.Parentheses => HasOperator(((ParenthesizedExpressionSyntax)e).Expression),
        Operator.Not => HasOperator(((PrefixUnaryExpressionSyntax)e).Operand),
        _ => true
    };

    private bool IsDecisionRoot(ExpressionSyntax e)
    {
        if (OperatorOf(e) != Operator.None && HasOperator(e))
        {
            // the topmost node of the tree; the inner ones are handled together with it
            return !(e.Parent is ExpressionSyntax p && OperatorOf(p) != Operator.None);
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

    // a condition: an expression, or a primitive pattern of an "is" (Text says what it tests)
    private sealed record Condition(SyntaxNode Node, string Text);

    // the decision's shape (c0, c1, ... for its conditions, see DecisionEntry.Shape) and its conditions, left to right
    private string Structure(ExpressionSyntax e, List<Condition> conditions)
    {
        switch (OperatorOf(e))
        {
            case Operator.Parentheses:
                return Structure(((ParenthesizedExpressionSyntax)e).Expression, conditions);
            case Operator.Logical or Operator.BoolBinary:
                {
                    var b = (BinaryExpressionSyntax)e;
                    var l = Structure(b.Left, conditions);
                    var r = Structure(b.Right, conditions);
                    return $"({l}) {b.OperatorToken.Text} ({r})";
                }
            case Operator.Not:
                return $"!({Structure(((PrefixUnaryExpressionSyntax)e).Operand, conditions)})";
            case Operator.BoolConditional:
                {
                    var c = (ConditionalExpressionSyntax)e;
                    var condition = Structure(c.Condition, conditions);
                    var whenTrue = Structure(c.WhenTrue, conditions);
                    var whenFalse = Structure(c.WhenFalse, conditions);
                    return $"({condition}) ? ({whenTrue}) : ({whenFalse})";
                }
            case Operator.Patterns:
                {
                    var p = (IsPatternExpressionSyntax)e;
                    return PatternStructure(p.Pattern, p.Expression.ToString(), conditions);
                }
            default:
                conditions.Add(new Condition(e, e.ToString()));
                return "c" + (conditions.Count - 1);
        }
    }

    private static string PatternStructure(PatternSyntax pattern, string input, List<Condition> conditions)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax p:
                return PatternStructure(p.Pattern, input, conditions);
            case BinaryPatternSyntax b:
                {
                    var l = PatternStructure(b.Left, input, conditions);
                    var r = PatternStructure(b.Right, input, conditions);
                    return $"({l}) {(b.IsKind(SyntaxKind.AndPattern) ? "&&" : "||")} ({r})";
                }
            case UnaryPatternSyntax u when u.IsKind(SyntaxKind.NotPattern) && HasCombinator(u.Pattern):
                return $"!({PatternStructure(u.Pattern, input, conditions)})";
            default:
                conditions.Add(new Condition(pattern, $"{input} is {pattern}"));
                return "c" + (conditions.Count - 1);
        }
    }

    // and/or at the top of a pattern (under parentheses and not)
    private static bool HasCombinator(PatternSyntax pattern) => pattern switch
    {
        ParenthesizedPatternSyntax p => HasCombinator(p.Pattern),
        UnaryPatternSyntax u => HasCombinator(u.Pattern),
        BinaryPatternSyntax => true,
        _ => false
    };

    /*
     * x is P1 and P2 or ...: decomposed when it has and/or at its top, and every "and" whose left side
     * narrows the type (x is string and { Length: > 0 }) narrows to a type that can be written out
     */
    private bool CanDecompose(IsPatternExpressionSyntax p)
    {
        if (!HasCombinator(p.Pattern) || _model.GetTypeInfo(p.Expression).Type is not { IsRefLikeType: false })
        {
            return false;
        }
        return p.Pattern.DescendantNodesAndSelf(n => n is PatternSyntax && n is not (RecursivePatternSyntax or ListPatternSyntax))
            .OfType<BinaryPatternSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AndPattern))
            .All(b => NarrowedCast(b.Left) is not "");
    }

    // the cast for the right side of an "and" whose left side narrows the type; null if it doesn't, "" if it can't be written
    private string? NarrowedCast(PatternSyntax left)
    {
        if (_model.GetOperation(left) is not IPatternOperation op || SymbolEqualityComparer.IncludeNullability.Equals(op.InputType, op.NarrowedType) ||
            SymbolEqualityComparer.Default.Equals(op.InputType, op.NarrowedType))
        {
            return null;
        }
        var type = TypeArgument(op.NarrowedType);
        if (type == null)
        {
            return "";
        }
        // from a type parameter, through object
        return op.InputType is ITypeParameterSymbol ? $"({type})(object)" : $"({type})";
    }

    private static string? TypeArgument(ITypeSymbol type)
    {
        static bool Ok(ITypeSymbol t) => t switch
        {
            IErrorTypeSymbol or IPointerTypeSymbol or IFunctionPointerTypeSymbol => false,
            { IsAnonymousType: true } or { IsRefLikeType: true } => false,
            IArrayTypeSymbol a => Ok(a.ElementType),
            INamedTypeSymbol n => n.TypeArguments.All(Ok),
            _ => true
        };
        return Ok(type) ? type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null;
    }

    private ExpressionSyntax InstrumentDecision(ExpressionSyntax e)
    {
        var conditions = new List<Condition>();
        var shape = Structure(e, conditions);

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
            Shape = shape,
            Conditions = conditions.Select(c => c.Text).ToList()
        });

        // && || ! only: the markers know statically which value completes the decision (McDcFunctions);
        // otherwise (& | ^ == != ?:) every marker records the evaluation so far, and the report keeps
        // the complete ones (see McDcAnalyzer)
        var tree = DecisionTree.Parse(shape, new List<ExpressionSyntax>(), allOperators: true);
        var markers = DecisionTree.IsClassic(tree) ? new McDcFunctions().CreateMarkers(tree) : null;
        var index = 0;
        return Rewrite(e, id, markers, ref index);
    }

    // rewrites the structure of the decision, adding markers to each condition (unless id is null)
    private ExpressionSyntax Rewrite(ExpressionSyntax e, int? id, IReadOnlyList<MarkerFunction>? markers, ref int index)
    {
        switch (OperatorOf(e))
        {
            case Operator.Parentheses:
                {
                    var p = (ParenthesizedExpressionSyntax)e;
                    return p.WithExpression(Rewrite(p.Expression, id, markers, ref index));
                }
            case Operator.Logical or Operator.BoolBinary:
                {
                    var b = (BinaryExpressionSyntax)e;
                    var l = Rewrite(b.Left, id, markers, ref index);
                    var r = Rewrite(b.Right, id, markers, ref index);
                    return b.WithLeft(l).WithRight(r);
                }
            case Operator.Not:
                {
                    var u = (PrefixUnaryExpressionSyntax)e;
                    return u.WithOperand(Rewrite(u.Operand, id, markers, ref index));
                }
            case Operator.BoolConditional:
                {
                    var c = (ConditionalExpressionSyntax)e;
                    var condition = Rewrite(c.Condition, id, markers, ref index);
                    var whenTrue = Rewrite(c.WhenTrue, id, markers, ref index);
                    var whenFalse = Rewrite(c.WhenFalse, id, markers, ref index);
                    return c.WithCondition(condition).WithWhenTrue(whenTrue).WithWhenFalse(whenFalse);
                }
            case Operator.Patterns when id != null:
                {
                    // x is P  ->  (x is var __tarpValue7x0 && (<P on __tarpValue7x0>)): x evaluated once
                    var p = (IsPatternExpressionSyntax)e;
                    var input = (ExpressionSyntax)base.Visit(p.Expression)!;
                    var value = $"__tarpValue{id}x{index}";
                    var body = RewritePattern(p.Pattern, IdentifierName(value), id.Value, markers, ref index);
                    var hoisted = ParseExpression($"(__tarpInput is var {value} && __tarpBody)");
                    hoisted = hoisted.ReplaceNodes(
                        hoisted.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.Text is "__tarpInput" or "__tarpBody"),
                        (n, _) => ParenthesizedExpression(n.Identifier.Text == "__tarpInput" ? input.WithoutTrivia() : body));
                    return hoisted.WithTriviaFrom(e);
                }
            default:
                {
                    // conditions may contain decisions of their own (e.g. in a lambda), so visit them too
                    var condition = (ExpressionSyntax)base.Visit(e)!;
                    if (id == null)
                    {
                        return condition;
                    }

                    var i = index++;
                    return Mark(e, condition, id.Value, i, markers?[i]);
                }
        }
    }

    // a pattern as a bool expression on input: and/or/not are &&/||/!, primitive patterns conditions
    private ExpressionSyntax RewritePattern(PatternSyntax pattern, ExpressionSyntax input, int id, IReadOnlyList<MarkerFunction>? markers, ref int index)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax p:
                return RewritePattern(p.Pattern, input, id, markers, ref index);

            case BinaryPatternSyntax b:
                {
                    var l = RewritePattern(b.Left, input, id, markers, ref index);
                    // after "and", the right side tests what the left side narrowed the value to
                    var cast = b.IsKind(SyntaxKind.AndPattern) ? NarrowedCast(b.Left) : null;
                    var rightInput = cast == null ? input : ParseExpression($"({cast}{input})");
                    var r = RewritePattern(b.Right, rightInput, id, markers, ref index);
                    return BinaryExpression(b.IsKind(SyntaxKind.AndPattern) ? SyntaxKind.LogicalAndExpression : SyntaxKind.LogicalOrExpression,
                        ParenthesizedExpression(l), ParenthesizedExpression(r));
                }

            case UnaryPatternSyntax u when u.IsKind(SyntaxKind.NotPattern) && HasCombinator(u.Pattern):
                return PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, ParenthesizedExpression(RewritePattern(u.Pattern, input, id, markers, ref index)));

            default:
                {
                    var test = IsPatternExpression(input, Token(SyntaxKind.IsKeyword).WithLeadingTrivia(Space).WithTrailingTrivia(Space), pattern.WithoutTrivia());
                    var i = index++;
                    return Mark(test, test, id, i, markers?[i]);
                }
        }
    }

    /*
     * condition: the original, to look at; rewritten: what goes into the output. marker: what completes
     * the decision (null: record every evaluation so far, done = 1).
     */
    private static ExpressionSyntax Mark(ExpressionSyntax condition, ExpressionSyntax rewritten, int id, int i, MarkerFunction? marker)
    {
        var frame = "__tarp" + id;

        string Call(string name, bool value, bool declare)
        {
            var done = 1;
            if (marker != null)
            {
                var outcome = McDcFunctions.DecisionOutcome(marker.Path, value);
                done = outcome == null ? 0 : outcome.Value ? 2 : 1;
            }
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

    private string? WhyNotInstrumentable(ExpressionSyntax e, List<Condition> conditions)
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

        // && / || / ! must be the built-in operators on bool (& | ^ == != ?: are, by OperatorOf)
        foreach (var n in e.DescendantNodesAndSelf(n => n == e || n is ExpressionSyntax x && OperatorOf(x) != Operator.None))
        {
            if (n is ExpressionSyntax x && OperatorOf(x) is Operator.Logical or Operator.Not &&
                _model.GetTypeInfo(x).Type?.SpecialType != SpecialType.System_Boolean)
            {
                return "not a bool expression";
            }
        }
        foreach (var c in conditions.Select(c => c.Node).OfType<ExpressionSyntax>())
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
            if (a is QueryExpressionSyntax q && IsExpressionTree(q))
            {
                return "in a query that becomes an expression tree";
            }
            if (a is AnonymousFunctionExpressionSyntax f && IsExpressionTree(f))
            {
                return "in an expression tree lambda";
            }
        }

        return null;
    }

    // in a lambda or a query that becomes an expression tree: no calls to the runtime there
    protected bool IsInExpressionTree(SyntaxNode node) =>
        node.AncestorsAndSelf().Any(a => a is QueryExpressionSyntax q && IsExpressionTree(q) || a is AnonymousFunctionExpressionSyntax f && IsExpressionTree(f));

    private readonly Dictionary<QueryExpressionSyntax, bool> _expressionTreeQueries = new();

    // a query is lambdas passed to the methods its clauses bind to (Where, Select, ...): over IQueryable,
    // those take expression trees; over in-memory collections, delegates
    private bool IsExpressionTree(QueryExpressionSyntax query)
    {
        if (_expressionTreeQueries.TryGetValue(query, out var known))
        {
            return known;
        }

        var methods = new List<ISymbol?>();
        foreach (var n in query.DescendantNodesAndSelf())
        {
            if (n is QueryClauseSyntax clause)
            {
                var info = _model.GetQueryClauseInfo(clause);
                methods.Add(info.OperationInfo.Symbol);
                methods.Add(info.CastInfo.Symbol);
            }
            else if (n is SelectOrGroupClauseSyntax or QueryContinuationSyntax)
            {
                methods.Add(_model.GetSymbolInfo(n).Symbol);
            }
        }

        // a method that didn't bind (an error) counts as one: better not instrumented than broken
        var result = methods.Any(m => m is IMethodSymbol method
            ? method.Parameters.Any(p => p.Type.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>")
            : false) || query.FromClause != null && _model.GetTypeInfo(query.FromClause.Expression).Type is IErrorTypeSymbol;
        _expressionTreeQueries[query] = result;
        return result;
    }

    protected bool IsExpressionTree(AnonymousFunctionExpressionSyntax f)
    {
        var type = _model.GetTypeInfo(f).ConvertedType;
        return type?.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>";
    }

    private static IEnumerable<SyntaxNode> DescendantsOutsideNestedFunctions(SyntaxNode node) =>
        node.DescendantNodesAndSelf(n => n == node || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

}
