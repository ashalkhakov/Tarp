using System.Diagnostics;
using Tarp.McDc;

namespace Tarp.BuildTool;

/*
 * dotnet tarp test [dotnet test arguments]: runs dotnet test with Tarp.targets imported into
 * every project (no change to the projects needed), then reports MC/DC from the results.
 */
internal static class TestCommand
{
    public const string DefaultResultsDirectory = "TestResults/tarp";

    public static async Task<int> Run(string resultsDirectory, IReadOnlyList<string> testArguments, string? reportFile, bool exceptionExits, CancellationToken cts)
    {
        var results = Path.GetFullPath(resultsDirectory);
        Directory.CreateDirectory(results);

        // hits from earlier runs: maps stay (each build updates its own), hits would be counted twice
        foreach (var f in Directory.EnumerateFiles(results, "*.hits"))
        {
            File.Delete(f);
        }

        var targets = Path.Combine(AppContext.BaseDirectory, "Tarp.targets");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add("test");
        foreach (var a in testArguments)
        {
            start.ArgumentList.Add(a);
        }
        start.ArgumentList.Add("-p:Tarp=true");
        start.ArgumentList.Add($"-p:CustomAfterMicrosoftCommonTargets={targets}");
        start.ArgumentList.Add($"-p:TarpResultsDirectory={results}{Path.DirectorySeparatorChar}");
        if (exceptionExits)
        {
            start.ArgumentList.Add("-p:TarpExceptionExits=true");
        }
        start.Environment["TARP_HITS_DIR"] = results;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start dotnet test");
        await process.WaitForExitAsync(cts);

        // report even when tests fail: the coverage of the passing ones is still useful
        var text = McDcReport.Format(McDcReport.AnalyzeDirectory(results));
        if (reportFile != null)
        {
            File.WriteAllText(reportFile, text);
            Console.WriteLine($"Tarp: MC/DC report written to {reportFile}");
        }
        else
        {
            Console.WriteLine();
            Console.Write(text);
        }

        return process.ExitCode;
    }
}
