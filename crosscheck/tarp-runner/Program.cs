// TarpRunner <source.cs> <inputs.json> <out.json>
// Instruments the source as Tarp's build does (CoverageRewriter), compiles it with the runtime, calls each
// method with its inputs, and writes Tarp's MC/DC verdicts per decision (by line).
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tarp.McDc;

var source = File.ReadAllText(args[0]);
var inputs = JsonSerializer.Deserialize<Dictionary<string, List<List<JsonElement>>>>(File.ReadAllText(args[1]))!;

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
var tree = CSharpSyntaxTree.ParseText(source, path: "Cases.cs");
var original = CSharpCompilation.Create("Original", new[] { tree }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

var map = new McDcMap { ModuleId = "crosscheck" };
var root = (CompilationUnitSyntax)new CoverageRewriter(original.GetSemanticModel(tree), map, "Cases.cs").Visit(tree.GetRoot())!;
var instrumented = CSharpCompilation.Create("Instrumented",
    new[]
    {
        CSharpSyntaxTree.ParseText(McDcProjectInstrumenter.WithPrologue(root, "Cases.cs", original.Options.NullableContextOptions).ToFullString(), path: "Cases.instrumented.cs"),
        CSharpSyntaxTree.ParseText(McDcRuntimeSource.Generate(map.ModuleId, "", map.Probes.Count))
    },
    references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

using var stream = new MemoryStream();
var emit = instrumented.Emit(stream);
if (!emit.Success)
{
    Console.Error.WriteLine(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    return 1;
}
stream.Position = 0;
var assembly = new AssemblyLoadContext("cases", true).LoadFromStream(stream);
var type = assembly.GetType("Cases")!;

foreach (var (method, calls) in inputs)
{
    var m = type.GetMethod(method)!;
    var parameters = m.GetParameters();
    foreach (var call in calls)
    {
        var values = call.Select((v, i) => parameters[i].ParameterType == typeof(bool) ? (object)v.GetBoolean() : v.GetInt32()).ToArray();
        m.Invoke(null, values);
    }
}

var snapshot = (string)assembly.GetType("TarpGen.McDc")!.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
var results = McDcReport.Analyze(map, McDcHits.Parse(snapshot, map.ModuleId));

var output = results.ToDictionary(
    r => r.Decision.Line.ToString(),
    r => new
    {
        r.Decision.Text,
        r.Decision.Shape,
        Inconsistent = r.Inconsistent.Count,
        Conditions = r.Conditions.Select(c => new { c.Text, c.ShownTrue, c.ShownFalse, c.Paired, c.Covered }).ToList()
    });
File.WriteAllText(args[2], JsonSerializer.Serialize(new { Decisions = output, map.Skipped }, new JsonSerializerOptions { WriteIndented = true }));
return 0;
