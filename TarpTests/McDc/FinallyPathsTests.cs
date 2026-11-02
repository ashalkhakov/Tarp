using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using Tarp.McDc;

namespace TarpTests.McDc;

// which way a finally was entered (a local says how far the protected code got)
[TestClass]
public class FinallyPathsTests
{
    private const string Source = @"
using System;
using System.Collections.Generic;
public static class T
{
    public static string Paths(int mode)
    {
        var log = """";
        try
        {
            if (mode == 1) throw new InvalidOperationException();
            if (mode == 2) throw new ArgumentException();
            if (mode == 3) return ""returned"";
            log += ""body"";
        }
        catch (InvalidOperationException)
        {
            log += ""caught"";
        }
        finally
        {
            log += ""+finally"";
        }
        return log;
    }

    public static int Loop(int n) { var i = 0; while (true) { try { if (i >= n) break; i++; } finally { } } return i; }

    public static void Rethrow() { try { throw new InvalidOperationException(); } catch (InvalidOperationException) { throw; } finally { } }

    public static int CatchAll(bool fail) { try { if (fail) throw new Exception(); return 1; } catch { return 2; } finally { } }

    public static IEnumerable<int> Iterate() { try { yield return 1; } finally { } }
    public static int First() { foreach (var x in Iterate()) return x; return 0; }
}";

    private static int LineOf(string text) =>
        Source.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;

    private static void Throws(McDcEndToEndTests.Program p, string method, params object?[] args)
    {
        var call = () => p.Run(method, args);
        call.Should().Throw<TargetInvocationException>();
    }

    [TestMethod]
    public void TheWaysIntoAFinally()
    {
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Paths", 0);
        p.Check("Paths", 1);
        p.Check("Paths", 3);

        var paths = p.Coverage().Switches.Single(s => s.Switch.Kind == SwitchEntry.Finally && s.Switch.Line == LineOf("        finally"));
        paths.Switch.Labels.Should().Equal(
            "an exception from the try block, not caught here",
            "the try block completed",
            "a return, break, continue or goto out of the try block",
            "catch (InvalidOperationException) completed");
        paths.NotTaken.Should().Equal("an exception from the try block, not caught here");

        Throws(p, "Paths", 2);
        p.Coverage().Switches.Single(s => s.Switch.Kind == SwitchEntry.Finally && s.Switch.Line == LineOf("        finally")).NotTaken.Should().BeEmpty();
    }

    [TestMethod]
    public void JumpsRethrowsAndCatchAlls()
    {
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Loop", 2);
        Throws(p, "Rethrow");
        p.Check("CatchAll", false);
        p.Check("CatchAll", true);
        p.Check("First");

        var finallies = p.Coverage().Switches.Where(s => s.Switch.Kind == SwitchEntry.Finally).ToDictionary(s => s.Switch.Line);

        // break out of the loop around the try: a jump out of the try block
        var loop = finallies[LineOf("public static int Loop")];
        loop.Switch.Labels.Should().Equal("an exception from the try block, not caught here", "the try block completed",
            "a return, break, continue or goto out of the try block");
        loop.NotTaken.Should().Equal("an exception from the try block, not caught here");

        var rethrow = finallies[LineOf("public static void Rethrow")];
        rethrow.Switch.Labels.Should().Equal("an exception from the try block, not caught here", "an exception from catch (InvalidOperationException)");
        rethrow.NotTaken.Should().Equal("an exception from the try block, not caught here");

        // a catch-all: nothing goes uncaught
        var catchAll = finallies[LineOf("public static int CatchAll")];
        catchAll.Switch.Labels.Should().Equal("a return, break, continue or goto out of the try block", "a return, break, continue or goto out of catch");
        catchAll.NotTaken.Should().BeEmpty();

        // yield return in the try block: stopping the enumeration runs the finally too, so no ways in
        finallies.Should().NotContainKey(LineOf("public static IEnumerable<int> Iterate"));
    }
}
