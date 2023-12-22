using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Generic;

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

namespace Tarp.Types
{
    public class McDcNode
    {
        public McDcNodeKind Kind { get; set; }
        public CSharpSyntaxNode SyntaxNode { get; set; }
        public List<McDcNode> Children { get; set; }
    }
}
