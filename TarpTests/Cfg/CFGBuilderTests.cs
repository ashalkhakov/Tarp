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

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestLocalDeclarationsAndSequence)}.gv");
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

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestIfThenElseAndWhile)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestLabel()
    {
        string code = @"
public class MyClass
{
    public int Method(int a)
    {
        int r;

        r = 1;
        r++;
        r *= 2;

        lab1:
        lab2: r += 3;

        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestLabel)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestIfElse()
    {
        string code = @"
public class MyClass
{
    public int Method1(int a)
    {
        int r;

        if (a > 0)
        {
            r = 1;
        }
        else
        {
            r = -1;
        }

        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestIfElse)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestIfElseIf()
    {
        string code = @"
class MyClass
{
    public int Method1_1(int a)
    {
        int r;

        if (a > 0)
        {
            r = 1;
        }
        else if (a < 0)
        {
            r = -1;
        }
        else
        {
            r = 0;
        }
        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestIfElseIf)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestIf()
    {
        string code = @"
class MyClass
{
    public int Method1_2(int a)
    {
        if (a > 0)
        {
            return 1;
        }
        return -1;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestIf)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestIfNest()
    {
        string code = @"
class MyClass
{
    public int Method1_2(int a, int b)
    {
        int ret;
        if (a > 0)
        {
            if (b > 0)
            {
                ret = 3;
            }
            else
            {
                ret = 2;
            }
        }
        else
        {
            if (b > 0)
            {
                ret = 1;
            }
            else
            {
                ret = 0;
            }
        }
        return ret;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestIfNest)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestIfNest2()
    {
        string code = @"
class MyClass
{
    public int Method1_2(int a, int b)
    {
        int ret = 0;
        if (a > 0)
        {
            if (b > 0)
            {
                ret = 3;
            }
            else
            {
                ret = 2;
            }
        }
        return ret;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestIfNest2)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestIfNest2Return()
    {
        string code = @"
class MyClass
{
    public int Method1_2(int a, int b)
    {
        if (a > 0)
        {
            if (b > 0)
            {
                return 3;
            }
            else
            {
                return 2;
            }
        }
        return 0;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestIfNest2Return)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestIfNestReturn()
    {
        string code = @"
class MyClass
{
    public int Method1_2(int a, int b)
    {
        if (a > 0)
        {
            if (b > 0)
            {
                return 3;
            }
            else
            {
                return 2;
            }
        }
        else
        {
            if (b > 0)
            {
                return 1;
            }
            else
            {
                return 0;
            }
        }
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestIfNestReturn)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestWhile()
    {
        string code = @"public class MyClass
{
    public int Method2(int a)
    {
        int r = 1;

        while (a > 0)
        {
            r *= a;
            a--;
        }

        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestWhile)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestWhileNested()
    {
        string code = @"
class MyClass
{
    public void ShowMatrix(int[, ] matrix)
    {
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);

        int i = 0;
        while (i < rows)
        {
            int j = 0;
            while (j < cols)
            {
                Console.Write(""{0}\t"", matrix[i, j]);
                j++;
            }
            Console.WriteLine();
            i++;
        }
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestWhileNested)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestWhileBreak()
    {
        string code = @"public class MyClass
{
    public void Method2_1(int a)
    {
        var i = 0;
        while (true)
        {
            if (i > a)
            {
                break;
            }
            i++;
        }
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestWhileBreak)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestWhileContinue()
    {
        string code = @"public class MyClass
{
    public void Method2_1(int a)
    {
        var i = 0;
        while (true)
        {
            if (i <= a)
            {
                i++;
                continue;
            }
            break;
        }
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestWhileContinue)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestDo()
    {
        string code = @"
public class MyClass
{
    public int Method3(int c)
    {
        int r = 1;
        do
        {
            r *= c;
            c--;
        }
        while (c > 0);
        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestDo)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestDoBreak()
    {
        string code = @"public class MyClass
{
    public void Method2_1(int a)
    {
        var i = 0;
        do
        {
            if (i > a)
            {
                break;
            }
            i++;
        } while (true);
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestDoBreak)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestDoContinue()
    {
        string code = @"public class MyClass
{
    public void Method2_1(int a)
    {
        var i = 0;
        do
        {
            if (i <= a)
            {
                i++;
                continue;
            }
            break;
        } while (true);
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestDoContinue)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestDoNested()
    {
        string code = @"
class MyClass
{
    int Method()
    {
        int i, j;

        i = 1;
        do {
            j = 1;
            do {
                Console.WriteLine(""i = {0}, j = {1}"", i, j);
                j++;
            } while (j <= 3);
            i++;
        } while (i <= 2);

        return 0;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestDoNested)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestFor()
    {
        string code = @"class MyClass
{
    public int Method4(int c)
    {
        int r;
        for (r = 1; c > 0; c--)
        {
            r *= c;
        }
        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestFor)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestForBreak()
    {
        string code = @"class MyClass
{
    public int Method4(int c)
    {
        int r;
        for (r = 1; ; c--)
        {
            if (c <= 0)
            {
                break;
            }
            r *= c;
        }
        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestForBreak)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestForContinue()
    {
        string code = @"class MyClass
{
    public int Method4(int c)
    {
        int r;
        for (r = 1; ; c--)
        {
            if (c > 0)
            {
                r *= c;
                continue;
            }
            break;
        }
        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestForContinue)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestForNoInitializerNoIncrement()
    {
        string code = @"class MyClass
{
    public int Method4(int c)
    {
        int r = 1;
        for (; c > 0; )
        {
            r *= c;
            c--;
        }
        return r;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestForNoInitializerNoIncrement)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestForNested()
    {
        string code = @"
class MyClass
{
    public void ShowMatrix(int[, ] matrix)
    {
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);

        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                Console.Write(""{0}\t"", matrix[i, j]);
            }
            Console.WriteLine();
        }
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestForNested)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestForNestedReturn()
    {
        string code = @"
class MyClass
{
    public void ShowMatrix(int[, ] matrix)
    {
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);

        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                Console.Write(""{0}\t"", matrix[i, j]);
                return;
            }
            Console.WriteLine();
        }
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestForNestedReturn)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestForEach()
    {
        string code = @"class MyClass
{
    public int Method4()
    {
        var c = 0;
        foreach (var r in new[] { 1, 2, 3, 4, 5 })
        {
            c += r;
        }
        return c;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestFor)}.gv");
        results.Should().BeEquivalentTo(expected);
    }


    [TestMethod]
    public void TestSwitch()
    {
        string code = @"class MyClass
{
    public int Method5(int c)
    {
        switch (c)
        {
            case 0:
                return 1;
            case 1:
            default:
                return 3;
            case 2:
            case 3:
                return 2;
        }
    }
    public int Method5_0(int c)
    {
        int r;
        switch (c)
        {
            case 0:
                r = 1;
                break;
            case 1:
            default:
                r = 3;
                break;
            case 2:
            case 3:
                r = 2;
                break;
        }
        return r;
    }

    public int Method5_1(int c)
    {
        switch (c)
        {
            case 0:
                return 1;
            case 1:
                return 2;
        }
        return 3;
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestSwitch)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestBinarySearch()
    {
        string code = @"
class MyClass
{
    int BinarySearch(int[] arr, int left, int right, int key)
    {
        while (left <= right)
        {
            var mid = left + (right - left) / 2;

            if (arr[mid] == key)
            {
                return mid;
            }
            else if (arr[mid] < key)
            {
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        return -1;
    }
}";
        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestBinarySearch)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestGoto()
    {
        string code = @"
class MyClass
{
    int F(int x)
    {
        int c = 0;

        Lstart:

        Console.WriteLine(c);

        c++;

        if (c < x)
        {
            goto Lstart;
        }
    }
}";
        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestGoto)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestGotoForward()
    {
        string code = @"
class MyClass
{
    int F(int x)
    {
        int c = 0;
        IEnumerator<int> tmp;
        tmp = Enumerable.Range(0, x).GetEnumerator();

        goto IL_001a;
        IL_000f:
        c += tmp.Current;
        IL_001a:
        if (tmp.MoveNext())
        {
            goto IL_000f;
        }
        goto IL_002e;

        IL_002e:
        return c;
    }
}";
        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestGotoForward)}.gv");
        results.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void TestGotoBreakOutOfNestedFors()
    {
        string code = @"
class MyClass
{
    void Method(int[,] matrix, int x)
    {
        int i, j;

        for (i = 0; i < matrix.GetLength(0); i++)
        {
            for (j = 0; j < matrix.GetLength(1); j++)
            {
                if (matrix[i,j] == x)
                {
                    goto end;
                }
            }
        }

        Console.WriteLine(""{0} not found"", x);
        return;
        
        end:
        Console.WriteLine(""{0} located at {1}, {2}"", x, i, j);
    }
}";

        var results = GetCFGsFromSource(code);

        var expected = File.ReadAllText($@"Cfg/Data/{nameof(TestGotoBreakOutOfNestedFors)}.gv");
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
