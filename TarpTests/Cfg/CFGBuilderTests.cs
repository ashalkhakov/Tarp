using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp.Cfg;

namespace TarpTests.Cfg;

[TestClass]
public class CFGBuilderTests
{
    [TestMethod]
    public void Test()
    {
        string code = @"
public class MyClass
{
    public void MyMethod()
    {
        bool x = true;
        bool y = false;
        if (x && !y)
        {
            Console.WriteLine(""Decision made"");
        }
        else
        {
            Console.WriteLine(""Alternative decision"");
        }

        while (x)
        {
            Console.WriteLine(""Looping"");
            x = false;
        }
    }
}";

        // Parse the code
        SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        // Create and use the CFG builder
        var cfgBuilder = new CFGBuilder();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Cast<MethodDeclarationSyntax>())
        {
            var cfg = cfgBuilder.Build(method);

            // Output the CFG
            var dot = cfg.EmitCFG();
            Console.WriteLine("Control Flow Graph");
            Console.WriteLine(dot);
        }
    }
}
