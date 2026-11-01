using System.IO;

namespace Tarp.McDc;

// the runtime added to an instrumented assembly: GenerationTemplates/McDcRuntime.cs, embedded as a resource
public static class McDcRuntimeSource
{
    public const string FileName = "TarpGen.McDc.g.cs";

    public static string Generate(string moduleId, string hitsDirectory)
    {
        using var stream = typeof(McDcRuntimeSource).Assembly.GetManifestResourceStream("Tarp.McDcRuntime.cs")
            ?? throw new FileNotFoundException("embedded MC/DC runtime is missing");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd()
            .Replace("__TARP_MODULE_ID__", moduleId, System.StringComparison.Ordinal)
            .Replace("__TARP_HITS_DIR__", hitsDirectory.Replace("\"", "\"\"", System.StringComparison.Ordinal), System.StringComparison.Ordinal);
    }
}
