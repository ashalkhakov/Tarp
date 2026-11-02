using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace Tarp.McDc;

/*
 * What Tarp.targets passes in (one "key=value" per line, keys may repeat): the inputs the
 * compiler is about to get, so that the instrumenter sees the same program as the build.
 */
public class McDcBuildRequest
{
    public string ProjectName { get; set; } = "";
    public string ProjectDirectory { get; set; } = "";
    public string AssemblyName { get; set; } = "";
    public string TargetFramework { get; set; } = "";
    public string IntermediateDirectory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string ResultsDirectory { get; set; } = "";
    public string LangVersion { get; set; } = "";
    public string Nullable { get; set; } = "";
    public bool AllowUnsafe { get; set; }
    public List<string> Defines { get; } = new();
    public List<string> References { get; } = new();
    public List<string> Analyzers { get; } = new();
    public List<string> AdditionalFiles { get; } = new();
    public List<string> EditorConfigFiles { get; } = new();
    // the Compile item as written in the project (to swap it out) and its full path
    public List<(string Identity, string FullPath)> Sources { get; } = new();

    public static McDcBuildRequest Parse(IEnumerable<string> lines)
    {
        var r = new McDcBuildRequest();
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            var value = line[(eq + 1)..].Trim();
            switch (line[..eq].Trim())
            {
                case "project-name": r.ProjectName = value; break;
                case "project-dir": r.ProjectDirectory = value; break;
                case "assembly-name": r.AssemblyName = value; break;
                case "target-framework": r.TargetFramework = value; break;
                case "intermediate-dir": r.IntermediateDirectory = value; break;
                case "output-dir": r.OutputDirectory = value; break;
                case "results-dir": r.ResultsDirectory = value; break;
                case "lang-version": r.LangVersion = value; break;
                case "nullable": r.Nullable = value; break;
                case "allow-unsafe": r.AllowUnsafe = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                case "define": if (value.Length > 0) r.Defines.Add(value); break;
                case "reference": r.References.Add(value); break;
                case "analyzer": r.Analyzers.Add(value); break;
                case "additional-file": r.AdditionalFiles.Add(value); break;
                case "editorconfig": r.EditorConfigFiles.Add(value); break;
                case "source":
                    {
                        var bar = value.LastIndexOf('|');
                        r.Sources.Add(bar < 0 ? (value, value) : (value[..bar], value[(bar + 1)..]));
                        break;
                    }
            }
        }
        return r;
    }
}

/*
 * Instruments a project's sources during its build (see Tarp.targets): builds the same
 * compilation the compiler is about to build, rewrites the decisions, and checks that the
 * result still compiles. A decision whose instrumentation breaks the build is left out, and
 * listed as skipped, so instrumenting never fails a build that would otherwise succeed.
 *
 * Writes to the output directory:
 * - src/...: the instrumented sources, and the runtime
 * - replaced.txt: the Compile items to remove, instrumented.txt: the ones to add
 * - <assembly>.<target framework>.map.json: the map (Tarp.targets copies it to the results)
 */
public class McDcBuildInstrumenter
{
    public const string ReplacedFileName = "replaced.txt";
    public const string InstrumentedFileName = "instrumented.txt";

    private const int MaxRounds = 5;

    private readonly IFileSystem fileSystem;
    private readonly TextWriter log;

    public McDcBuildInstrumenter(IFileSystem fileSystem, TextWriter log)
    {
        this.fileSystem = fileSystem;
        this.log = log;
    }

    public McDcMap Instrument(McDcBuildRequest request)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersionFacts.TryParse(request.LangVersion, out var version) ? version : LanguageVersion.Latest,
            preprocessorSymbols: request.Defines);

        var trees = new List<SyntaxTree>();
        var toInstrument = new List<(SyntaxTree Tree, string Identity)>();
        foreach (var (identity, fullPath) in request.Sources)
        {
            if (!fileSystem.File.Exists(fullPath))
            {
                continue;
            }
            var text = fileSystem.File.ReadAllText(fullPath);
            var tree = CSharpSyntaxTree.ParseText(text, parseOptions, fullPath, Encoding.UTF8);
            trees.Add(tree);

            if (IsGenerated(request, fullPath, text))
            {
                continue;
            }
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                log.WriteLine($"Tarp: not instrumenting {fullPath}: it doesn't parse with Tarp's C# version");
                continue;
            }
            toInstrument.Add((tree, identity));
        }

        var compilation = CSharpCompilation.Create(
            request.AssemblyName,
            trees,
            request.References.Where(fileSystem.File.Exists).Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: ParseNullable(request.Nullable),
                allowUnsafe: request.AllowUnsafe));

        compilation = RunGenerators(request, compilation, parseOptions);

        var originalErrors = toInstrument
            .SelectMany(t => compilation.GetSemanticModel(t.Tree).GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(ErrorKey)
            .ToHashSet();

        // decisions to leave alone, per file, by span start
        var excluded = toInstrument.ToDictionary(t => t.Tree.FilePath, _ => new Dictionary<int, string>());

        McDcMap map;
        Dictionary<SyntaxTree, SyntaxTree> rewritten;
        Dictionary<int, string> decisionFiles;
        var round = 0;
        while (true)
        {
            (map, rewritten, decisionFiles) = Rewrite(request, compilation, parseOptions, toInstrument, excluded);

            var check = compilation;
            foreach (var (original, instrumented) in rewritten)
            {
                check = check.ReplaceSyntaxTree(original, instrumented);
            }
            check = check.AddSyntaxTrees(CSharpSyntaxTree.ParseText(McDcRuntimeSource.Generate("check", "", map.Probes.Count), parseOptions));

            var newErrors = rewritten.Values
                .SelectMany(t => check.GetSemanticModel(t).GetDiagnostics())
                .Where(d => d.Severity == DiagnosticSeverity.Error && !originalErrors.Contains(ErrorKey(d)))
                .ToList();

            if (newErrors.Count == 0)
            {
                break;
            }

            var progress = false;
            foreach (var error in newErrors)
            {
                progress |= Exclude(error, map, decisionFiles, excluded, giveUp: round >= MaxRounds - 1);
            }

            round++;
            if (!progress)
            {
                // excluding everything around the errors didn't help: leave those files alone
                foreach (var file in newErrors.Select(e => e.Location.GetMappedLineSpan().Path).Distinct())
                {
                    toInstrument.RemoveAll(t => t.Tree.FilePath == file);
                    log.WriteLine($"Tarp: not instrumenting {file}: instrumentation breaks its compilation");
                }
            }
        }

        map.ModuleId = ModuleId(request, map);
        WriteOutputs(request, map, rewritten, toInstrument);

        log.WriteLine($"Tarp: {request.AssemblyName}: {map.Decisions.Count} decisions instrumented, {map.Skipped.Count} skipped");
        return map;
    }

    private static (McDcMap, Dictionary<SyntaxTree, SyntaxTree>, Dictionary<int, string>) Rewrite(
        McDcBuildRequest request, CSharpCompilation compilation, CSharpParseOptions parseOptions,
        List<(SyntaxTree Tree, string Identity)> toInstrument, Dictionary<string, Dictionary<int, string>> excluded)
    {
        var map = new McDcMap();
        var rewritten = new Dictionary<SyntaxTree, SyntaxTree>();
        // decision id -> the file it is in
        var decisionFiles = new Dictionary<int, string>();

        foreach (var (tree, _) in toInstrument)
        {
            var first = map.Decisions.Count;
            var rewriter = new CoverageRewriter(compilation.GetSemanticModel(tree), map, DisplayPath(request, tree.FilePath), excluded[tree.FilePath]);
            var root = (CompilationUnitSyntax)rewriter.Visit(tree.GetRoot())!;
            var text = McDcProjectInstrumenter.WithPrologue(root, tree.FilePath).ToFullString();

            rewritten[tree] = CSharpSyntaxTree.ParseText(text, parseOptions, InstrumentedPath(request, tree.FilePath), Encoding.UTF8);
            for (var id = first; id < map.Decisions.Count; id++)
            {
                decisionFiles[id] = tree.FilePath;
            }
        }

        return (map, rewritten, decisionFiles);
    }

    // excludes the decision an error is in, or (when it's outside any) all decisions of the enclosing member
    private static bool Exclude(Diagnostic error, McDcMap map, Dictionary<int, string> decisionFiles, Dictionary<string, Dictionary<int, string>> excluded, bool giveUp)
    {
        var reason = $"instrumenting it breaks compilation ({error.Id})";
        var location = error.Location.GetMappedLineSpan();
        if (!excluded.TryGetValue(location.Path, out var inFile))
        {
            return false;
        }

        var node = error.Location.SourceTree!.GetRoot().FindNode(error.Location.SourceSpan, getInnermostNodeForTie: true);
        // the innermost marked condition the error is in (an excluded decision has no markers, so the next
        // round finds the decision around it)
        var id = node.AncestorsAndSelf().Select(MarkedDecisionId).FirstOrDefault(i => i != null);

        IEnumerable<DecisionEntry> decisions;
        if (id != null && !giveUp)
        {
            decisions = map.Decisions.Where(d => d.Id == id);
        }
        else
        {
            // e.g. a use of a variable no longer definitely assigned, or a lost "unreachable end"
            var member = node.AncestorsAndSelf().FirstOrDefault(n => n is MemberDeclarationSyntax or LocalFunctionStatementSyntax);
            var span = (member ?? node.SyntaxTree.GetRoot()).GetLocation().GetMappedLineSpan();
            decisions = map.Decisions.Where(d => decisionFiles[d.Id] == location.Path).Where(d => d.Line >= span.StartLinePosition.Line + 1 && d.Line <= span.EndLinePosition.Line + 1);
        }

        var progress = false;
        foreach (var d in decisions)
        {
            progress |= inFile.TryAdd(d.SpanStart, reason);
        }
        return progress;
    }

    // a marked condition is "(condition && Yes...(...) || No...(..., id, ...) && false)" (see DecisionRewriter)
    private static int? MarkedDecisionId(SyntaxNode node)
    {
        if (node is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalOrExpression } or &&
            or.Right is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression, Left: InvocationExpressionSyntax marker } &&
            marker.Expression.ToString().StartsWith("global::TarpGen.McDc.", StringComparison.Ordinal) &&
            marker.ArgumentList.Arguments.Count > 1 &&
            int.TryParse(marker.ArgumentList.Arguments[1].ToString(), System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }
        return null;
    }

    private static (string, int, string) ErrorKey(Diagnostic d)
    {
        var span = d.Location.GetMappedLineSpan();
        return (span.Path, span.StartLinePosition.Line, d.Id);
    }

    private void WriteOutputs(McDcBuildRequest request, McDcMap map, Dictionary<SyntaxTree, SyntaxTree> rewritten, List<(SyntaxTree Tree, string Identity)> instrumented)
    {
        fileSystem.Directory.CreateDirectory(request.OutputDirectory);

        var added = new List<string>();
        foreach (var (tree, _) in instrumented)
        {
            var path = InstrumentedPath(request, tree.FilePath);
            WriteIfChanged(path, rewritten[tree].ToString());
            added.Add(path);
        }

        var runtimePath = Path.GetFullPath(Path.Combine(request.OutputDirectory, McDcRuntimeSource.FileName));
        WriteIfChanged(runtimePath, McDcRuntimeSource.Generate(map.ModuleId, request.ResultsDirectory, map.Probes.Count));
        added.Add(runtimePath);

        WriteIfChanged(Path.Combine(request.OutputDirectory, ReplacedFileName), string.Join('\n', instrumented.Select(t => t.Identity)) + "\n");
        WriteIfChanged(Path.Combine(request.OutputDirectory, InstrumentedFileName), string.Join('\n', added) + "\n");
        WriteIfChanged(Path.Combine(request.OutputDirectory, MapFileName(request)), map.ToJson());
    }

    public static string MapFileName(McDcBuildRequest request) =>
        request.AssemblyName + (request.TargetFramework.Length > 0 ? "." + request.TargetFramework : "") + McDcMap.FileSuffix;

    // keeps timestamps, so that an unchanged project doesn't recompile
    private void WriteIfChanged(string path, string text)
    {
        if (fileSystem.File.Exists(path) && fileSystem.File.ReadAllText(path) == text)
        {
            return;
        }
        fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        fileSystem.File.WriteAllText(path, text, Encoding.UTF8);
    }

    private static string InstrumentedPath(McDcBuildRequest request, string fullPath)
    {
        var relative = Path.GetRelativePath(request.ProjectDirectory, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            // a linked file: keep the name, make it unique
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..8];
            relative = Path.Combine("_linked", hash + "_" + Path.GetFileName(fullPath));
        }
        // normalized: MSBuild compares the Compile items it reads back from instrumented.txt
        return Path.GetFullPath(Path.Combine(request.OutputDirectory, "src", relative));
    }

    private static string DisplayPath(McDcBuildRequest request, string fullPath)
    {
        var relative = Path.GetRelativePath(request.ProjectDirectory, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? fullPath.Replace('\\', '/')
            : (request.ProjectName + "/" + relative).Replace('\\', '/');
    }

    // the same program instrumented the same way gets the same id: hits from earlier runs still count
    private static string ModuleId(McDcBuildRequest request, McDcMap map)
    {
        var text = request.AssemblyName + "\n" + request.TargetFramework + "\n" + map.ToJson();
        return string.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(text)).Take(16).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static bool IsGenerated(McDcBuildRequest request, string fullPath, string text)
    {
        if (request.IntermediateDirectory.Length > 0 &&
            // normalized: on Windows, either may mix / and \
            Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(request.IntermediateDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = Path.GetFileName(fullPath);
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var head = text.Length > 1000 ? text[..1000] : text;
        return head.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
    }

    private static NullableContextOptions ParseNullable(string value) => value.ToUpperInvariant() switch
    {
        "ENABLE" => NullableContextOptions.Enable,
        "WARNINGS" => NullableContextOptions.Warnings,
        "ANNOTATIONS" => NullableContextOptions.Annotations,
        _ => NullableContextOptions.Disable
    };

    /*
     * Source generators add code the instrumented sources use (e.g. partial members): run them,
     * so that conditions using generated members get their types. A generator that can't be loaded
     * is reported and skipped; the decisions that need it are then skipped too ("not a bool").
     */
    private CSharpCompilation RunGenerators(McDcBuildRequest request, CSharpCompilation compilation, CSharpParseOptions parseOptions)
    {
        var loader = new GeneratorLoader();
        var generators = new List<ISourceGenerator>();
        foreach (var path in request.Analyzers.Where(fileSystem.File.Exists))
        {
            try
            {
                generators.AddRange(new AnalyzerFileReference(path, loader).GetGenerators(LanguageNames.CSharp));
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or ReflectionTypeLoadException or TypeLoadException)
            {
                log.WriteLine($"Tarp: can't load generators from {path}: {e.Message}");
            }
        }
        if (generators.Count == 0)
        {
            return compilation;
        }

        var additionalTexts = request.AdditionalFiles.Where(fileSystem.File.Exists)
            .Select(p => (AdditionalText)new FileText(p, fileSystem.File.ReadAllText(p))).ToImmutableArray();
        var options = new ConfigOptionsProvider(request.EditorConfigFiles.Where(fileSystem.File.Exists)
            .Select(p => AnalyzerConfig.Parse(fileSystem.File.ReadAllText(p), p)));

        var driver = CSharpGeneratorDriver.Create(generators, additionalTexts, parseOptions, options);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        foreach (var d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            log.WriteLine($"Tarp: generator error: {d}");
        }
        return (CSharpCompilation)updated;
    }

    private sealed class GeneratorLoader : IAnalyzerAssemblyLoader
    {
        private readonly List<string> _directories = new();

        public GeneratorLoader()
        {
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                foreach (var dir in _directories)
                {
                    var candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        return context.LoadFromAssemblyPath(candidate);
                    }
                }
                return null;
            };
        }

        public void AddDependencyLocation(string fullPath)
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null && !_directories.Contains(dir))
            {
                _directories.Add(dir);
            }
        }

        public Assembly LoadFromPath(string fullPath)
        {
            AddDependencyLocation(fullPath);
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        }
    }

    private sealed class FileText : AdditionalText
    {
        private readonly SourceText _text;

        public FileText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text, Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(System.Threading.CancellationToken cancellationToken = default) => _text;
    }

    // the analyzer config the compiler would see: .editorconfig files and the build's global config
    private sealed class ConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigSet _set;

        public ConfigOptionsProvider(IEnumerable<AnalyzerConfig> configs)
        {
            _set = AnalyzerConfigSet.Create(configs.ToList());
            GlobalOptions = new Options(_set.GlobalConfigOptions.AnalyzerOptions);
        }

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
            new Options(_set.GetOptionsForSourcePath(tree.FilePath).AnalyzerOptions);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            new Options(_set.GetOptionsForSourcePath(textFile.Path).AnalyzerOptions);

        private sealed class Options : AnalyzerConfigOptions
        {
            private readonly ImmutableDictionary<string, string> _options;

            public Options(ImmutableDictionary<string, string> options) => _options = options;

            public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) =>
                _options.TryGetValue(key, out value);
        }
    }
}
