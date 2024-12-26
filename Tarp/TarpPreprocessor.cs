using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using System.Linq;
using System.IO;
using System.Collections.Immutable;
using Project = Microsoft.CodeAnalysis.Project;
using System.Threading;
using System.IO.Abstractions;
using Tarp.Basics;
using Tarp.Rewriting;

namespace Tarp;

public class TarpPreprocessor
{
    IFileSystem fileSystem;

    public TarpPreprocessor(IFileSystem fileSystem)
    {
        this.fileSystem = fileSystem;
    }

    public async Task ProcessAssembly(string projectFilePath, string outputDir, CancellationToken cts)
    {
        fileSystem.Directory.CreateDirectory(outputDir);

        MSBuildLocator.RegisterDefaults();

        using var workspace = MSBuildWorkspace.Create();

        var originalProj = await workspace.OpenProjectAsync(projectFilePath, cancellationToken: cts)
                            .ConfigureAwait(false);

        if (!IsCSharpProject(originalProj))
        {
            // log this?
            return;
        }

        var newProjPath = fileSystem.Path.Combine(outputDir, fileSystem.Path.GetFileName(originalProj.FilePath));
        var newProj = originalProj
            .RemoveDocuments(originalProj.DocumentIds.ToImmutableArray());

        var gen = new FreshNameGenerator("__tmp");
        var probeGenerator = new ProbeGenerator();

        var sourceToNf = new SourceToNf(gen);
        var nfToProbed = new NfToProbed(probeGenerator);

        foreach (var documentId in originalProj.DocumentIds)
        {
            var document = originalProj.GetDocument(documentId);
            var root = await document.GetSyntaxRootAsync(cancellationToken: cts).ConfigureAwait(false);

            var nfRoot = sourceToNf.Visit(root);
            var probedRoot = nfToProbed.Visit(nfRoot);

            var baseDir = fileSystem.Path.GetDirectoryName(originalProj.FilePath);
            var relativeDocumentFilePath = fileSystem.Path.GetRelativePath(baseDir, document.FilePath);
            var newDocumentFilePath = fileSystem.Path.Combine(outputDir, relativeDocumentFilePath);

            fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(newDocumentFilePath));

            fileSystem.File.WriteAllText(newDocumentFilePath, probedRoot.ToFullString());

            var newDoc = newProj.AddDocument(
                document.Name,
                probedRoot.GetText(),
                folders: document.Folders,
                filePath: newDocumentFilePath
            );
            newProj = newDoc.Project;
        }

        // TODO: generate a template file that will provide the implementation for the hits recording code
        // TODO: add this template file to the compilation

        // TODO: dump in some well-known format?
        probeGenerator.Dump();

        var originalProjectFile = fileSystem.File.ReadAllText(originalProj.FilePath);
        fileSystem.File.WriteAllText(newProjPath, originalProjectFile);
    }

    private static bool IsTestProject(Project project)
    {
        // Check for common test project patterns

        if (project.MetadataReferences.Any(r => r.Display == "Microsoft.NET.Test.Sdk"))
        {
            return true;
        }
        /*
        // 1. Check project name for common test-related suffixes (like "Tests", "Test", "Specs")
        var testSuffixes = new[] { "Tests", "Test", "Specs" };
        if (testSuffixes.Any(suffix => project.Name.EndsWith(suffix, StringComparison.InvariantCultureIgnoreCase)))
        {
            return true;
        }

        // 2. Check for test framework references (like xUnit, NUnit, MSTest)
        var testReferences = new[] { "xunit", "nunit", "mstest" };
        if (project.MetadataReferences.Any(r => testReferences.Any(tr => r.Display.Contains(tr, StringComparison.InvariantCultureIgnoreCase))))
        {
            return true;
        }

        // 3. Optionally, check for specific files (like "Test" classes)
        // You can scan the project's files for test-related attributes or file names.
        // (This is more of a heuristic approach and can be omitted depending on your needs.)

        */

        return false;
    }

    private static bool IsCSharpProject(Project project)
    {
        return project.Language == LanguageNames.CSharp;
    }
}
