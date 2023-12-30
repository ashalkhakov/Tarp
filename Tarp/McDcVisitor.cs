using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Diagnostics;
using Tarp.Types;

/*
 * TODO what's a proper interpretation?
 * - decisions are at literal branch points
 *    - e.g. in [if E S1 S2], E is the decision
 * - decisions are all boolean expressions in the program
 *    - ALL?
 *
 * wikipedia says: ANY boolean expression is a decision!
 * so, decisions can even be nested, then!
 * 
 * what's a condition then?
 * - variable of bool type
 * - e, when type of e is boolean, but it's none of the above
 *    - e.g., e could be x>0 where x : int
 * what's a decision, again? it's one of:
 * - c, where c is a condition, is the base case of decision.
 * - d1 && d2, where d1 and d2 are decisions
 * - !d, where d is a decision
 * - d1 || d2, where d1 and d2 are decisions
 * what about nesting decisions in conditions?
 * - d ? e1 : e2, where d is a decision, can be a condition if both e1 and e2 are of type bool
 */

namespace Tarp;

public class McDcVisitor : CSharpSyntaxVisitor
{
    public McDcVisitor(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
        _stack = new();
        _roots = new();
    }
    private readonly SemanticModel _semanticModel;
    private readonly Stack<McDcNode> _stack;
    private readonly List<McDcNode> _roots;

    public IReadOnlyList<McDcNode> Roots => _roots;
    public Stack<McDcNode> Stack => _stack;

    private bool IsBooleanExpression(CSharpSyntaxNode node)
    {
        var type = _semanticModel.GetTypeInfo(node).Type;
        if (type != null && type.SpecialType == SpecialType.System_Boolean)
        {
            return true;
        }
        return false;
    }

    public override void DefaultVisit(SyntaxNode node)
    {
        Debug.Assert(node != null);

        var heightBefore = _stack.Count;
        foreach (var c in node.ChildNodes())
        {
            Visit(c);
        }

        var expression = node as ExpressionSyntax;
        if (expression == null)
        {
            // we have exited the expression context
            while (_stack.Count > heightBefore)
            {
                // shouldn't we create decisions here? a decision may just be a condition though?
                var c = _stack.Pop();
                if (c.Kind == McDcNodeKind.Condition)
                {
                    // condition: put it inside a decision
                    var d = new McDcNode
                    {
                        SyntaxNode = c.SyntaxNode,
                        Kind = McDcNodeKind.Decision,
                        Children = new List<McDcNode> { c }
                    };
                    _roots.Add(d);
                }
                else
                {
                    // decision: embed it directly
                    var d = new McDcNode
                    {
                        SyntaxNode = c.SyntaxNode,
                        Kind = McDcNodeKind.Decision,
                        Children = c.Children
                    };
                    _roots.Add(d);
                }
            }
            return;
        }
        if (/*expression is LiteralExpressionSyntax ||*/ expression is PredefinedTypeSyntax)
        {
            return;
        }

        if (IsBooleanExpression((CSharpSyntaxNode)node))
        {
            if (node is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)))
            {
                // it's a decision
                var r = _stack.Pop();
                var l = _stack.Pop();
                var d = new McDcNode
                {
                    Kind = McDcNodeKind.Decision,
                    SyntaxNode = b,
                    Children = new List<McDcNode> { l, r }
                };
                _stack.Push(d);
            }
            else if (node is BinaryExpressionSyntax rb && (
                rb.IsKind(SyntaxKind.LessThanExpression) ||
                rb.IsKind(SyntaxKind.LessThanOrEqualExpression) ||
                rb.IsKind(SyntaxKind.EqualsExpression) ||
                rb.IsKind(SyntaxKind.GreaterThanOrEqualExpression) ||
                rb.IsKind(SyntaxKind.GreaterThanExpression)
                ) &&
                IsBooleanExpression(rb.Left) &&
                IsBooleanExpression(rb.Right))
            {
                // it's a decision
                var r = _stack.Pop();
                var l = _stack.Pop();
                var d = new McDcNode
                {
                    Kind = McDcNodeKind.Decision,
                    SyntaxNode = rb,
                    Children = new List<McDcNode> { l, r }
                };
                _stack.Push(d);
            }
            else if (node is PrefixUnaryExpressionSyntax u && u.IsKind(SyntaxKind.LogicalNotExpression))
            {
                // it's a decision
                var p = _stack.Pop();
                var d = new McDcNode
                {
                    Kind = McDcNodeKind.Decision,
                    SyntaxNode = u,
                    Children = new List<McDcNode>() { p }
                };
                _stack.Push(d);
            }
            else if (node is ConditionalExpressionSyntax cond)
            {
                // it's a decision with three conditions
                var r = _stack.Pop();
                var l = _stack.Pop();
                var c = _stack.Pop();
                var d = new McDcNode
                {
                    Kind = McDcNodeKind.Decision,
                    SyntaxNode = cond,
                    Children = new List<McDcNode>() { c, l, r }
                };
                _stack.Push(d);
            }
            else if (node is ParenthesizedExpressionSyntax parens)
            {
                // skip it completely
            }
            else
            {
                // it's a condition
                // NOTE: we might want to examine the stack here. if it's a boolean relational expression, then both subexpressions are decisions or conditions.
                var c0 = new McDcNode
                {
                    Kind = McDcNodeKind.Condition,
                    SyntaxNode = (CSharpSyntaxNode)node,
                    Children = null
                };
                _stack.Push(c0);
            }
        }
    }
}
