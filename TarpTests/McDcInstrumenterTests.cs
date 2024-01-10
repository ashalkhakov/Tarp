using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Tarp.Types;

namespace Tarp.Tests;

[TestClass]
public class McDcInstrumenterTests
{
    [TestMethod]
    public void Test1()
    {
        RunInstrumenterOnFile("../../../TestSources/Instrument1.cs");
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

        var sourceTree = test.SyntaxTrees.Single();
        var model = test.GetSemanticModel(sourceTree);
        /*
        var instrumenter = new McDcInstrumenter();
        
        var newRoot = instrumenter.Visit(sourceTree.GetRoot());

        if (newRoot != sourceTree.GetRoot())
        {
            Console.WriteLine(sourceTree.FilePath);
            var printed = newRoot.ToFullString();

            Console.WriteLine(printed);
        }
        */
        var methods = sourceTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>();
        foreach (var method in methods)
        {
            //var methodCfg = ControlFlowGraph.Create(method, model);
            //instrumenter.Instrument(methodCfg);

            //var methodCfg = Cfg.Build(method);

            //var foo = new CfgBuildingVisitor2();
            //foo.Visit(method);
            //var dot = foo.EmitCFG();

            var bar = new CfgBuildingVisitor3(method);
            bar.Visit(method);

            var dot = bar.GraphBuilder.EmitCFG();
        }
    }
}
