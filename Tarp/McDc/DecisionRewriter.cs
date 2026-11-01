using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Tarp.McDc;

/*
 * Rewrites every decision
 *
 *   a && (b || !c)
 *
 * into
 *
 *   global::TarpGen.McDc.End(global::TarpGen.McDc.Begin(17), global::TarpGen.McDc.C(17, 0, a) && (global::TarpGen.McDc.C(17, 1, b) || !global::TarpGen.McDc.C(17, 2, c)))
 *
 * The calls are identities with a side effect: C records the condition's value, End records the
 * vector of evaluated conditions (and their values) together with the outcome. Begin is an argument,
 * not a "Begin(17) && ...", so whatever is assigned unconditionally in the decision stays definitely
 * assigned.
 *
 * Decisions are:
 * - maximal &&/|| expressions (anywhere, including nested in a condition, e.g. in a lambda)
 * - the controlling expression of if/while/do/for/?:, even with a single condition
 *
 * Decisions we can't wrap without changing the program's meaning (or making it not compile) are
 * left alone and listed in the map as skipped, with the reason.
 */
public sealed class DecisionRewriter : CSharpSyntaxRewriter
{
    // the runtime's bit vectors and McDcFunctions' masks (2 bits per condition in an int)
    public const int MaxConditions = 16;

    private const string Runtime = "global::TarpGen.McDc";

    private readonly SemanticModel _model;
    private readonly McDcMap _map;
    private readonly string _displayPath;

    public DecisionRewriter(SemanticModel model, McDcMap map, string displayPath)
        : base(visitIntoStructuredTrivia: false)
    {
        _model = model;
        _map = map;
        _displayPath = displayPath;
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (node is ExpressionSyntax e && IsDecisionRoot(e))
        {
            return InstrumentDecision(e);
        }
        return base.Visit(node);
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
        _ => false
    };

    private ExpressionSyntax InstrumentDecision(ExpressionSyntax e)
    {
        var conditions = new List<ExpressionSyntax>();
        DecisionTree.Build(e, conditions);

        var lineSpan = e.GetLocation().GetLineSpan().StartLinePosition;
        var reason = WhyNotInstrumentable(e, conditions);
        if (reason != null)
        {
            _map.Skipped.Add(new SkippedDecision
            {
                File = _displayPath,
                Line = lineSpan.Line + 1,
                Column = lineSpan.Character + 1,
                Text = e.ToString(),
                Reason = reason
            });
            var index0 = 0;
            return Rewrite(e, null, ref index0);
        }

        var id = _map.Decisions.Count;
        _map.Decisions.Add(new DecisionEntry
        {
            Id = id,
            File = _displayPath,
            Line = lineSpan.Line + 1,
            Column = lineSpan.Character + 1,
            Text = e.ToString(),
            Conditions = conditions.Select(c => c.ToString()).ToList()
        });

        var index = 0;
        var body = Rewrite(e, id, ref index);

        var call = InvocationExpression(
            ParseExpression(Runtime + ".End"),
            ArgumentList(SeparatedList(new[]
            {
                Argument(InvocationExpression(ParseExpression(Runtime + ".Begin"), ArgumentList(SingletonSeparatedList(Argument(Literal(id)))))),
                Argument(body.WithoutTrivia())
            })));

        return call.WithTriviaFrom(e);
    }

    // rewrites the structure of the decision, wrapping each condition with C(...) (unless id is null)
    private ExpressionSyntax Rewrite(ExpressionSyntax e, int? id, ref int index)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return p.WithExpression(Rewrite(p.Expression, id, ref index));

            case BinaryExpressionSyntax b when DecisionTree.IsLogical(b):
                {
                    var l = Rewrite(b.Left, id, ref index);
                    var r = Rewrite(b.Right, id, ref index);
                    return b.WithLeft(l).WithRight(r);
                }

            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                return u.WithOperand(Rewrite(u.Operand, id, ref index));

            default:
                {
                    // conditions may contain decisions of their own (e.g. in a lambda), so visit them too
                    var condition = (ExpressionSyntax)base.Visit(e)!;
                    if (id == null)
                    {
                        return condition;
                    }

                    var i = index++;
                    var call = InvocationExpression(
                        ParseExpression(Runtime + ".C"),
                        ArgumentList(SeparatedList(new[]
                        {
                            Argument(Literal(id.Value)),
                            Argument(Literal(i)),
                            Argument(condition.WithoutTrivia())
                        })));
                    return call.WithTriviaFrom(condition);
                }
        }
    }

    private static LiteralExpressionSyntax Literal(int value) =>
        LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));

    private string? WhyNotInstrumentable(ExpressionSyntax e, List<ExpressionSyntax> conditions)
    {
        if (conditions.Count > MaxConditions)
        {
            return $"more than {MaxConditions} conditions";
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

        var inThisFunction = DescendantsOutsideNestedFunctions(e).ToList();

        if (inThisFunction.Any(n => n is AwaitExpressionSyntax))
        {
            return "contains await";
        }

        // pattern variables are assigned "when true" (or "when false"): wrapping the conditions loses that
        if (inThisFunction.Any(n => n is SingleVariableDesignationSyntax d && d.Parent is not DeclarationExpressionSyntax ||
                                    n is ParenthesizedVariableDesignationSyntax))
        {
            return "declares pattern variables";
        }

        // same for anything assigned by a condition that isn't always evaluated (all but the first)
        foreach (var c in conditions.Skip(1))
        {
            if (DescendantsOutsideNestedFunctions(c).Any(IsAssignment))
            {
                return "assigns a variable in a condition after the first";
            }
        }

        return null;
    }

    private bool IsExpressionTree(AnonymousFunctionExpressionSyntax f)
    {
        var type = _model.GetTypeInfo(f).ConvertedType;
        return type?.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>";
    }

    private static IEnumerable<SyntaxNode> DescendantsOutsideNestedFunctions(SyntaxNode node) =>
        node.DescendantNodesAndSelf(n => n == node || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    private static bool IsAssignment(SyntaxNode n) => n switch
    {
        AssignmentExpressionSyntax => true,
        DeclarationExpressionSyntax => true,
        ArgumentSyntax a => a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) || a.RefKindKeyword.IsKind(SyntaxKind.RefKeyword),
        PrefixUnaryExpressionSyntax u => u.IsKind(SyntaxKind.PreIncrementExpression) || u.IsKind(SyntaxKind.PreDecrementExpression),
        PostfixUnaryExpressionSyntax u => u.IsKind(SyntaxKind.PostIncrementExpression) || u.IsKind(SyntaxKind.PostDecrementExpression),
        _ => false
    };
}
