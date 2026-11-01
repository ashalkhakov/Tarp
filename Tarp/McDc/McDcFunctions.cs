using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Generic;
using Tarp.Types;
using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Tarp.McDc
{
    /*
     * see: https://conservancy.umn.edu/bitstream/handle/11299/217382/monitorAlgorithm_0.pdf?sequence=1
     */
    public class McDcFunctions
    {
        public int MarkAst(McDcNode node, int pos)
        {
            Debug.Assert(node != null);

            if (node.SyntaxNode is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)))
            {
                Debug.Assert(node.Children != null && node.Children.Count == 2);

                var maxL = MarkAst(node.Children[0], pos);
                var maxR = MarkAst(node.Children[1], maxL);

                // this says that if this decision RHS evaluates to True or False, then which conditions to the left are masked
                node.MaskL = pos;
                node.MaskR = maxL - 1;
                return maxR;
            }
            else if (node.SyntaxNode is PrefixUnaryExpressionSyntax u && u.IsKind(SyntaxKind.LogicalNotExpression))
            {
                Debug.Assert(node.Children != null && node.Children.Count == 1);

                return MarkAst(node.Children[0], pos);
            }
            else
            {
                Debug.Assert(node.Children == null);

                // this gives a left-to-right ordering of conditions (leafs) in the tree
                node.Position = pos;
                return pos + 1;
            }
        }

        public static bool? OpVal(PathElem n, bool? v)
        {
            Debug.Assert(n != null);

            if (n.Side == SideOfParent.Right)
                return v;
            else if (n.Expr.SyntaxNode is BinaryExpressionSyntax a && a.IsKind(SyntaxKind.LogicalAndExpression) && v == false)
                return false;
            else if (n.Expr.SyntaxNode is BinaryExpressionSyntax o && o.IsKind(SyntaxKind.LogicalOrExpression) && v == true)
                return true;
            else if (n.Expr.SyntaxNode is PrefixUnaryExpressionSyntax p1 && p1.IsKind(SyntaxKind.LogicalNotExpression) && v == false)
                return true;
            else if (n.Expr.SyntaxNode is PrefixUnaryExpressionSyntax p2 && p2.IsKind(SyntaxKind.LogicalNotExpression) && v == true)
                return false;

            return null;
        }

        static int AddToMask(int mask, int l, int r)
        {
            //Debug.Assert(l >= 0 && l < 32);
            //Debug.Assert(r >= 0 && r < 32);
            //Debug.Assert(l <= r);

            // mask both the true and the false bit of every condition in [l, r], not just the two ends
            for (var pos = l; pos <= r; pos++)
            {
                mask |= 3 << 2 * pos;
            }

            return mask;
        }

        public static int SetMask(PathElem? path, int mask, bool? v)
        {
            if (path == null || v == null)
            {
                return mask;
            }
            else
            {
                var n = path;
                var tl = path.Up;
                if (n.Side == SideOfParent.Right &&
                    (
                        n.Expr.SyntaxNode is BinaryExpressionSyntax a && a.IsKind(SyntaxKind.LogicalAndExpression) && v == false ||
                        n.Expr.SyntaxNode is BinaryExpressionSyntax o && o.IsKind(SyntaxKind.LogicalOrExpression) && v == true
                    ))
                {
                    mask = AddToMask(mask, n.Expr.MaskL, n.Expr.MaskR);
                }
                return SetMask(tl, mask, OpVal(n, v));
            }
        }

        public static bool DecisionComplete(PathElem? path, bool? v)
        {
            if (v == null)
            {
                return false;
            }
            else if (path == null)
            {
                return true;
            }
            else
            {
                v = OpVal(path, v);
                return DecisionComplete(path.Up, v);
            }
        }

        // like DecisionComplete, but gives the decision's outcome when the condition's value completes it
        public static bool? DecisionOutcome(PathElem? path, bool? v)
        {
            if (v == null || path == null)
            {
                return v;
            }
            return DecisionOutcome(path.Up, OpVal(path, v));
        }

        public static MarkerFunction GenMarker(McDcNode node, PathElem? path)
        {
            Debug.Assert(node != null);
            Debug.Assert(node.Kind == McDcNodeKind.Condition);

            var pos = node.Position;
            var trueBit = 1 << 2 * pos;
            var trueMask = ~SetMask(path, 0, true);
            var trueDone = DecisionComplete(path, true);
            var falseBit = 1 << 2 * pos + 1;
            var falseMask = ~SetMask(path, 0, false);
            var falseDone = DecisionComplete(path, false);

            // the marker is not emitted into the instrumented program: the runtime records the
            // evaluated conditions, and Mark replays them offline (see McDcAnalyzer)
            var result = new MarkerFunction(node, path, trueBit, trueMask, trueDone, falseBit, falseMask, falseDone);

            return result;
        }

        /*
         * The behaviour of one condition's marker: fold the condition's value into the vector of the
         * current evaluation (temp), clearing the conditions it masks, and once the decision is complete,
         * add the surviving (non-masked) condition values to the indicator.
         *
         * NOTE: temp accumulates (|=) over the evaluation, it is reset per evaluation of the decision.
         */
        public static void Mark(MarkerFunction marker, bool isTrue, ref int temp, ref int indicator)
        {
            Debug.Assert(marker != null);

            if (isTrue)
            {
                temp |= marker.TrueBit;
                temp &= marker.TrueMask;
                if (marker.TrueDone)
                {
                    indicator |= temp;
                }
            }
            else
            {
                temp |= marker.FalseBit;
                temp &= marker.FalseMask;
                if (marker.FalseDone)
                {
                    indicator |= temp;
                }
            }
        }

        private void GenMarkers(McDcNode node, PathElem? path, List<MarkerFunction> functions)
        {
            Debug.Assert(node != null);

            if (node.SyntaxNode is BinaryExpressionSyntax a && (a.IsKind(SyntaxKind.LogicalAndExpression) || a.IsKind(SyntaxKind.LogicalOrExpression)))
            {
                Debug.Assert(node.Children != null && node.Children.Count == 2);

                var left = new PathElem(node, SideOfParent.Left, path);
                var right = new PathElem(node, SideOfParent.Right, path);
                GenMarkers(node.Children[0], left, functions);
                GenMarkers(node.Children[1], right, functions);
            }
            else if (node.SyntaxNode is PrefixUnaryExpressionSyntax u && u.IsKind(SyntaxKind.LogicalNotExpression))
            {
                Debug.Assert(node.Children != null && node.Children.Count == 1);

                var child = new PathElem(node, SideOfParent.Unary, path);
                GenMarkers(node.Children[0], child, functions);
            }
            else
            {
                Debug.Assert(node.Children == null);

                var f = GenMarker(node, path);
                functions.Add(f);
            }
        }

        public IReadOnlyList<MarkerFunction> CreateMarkers(McDcNode node)
        {
            var numPositions = MarkAst(node, 0);

            var functions = new List<MarkerFunction>(numPositions);

            GenMarkers(node, null, functions);

            return functions;
        }
    }
}
