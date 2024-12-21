using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp;

namespace TarpTests;

[TestClass]
public class SourceToNfTests
{
    [TestMethod]
    [DataTestMethod]
    [DataRow("../../../TestSources/Instrument1.cs")]
    [DataRow("../../../TestSources/AllBooleanExpressions.cs")]
    [DataRow("../../../TestSources/NewVariablesAtReturns.cs")]
    public void Test1(string filename)
    {
        RunInstrumenterOnFile(filename);
    }

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

    private void RunInstrumenterOnFile(string filepath)
    {
        var test = CreateAssembly(filepath);

        var gen = new FreshNameGenerator("__tmp");
        var sourceToNf = new SourceToNf(gen);

        foreach (var sourceTree in test.SyntaxTrees)
        {
            var newRoot = sourceToNf.Visit(sourceTree.GetRoot());

            Console.WriteLine(sourceTree.FilePath);
            var printed = newRoot.ToFullString();

            Console.WriteLine(printed);
            test.ReplaceSyntaxTree(sourceTree, newRoot.SyntaxTree);
        }
    }
}
