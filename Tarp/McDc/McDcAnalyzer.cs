using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tarp.Types;

namespace Tarp.McDc;

// one distinct evaluation of a decision, as recorded by the runtime
public record McDcHit(int DecisionId, uint Evaluated, uint Values, bool Outcome);

public static class McDcHits
{
    // parses a hits file (see TarpGen.McDc.Snapshot); hits from other modules are ignored
    public static IEnumerable<McDcHit> Parse(string text, string moduleId)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            yield break;
        }

        var header = lines[0].Split(' ');
        if (header.Length != 3 || header[0] != "tarp-mcdc" || header[1] != "1")
        {
            throw new FormatException("not a Tarp MC/DC hits file");
        }
        if (header[2] != moduleId)
        {
            yield break;
        }

        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(' ');
            yield return new McDcHit(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                uint.Parse(f[1], CultureInfo.InvariantCulture),
                uint.Parse(f[2], CultureInfo.InvariantCulture),
                f[3] == "1");
        }
    }

    public static IEnumerable<McDcHit> LoadDirectory(string directory, string moduleId) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.hits").SelectMany(f => Parse(File.ReadAllText(f), moduleId)).Distinct()
            : Enumerable.Empty<McDcHit>();
}

public enum ConditionCoverage { Covered, MissingTrue, MissingFalse, MissingBoth }

public class ConditionResult
{
    public int Index { get; init; }
    public string Text { get; init; } = "";
    public bool ShownTrue { get; init; }
    public bool ShownFalse { get; init; }
    public bool Covered => ShownTrue && ShownFalse;

    // condition values (null = not evaluated) that would show the missing value(s), if any
    public List<bool?[]> Suggestions { get; } = new();
}

public class DecisionResult
{
    public DecisionEntry Decision { get; init; } = new();
    public List<McDcHit> Evaluations { get; init; } = new();
    public List<ConditionResult> Conditions { get; init; } = new();
    // evaluations whose recorded outcome or evaluated conditions don't match the decision: an instrumentation bug
    public List<McDcHit> Inconsistent { get; init; } = new();
    public bool Executed => Evaluations.Count > 0;
}

/*
 * Masking MC/DC, computed by replaying the recorded evaluations through McDcFunctions' markers
 * (the monitor algorithm): a condition value that survives masking had an independent effect
 * on the outcome. A condition is shown when both its true and its false value survive in some
 * evaluation.
 */
public static class McDcAnalyzer
{
    public static DecisionResult Analyze(DecisionEntry decision, IEnumerable<McDcHit> hits)
    {
        var conditionSyntax = new List<ExpressionSyntax>();
        var tree = DecisionTree.Parse(decision.Text, conditionSyntax);
        var markers = new McDcFunctions().CreateMarkers(tree);
        var n = markers.Count;

        if (n != decision.Conditions.Count)
        {
            throw new InvalidDataException($"decision {decision.Id} has {decision.Conditions.Count} conditions in the map, but {n} in its text");
        }

        var evaluations = hits.Where(h => h.DecisionId == decision.Id).Distinct()
                              .OrderBy(h => h.Evaluated).ThenBy(h => h.Values).ToList();

        var indicator = 0;
        var inconsistent = new List<McDcHit>();
        foreach (var h in evaluations)
        {
            uint evaluated = 0;
            var outcome = DecisionTree.Evaluate(tree, h.Values, ref evaluated);
            if (evaluated != h.Evaluated || outcome != h.Outcome)
            {
                inconsistent.Add(h);
                continue;
            }
            indicator |= Monitor(markers, h.Evaluated, h.Values);
        }

        var result = new DecisionResult { Decision = decision, Evaluations = evaluations, Inconsistent = inconsistent };
        for (var i = 0; i < n; i++)
        {
            result.Conditions.Add(new ConditionResult
            {
                Index = i,
                Text = decision.Conditions[i],
                ShownTrue = (indicator & TrueBit(i)) != 0,
                ShownFalse = (indicator & FalseBit(i)) != 0
            });
        }

        Suggest(tree, markers, result.Conditions);

        return result;
    }

    // replays one evaluation: the markers of the evaluated conditions, in evaluation (left to right) order
    public static int Monitor(IReadOnlyList<MarkerFunction> markers, uint evaluated, uint values)
    {
        int temp = 0, indicator = 0;
        for (var i = 0; i < markers.Count; i++)
        {
            if ((evaluated & (1u << i)) != 0)
            {
                McDcFunctions.Mark(markers[i], (values & (1u << i)) != 0, ref temp, ref indicator);
            }
        }
        return indicator;
    }

    private static int TrueBit(int i) => 1 << 2 * i;
    private static int FalseBit(int i) => 1 << 2 * i + 1;

    // for every missing condition value, find one test (by trying all inputs) whose evaluation shows it
    private static void Suggest(McDcNode tree, IReadOnlyList<MarkerFunction> markers, List<ConditionResult> conditions)
    {
        if (conditions.All(c => c.Covered))
        {
            return;
        }

        var n = markers.Count;
        var missing = new List<(ConditionResult Condition, int Bit)>();
        foreach (var c in conditions)
        {
            if (!c.ShownTrue) missing.Add((c, TrueBit(c.Index)));
            if (!c.ShownFalse) missing.Add((c, FalseBit(c.Index)));
        }

        var seen = new HashSet<(uint, uint)>();
        for (uint input = 0; input < 1u << n && missing.Count > 0; input++)
        {
            uint evaluated = 0;
            DecisionTree.Evaluate(tree, input, ref evaluated);
            var values = input & evaluated;
            if (!seen.Add((evaluated, values)))
            {
                continue;
            }

            var shown = Monitor(markers, evaluated, values);
            foreach (var m in missing.Where(m => (shown & m.Bit) != 0).ToList())
            {
                var vector = new bool?[n];
                for (var i = 0; i < n; i++)
                {
                    vector[i] = (evaluated & (1u << i)) != 0 ? (values & (1u << i)) != 0 : null;
                }
                m.Condition.Suggestions.Add(vector);
                missing.Remove(m);
            }
        }
    }
}
