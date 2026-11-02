using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Tarp.McDc;

/*
 * A Cobertura XML report (coverage-04.dtd, as coverlet writes it): Azure DevOps'
 * PublishCodeCoverageResults, GitLab, Jenkins, Codecov and ReportGenerator read it. One package per
 * module, one class per source file; per line how many times it ran and, where it has decisions or
 * branch points, its branches taken ("condition-coverage", counted as in the lcov tracefile).
 * Cobertura has no MC/DC: the other reports have it. Methods are left out (the map has no function
 * ends to give them their lines).
 */
public static class CoberturaReport
{
    public static string Format(IReadOnlyList<ModuleResult> modules) => Format(modules, DateTimeOffset.UtcNow);

    public static string Format(IReadOnlyList<ModuleResult> modules, DateTimeOffset timestamp)
    {
        var files = modules
            .SelectMany(m => FileCoverage.Of(m).Select(f => (Module: m, File: f, Path: m.Map.Sources.TryGetValue(f.File, out var p) ? p : f.File)))
            .ToList();
        // file names relative to the sources' common directory, as coverlet writes them
        var root = CommonDirectory(files.Select(f => f.Path).ToList());

        var packages = new XElement("packages");
        int lines = 0, linesRun = 0, branches = 0, taken = 0;
        foreach (var module in modules)
        {
            var classes = new XElement("classes");
            int moduleLines = 0, moduleRun = 0, moduleBranches = 0, moduleTaken = 0;
            foreach (var (_, file, path) in files.Where(f => f.Module == module).OrderBy(f => f.File.File, StringComparer.Ordinal))
            {
                var perLine = Branches(module, file);
                var lineElements = new XElement("lines");
                foreach (var line in file.Lines.Union(perLine.Keys).OrderBy(l => l))
                {
                    var element = new XElement("line",
                        new XAttribute("number", line),
                        new XAttribute("hits", file.LineCounts.TryGetValue(line, out var c) ? c : 0),
                        new XAttribute("branch", perLine.ContainsKey(line) ? "true" : "false"));
                    if (perLine.TryGetValue(line, out var b))
                    {
                        element.Add(new XAttribute("condition-coverage", Invariant($"{Percent(b.Taken, b.Total)}% ({b.Taken}/{b.Total})")));
                    }
                    lineElements.Add(element);
                }
                var fileBranches = perLine.Values.Sum(b => b.Total);
                var fileTaken = perLine.Values.Sum(b => b.Taken);
                classes.Add(new XElement("class",
                    new XAttribute("name", ClassName(file.File)),
                    new XAttribute("filename", Relative(root, path)),
                    Rates(file.LinesRun.Count, file.Lines.Count, fileTaken, fileBranches),
                    new XAttribute("complexity", 0),
                    new XElement("methods"),
                    lineElements));
                moduleLines += file.Lines.Count;
                moduleRun += file.LinesRun.Count;
                moduleBranches += fileBranches;
                moduleTaken += fileTaken;
            }
            packages.Add(new XElement("package",
                new XAttribute("name", module.Map.Name.Length > 0 ? module.Map.Name : module.Map.ModuleId),
                Rates(moduleRun, moduleLines, moduleTaken, moduleBranches),
                new XAttribute("complexity", 0),
                classes));
            lines += moduleLines;
            linesRun += moduleRun;
            branches += moduleBranches;
            taken += moduleTaken;
        }

        var coverage = new XElement("coverage",
            Rates(linesRun, lines, taken, branches),
            new XAttribute("lines-covered", linesRun),
            new XAttribute("lines-valid", lines),
            new XAttribute("branches-covered", taken),
            new XAttribute("branches-valid", branches),
            new XAttribute("complexity", 0),
            new XAttribute("version", "1.9"),
            new XAttribute("timestamp", timestamp.ToUnixTimeSeconds()),
            new XElement("sources", new XElement("source", root)),
            packages);
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XDocumentType("coverage", null, "http://cobertura.sourceforge.net/xml/coverage-04.dtd", null),
            coverage);
        return document.Declaration + "\n" + document.ToString() + "\n";
    }

    // per line: its branches (decision outcomes, and branch points' ways), and how many were taken
    private static Dictionary<int, (int Taken, int Total)> Branches(ModuleResult module, FileCoverage file)
    {
        long Count(int probe) => module.Counts.TryGetValue(probe, out var c) ? c : 0;
        var perLine = new Dictionary<int, (int Taken, int Total)>();
        void Add(int line, int taken, int total) =>
            perLine[line] = perLine.TryGetValue(line, out var b) ? (b.Taken + taken, b.Total + total) : (taken, total);

        foreach (var d in file.Decisions)
        {
            Add(d.Decision.Line, (d.OutcomeTrue ? 1 : 0) + (d.OutcomeFalse ? 1 : 0), 2);
        }
        foreach (var s in file.Switches)
        {
            var sections = s.Switch.Sections.Where(p => p >= 0).ToList();
            Add(s.Switch.Line, sections.Count(p => Count(p) > 0), sections.Count);
        }
        return perLine;
    }

    private static XAttribute[] Rates(int linesRun, int lines, int taken, int branches) => new[]
    {
        new XAttribute("line-rate", Rate(linesRun, lines)),
        new XAttribute("branch-rate", Rate(taken, branches))
    };

    private static string Rate(int covered, int total) =>
        (total == 0 ? 1.0 : (double)covered / total).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Percent(int covered, int total) =>
        ((int)Math.Round(total == 0 ? 100.0 : 100.0 * covered / total)).ToString(CultureInfo.InvariantCulture);

    // Shop.Core/Orders.cs -> Shop.Core.Orders
    private static string ClassName(string file) =>
        Path.ChangeExtension(file, null).Replace('\\', '.').Replace('/', '.');

    private static string CommonDirectory(IReadOnlyList<string> paths)
    {
        var rooted = paths.Where(Path.IsPathRooted).Select(p => Path.GetDirectoryName(p) ?? "").ToList();
        if (rooted.Count == 0 || rooted.Count < paths.Count)
        {
            return "";
        }
        var common = rooted[0];
        foreach (var dir in rooted.Skip(1))
        {
            while (common.Length > 0 && !(dir == common || dir.StartsWith(common.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                         || dir.StartsWith(common.TrimEnd('/', '\\') + '/', StringComparison.Ordinal)))
            {
                common = Path.GetDirectoryName(common) ?? "";
            }
        }
        return common;
    }

    private static string Relative(string root, string path) =>
        root.Length == 0 ? path.Replace('\\', '/') : Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
