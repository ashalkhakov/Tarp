using System.Globalization;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tarp.McDc;

namespace TarpTests.McDc;

// the lcov tracefile, Markdown and HTML reports
[TestClass]
public class ReportFormatsTests
{
    private const string Source = @"
public static class T
{
    public static bool Both(bool a, bool b)
    {
        if (a && b)
            return true;
        return false;
    }

    public static int Never(int x) => x;

    public static string Kind(int x)
    {
        switch (x)
        {
            case 1: return ""one"";
            default: return ""<other>"";
        }
    }
}";

    private static List<ModuleResult> Run()
    {
        var p = McDcEndToEndTests.Build(Source);
        p.Check("Both", true, true);
        p.Check("Both", true, false);
        p.Check("Both", true, true);
        p.Check("Kind", 1);
        return new List<ModuleResult> { new(p.Map, p.Results(), p.Counts()) };
    }

    private static int LineOf(string text) =>
        Source.Split('\n').Select((l, i) => (l, i)).Single(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;

    [TestMethod]
    public void Lcov()
    {
        var lines = LcovReport.Format(Run()).Split('\n');

        lines[0].Should().Be("TN:");
        lines[1].Should().Be("SF:T.cs");
        lines.Should().Contain(new[]
        {
            $"FN:{LineOf("public static bool Both")},T.Both(bool, bool)",
            "FNDA:3,T.Both(bool, bool)",
            "FNDA:0,T.Never(int)",
            "FNF:3", "FNH:2",
            // a && b: true and false outcomes; the switch: case 1 taken once, default never
            $"BRDA:{LineOf("if (a && b)")},0,0,1", $"BRDA:{LineOf("if (a && b)")},0,1,1",
            $"BRDA:{LineOf("switch (x)")},0,0,1", $"BRDA:{LineOf("switch (x)")},0,1,0",
            // MC/DC: a was never false; b was shown both ways
            $"MCDC:{LineOf("if (a && b)")},2,t,1,0,a", $"MCDC:{LineOf("if (a && b)")},2,f,0,0,a",
            $"MCDC:{LineOf("if (a && b)")},2,t,1,1,b", $"MCDC:{LineOf("if (a && b)")},2,f,1,1,b",
            "MCF:4", "MCH:3",
            $"DA:{LineOf("if (a && b)")},3", $"DA:{LineOf("return true;")},2", $"DA:{LineOf("return false;")},1",
            $"DA:{LineOf("public static int Never")},0",
            "end_of_record"
        });
        // the summary records follow their records, as lcov requires
        Array.IndexOf(lines, "BRF:4").Should().BeGreaterThan(Array.FindLastIndex(lines, l => l.StartsWith("BRDA:", StringComparison.Ordinal)));
        Array.IndexOf(lines, "LH:" + lines.Count(l => l.StartsWith("DA:", StringComparison.Ordinal) && !l.EndsWith(",0", StringComparison.Ordinal)))
            .Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Cobertura()
    {
        var xml = XDocument.Parse(CoberturaReport.Format(Run(), DateTimeOffset.FromUnixTimeSeconds(1700000000)));
        var coverage = xml.Root!;

        xml.DocumentType!.SystemId.Should().Be("http://cobertura.sourceforge.net/xml/coverage-04.dtd");
        coverage.Attribute("timestamp")!.Value.Should().Be("1700000000");
        coverage.Attribute("lines-valid")!.Value.Should().Be("7");
        coverage.Attribute("lines-covered")!.Value.Should().Be("5");
        coverage.Attribute("branches-valid")!.Value.Should().Be("4");
        coverage.Attribute("branches-covered")!.Value.Should().Be("3");
        coverage.Attribute("line-rate")!.Value.Should().Be("0.7143");

        var c = coverage.Descendants("class").Single();
        c.Attribute("name")!.Value.Should().Be("T");
        c.Attribute("filename")!.Value.Should().Be("T.cs");
        var lines = c.Element("lines")!.Elements("line").ToDictionary(l => int.Parse(l.Attribute("number")!.Value, CultureInfo.InvariantCulture));
        lines[LineOf("if (a && b)")].Attribute("hits")!.Value.Should().Be("3");
        lines[LineOf("if (a && b)")].Attribute("condition-coverage")!.Value.Should().Be("100% (2/2)");
        lines[LineOf("switch (x)")].Attribute("condition-coverage")!.Value.Should().Be("50% (1/2)");
        lines[LineOf("return true;")].Attribute("branch")!.Value.Should().Be("false");
        lines[LineOf("public static int Never")].Attribute("hits")!.Value.Should().Be("0");
    }

    [TestMethod]
    public void Markdown()
    {
        var md = MarkdownReport.Format(Run());

        md.Should().StartWith("# Tarp coverage report");
        md.Should().Contain("| Functions | 2 | 3 | 66.7% |");
        md.Should().Contain("| `T.cs` |");
        md.Should().Contain("- Never entered: `T.Never(int)`");
        md.Should().Contain($"- Line {LineOf("switch (x)")}, `switch`: not taken: `default:`");
        // MC/DC gaps as tables: the evaluations, then the conditions
        md.Should().Contain($"### Line {LineOf("if (a && b)")}: `a && b`\n\n| c0 | c1 | Outcome |\n|:-:|:-:|:-:|\n| T | F | F |\n| T | T | T |\n");
        md.Should().Contain("| c0 | `a` | ✓ | ✗ | **not shown** | `c0=F c1=-` |");
        md.Should().Contain("| c1 | `b` | ✓ | ✓ | shown |  |");
    }

    [TestMethod]
    public void Html()
    {
        var html = HtmlReport.Format(Run());

        html.Should().StartWith("<!doctype html>");
        html.Should().Contain("<title>Tarp coverage report</title>");
        html.Should().Contain("prefers-color-scheme: dark");
        // no source file on disk here (T.cs): the gaps are listed instead of the source
        html.Should().Contain($"<li>line {LineOf("if (a && b)")}: MC/DC: a not shown</li>");
        html.Should().Contain("<tr><td>T</td><td>F</td><td>F</td></tr>");
        html.Should().Contain("<td class=\"gap\">not shown</td><td><code>c0=F c1=-</code></td>");
        // text from the program is escaped
        html.Should().Contain("<code>a &amp;&amp; b</code>").And.NotContain("a && b");
    }
}
