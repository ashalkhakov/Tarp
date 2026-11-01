using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using Tarp.Types;

namespace Tarp.McDc;

/*
 * A decision is a boolean expression built out of &&, || and ! (parentheses don't count),
 * and its conditions are the leaves: anything that is not one of these operators.
 *
 * The tree is purely syntactic so that the report can rebuild it from the decision's text
 * and get exactly the same conditions, in the same (left-to-right, i.e. evaluation) order
 * as the instrumenter did.
 */
public static class DecisionTree
{
    public static bool IsLogical(ExpressionSyntax e) =>
        e.IsKind(SyntaxKind.LogicalAndExpression) || e.IsKind(SyntaxKind.LogicalOrExpression);

    public static bool IsStructural(ExpressionSyntax e) =>
        IsLogical(e) || e.IsKind(SyntaxKind.LogicalNotExpression) || e is ParenthesizedExpressionSyntax;

    // true if there is an && or || among the structural nodes of e
    public static bool ContainsLogical(ExpressionSyntax e) => e switch
    {
        BinaryExpressionSyntax b when IsLogical(b) => true,
        PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression) => ContainsLogical(u.Operand),
        ParenthesizedExpressionSyntax p => ContainsLogical(p.Expression),
        _ => false
    };

    // builds the tree McDcFunctions works on, collecting the conditions left to right
    public static McDcNode Build(ExpressionSyntax e, List<ExpressionSyntax> conditions)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return Build(p.Expression, conditions);

            case BinaryExpressionSyntax b when IsLogical(b):
                {
                    var l = Build(b.Left, conditions);
                    var r = Build(b.Right, conditions);
                    return new McDcNode { Kind = McDcNodeKind.Decision, SyntaxNode = b, Children = new List<McDcNode> { l, r } };
                }

            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                {
                    var c = Build(u.Operand, conditions);
                    return new McDcNode { Kind = McDcNodeKind.Decision, SyntaxNode = u, Children = new List<McDcNode> { c } };
                }

            default:
                conditions.Add(e);
                return new McDcNode { Kind = McDcNodeKind.Condition, SyntaxNode = e, Children = null };
        }
    }

    public static McDcNode Parse(string text, List<ExpressionSyntax> conditions) =>
        Build(SyntaxFactory.ParseExpression(text), conditions);

    // short-circuit evaluation of the tree, given the values of all conditions (bit i = condition i)
    public static bool Evaluate(McDcNode node, uint values, ref uint evaluated)
    {
        if (node.Kind == McDcNodeKind.Condition)
        {
            evaluated |= 1u << node.Position;
            return (values & (1u << node.Position)) != 0;
        }

        var children = node.Children!;
        if (node.SyntaxNode.IsKind(SyntaxKind.LogicalAndExpression))
        {
            return Evaluate(children[0], values, ref evaluated) && Evaluate(children[1], values, ref evaluated);
        }
        else if (node.SyntaxNode.IsKind(SyntaxKind.LogicalOrExpression))
        {
            return Evaluate(children[0], values, ref evaluated) || Evaluate(children[1], values, ref evaluated);
        }
        else
        {
            return !Evaluate(children[0], values, ref evaluated);
        }
    }
}
