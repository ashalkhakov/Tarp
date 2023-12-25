using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using FluentAssertions;
using Tarp.Types;

namespace Tarp.Tests
{
    [TestClass()]
    public class McDcVisitorTests
    {
        private static Compilation CreateAssembly(string filePath)
        {
            var fileText = File.ReadAllText(filePath);
            var fileTree = CSharpSyntaxTree.ParseText(fileText)
                                           .WithFilePath(filePath);

            SyntaxTree[] sourceTrees = { fileTree };

            MetadataReference mscorlib =
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

            MetadataReference[] references = { mscorlib };

            return CSharpCompilation.Create("TransformationCS",
                sourceTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        private McDcVisitor RunVisitorOnFile(string filepath)
        {
            var test = CreateAssembly(filepath);

            var sourceTree = test.SyntaxTrees.Single();
            var model = test.GetSemanticModel(sourceTree);
            var visitor = new McDcVisitor(model);
            visitor.Visit(sourceTree.GetRoot());

            return visitor;
        }

        [TestMethod]
        [Ignore("Not implemented yet")]
        public void SimpleDecisions()
        {
            var visitor = RunVisitorOnFile("../../../TestSources/SimpleDecisions.cs");

            visitor.GetStack().Should().BeEmpty();

            var roots = visitor.GetRoots();

            throw new NotImplementedException();
        }

        [TestMethod()]
        public void NestedConditionals()
        {
            var visitor = RunVisitorOnFile("../../../TestSources/NestedConditionals.cs");

            visitor.GetStack().Should().BeEmpty();

            var roots = visitor.GetRoots();
            roots.Should().HaveCount(2);
            roots[0].Kind.Should().Be(McDcNodeKind.Decision);
            roots[0].SyntaxNode.ToFullString().Should().BeEquivalentTo("A && (B ? C : D)");
            roots[0].Children.Should().HaveCount(2);

            roots[0].Children[0].SyntaxNode.ToFullString().Should().BeEquivalentTo("A ");
            roots[0].Children[0].Kind.Should().Be(McDcNodeKind.Condition);
            roots[0].Children[0].Children.Should().BeNull();

            roots[0].Children[1].SyntaxNode.ToFullString().Should().BeEquivalentTo("B ? C : D");
            roots[0].Children[1].Kind.Should().Be(McDcNodeKind.Decision);
            roots[0].Children[1].Children.Should().HaveCount(3);
            // TODO: check that B, C, D are the children

            roots[1].Kind.Should().Be(McDcNodeKind.Decision);
            roots[1].SyntaxNode.ToFullString().Should().BeEquivalentTo("a == (b || c)");
            roots[1].Children.Should().HaveCount(2);

            roots[1].Children[0].SyntaxNode.ToFullString().Should().BeEquivalentTo("a ");
            roots[1].Children[0].Kind.Should().Be(McDcNodeKind.Condition);
            roots[1].Children[0].Children.Should().BeNull();

            roots[1].Children[1].SyntaxNode.ToFullString().Should().BeEquivalentTo("b || c");
            roots[1].Children[1].Kind.Should().Be(McDcNodeKind.Decision);
            roots[1].Children[1].Children.Should().HaveCount(2);
            
            roots[1].Children[1].Children[0].SyntaxNode.ToFullString().Should().BeEquivalentTo("b ");
            roots[1].Children[1].Children[0].Kind.Should().Be(McDcNodeKind.Condition);
            roots[1].Children[1].Children[0].Children.Should().BeNull();
            
            roots[1].Children[1].Children[1].SyntaxNode.ToFullString().Should().BeEquivalentTo("c");
            roots[1].Children[1].Children[1].Kind.Should().Be(McDcNodeKind.Condition);
            roots[1].Children[1].Children[1].Children.Should().BeNull();
        }

        [TestMethod]
        public void MaskedConditions()
        {
            var visitor = RunVisitorOnFile("../../../TestSources/MaskedConditions.cs");

            visitor.GetStack().Should().BeEmpty();

            var roots = visitor.GetRoots();
            roots.Should().HaveCount(1);

            CheckNode(roots[0], McDcNodeKind.Decision, "(A || B) && (C || D)", 2);
            
            CheckNode(roots[0].Children[0], McDcNodeKind.Decision, "A || B", 2);
            CheckNode(roots[0].Children[0].Children[0], McDcNodeKind.Condition, "A ", 0);
            CheckNode(roots[0].Children[0].Children[1], McDcNodeKind.Condition, "B", 0);

            CheckNode(roots[0].Children[1], McDcNodeKind.Decision, "C || D", 2);
            CheckNode(roots[0].Children[1].Children[0], McDcNodeKind.Condition, "C ", 0);
            CheckNode(roots[0].Children[1].Children[1], McDcNodeKind.Condition, "D", 0);
        }

        void CheckNode(McDcNode node, McDcNodeKind kind, string syntaxNode, int numChildren)
        {
            node.Kind.Should().Be(kind);
            node.SyntaxNode.ToFullString().Should().BeEquivalentTo(syntaxNode);
            if (kind == McDcNodeKind.Condition)
            {
                numChildren.Should().Be(0);
                node.Children.Should().BeNull();
            }
            else
            {
                node.Children.Should().HaveCount(numChildren);
            }
        }

        void CheckMarkerFunction(McDcFunctions.MarkerFunction f, int falseBit, bool falseDone, int falseMask, int trueBit, bool trueDone, int trueMask, string node)
        {
            f.FalseBit.Should().Be(falseBit);
            f.FalseDone.Should().Be(falseDone);
            f.FalseMask.Should().Be(falseMask);
            f.TrueBit.Should().Be(trueBit);
            f.TrueDone.Should().Be(trueDone);
            f.TrueMask.Should().Be(trueMask);
            f.Node.SyntaxNode.ToFullString().Should().BeEquivalentTo(node);
        }

        [TestMethod]
        public void MarkingFunctionsTest()
        {
            var visitor = RunVisitorOnFile("../../../TestSources/MarkingFunctions.cs");

            visitor.GetStack().Should().BeEmpty();
            
            var roots = visitor.GetRoots();

            var functions = new McDcFunctions();
            var markerFunctions = functions.CreateMarkers(roots[0]);

            markerFunctions.Should().HaveCount(3);
            CheckMarkerFunction(markerFunctions[0], 2, true, -1, 1, false, -1, "a == 5");
            // TODO:   path {side=left,up=null,node="a == 5 && (b || c)"}
            CheckMarkerFunction(markerFunctions[1], 8, false, -1, 4, true, -1, "b ");
            // TOOD: path {side=left,up={side=right,up=null,node="a == 5 && (b || c)"},node="b || c"}
            CheckMarkerFunction(markerFunctions[2], 32, true, -4, 16, true, -13, "c");
        }
    }
}