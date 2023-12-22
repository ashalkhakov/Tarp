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



        [TestMethod()]
        public void NestedConditionals()
        {
            var test = CreateAssembly("../../../TestSources/NestedConditionals.cs");

            test.SyntaxTrees.Should().HaveCount(1);
            var sourceTree = test.SyntaxTrees.First();
            var model = test.GetSemanticModel(sourceTree);
            var visitor = new McDcVisitor(model);
            visitor.Visit(sourceTree.GetRoot());

            visitor.GetStack().Should().BeEmpty();

            var roots = visitor.GetRoots();
            roots.Should().HaveCount(2);
            roots[0].Kind.Should().Be(McDcNodeKind.Decision);
            roots[0].SyntaxNode.ToFullString().Should().BeEquivalentTo("A && (B ? C : D)");
            roots[0].Children.Should().HaveCount(2);

            roots[0].Children[0].SyntaxNode.ToFullString().Should().BeEquivalentTo("A ");
            roots[0].Children[0].Kind.Should().Be(McDcNodeKind.Condition);
            roots[0].Children[0].Children.Should().BeNull();

            roots[0].Children[1].SyntaxNode.ToFullString().Should().BeEquivalentTo("(B ? C : D)");
            roots[0].Children[1].Kind.Should().Be(McDcNodeKind.Decision);
            roots[0].Children[1].Children.Should().HaveCount(3);
            // B, C, D are the children

            /*
        // a == (b || c) is a decision
        // b || c is a decision also!
        // and also [a] is a decision! because we don't interpet [==]

            however, in this case we kinda 'forget' that b||c and a are both subexpressions of a == (b||c).
            we must fix this.
             */

            roots[1].Kind.Should().Be(McDcNodeKind.Decision);
            roots[1].SyntaxNode.ToFullString().Should().BeEquivalentTo("a == (b || c)");
            roots[1].Children.Should().HaveCount(2);

            roots[1].Children[0].SyntaxNode.ToFullString().Should().BeEquivalentTo("a ");
            roots[1].Children[0].Kind.Should().Be(McDcNodeKind.Condition);
            roots[1].Children[0].Children.Should().BeNull();

            roots[1].Children[1].SyntaxNode.ToFullString().Should().BeEquivalentTo("(b || c)");
            roots[1].Children[1].Kind.Should().Be(McDcNodeKind.Decision);
            roots[1].Children[1].Children.Should().HaveCount(2);
            
            roots[1].Children[1].Children[0].SyntaxNode.ToFullString().Should().BeEquivalentTo("b ");
            roots[1].Children[1].Children[0].Kind.Should().Be(McDcNodeKind.Condition);
            roots[1].Children[1].Children[0].Children.Should().BeNull();
            
            roots[1].Children[1].Children[1].SyntaxNode.ToFullString().Should().BeEquivalentTo("c");
            roots[1].Children[1].Children[1].Kind.Should().Be(McDcNodeKind.Condition);
            roots[1].Children[1].Children[1].Children.Should().BeNull();
        }
    }
}