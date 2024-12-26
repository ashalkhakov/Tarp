using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp.Cfg;
using FluentAssertions;

namespace TarpTests.Cfg;

[TestClass]
public class CFGBuilderTests
{
    [TestMethod]
    public void TestLocalDeclarationsAndSequence()
    {
        string code = @"
public class MyClass
{
    public int MyMethod(int x, int y)
    {
        x += 1;
        var z = 2 * x;

        y = x + 2;

        return x + y + z;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Json/{nameof(TestLocalDeclarationsAndSequence)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestIfThenElseAndWhile()
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
        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Json/{nameof(TestIfThenElseAndWhile)}.gv");
        results.Should().BeEquivalentTo(expected);
    }


    List<string> GetCFGsFromSource(string code)
    {
        // Parse the code
        SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        var results = new List<string>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Cast<MethodDeclarationSyntax>())
        {
            var cfgBuilder = new CFGBuilder();

            var cfg = cfgBuilder.Build(method);

            cfg.SanityCheck();

            results.Add(cfg.EmitCFG());
        }

        return results;
    }
}
