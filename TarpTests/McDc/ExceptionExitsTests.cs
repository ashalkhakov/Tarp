using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using Tarp.McDc;

namespace TarpTests.McDc;

// the exception exits option: functions left by an exception (an exception filter around each body)
[TestClass]
public class ExceptionExitsTests
{
    private const string Source = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
public struct S { public int A; public S(int a) { if (a < 0) throw new ArgumentException(); A = a; } }
public static class T
{
    public static int Thrower(int x) { if (x < 0) throw new ArgumentException(""negative""); return x; }
    public static int Caller(int x) => Thrower(x) + 1;
    public static int Catches(int x) { try { return Thrower(x); } catch (ArgumentException) { return -1; } }
    public static IEnumerable<int> Iterate(int n) { for (var i = 0; i < n; i++) yield return i; }
    public static int Sum(int n) { var s = 0; foreach (var v in Iterate(n)) s += v; return s; }
    public static async Task<int> Later(int x) { await Task.Yield(); return Thrower(x); }
    public static bool Out(int x, out int y) { y = Thrower(x); return true; }
    public static int Struct(int a) => new S(a).A;
}";

    private static Exception Throws(McDcEndToEndTests.Program p, string method, params object?[] args)
    {
        var call = () => p.Run(method, args);
        return call.Should().Throw<TargetInvocationException>().Which.InnerException!;
    }

    [TestMethod]
    public void FunctionsLeftByAnException()
    {
        var p = McDcEndToEndTests.Build(Source, exceptionExits: true);
        p.Check("Caller", 1);
        p.Check("Catches", -1);
        p.Check("Sum", 3);
        p.Check("Struct", 2);

        // the exception arrives as it was thrown, from Thrower: not caught, not rethrown
        var e = Throws(p, "Caller", -1);
        e.Should().BeOfType<ArgumentException>().Which.Message.Should().Be("negative");
        e.StackTrace!.Split('\n')[0].Should().Contain("T.Thrower");

        Throws(p, "Struct", -1).Should().BeOfType<ArgumentException>();
        var later = () => ((Task<int>)p.Run("Later", -1)!).GetAwaiter().GetResult();
        later.Should().Throw<ArgumentException>();
        var args = new object?[] { -1, null };
        Throws(p, "Out", args).Should().BeOfType<ArgumentException>();

        var functions = p.Coverage().Functions.ToDictionary(f => f.Function.Name);
        functions["T.Thrower(int)"].LeftByException.Should().BeTrue("its own throw leaves it");
        functions["T.Caller(int)"].LeftByException.Should().BeTrue("Thrower's exception left it");
        functions["T.Catches(int)"].LeftByException.Should().BeFalse("it catches Thrower's exception");
        functions["T.Later(int)"].LeftByException.Should().BeTrue("its task faulted");
        functions["T.Out(int, out int)"].LeftByException.Should().BeTrue();
        functions["S.S(int)"].LeftByException.Should().BeTrue();
        // not a gap: the other exits are as without the option
        functions["T.Catches(int)"].Exits.Should().Be(2);
        functions["T.Catches(int)"].ExitsNotTaken.Should().BeEmpty("a return counts when it starts");

        // yield return can't be in a try block with a catch: iterators aren't wrapped
        var iterate = functions["T.Iterate(int)"];
        iterate.Function.Exits.Select(id => p.Map.Probes[id].Kind).Should().NotContain(ProbeEntry.ExceptionExit);
        iterate.LeftByException.Should().BeNull();

        var report = McDcReport.Format(p.Map, p.Results(), p.Counts());
        report.Should().NotContain("an exception\n").And.MatchRegex(@"Exceptions +\d+/\d+ .*not counted as gaps");
    }

    [TestMethod]
    public void OffByDefault()
    {
        var p = McDcEndToEndTests.Build(Source);
        p.Map.Probes.Should().NotContain(x => x.Kind == ProbeEntry.ExceptionExit);
        p.InstrumentedSource.Should().NotContain("Escaped");
    }
}
