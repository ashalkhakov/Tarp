using Tarp.McDc;

namespace Tarp.BuildTool;

// where each format of the report goes; the text report goes to the console unless it has a file
internal sealed record ReportFiles(string? Text, string? Lcov, string? Markdown, string? Html)
{
    public void Write(IReadOnlyList<ModuleResult> modules)
    {
        WriteFile(Lcov, "lcov", () => LcovReport.Format(modules));
        WriteFile(Markdown, "Markdown", () => MarkdownReport.Format(modules));
        WriteFile(Html, "HTML", () => HtmlReport.Format(modules));

        var text = McDcReport.Format(modules);
        if (Text != null)
        {
            WriteFile(Text, "text", () => text);
        }
        else
        {
            Console.WriteLine();
            Console.Write(text);
        }
    }

    private static void WriteFile(string? path, string format, Func<string> report)
    {
        if (path == null)
        {
            return;
        }
        File.WriteAllText(path, report());
        Console.WriteLine($"Tarp: {format} report written to {path}");
    }
}
