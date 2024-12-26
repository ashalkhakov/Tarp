using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO.Abstractions;

namespace Tarp.BuildTool;

internal class Program
{
    static Task<int> Main(string[] args)
        => new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddSingleton<IFileSystem, FileSystem>();
            services.AddSingleton<TarpPreprocessor>();
        })
        .RunCommandLineApplicationAsync(args, (app) =>
        {
            var preprocessor = app.GetRequiredService<TarpPreprocessor>();

            app.HelpOption();

            var project = app.Option("-p|--project <PROJECT>", "The input project", CommandOptionType.SingleValue);
            project.IsRequired(errorMessage: "Please provide path to the input project");

            var outputDirectory = app.Option("-o|--output <OUTPUT>", "The output directory", CommandOptionType.SingleValue);
            outputDirectory.IsRequired(errorMessage: "Please provide output directory for generated files");

            app.OnExecuteAsync(async (CancellationToken cts) =>
            {
                await preprocessor.ProcessAssembly(project.Value(), outputDirectory.Value(), cts);
            });
        });
}
