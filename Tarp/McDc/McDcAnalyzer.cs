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

        if (!IsFor(lines[0], moduleId))
        {
            yield break;
        }

        // "p <id>" lines are probes (see ProbeHits)
        foreach (var line in lines.Skip(1).Where(l => !l.StartsWith("p ", StringComparison.Ordinal)))
        {
            var f = line.Split(' ');
            yield return new McDcHit(
                int.Parse(f[0], CultureInfo.InvariantCulture),
                uint.Parse(f[1], CultureInfo.InvariantCulture),
                uint.Parse(f[2], CultureInfo.InvariantCulture),
                f[3] == "1");
        }
    }

    // version 1: decisions only; version 2: also probes
    internal static bool IsFor(string headerLine, string moduleId)
    {
        var header = headerLine.Split(' ');
        if (header.Length != 3 || header[0] != "tarp-mcdc" || header[1] is not ("1" or "2"))
        {
            throw new FormatException("not a Tarp MC/DC hits file");
        }
        return header[2] == moduleId;
    }

    // the probes that ran
    public static IEnumerable<int> ParseProbes(string text, string moduleId)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !IsFor(lines[0], moduleId))
        {
            return Enumerable.Empty<int>();
        }
        return lines.Skip(1)
            .Where(l => l.StartsWith("p ", StringComparison.Ordinal))
            .Select(l => int.Parse(l[2..], CultureInfo.InvariantCulture))
            .ToList();
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
    // condition coverage: evaluated to true / to false at least once (masked or not)
    public bool SeenTrue { get; init; }
    public bool SeenFalse { get; init; }
    /*
     * shown true and shown false in evaluations with different outcomes. With && || ! only, a condition
     * that isn't masked decides the outcome, so that follows; with ^ == != ?: it doesn't: a ^ b with
     * TT and FF shows a true and false, both times with outcome false (the tutorial's "any three of four")
     */
    public bool Paired { get; init; } = true;
    public bool Covered => ShownTrue && ShownFalse && Paired;

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
    // decision coverage: the outcomes seen
    public bool OutcomeTrue => Evaluations.Except(Inconsistent).Any(h => h.Outcome);
    public bool OutcomeFalse => Evaluations.Except(Inconsistent).Any(h => !h.Outcome);
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
        var leaves = new List<ExpressionSyntax>();
        var tree = decision.Shape != null
            ? DecisionTree.Parse(decision.Shape, leaves, allOperators: true)
            : DecisionTree.Parse(decision.Text, leaves);
        var n = leaves.Count;

        if (n != decision.Conditions.Count)
        {
            throw new InvalidDataException($"decision {decision.Id} has {decision.Conditions.Count} conditions in the map, but {n} in its shape");
        }

        // && || !: McDcFunctions' monitor; with & | ^ == != ?: too, masking computed directly
        var classic = DecisionTree.IsClassic(tree);
        var markers = classic ? McDcFunctions.CreateMarkers(tree) : null;
        Func<uint, uint, int> shown = classic
            ? (evaluated, values) => Monitor(markers!, evaluated, values)
            : (evaluated, values) => DecisionTree.Shown(tree, evaluated, values);

        var recorded = hits.Where(h => h.DecisionId == decision.Id).Distinct().ToList();
        var evaluations = new List<McDcHit>();
        var inconsistent = new List<McDcHit>();
        foreach (var h in recorded)
        {
            uint evaluated = 0;
            var outcome = DecisionTree.Evaluate(tree, h.Values, ref evaluated);
            if (classic)
            {
                // complete evaluations, with their outcome: anything else is an instrumentation bug
                evaluations.Add(h);
                if (evaluated != h.Evaluated || outcome != h.Outcome)
                {
                    inconsistent.Add(h);
                }
            }
            else if (evaluated == h.Evaluated)
            {
                // every marker records the evaluation so far: the complete ones evaluate exactly what the decision does
                evaluations.Add(h with { Outcome = outcome });
            }
        }
        evaluations = evaluations.Distinct().OrderBy(h => h.Evaluated).ThenBy(h => h.Values).ToList();

        var result = new DecisionResult { Decision = decision, Evaluations = evaluations, Inconsistent = inconsistent };
        var consistent = evaluations.Except(inconsistent).ToList();

        var indicator = 0;
        var perEvaluation = consistent.Select(h => (Shown: shown(h.Evaluated, h.Values), h.Outcome)).ToList();
        foreach (var e in perEvaluation)
        {
            indicator |= e.Shown;
        }
        for (var i = 0; i < n; i++)
        {
            result.Conditions.Add(new ConditionResult
            {
                Index = i,
                Text = decision.Conditions[i],
                ShownTrue = (indicator & TrueBit(i)) != 0,
                ShownFalse = (indicator & FalseBit(i)) != 0,
                Paired = classic || HasPair(perEvaluation, i),
                SeenTrue = consistent.Any(h => (h.Evaluated & h.Values & (1u << i)) != 0),
                SeenFalse = consistent.Any(h => (h.Evaluated & ~h.Values & (1u << i)) != 0)
            });
        }

        if (classic)
        {
            Suggest(tree, shown, result.Conditions);
        }
        else
        {
            SuggestPairs(tree, shown, result.Conditions, perEvaluation);
        }

        return result;
    }

    // two evaluations showing condition i true and false, with different outcomes
    private static bool HasPair(IReadOnlyList<(int Shown, bool Outcome)> evaluations, int i) =>
        evaluations.Any(a => (a.Shown & TrueBit(i)) != 0 &&
                             evaluations.Any(b => (b.Shown & FalseBit(i)) != 0 && b.Outcome != a.Outcome));

    // with ^ == != ?:: a test that makes a pair with one already run, or else two that make one
    private static void SuggestPairs(McDcNode tree, Func<uint, uint, int> shownBy, List<ConditionResult> conditions,
        List<(int Shown, bool Outcome)> run)
    {
        var n = conditions.Count;
        var candidates = new List<(bool?[] Vector, int Shown, bool Outcome)>();
        var seen = new HashSet<(uint, uint)>();
        for (uint input = 0; input < 1u << n; input++)
        {
            uint evaluated = 0;
            var outcome = DecisionTree.Evaluate(tree, input, ref evaluated);
            var values = input & evaluated;
            if (!seen.Add((evaluated, values)))
            {
                continue;
            }
            var vector = new bool?[n];
            for (var i = 0; i < n; i++)
            {
                vector[i] = (evaluated & (1u << i)) != 0 ? (values & (1u << i)) != 0 : null;
            }
            candidates.Add((vector, shownBy(evaluated, values), outcome));
        }

        static bool Pair(int aShown, bool aOutcome, int bShown, bool bOutcome, int i) =>
            aOutcome != bOutcome &&
            ((aShown & TrueBit(i)) != 0 && (bShown & FalseBit(i)) != 0 || (aShown & FalseBit(i)) != 0 && (bShown & TrueBit(i)) != 0);

        foreach (var c in conditions.Where(c => !c.Covered))
        {
            var one = candidates.FirstOrDefault(u => run.Any(e => Pair(u.Shown, u.Outcome, e.Shown, e.Outcome, c.Index)));
            if (one.Vector != null)
            {
                c.Suggestions.Add(one.Vector);
                continue;
            }
            foreach (var a in candidates)
            {
                var b = candidates.FirstOrDefault(x => Pair(a.Shown, a.Outcome, x.Shown, x.Outcome, c.Index));
                if (b.Vector != null)
                {
                    c.Suggestions.Add(a.Vector);
                    c.Suggestions.Add(b.Vector);
                    break;
                }
            }
        }
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
    private static string Normalize(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static void Suggest(McDcNode tree, Func<uint, uint, int> shownBy, List<ConditionResult> conditions)
    {
        if (conditions.All(c => c.Covered))
        {
            return;
        }

        var n = conditions.Count;
        var missing = new List<(ConditionResult Condition, int Bit)>();
        foreach (var c in conditions)
        {
            if (!c.ShownTrue) missing.Add((c, TrueBit(c.Index)));
            if (!c.ShownFalse) missing.Add((c, FalseBit(c.Index)));
        }

        // the same condition (by its text) twice: one evaluation can't have it both true and false,
        // e.g. x > 0 && y || x > 0 (a heuristic: x > 0 and x <= 0 aren't seen as related)
        var repeated = conditions
            .SelectMany(a => conditions.Where(b => b.Index > a.Index && Normalize(b.Text) == Normalize(a.Text)), (a, b) => (a.Index, b.Index))
            .ToList();
        bool Possible(uint evaluated, uint values) => repeated.All(p =>
            (evaluated & (1u << p.Item1)) == 0 || (evaluated & (1u << p.Item2)) == 0 ||
            ((values >> p.Item1) & 1) == ((values >> p.Item2) & 1));

        var seen = new HashSet<(uint, uint)>();
        for (uint input = 0; input < 1u << n && missing.Count > 0; input++)
        {
            uint evaluated = 0;
            DecisionTree.Evaluate(tree, input, ref evaluated);
            var values = input & evaluated;
            if (!seen.Add((evaluated, values)) || !Possible(evaluated, values))
            {
                continue;
            }

            var shown = shownBy(evaluated, values);
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
