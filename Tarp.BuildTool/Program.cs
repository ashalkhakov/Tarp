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
            services.AddSingleton<TarpPreprocessor>();
            services.AddSingleton<McDcProjectInstrumenter>();
        })
        .RunCommandLineApplicationAsync(args, (app) =>
        {
            var preprocessor = app.GetRequiredService<TarpPreprocessor>();

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
                var report = cmd.Option("--tarp-report <FILE>", "Write the report to a file instead of the console", CommandOptionType.SingleValue);

                cmd.OnExecuteAsync((CancellationToken cts) =>
                    TestCommand.Run(results.Value() ?? TestCommand.DefaultResultsDirectory, cmd.RemainingArguments, report.Value(), cts));
            });

            app.Command("mcdc-report", cmd =>
            {
                cmd.Description = "Report MC/DC from the maps and hits in a directory (dotnet tarp test's results, or mcdc-instrument's output)";
                cmd.HelpOption();

                var directory = cmd.Option("-d|--dir <DIR>", $"The directory with the maps (default: {TestCommand.DefaultResultsDirectory})", CommandOptionType.SingleValue);
                var hits = cmd.Option("--hits <HITS>", "The directory with the hits (default: <DIR>, searched recursively)", CommandOptionType.SingleValue);
                var report = cmd.Option("-r|--report <FILE>", "Write the report to a file instead of the console", CommandOptionType.SingleValue);

                cmd.OnExecute(() =>
                {
                    var text = McDcReport.Format(McDcReport.AnalyzeDirectory(directory.Value() ?? TestCommand.DefaultResultsDirectory, hits.Value()));

                    if (report.HasValue())
                        File.WriteAllText(report.Value()!, text);
                    else
                        Console.Write(text);
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

            // not IsRequired(): that would also be enforced when running a subcommand
            var project = app.Option("-p|--project <PROJECT>", "The input project", CommandOptionType.SingleValue);
            var outputDirectory = app.Option("-o|--output <OUTPUT>", "The output directory", CommandOptionType.SingleValue);

            app.OnExecuteAsync(async (CancellationToken cts) =>
            {
                if (!project.HasValue())
                {
                    Console.Error.WriteLine("Please provide path to the input project");
                    return 1;
                }
                if (!outputDirectory.HasValue())
                {
                    Console.Error.WriteLine("Please provide output directory for generated files");
                    return 1;
                }
                await preprocessor.ProcessAssembly(project.Value()!, outputDirectory.Value()!, cts);
                return 0;
            });
        });
}
