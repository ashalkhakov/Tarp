using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static System.Console;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tarp
{
    internal class Program
    {
        private const string sampleCode =
        @"using System;
using System.Collections;
using System.Linq;
using System.Text;

namespace HelloWorld
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine(""Hello, World!"");
        }
    }
}";

        static async Task Main(string[] args)
        {
            var test = CreateTestCompilation();

            foreach (SyntaxTree sourceTree in test.SyntaxTrees)
            {
                var model = test.GetSemanticModel(sourceTree);
                var state = new InstrumentationState(model);

                var rewriter = new McDcVisitor(model);

#if false
                // TODO: perhaps narrow down the roots to methods?
                var roots = new Queue<SyntaxNode>();
                roots.Enqueue(sourceTree.GetRoot());
                while (roots.TryDequeue(out var root))
                {
                    var allDecisions =
                        root.DescendantNodesAndSelf(n =>
                            // refuse going under binders. there must be no binders between the root and any node in allDecisions:
                            // if there were, then we would have to avoid variable capture somehow
                            n is not SimpleLambdaExpressionSyntax
                            && n is not LambdaExpressionSyntax
                            && n is not AnonymousMethodExpressionSyntax
                            && n is not AnonymousFunctionExpressionSyntax
                            && n is not ParenthesizedLambdaExpressionSyntax)
                        .Where(c => c is not LiteralExpressionSyntax
                            && c is not PredefinedTypeSyntax
                            && model.GetTypeInfo(c).Type?.SpecialType == SpecialType.System_Boolean)
                        .OfType<CSharpSyntaxNode>()
                        .ToList();

                    foreach (var decision in allDecisions)
                    {
                        var decisionInfo = new DecisionInfo()
                        {
                            Location = SourceLocation.OfNodeLocation(decision),
                            Node = decision,
                            Conditions = new()
                        };

                        /*
                         * as you go inside the expression, when does it cease to be of boolean type?
                         */

                        // collect inputs
                        // - if it's an identifier, it's a boolean then. take it. don't duplicate
                        // - otherwise
                        //   - don't go under a binder. stop.
                        //     AnonymousMethodExpressionSyntax / SimpleLambdaExpressionSyntax / ParenthesizedLambdaExpressionSyntax
                        //   - if Parenthesized expr, just go inside and do what's needed
                        //   - if logical not, logical and, logical or: explore the subtrees.
                        //   - else: stop for now.
                        //     - but, E ? E1 : E2 may work if we go inside E, E1, E2 too
                        var inputs = decision.ChildNodes()
                            .Where(c =>
                                c is IdentifierNameSyntax ident && model.GetTypeInfo(ident).Type?.SpecialType == SpecialType.System_Boolean

                                || !(
                                    c is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.LogicalNotExpression)
                                    || c is BinaryExpressionSyntax binary && (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression))
                                    || c is LiteralExpressionSyntax literal
                                    || c is ParenthesizedExpressionSyntax parenthesized
                                    )
                                );

                        // FIXME: how does this work with scoping???
                        var variables = new HashSet<IdentifierNameSyntax>();

                        foreach (var input in inputs)
                        {
                            if (input is IdentifierNameSyntax id)
                            {
                                if (variables.Contains(id))
                                {
                                    continue;
                                }
                                variables.Add(id);
                            }

                            var c = new ConditionInfo()
                            {
                                Location = SourceLocation.OfNodeLocation((CSharpSyntaxNode)input),
                                Node = (CSharpSyntaxNode)input,
                            };
                            state.AllocCondition(c);
                            decisionInfo.Conditions.Add(c);

                            if (input is IdentifierNameSyntax identifierName)
                            {
                                continue;
                            }

                            // an atomic expression may contain boolean expressions
                            foreach (var child in input.ChildNodes())
                            {
                                roots.Enqueue(child);
                            }
                        }

                        state.AllocDecision(decisionInfo);

                        //var fooBar = new DecisionInstrumenter(model, decisionInfo, state);
                        //fooBar.Visit(decision);

                        // how do I tease out conditions? do I need it?
                        // do I just need to tease out 'inputs'?
                        // - inputs are boolean variables or atomic expressions (e.g. x>0 is an input, and y is an input)
                        // - if you find a decision though, e.g. a conditional, you may want to process it in the same way as the decisions at the root level
                        //   generally if you find an atomic expression, then check if has chidlren expressions of boolean type. each such child
                        //       is itself a decision. and you repeat the whole process again.

                        // - binary expression with ||, && contains left/right conditions OR further decisions
                        // - unary expression with ! contains a condition OR further decision
                        // - variable aka identifier is a condition
                        // - everything else is a condition
                        //var conditions = decision.ChildNodes();
                    }
                }
#endif
                rewriter.Visit(sourceTree.GetRoot());
                /*
                var newSource = rewriter.Visit(sourceTree.GetRoot());
                if (newSource != sourceTree.GetRoot())
                {
                    Console.WriteLine(sourceTree.FilePath);
                    Console.WriteLine(newSource.ToFullString());
                }*/
            }
        }

        private static Compilation CreateTestCompilation()
        {
            var programPath = @"..\..\..\Program.cs";
            var programText = File.ReadAllText(programPath);
            var programTree =
                           CSharpSyntaxTree.ParseText(programText)
                                           .WithFilePath(programPath);

            var rewriterPath = @"..\..\..\Sample.cs";
            var rewriterText = File.ReadAllText(rewriterPath);
            var rewriterTree =
                           CSharpSyntaxTree.ParseText(rewriterText)
                                           .WithFilePath(rewriterPath);

            SyntaxTree[] sourceTrees = { /*programTree,*/ rewriterTree };

            MetadataReference mscorlib =
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
            MetadataReference codeAnalysis =
                    MetadataReference.CreateFromFile(typeof(SyntaxTree).Assembly.Location);
            MetadataReference csharpCodeAnalysis =
                    MetadataReference.CreateFromFile(typeof(CSharpSyntaxTree).Assembly.Location);

            MetadataReference[] references = { mscorlib, codeAnalysis, csharpCodeAnalysis };

            return CSharpCompilation.Create("TransformationCS",
                sourceTrees,
                references,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        }
    }
}
