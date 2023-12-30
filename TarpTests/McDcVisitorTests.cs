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
using static Tarp.McDcFunctions;
using System.Drawing;
using System.Diagnostics;

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

            visitor.
            Stack.Should().BeEmpty();

            var roots = visitor.Roots;

            throw new NotImplementedException();
        }

        [TestMethod()]
        public void NestedConditionals()
        {
            var visitor = RunVisitorOnFile("../../../TestSources/NestedConditionals.cs");

            visitor.
            Stack.Should().BeEmpty();

            var roots = visitor.Roots;
            roots.Should().HaveCount(2);

            McDcTreeOfNode(roots[0]).Should().BeEquivalentTo(
                new McDcDec("A && (B ? C : D)", new()
                {
                    new McDcCond("A"),
                    new McDcDec("B ? C : D", new()
                    {
                        new McDcCond("B"),
                        new McDcCond("C"),
                        new McDcCond("D")
                    })
                }));

            McDcTreeOfNode(roots[1]).Should().BeEquivalentTo(
                new McDcDec("a == (b || c)", new()
                {
                    new McDcCond("a "),
                    new McDcDec("b || c", new()
                    {
                        new McDcCond("b"),
                        new McDcCond("c")
                    })
                }));
        }

        [TestMethod]
        public void MaskedConditions()
        {
            var visitor = RunVisitorOnFile("../../../TestSources/MaskedConditions.cs");

            visitor.
            Stack.Should().BeEmpty();

            var roots = visitor.Roots;
            roots.Should().HaveCount(1);

            McDcTreeOfNode(roots[0]).Should().BeEquivalentTo(
                new McDcDec("(A || B) && (C || D)", new()
                {
                    new McDcDec("A || B", new() { new McDcCond("A"), new McDcCond("B") }),
                    new McDcDec("C || D", new() { new McDcCond("C"), new McDcCond("D") })
                }));
        }

        public abstract record McDcTree();
        public record McDcCond(string Expr) : McDcTree();
        public record McDcDec(string Expr, List<McDcTree> Children) : McDcTree();

        private McDcTree McDcTreeOfNode(McDcNode node)
        {
            Debug.Assert(node != null);
            
            if (node.Kind == McDcNodeKind.Condition)
            {
                Debug.Assert(node.Children == null);
                
                return new McDcCond(node.SyntaxNode.ToFullString());
            }
            else
            {
                Debug.Assert(node.Children != null && node.Children.Count > 0);

                var children = node.Children.Select(c => McDcTreeOfNode(c));
                return new McDcDec(node.SyntaxNode.ToFullString(), children.ToList());
            }
        }

        void CheckMarkerFunction(MarkerFunction f, int falseBit, bool falseDone, int falseMask, int trueBit, bool trueDone, int trueMask, string node, PathElement path)
        {
            Debug.Assert(f != null);

            f.FalseBit.Should().Be(falseBit);
            f.FalseDone.Should().Be(falseDone);
            f.FalseMask.Should().Be(falseMask);
            f.TrueBit.Should().Be(trueBit);
            f.TrueDone.Should().Be(trueDone);
            f.TrueMask.Should().Be(trueMask);
            f.Node.SyntaxNode.ToFullString().Should().BeEquivalentTo(node);
            PathElementOfElem(f.Path).Should().BeEquivalentTo(path);
        }


        // TODO: test with https://tsapps.nist.gov/publication/get_pdf.cfm?pub_id=923681
        // TODO: is this useful? https://github.com/Armin-Montigny/MCDC
        // TODO: is this useful? https://www.isp.uni-luebeck.de/sites/default/files/McdcThesisDinoLange.pdf see Iterative MC/DC Evaluation (python code)
        // TODO: test with https://docs.google.com/document/d/1kG_Tm0caXzegua_ScRCSEq8wVb49mXPi/edit
        // TODO: THE source of truth https://www.tc.faa.gov/its/worldpac/techrpt/ar01-18.pdf
        // TODO what are the bitmap coverage objects? I think we need something similar https://llvm.org/devmtg/2022-11/slides/TechTalk4-MCDC-EnablingSafetyCriticalCodeCoverage.pdf
        [TestMethod]
        public void MarkingFunctionsTest()
        {
            var visitor = RunVisitorOnFile("../../../TestSources/MarkingFunctions.cs");

            visitor.
            Stack.Should().BeEmpty();
            
            var roots = visitor.Roots;

            var functions = new McDcFunctions();
            var markerFunctions = functions.CreateMarkers(roots[0]);

            markerFunctions.Should().HaveCount(3);
            CheckMarkerFunction(markerFunctions[0], 2, true, -1, 1, false, -1, "a == 5",
                new PathElement(
                    "(a == 5) && (b || c)",
                    SideOfParent.Left,
                    null));
            CheckMarkerFunction(markerFunctions[1], 8, false, -1, 4, true, -1, "b ",
                new PathElement
                (
                    "b || c", 
                    SideOfParent.Left, 
                    new PathElement
                    (
                        "(a == 5) && (b || c)", 
                        SideOfParent.Right,
                        null
                    )
                ));
            CheckMarkerFunction(markerFunctions[2], 32, true, -4, 16, true, -13, "c",
                new PathElement(
                    "b || c", 
                    SideOfParent.Right, 
                    new PathElement
                    (
                        "(a == 5) && (b || c)", 
                        SideOfParent.Right, 
                        null
                    )
                ));
        }

        private PathElement? PathElementOfElem(PathElem? p)
        {
            if (p == null)
            {
                return null;
            }
            var x = new PathElement(p.Expr.SyntaxNode.ToFullString(), p.Side, PathElementOfElem(p.Up));
            return x;
        }

        public record PathElement(string Expr, SideOfParent Side, PathElement? Up);
    }
}