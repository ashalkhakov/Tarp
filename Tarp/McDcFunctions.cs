using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Generic;
using Tarp.Types;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.IO;

namespace Tarp
{
    public class McDcFunctions
    {
        public int MarkAst(McDcNode node, int pos)
        {
            if (node.SyntaxNode is ParenthesizedExpressionSyntax p && node.Kind == McDcNodeKind.Decision)
            {
                return MarkAst(node.Children[0], pos);
            }
            else if (node.SyntaxNode is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)))
            {
                var maxL = MarkAst(node.Children[0], pos);
                var maxR = MarkAst(node.Children[1], maxL);

                // this says that if this decision RHS evaluates to True or False, then which conditions to the left are masked
                node.MaskL = pos;
                node.MaskR = maxL - 1;
                return maxR;
            }
            else if (node.SyntaxNode is PrefixUnaryExpressionSyntax u && u.IsKind(SyntaxKind.LogicalNotExpression))
            {
                return MarkAst(node.Children[0], pos);
            }
            else
            {
                // this gives a left-to-right ordering of conditions (leafs) in the tree
                node.Position = pos;
                return pos + 1;
            }
        }

        public enum SideOfParent { Left, Right, Unary };
        public class PathElem
        {
            public McDcNode Expr { get; set; }
            public SideOfParent Side { get; set; }
            public PathElem? Up { get; set; }
        }

        public bool? OpVal(PathElem n, bool? v)
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

        int AddToMask(int mask, int l, int r)
        {
            //Debug.Assert(l >= 0 && l < 32);
            //Debug.Assert(r >= 0 && r < 32);
            //Debug.Assert(l <= r);
            //var range = (((1 << (l - 1)) - 1) ^  ((1 << (r)) - 1));
            var min = 1 << (2 * l);
            var min2 = 1 << (2 * l + 1);
            var range = (((1 << (min - 1)) - 1) ^ ((1 << (min2)) - 1));

            var max = 1 << (2 * r);
            var max2 = 1 << (2 * r + 1);
            var range2 = (((1 << (max - 1)) - 1) ^ ((1 << (max2)) - 1));

            return mask | min | min2 | max | max2;
        }

        public int SetMask(PathElem? path, int mask, bool? v)
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
                        (n.Expr.SyntaxNode is BinaryExpressionSyntax a && a.IsKind(SyntaxKind.LogicalAndExpression) && v == false) ||
                        (n.Expr.SyntaxNode is BinaryExpressionSyntax o && o.IsKind(SyntaxKind.LogicalOrExpression) && v == true)
                    ))
                {
                    mask = AddToMask(mask, n.Expr.MaskL, n.Expr.MaskR);
                }
                return SetMask(tl, mask, OpVal(n, v));
            }
        }

        public bool DecisionComplete(PathElem? path, bool? v)
        {
            if (v == null)
                return false;
            else if (path == null)
                return true;
            else
            {
                v = OpVal(path, v);
                return DecisionComplete(path.Up, v);
            }
        }

        public class MarkerFunction
        {
            public McDcNode Node { get; set; }
            public PathElem Path { get; set; }
            public int TrueBit { get; set; }
            public int TrueMask { get; set; }
            public bool TrueDone { get; set; }
            public int FalseBit { get; set; }
            public int FalseMask { get; set; }
            public bool FalseDone { get; set; }
        }

        public MarkerFunction GenMarker(McDcNode node, PathElem path)
        {
            Debug.Assert(node != null);
            Debug.Assert(node.Kind == McDcNodeKind.Condition);

            var pos = node.Position;
            var trueBit = 1 << (2 * pos);
            var trueMask = ~SetMask(path, 0, true);
            var trueDone = DecisionComplete(path, true);
            var falseBit = 1 << (2 * pos + 1);
            var falseMask = ~SetMask(path, 0, false);
            var falseDone = DecisionComplete(path, false);

            // TODO: code to emit the C# function (one per condition)
            /*
             * void Mark(bool isTrue) {
             *   if (isTrue) {
             *     temp[0] = <trueBit>;
             *     <if trueMask {>
             *       temp[0] &= <trueMask>;
             *     <}>
             *     <if trueDone {>
             *       indArray[0] |= temp[0];
             *     <}>
             *   } else {
             *     temp[0] = <falseBit>;
             *     <if falseMask {>
             *       temp[0] &= <falseMask>;
             *     <}>
             *     <if falseDone {>
             *       indArray[0] |= temp[0];
             *     <}>
             *   }
             * }
             */
            var result = new MarkerFunction
            {
                Node = node,
                TrueBit = trueBit,
                TrueMask = trueMask,
                TrueDone = trueDone,
                FalseBit = falseBit,
                FalseMask = falseMask,
                FalseDone = falseDone,
                Path = path
            };
            
            return result;
        }

        public void GenMarkers(McDcNode node, PathElem? path, List<MarkerFunction> functions)
        {
            if (node.SyntaxNode is BinaryExpressionSyntax a && (a.IsKind(SyntaxKind.LogicalAndExpression) || a.IsKind(SyntaxKind.LogicalOrExpression)))
            {
                var left = new PathElem { Expr = node, Side = SideOfParent.Left, Up = path };
                var right = new PathElem { Expr = node, Side = SideOfParent.Right, Up = path };
                GenMarkers(node.Children[0], left, functions);
                GenMarkers(node.Children[1], right, functions);
            }
            else if (node.SyntaxNode  is PrefixUnaryExpressionSyntax u && u.IsKind(SyntaxKind.LogicalNotExpression))
            {
                var child = new PathElem { Expr = node, Side = SideOfParent.Unary, Up = path };
                GenMarkers(node.Children[0], child, functions);
            }
            else
            {
                var f = GenMarker(node, path);
                functions.Add(f);
            }
        }

        public List<MarkerFunction> CreateMarkers(McDcNode node)
        {
            var numPositions = MarkAst(node, 0);

            var functions = new List<MarkerFunction>(numPositions);

            GenMarkers(node, null, functions);

            return functions;
        }
    }
}
