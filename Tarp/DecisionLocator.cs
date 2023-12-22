using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;

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

namespace Tarp
{
    public class SourceLocation
    {
        public string FilePath { get; set; }
        public int StartLine { get; set; }
        public int StartColumn { get; set; }
        public int EndLine { get; set; }
        public int EndColumn { get; set; }

        public static SourceLocation OfNodeLocation(CSharpSyntaxNode node)
        {
            var loc = node.GetLocation().GetMappedLineSpan();

            return new SourceLocation
            {
                FilePath = loc.Path,
                StartLine = loc.Span.Start.Line,
                StartColumn = loc.Span.Start.Character,
                EndLine = loc.Span.End.Line,
                EndColumn = loc.Span.End.Character,
            };
        }
    }

    public class InstrumentationState
    {
        public InstrumentationState(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
            _conditions = new();
            _conditionStack = new();
            _decisions = new();
        }

        private SemanticModel _semanticModel;
        private List<ConditionInfo> _conditions;
        private Stack<ConditionInfo> _conditionStack;
        private List<DecisionInfo> _decisions;

        public int AllocCondition(ConditionInfo condition)
        {
            condition.Id = _conditions.Count;
            _conditions.Add(condition);
            _conditionStack.Push(condition);
            return condition.Id;
        }
        public int AllocDecision(DecisionInfo decision)
        {
            decision.Id = _decisions.Count;
            _decisions.Add(decision);
            return decision.Id;
        }

        private bool IsNodeAnExpressionOfBooleanType(CSharpSyntaxNode node)
        {
            if (node == null)
                return false;

            var type = _semanticModel.GetTypeInfo(node).Type;
            if (type != null && type.SpecialType == SpecialType.System_Boolean)
            {
                return true;
            }

            return false;
        }

        public void LatestConditionsToDecision(CSharpSyntaxNode node)
        {
            /*
             * if current node is of boolean type. and its parent is NOT of boolean type. (i.e we are exiting the expression context)
             * if current node is NOT of boolean type.
             */
            if (IsNodeAnExpressionOfBooleanType(node) && !IsNodeAnExpressionOfBooleanType(node.Parent as CSharpSyntaxNode))
            {
                // we are exiting the expression context

                // all conditions in the stack which are children of current node -->
                // become conditions of current node's decision.
                var dec = new DecisionInfo();
                dec.Node = node;
                dec.Location = SourceLocation.OfNodeLocation(node);
                dec.Conditions = new(_conditionStack.Count);
                while (_conditionStack.Count > 0)
                {
                    var c0 = _conditionStack.Pop();
                    dec.Conditions.Insert(0, c0);
                }
                AllocDecision(dec);
            }

            if (!IsNodeAnExpressionOfBooleanType(node))
            {
                //all conditions in the stack which are children of node
                // --->become conditions of their corresponding decisions.
                while (_conditionStack.Count > 0)
                {
                    var n = _conditionStack.Pop();
                    var dec = new DecisionInfo();
                    dec.Node = n.Node;
                    dec.Location = SourceLocation.OfNodeLocation(n.Node);
                    dec.Conditions = new(1)
                    {
                        n
                    };
                    AllocDecision(dec);
                }
            }

            // not a decision
            // collect all subtrees rooted at this node as separate decisions
            /*
            if (_conditionStack.Count == 0)
            {
                return;
            }

            var dec = new DecisionInfo();
            var c = _conditionStack.Peek();

            dec.Node = c.Node;
            dec.Location = c.Location;
            dec.Conditions = new();

            while (_conditionStack.Count > 0)
            {
                var c0 = _conditionStack.Peek();
                if (c0.Node.Parent != node)
                {

                    break;
                }
                dec.Conditions.Insert(0, c0);
            }

            AllocDecision(dec);*/
        }
    }

    public class ConditionInfo
    {
        public int Id { get; set; }
        public SourceLocation Location { get; set; }
        public CSharpSyntaxNode Node { get; set; }
    }

    public class DecisionInfo
    {
        public int Id { get; set; }
        public SourceLocation Location { get; set; }
        public List<ConditionInfo> Conditions { get; set; }
        public CSharpSyntaxNode Node { get; set; }
    }

    public class DecisionInstrumenterV2 : CSharpSyntaxWalker
    {
        public DecisionInstrumenterV2(SemanticModel semanticModel, InstrumentationState state)
        {
            _semanticModel = semanticModel;
            _decisions = new Stack<DecisionInfo>();
            _state = state;
        }

        private readonly SemanticModel _semanticModel;
        private readonly Stack<DecisionInfo> _decisions;
        private readonly InstrumentationState _state;

        /*
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
 * 
 * there's also this thing... that we want decisions to be 'as big as possible',
 * i.e. moving upwards, we see if we can incorporate each new parent into the decision.
 * if the new parent's type is of the 'decisional' form (see above), then we CAN move upwards.
 * otherwise we must stop, and mark it as a CONDITION instead. even though it'll have decisions inside.
 * 
 * 
 * E ? A : B
 *    E is a decision, but if it is of the form
 *    (E1 ? A1 : B1)
 *    then it is also a condition, and likewise
 *    E1 is a decision
 *    A1 is a decision
 *    B1 is a decision
         */

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            base.VisitIdentifierName(node);

            var type = _semanticModel.GetTypeInfo(node).Type;
            if (type != null && type.SpecialType == SpecialType.System_Boolean)
            {
                // it's a condition
                var c0 = new ConditionInfo
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node,
                };
                _state.AllocCondition(c0);
            }
            //an identifier is non-recursive, so no need for this
            //_state.LatestConditionsToDecision(node);
        }

        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        {
            base.VisitAnonymousMethodExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
        {
            base.VisitArrayCreationExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitAnonymousObjectCreationExpression(AnonymousObjectCreationExpressionSyntax node)
        {
            base.VisitAnonymousObjectCreationExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitArrowExpressionClause(ArrowExpressionClauseSyntax node)
        {
            base.VisitArrowExpressionClause(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitAwaitExpression(AwaitExpressionSyntax node)
        {
            base.VisitAwaitExpression(node);

            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }

            _state.LatestConditionsToDecision(node);
        }
        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            base.VisitAssignmentExpression(node);

            var tp = _semanticModel.GetTypeInfo(node.Right);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node.Right),
                    Node = node.Right
                });
            }

            _state.LatestConditionsToDecision(node);
        }
        public override void VisitBaseExpression(BaseExpressionSyntax node)
        {
            base.VisitBaseExpression(node);
            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            base.VisitBinaryExpression(node);
            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }

            _state.LatestConditionsToDecision(node);
        }
        public override void VisitCastExpression(CastExpressionSyntax node)
        {
            base.VisitCastExpression(node);
            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }

            _state.LatestConditionsToDecision(node);
        }
        public override void VisitCheckedExpression(CheckedExpressionSyntax node)
        {
            base.VisitCheckedExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        {
            base.VisitConditionalAccessExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            base.VisitConditionalExpression(node);
            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitDeclarationExpression(DeclarationExpressionSyntax node)
        {
            base.VisitDeclarationExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitDefaultExpression(DefaultExpressionSyntax node)
        {
            base.VisitDefaultExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            base.VisitElementAccessExpression(node);
            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitElementBindingExpression(ElementBindingExpressionSyntax node)
        {
            base.VisitElementBindingExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitImplicitArrayCreationExpression(ImplicitArrayCreationExpressionSyntax node)
        {
            base.VisitImplicitArrayCreationExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitImplicitStackAllocArrayCreationExpression(ImplicitStackAllocArrayCreationExpressionSyntax node)
        {
            base.VisitImplicitStackAllocArrayCreationExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitInitializerExpression(InitializerExpressionSyntax node)
        {
            base.VisitInitializerExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
        {
            base.VisitInterpolatedStringExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            base.VisitInvocationExpression(node);

            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }

            _state.LatestConditionsToDecision(node);
        }
        public override void VisitIsPatternExpression(IsPatternExpressionSyntax node)
        {
            base.VisitIsPatternExpression(node);

            _state.AllocCondition(new ConditionInfo()
            {
                Location = SourceLocation.OfNodeLocation(node),
                Node = node
            });

            _state.LatestConditionsToDecision(node);
        }
        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            base.VisitLiteralExpression(node);
            // no need for this.
            //_state.LatestConditionsToDecision(node);
        }
        public override void VisitMakeRefExpression(MakeRefExpressionSyntax node)
        {
            base.VisitMakeRefExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            base.VisitMemberAccessExpression(node);
            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }

            _state.LatestConditionsToDecision(node);
        }
        public override void VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
        {
            base.VisitMemberBindingExpression(node);
            var tp = _semanticModel.GetTypeInfo(node);
            if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            base.VisitObjectCreationExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitOmittedArraySizeExpression(OmittedArraySizeExpressionSyntax node)
        {
            base.VisitOmittedArraySizeExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitParenthesizedExpression(ParenthesizedExpressionSyntax node)
        {
            base.VisitParenthesizedExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            base.VisitParenthesizedLambdaExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            base.VisitPostfixUnaryExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            base.VisitPrefixUnaryExpression(node);
            if (node.IsKind(SyntaxKind.LogicalNotExpression))
            {
                _state.AllocCondition(new ConditionInfo()
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node
                });
            }
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitQueryExpression(QueryExpressionSyntax node)
        {
            base.VisitQueryExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitRangeExpression(RangeExpressionSyntax node)
        {
            base.VisitRangeExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitRefExpression(RefExpressionSyntax node)
        {
            base.VisitRefExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitRefTypeExpression(RefTypeExpressionSyntax node)
        {
            base.VisitRefTypeExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitRefValueExpression(RefValueExpressionSyntax node)
        {
            base.VisitRefValueExpression(node);
            _state.LatestConditionsToDecision(node);
        }

        // id => expression
        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            base.VisitSimpleLambdaExpression(node);
            _state.LatestConditionsToDecision(node);
        }

        public override void VisitSizeOfExpression(SizeOfExpressionSyntax node)
        {
            base.VisitSizeOfExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitStackAllocArrayCreationExpression(StackAllocArrayCreationExpressionSyntax node)
        {
            base.VisitStackAllocArrayCreationExpression(node);
            _state.LatestConditionsToDecision(node);
        }

        public override void VisitSwitchExpression(SwitchExpressionSyntax node)
        {
            base.VisitSwitchExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitSwitchExpressionArm(SwitchExpressionArmSyntax node)
        {
            base.VisitSwitchExpressionArm(node);
            _state.LatestConditionsToDecision(node);
        }

        public override void VisitThisExpression(ThisExpressionSyntax node)
        {
            base.VisitThisExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitThrowExpression(ThrowExpressionSyntax node)
        {
            base.VisitThrowExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitTupleExpression(TupleExpressionSyntax node)
        {
            base.VisitTupleExpression(node);
            _state.LatestConditionsToDecision(node);
        }
        public override void VisitTypeOfExpression(TypeOfExpressionSyntax node)
        {
            base.VisitTypeOfExpression(node);
            _state.LatestConditionsToDecision(node);
        }

        /*
                public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
                {
                    var type = _semanticModel.GetTypeInfo(node).Type;
                    if (type == null)
                    {
                        return base.VisitIdentifierName(node);
                    }

                    if (type.SpecialType == SpecialType.System_Boolean)
                    {
                        // it's a condition
                        var c0 = new ConditionInfo
                        {
                            Location = SourceLocation.OfNodeLocation(node),
                            Node = node,
                        };
                        _state.AllocCondition(c0);
                    }
                    return base.VisitIdentifierName(node);
                }

                public override SyntaxNode VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
                {
                    if (node.IsKind(SyntaxKind.LogicalNotExpression))
                    {
                        // collect conditions
                        base.VisitPrefixUnaryExpression(node);
                    }
                    else
                    {
                        // it's a condition
                        var c0 = new ConditionInfo
                        {
                            Location = SourceLocation.OfNodeLocation(node),
                            Node = node,
                        };
                        _state.AllocCondition(c0);
                        GetCurrentDecisionForNode(node).Conditions.Add(c0);
                    }
                    return node;
                }

                public override SyntaxNode VisitBinaryExpression(BinaryExpressionSyntax node)
                {
                    // can it be broken down any further?
                    if (node.IsKind(SyntaxKind.LogicalOrExpression) || node.IsKind(SyntaxKind.LogicalAndExpression))
                    {
                        // collect conditions
                        base.VisitBinaryExpression(node);
                    }
                    else
                    {
                        // it's a condition
                        var c0 = new ConditionInfo
                        {
                            Location = SourceLocation.OfNodeLocation(node),
                            Node = node,
                        };
                        _state.AllocCondition(c0);
                        GetCurrentDecisionForNode(node).Conditions.Add(c0);
                    }
                    return node;
                }

                public override SyntaxNode VisitConditionalExpression(ConditionalExpressionSyntax node)
                {
                    // the condition is certainly a decision.
                    // the whole expression may be a condition though!

                    var c0 = new ConditionInfo
                    {
                        Location = SourceLocation.OfNodeLocation(node.Condition),
                        Node = node.Condition
                    };
                    _state.AllocCondition(c0);
                    GetCurrentDecisionForNode(node).Conditions.Add(c0);

                    return base.VisitConditionalExpression(node);
                }

                private DecisionInfo GetCurrentDecisionForNode(CSharpSyntaxNode node)
                {
                    Debug.Assert(_semanticModel.GetTypeInfo(node).Type.SpecialType == SpecialType.System_Boolean, "Input node must be either condition or decision");

                    while (_decisions.Count > 0 && _decisions.Peek().Node.Parent != node.Parent)
                    {
                        _decisions.Pop();
                    }

                    if (_decisions.Count == 0)
                    {
                        // allocate it
                        var dec = new DecisionInfo
                        {
                            Conditions = new List<ConditionInfo>(),
                            Location = SourceLocation.OfNodeLocation(node),
                            Node = node
                        };
                        _state.AllocDecision(dec);
                        _decisions.Push(dec);
                        return dec;
                    }
                    else
                    {
                        return _decisions.Peek();
                    }
                }*/
    }


    public class DecisionInstrumenter : CSharpSyntaxRewriter
    {
        public DecisionInstrumenter(SemanticModel semanticModel, DecisionInfo decision, InstrumentationState state)
        {
            _semanticModel = semanticModel;
            _decision = decision;
            _state = state;
        }

        private readonly SemanticModel _semanticModel;
        private readonly DecisionInfo _decision;
        private readonly InstrumentationState _state;

        public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
        {
            // it's a condition
            var c0 = new ConditionInfo
            {
                Location = SourceLocation.OfNodeLocation(node),
                Node = node,
            };
            _state.AllocCondition(c0);
            _decision.Conditions.Add(c0);

            return node;
        }

        public override SyntaxNode VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.LogicalOrExpression) || node.IsKind(SyntaxKind.LogicalAndExpression))
            {
                base.VisitBinaryExpression(node);
            }
            else
            {
                // it's a condition
                var c0 = new ConditionInfo
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node,
                };
                _state.AllocCondition(c0);
                _decision.Conditions.Add(c0);
            }

            return node;
        }

        public override SyntaxNode VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            // it's a condition
            var c0 = new ConditionInfo
            {
                Location = SourceLocation.OfNodeLocation(node),
                Node = node,
            };
            _state.AllocCondition(c0);
            _decision.Conditions.Add(c0);

            var type = _semanticModel.GetTypeInfo(node).Type;
            if (type.SpecialType == SpecialType.System_Boolean)
            {
                // woah, this condition is also a decision!
                // e.g. in a > 0 ? (b > 0 ? 1 : 0) : -1
                // the outer expression is a decision.
                // and the inner subexpression is a condition.
                // but if we have
                //   E1 ? E2 : E3
                // its type is boolean,
                // then both E2 and E3 are decisions in their own right.
                base.VisitConditionalExpression(node);
            }

            return node;
        }

        public override SyntaxNode VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            // it's a condition
            var c0 = new ConditionInfo
            {
                Location = SourceLocation.OfNodeLocation(node),
                Node = node,
            };
            _state.AllocCondition(c0);
            _decision.Conditions.Add(c0);
            
            return node;
        }

        public override SyntaxNode VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.LogicalNotExpression))
            {
                base.VisitPrefixUnaryExpression(node);
            }
            else
            {
                // it's a condition
                var c0 = new ConditionInfo
                {
                    Location = SourceLocation.OfNodeLocation(node),
                    Node = node,
                };
                _state.AllocCondition(c0);
                _decision.Conditions.Add(c0);
            }

            return node;
        }
    }

    public class DecisionLocator : CSharpSyntaxRewriter
    {
        public DecisionLocator(SemanticModel semanticModel, InstrumentationState state)
        {
            _semanticModel = semanticModel;
            _state = state;
        }

        private readonly SemanticModel _semanticModel;
        private readonly InstrumentationState _state;

        private void SaveDecision(ExpressionSyntax expression)
        {
            var decision = new DecisionInfo();
            decision.Node = expression;
            decision.Conditions = new List<ConditionInfo>();
            decision.Location = SourceLocation.OfNodeLocation(expression);

            var decisionInstrumenter = new DecisionInstrumenter(_semanticModel, decision, _state);
            decisionInstrumenter.Visit(expression);

            _state.AllocDecision(decision);
        }

        // a condition is a leaf-level boolean expression (cannot be broken down into simpler boolean exprs)
        // e.g. x==2 cannot be broken down.
        //         neither x, nor 2 are booleans
        // e.g. x (boolean) is a condition. it cannot be broken down.
        // e.g. x && b CAN be broken down.
        //         x, b
        // e.g. !x CAN be broken down.
        //         x

        public override SyntaxNode VisitIfStatement(IfStatementSyntax node)
        {
            // the decision:
            // node.Condition
            // go over it to uncover conditions

            SaveDecision(node.Condition);

            base.VisitIfStatement(node);

            return node;
        }

        // NOTE: switch statement is basically a list of if-else's, so technically for a switch of the form:
        // switch (E) { case A: ...; case B: ...; ... case Z: ...; default: ... }
        // we would get decisions of the form:
        //    E==A: ...
        //    E==B: ...
        //    ...
        //    E==Z: ...
        // (excluding the 'default' label, which is similar to the 'else' branch of an 'if')
        //   do we need to add our own 'default'? we want to ensure that each decision evalutes to a T and to a F.
        public override SyntaxNode VisitCaseSwitchLabel(CaseSwitchLabelSyntax node)
        {
            Console.WriteLine("Decision: {0} at {1}", node.Value.ToFullString(), node.Value.GetLocation().GetMappedLineSpan());
            Console.WriteLine("Conditions pending");

            return base.VisitCaseSwitchLabel(node);
        }

        public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node)
        {
            var type = _semanticModel.GetTypeInfo(node.Expression).Type;
            if (type.SpecialType == SpecialType.System_Boolean)
            {
                Console.WriteLine("Decision: {0} at {1}", node.Expression.ToFullString(), node.Expression.GetLocation().GetMappedLineSpan());
                Console.WriteLine("Conditions pending");

                SaveDecision(node.Expression);
            }

            return base.VisitReturnStatement(node);
        }

        public override SyntaxNode VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            var type = _semanticModel.GetTypeInfo(node.Expression).Type;
            if (type.SpecialType == SpecialType.System_Boolean)
            {
                Console.WriteLine("Decision: {0} at {1}", node.Expression.ToFullString(), node.Expression.GetLocation().GetMappedLineSpan());
                Console.WriteLine("Conditions pending");

                SaveDecision(node.Expression);
            }

            base.VisitExpressionStatement(node);

            return node;
        }

        public override SyntaxNode VisitVariableDeclarator(VariableDeclaratorSyntax node)
        {
            if (node.Initializer != null)
            {
                var tp = _semanticModel.GetTypeInfo(node.Initializer.Value);

                if (tp.Type != null && tp.Type.SpecialType == SpecialType.System_Boolean)
                {
                    SaveDecision(node.Initializer.Value);
                }
            }
            return base.VisitVariableDeclarator(node);
        }

        public override SyntaxNode VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            SaveDecision(node.Condition);

            return base.VisitConditionalExpression(node);
        }

        public override SyntaxNode VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            var type = _semanticModel.GetTypeInfo(node.Right).Type;
            if (type.SpecialType == SpecialType.System_Boolean)
            {
                Console.WriteLine("Decision: {0} at {1}", node.Right.ToFullString(), node.Right.GetLocation().GetMappedLineSpan());
                Console.WriteLine("Conditions pending");

                SaveDecision(node.Right);
            }

            base.VisitAssignmentExpression(node);

            return node;
        }

        public override SyntaxNode VisitForStatement(ForStatementSyntax node)
        {
            if (node.Condition != null)
            {
                SaveDecision(node.Condition);
            }

            base.VisitForStatement(node);

            return node;
        }

        public override SyntaxNode VisitWhileStatement(WhileStatementSyntax node)
        {
            SaveDecision(node.Condition);

            base.VisitWhileStatement(node);

            return node;
        }

        public override SyntaxNode VisitDoStatement(DoStatementSyntax node)
        {
            SaveDecision(node.Condition);

            base.VisitDoStatement(node);

            return node;
        }

        /*
        public override SyntaxNode VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (node.Declaration.Variables.Count > 1)
            {
                return node;
            }
            if (node.Declaration.Variables[0].Initializer == null)
            {
                return node;
            }

            var declarator = node.Declaration.Variables.First();
            var variableTypeName = node.Declaration.Type;

            var variableType = (ITypeSymbol)SemanticModel
                .GetSymbolInfo(variableTypeName)
                .Symbol;

            var initializerInfo = SemanticModel.GetTypeInfo(declarator.Initializer.Value);

            if (SymbolEqualityComparer.Default.Equals(variableType, initializerInfo.Type))
            {
                TypeSyntax varTypeName = SyntaxFactory.IdentifierName("var")
                    .WithLeadingTrivia(variableTypeName.GetLeadingTrivia())
                    .WithTrailingTrivia(variableTypeName.GetTrailingTrivia());

                return node.ReplaceNode(variableTypeName, varTypeName);
            }
            else
            {
                return node;
            }
        }*/
    }
}
