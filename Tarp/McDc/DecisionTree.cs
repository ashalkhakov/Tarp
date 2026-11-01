using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
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

    // operators on bools that DO-178C counts, besides && || !: they evaluate both sides
    public static bool IsBoolOperatorKind(SyntaxKind kind) => kind is
        SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression or SyntaxKind.ExclusiveOrExpression or
        SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;

    /*
     * Builds the tree McDcFunctions works on, collecting the conditions left to right. With allOperators
     * (a decision's shape, where every operand is a bool: see DecisionRewriter), & | ^ == != and ?: are
     * operators too.
     */
    public static McDcNode Build(ExpressionSyntax e, List<ExpressionSyntax> conditions, bool allOperators = false)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return Build(p.Expression, conditions, allOperators);

            case BinaryExpressionSyntax b when IsLogical(b) || allOperators && IsBoolOperatorKind(b.Kind()):
                {
                    var l = Build(b.Left, conditions, allOperators);
                    var r = Build(b.Right, conditions, allOperators);
                    return new McDcNode { Kind = McDcNodeKind.Decision, SyntaxNode = b, Children = new List<McDcNode> { l, r } };
                }

            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                {
                    var c = Build(u.Operand, conditions, allOperators);
                    return new McDcNode { Kind = McDcNodeKind.Decision, SyntaxNode = u, Children = new List<McDcNode> { c } };
                }

            case ConditionalExpressionSyntax c when allOperators:
                {
                    var condition = Build(c.Condition, conditions, allOperators);
                    var whenTrue = Build(c.WhenTrue, conditions, allOperators);
                    var whenFalse = Build(c.WhenFalse, conditions, allOperators);
                    return new McDcNode { Kind = McDcNodeKind.Decision, SyntaxNode = c, Children = new List<McDcNode> { condition, whenTrue, whenFalse } };
                }

            default:
                conditions.Add(e);
                return new McDcNode { Kind = McDcNodeKind.Condition, SyntaxNode = e, Children = null, Position = conditions.Count - 1 };
        }
    }

    public static McDcNode Parse(string text, List<ExpressionSyntax> conditions, bool allOperators = false) =>
        Build(SyntaxFactory.ParseExpression(text), conditions, allOperators);

    // only && || ! (what McDcFunctions handles)
    public static bool IsClassic(McDcNode node) =>
        node.Kind == McDcNodeKind.Condition ||
        (node.SyntaxNode.IsKind(SyntaxKind.LogicalAndExpression) || node.SyntaxNode.IsKind(SyntaxKind.LogicalOrExpression) ||
         node.SyntaxNode.IsKind(SyntaxKind.LogicalNotExpression)) && node.Children!.All(IsClassic);

    // evaluation of the tree (short-circuiting where C# does), given the values of all conditions (bit i = condition i)
    public static bool Evaluate(McDcNode node, uint values, ref uint evaluated)
    {
        if (node.Kind == McDcNodeKind.Condition)
        {
            evaluated |= 1u << node.Position;
            return (values & (1u << node.Position)) != 0;
        }

        var children = node.Children!;
        switch (node.SyntaxNode.Kind())
        {
            case SyntaxKind.LogicalAndExpression:
                return Evaluate(children[0], values, ref evaluated) && Evaluate(children[1], values, ref evaluated);
            case SyntaxKind.LogicalOrExpression:
                return Evaluate(children[0], values, ref evaluated) || Evaluate(children[1], values, ref evaluated);
            case SyntaxKind.LogicalNotExpression:
                return !Evaluate(children[0], values, ref evaluated);
            case SyntaxKind.ConditionalExpression:
                return Evaluate(children[0], values, ref evaluated)
                    ? Evaluate(children[1], values, ref evaluated)
                    : Evaluate(children[2], values, ref evaluated);
            default:
                {
                    var l = Evaluate(children[0], values, ref evaluated);
                    var r = Evaluate(children[1], values, ref evaluated);
                    return node.SyntaxNode.Kind() switch
                    {
                        SyntaxKind.BitwiseAndExpression => l & r,
                        SyntaxKind.BitwiseOrExpression => l | r,
                        SyntaxKind.ExclusiveOrExpression => l ^ r,
                        SyntaxKind.EqualsExpression => l == r,
                        _ => l != r
                    };
                }
        }
    }

    // the value of a subtree in an evaluation; null if it wasn't evaluated
    private static bool? Value(McDcNode node, uint evaluated, uint values)
    {
        if (node.Kind == McDcNodeKind.Condition)
        {
            return (evaluated & (1u << node.Position)) == 0 ? null : (values & (1u << node.Position)) != 0;
        }

        var c = node.Children!.Select(x => Value(x, evaluated, values)).ToList();
        return node.SyntaxNode.Kind() switch
        {
            SyntaxKind.LogicalNotExpression => !c[0],
            SyntaxKind.LogicalAndExpression => c[0] == false ? false : c[1],
            SyntaxKind.LogicalOrExpression => c[0] == true ? true : c[1],
            SyntaxKind.ConditionalExpression => c[0] == null ? null : c[0].Value ? c[1] : c[2],
            SyntaxKind.BitwiseAndExpression => c[0] & c[1],
            SyntaxKind.BitwiseOrExpression => c[0] | c[1],
            SyntaxKind.ExclusiveOrExpression => c[0] ^ c[1],
            SyntaxKind.EqualsExpression => c[0] == null || c[1] == null ? null : c[0] == c[1],
            _ => c[0] == null || c[1] == null ? null : c[0] != c[1]
        };
    }

    /*
     * Masking MC/DC for any tree, directly: an evaluated condition's value is shown unless an operator on
     * its way to the outcome masks it: && and & when the other side is false, || and | when it is true.
     * ^ == != and ?: never mask (changing one side always matters, or decides which side counts), nor does
     * a side that wasn't evaluated (short-circuited: don't care). For && || ! trees this is what
     * McDcFunctions' monitor computes (see the tests). Bits as in McDcFunctions: 2i true, 2i + 1 false.
     */
    public static int Shown(McDcNode tree, uint evaluated, uint values)
    {
        var shown = 0;
        void Walk(McDcNode node, bool masked)
        {
            if (node.Kind == McDcNodeKind.Condition)
            {
                var i = node.Position;
                if (!masked && (evaluated & (1u << i)) != 0)
                {
                    shown |= (values & (1u << i)) != 0 ? 1 << 2 * i : 1 << 2 * i + 1;
                }
                return;
            }

            var children = node.Children!;
            var kind = node.SyntaxNode.Kind();
            if (kind is SyntaxKind.LogicalAndExpression or SyntaxKind.BitwiseAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.BitwiseOrExpression)
            {
                var controlling = kind is SyntaxKind.LogicalOrExpression or SyntaxKind.BitwiseOrExpression;
                var l = Value(children[0], evaluated, values);
                var r = Value(children[1], evaluated, values);
                Walk(children[0], masked || r == controlling);
                Walk(children[1], masked || l == controlling);
                return;
            }
            foreach (var child in children)
            {
                Walk(child, masked);
            }
        }
        Walk(tree, false);
        return shown;
    }
}
