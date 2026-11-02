using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO.Abstractions;
using Tarp.McDc;

namespace Tarp.BuildTool;

internal class Program
{
    static Task<int> Main(string[] args)
        => new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddSingleton<IFileSystem, FileSystem>();
            services.AddSingleton<McDcProjectInstrumenter>();
        })
        .RunCommandLineApplicationAsync(args, (app) =>
        {
            app.HelpOption();

            app.Command("mcdc-instrument", cmd =>
            {
                cmd.Description = "Instrument a project for MC/DC; build and run the instrumented copy, then run mcdc-report";
                cmd.HelpOption();

                var project = cmd.Option("-p|--project <PROJECT>", "The input project", CommandOptionType.SingleValue).IsRequired();
                var output = cmd.Option("-o|--output <OUTPUT>", "The output directory", CommandOptionType.SingleValue).IsRequired();

                cmd.OnExecuteAsync(async (CancellationToken cts) =>
                {
                    var map = await app.GetRequiredService<McDcProjectInstrumenter>().InstrumentProject(project.Value()!, output.Value()!, cts);
                    Console.WriteLine($"{map.Decisions.Count} decisions instrumented, {map.Skipped.Count} skipped");
                });
            });

            app.Command("test", cmd =>
            {
                cmd.Description = "Run dotnet test with MC/DC instrumentation, then report; other arguments go to dotnet test";
                cmd.HelpOption();
                cmd.UnrecognizedArgumentHandling = UnrecognizedArgumentHandling.CollectAndContinue;

                var results = cmd.Option("--tarp-results <DIR>", $"Where maps and hits go (default: {TestCommand.DefaultResultsDirectory})", CommandOptionType.SingleValue);
                var report = cmd.Option("--tarp-report <FILE>", "Write the text report to a file instead of the console", CommandOptionType.SingleValue);
                var lcov = cmd.Option("--tarp-lcov <FILE>", "Also write an lcov tracefile (lines, functions, branches, MC/DC)", CommandOptionType.SingleValue);
                var cobertura = cmd.Option("--tarp-cobertura <FILE>", "Also write a Cobertura XML report (lines, branches)", CommandOptionType.SingleValue);
                var markdown = cmd.Option("--tarp-markdown <FILE>", "Also write the report in Markdown", CommandOptionType.SingleValue);
                var html = cmd.Option("--tarp-html <FILE>", "Also write the report as an HTML page, with the source", CommandOptionType.SingleValue);
                var exceptionExits = cmd.Option("--tarp-exception-exits", "Also count functions left by an exception (wraps each function body in an exception filter)", CommandOptionType.NoValue);
                var failLines = cmd.Option("--tarp-fail-under-lines <PERCENT>", "Fail when line coverage is below this", CommandOptionType.SingleValue);
                var failBranches = cmd.Option("--tarp-fail-under-branches <PERCENT>", "Fail when branch coverage is below this", CommandOptionType.SingleValue);
                var failMcDc = cmd.Option("--tarp-fail-under-mcdc <PERCENT>", "Fail when MC/DC (conditions shown) is below this", CommandOptionType.SingleValue);
                var exclusions = cmd.Option("--tarp-exclusions <FILE>", "Files and lines the fail-under checks leave out (path glob, or path:line, one a line)", CommandOptionType.SingleValue);

                cmd.OnExecuteAsync((CancellationToken cts) =>
                    TestCommand.Run(results.Value() ?? TestCommand.DefaultResultsDirectory, cmd.RemainingArguments,
                        new ReportFiles(report.Value(), lcov.Value(), cobertura.Value(), markdown.Value(), html.Value()),
                        GateOptions.Parse(failLines.Value(), failBranches.Value(), failMcDc.Value(), exclusions.Value()), exceptionExits.HasValue(), cts));
            });

            app.Command("mcdc-report", cmd =>
            {
                cmd.Description = "Report MC/DC from the maps and hits in a directory (dotnet tarp test's results, or mcdc-instrument's output)";
                cmd.HelpOption();

                var directory = cmd.Option("-d|--dir <DIR>", $"The directory with the maps (default: {TestCommand.DefaultResultsDirectory})", CommandOptionType.SingleValue);
                var hits = cmd.Option("--hits <HITS>", "The directory with the hits (default: <DIR>, searched recursively)", CommandOptionType.SingleValue);
                var report = cmd.Option("-r|--report <FILE>", "Write the text report to a file instead of the console", CommandOptionType.SingleValue);
                var lcov = cmd.Option("--lcov <FILE>", "Also write an lcov tracefile (lines, functions, branches, MC/DC)", CommandOptionType.SingleValue);
                var cobertura = cmd.Option("--cobertura <FILE>", "Also write a Cobertura XML report (lines, branches)", CommandOptionType.SingleValue);
                var markdown = cmd.Option("--markdown <FILE>", "Also write the report in Markdown", CommandOptionType.SingleValue);
                var html = cmd.Option("--html <FILE>", "Also write the report as an HTML page, with the source", CommandOptionType.SingleValue);
                var failLines = cmd.Option("--fail-under-lines <PERCENT>", "Fail when line coverage is below this", CommandOptionType.SingleValue);
                var failBranches = cmd.Option("--fail-under-branches <PERCENT>", "Fail when branch coverage is below this", CommandOptionType.SingleValue);
                var failMcDc = cmd.Option("--fail-under-mcdc <PERCENT>", "Fail when MC/DC (conditions shown) is below this", CommandOptionType.SingleValue);
                var exclusions = cmd.Option("--exclusions <FILE>", "Files and lines the fail-under checks leave out (path glob, or path:line, one a line)", CommandOptionType.SingleValue);

                cmd.OnExecute(() =>
                {
                    var gate = GateOptions.Parse(failLines.Value(), failBranches.Value(), failMcDc.Value(), exclusions.Value());
                    var modules = McDcReport.AnalyzeDirectory(directory.Value() ?? TestCommand.DefaultResultsDirectory, hits.Value());
                    new ReportFiles(report.Value(), lcov.Value(), cobertura.Value(), markdown.Value(), html.Value()).Write(modules);
                    return gate.Check(modules) ? 0 : 1;
                });
            });

            // called by Tarp.targets during a build
            app.Command("mcdc-build-instrument", cmd =>
            {
                cmd.Description = "Instrument a project's sources during its build (used by Tarp.targets)";
                cmd.ShowInHelpText = false;

                var argsFile = cmd.Argument("ARGS", "The arguments file Tarp.targets writes").IsRequired();

                cmd.OnExecute(() =>
                {
                    var request = McDcBuildRequest.Parse(File.ReadAllLines(argsFile.Value!));
                    new McDcBuildInstrumenter(app.GetRequiredService<IFileSystem>(), Console.Out).Instrument(request);
                });
            });

            // with no command: the commands
            app.OnExecute(() => app.ShowHelp());
        });
}
