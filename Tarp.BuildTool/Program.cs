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

            app.Command("mcdc-report", cmd =>
            {
                cmd.Description = "Report MC/DC from the map and the hits an instrumented build recorded";
                cmd.HelpOption();

                var directory = cmd.Option("-d|--dir <DIR>", "The output directory of mcdc-instrument", CommandOptionType.SingleValue).IsRequired();
                var hits = cmd.Option("--hits <HITS>", $"The hits directory (default: <DIR>/{McDcProjectInstrumenter.HitsDirectoryName})", CommandOptionType.SingleValue);
                var report = cmd.Option("-r|--report <FILE>", "Write the report to a file instead of the console", CommandOptionType.SingleValue);

                cmd.OnExecute(() =>
                {
                    var dir = directory.Value()!;
                    var map = McDcMap.FromJson(File.ReadAllText(Path.Combine(dir, McDcProjectInstrumenter.MapFileName)));
                    var hitsDir = hits.Value() ?? Path.Combine(dir, McDcProjectInstrumenter.HitsDirectoryName);

                    var results = McDcReport.Analyze(map, McDcHits.LoadDirectory(hitsDir, map.ModuleId));
                    var text = McDcReport.Format(map, results);

                    if (report.HasValue())
                        File.WriteAllText(report.Value()!, text);
                    else
                        Console.Write(text);
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
