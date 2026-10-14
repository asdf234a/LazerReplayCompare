using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// Brings the stable engine's judgements onto the judgement counts recorded in an osu!stable replay. .osr frame times
/// are whole milliseconds, so a judgement taken within about 1 ms of a boundary may have gone the other way in the game:
///  - timing candidates: a note, hold head or release error within 1 ms of a hit window edge (re-evaluated in closed form),
///  - rule candidates: a key event whose rule decision (undo window, tail window, which object takes a press...) was
///    taken within 1 ms of its threshold (re-simulated with that event moved by ±1 ms).
/// The smallest set of candidates that reproduces the recorded counts wins; ties go to the candidates closest to their
/// boundary, then to the ScoreV1 closest to the recorded score.
/// </summary>
internal static class StableCorrector
{
    private static readonly HitResult[] counted = [HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss];

    private const int max_rule_events = 300;
    private const int max_solution_size = 3;
    private const int max_members_per_vector = 24;
    private const int max_evaluations = 3000;
    private const int max_solutions = 100_000;
    private const int max_verifications = 20;

    /// <param name="Changes">New result per hit object.</param>
    /// <param name="Delta">Count change per result (Perfect..Miss).</param>
    /// <param name="Margin">Distance to the boundary (ms).</param>
    /// <param name="Event">Moved key event (rule candidates).</param>
    private sealed record Candidate(Dictionary<ManiaHitObject, HitResult> Changes, int[] Delta, double Margin, int? Event = null, double Shift = 0)
    {
        public bool IsRule => Event != null;
        public string VectorKey => string.Join(",", Delta);
    }

    /// <param name="Simulation">The simulation the judgements come from (re-run with moved key events when rule candidates were used).</param>
    /// <param name="Judgements">Final judgements, in processing order.</param>
    /// <param name="Changes">Candidates applied.</param>
    /// <param name="Gap">Sum of absolute count differences left against the recorded counts.</param>
    public sealed record Outcome(StableManiaReplaySimulator Simulation, IReadOnlyList<StableManiaReplaySimulator.Judgement> Judgements, int Changes, int Gap, int Solutions, string Note);

    /// <param name="simulate">Runs the engine with the given key events moved (null when the move is impossible).</param>
    /// <param name="score">ScoreV1 of a judgement list.</param>
    public static Outcome Correct(
        StableManiaReplaySimulator baseline,
        Func<IReadOnlyDictionary<int, double>, StableManiaReplaySimulator?> simulate,
        IReadOnlyDictionary<HitResult, int> recordedCounts,
        long recordedScore,
        Func<IReadOnlyList<StableManiaReplaySimulator.Judgement>, double> score)
    {
        var target = counted.Select(r => recordedCounts.GetValueOrDefault(r)).ToArray();
        var need = sub(target, countsOf(baseline.Judgements));
        int baseGap = need.Sum(Math.Abs);
        if (baseGap == 0)
            return new Outcome(baseline, baseline.Judgements, 0, 0, 1, "already exact");

        var simulations = new System.Collections.Concurrent.ConcurrentDictionary<string, StableManiaReplaySimulator?>();

        // Applies a candidate set. Unverified: every candidate's changes laid over the baseline (moved key events assumed
        // independent). Verified: re-simulated with all moved events at once; null when the set does not combine that way
        // (the moves interact, or a timing flip no longer applies).
        (StableManiaReplaySimulator sim, List<StableManiaReplaySimulator.Judgement> judgements)? apply(IReadOnlyList<Candidate> set, bool verify = true)
        {
            var sim = baseline;
            var rules = set.Where(c => c.IsRule).ToList();
            if (!verify)
            {
                var changes = set.SelectMany(c => c.Changes).ToDictionary(kv => kv.Key, kv => kv.Value);
                return (baseline, baseline.Judgements.Select(j => changes.TryGetValue(j.Object, out var to) ? j with { Result = to } : j).ToList());
            }

            if (rules.Count > 0)
            {
                var shifts = rules.ToDictionary(c => c.Event!.Value, c => c.Shift);
                var key = string.Join(";", shifts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
                sim = simulations.GetOrAdd(key, _ => simulate(shifts))!;
                if (sim == null) return null;
            }

            var flips = set.Where(c => !c.IsRule).SelectMany(c => c.Changes).ToDictionary(kv => kv.Key, kv => kv.Value);
            var judgements = new List<StableManiaReplaySimulator.Judgement>(sim.Judgements.Count);
            int applied = 0;
            foreach (var j in sim.Judgements)
            {
                if (flips.TryGetValue(j.Object, out var to))
                {
                    judgements.Add(j with { Result = to });
                    applied++;
                }
                else
                    judgements.Add(j);
            }

            return applied == flips.Count ? (sim, judgements) : null;
        }

        // --- exact: fewest candidates, then closest to the boundaries, then ScoreV1 closest to the recorded score ---
        Outcome? exact(List<Candidate> candidates)
        {
            var groups = candidates.GroupBy(c => c.VectorKey)
                                   .Select(g => g.OrderBy(c => c.Margin).Take(max_members_per_vector).ToList())
                                   .ToList();

            for (int size = 1; size <= max_solution_size; size++)
            {
                var solutions = new List<(List<Candidate> set, double margin)>();
                foreach (var combo in vectorCombinations(groups, size, need))
                {
                    solutions.AddRange(memberCombinations(combo).Take(max_solutions - solutions.Count));
                    if (solutions.Count >= max_solutions) break;
                }

                if (solutions.Count == 0) continue;

                // rank on the laid-over changes, then re-simulate only the winners that move key events
                var ranked = new List<(List<Candidate> set, double margin, double distance)>();
                foreach (var (set, margin) in solutions.OrderBy(s => s.margin).Take(max_evaluations))
                {
                    if (apply(set, verify: false) is not { } laid || sub(target, countsOf(laid.judgements)).Any(d => d != 0)) continue;
                    ranked.Add((set, margin, Math.Abs(score(laid.judgements) - recordedScore)));
                }

                foreach (var (set, _, _) in ranked.OrderBy(r => Math.Round(r.margin, 6)).ThenBy(r => r.distance).Take(max_verifications))
                {
                    if (apply(set) is { } result && sub(target, countsOf(result.judgements)).All(d => d == 0))
                        return new Outcome(result.sim, result.judgements, size, 0, ranked.Count, "exact");
                }
            }

            return null;
        }

        // Cheapest explanations first: hit↔hit timing, then timing that involves a miss, then moved key events
        // (each needs a re-simulation).
        var candidates = timingCandidates(baseline, includeMiss: false).ToList();
        if (exact(candidates) is { } hitOnly) return hitOnly;

        candidates = timingCandidates(baseline, includeMiss: true).ToList();
        if (exact(candidates) is { } withMiss) return withMiss;

        candidates.AddRange(ruleCandidates(baseline, simulate));
        if (exact(candidates) is { } withRules) return withRules;

        // --- approximate: greedily close as much of the count gap as possible ---
        var chosen = new List<Candidate>();
        var remaining = (int[])need.Clone();
        var used = new HashSet<ManiaHitObject>();
        while (true)
        {
            Candidate? pick = null;
            int bestGap = remaining.Sum(Math.Abs);
            foreach (var c in candidates)
            {
                if (c.Changes.Keys.Any(used.Contains) || (c.IsRule && chosen.Any(o => o.Event == c.Event))) continue;
                int gap = sub(remaining, c.Delta).Sum(Math.Abs);
                if (gap < bestGap || (pick != null && gap == bestGap && c.Margin < pick.Margin && gap < remaining.Sum(Math.Abs)))
                {
                    bestGap = gap;
                    pick = c;
                }
            }

            if (pick == null) break;
            chosen.Add(pick);
            remaining = sub(remaining, pick.Delta);
            foreach (var o in pick.Changes.Keys) used.Add(o);
        }

        if (chosen.Count > 0 && apply(chosen) is { } approx)
        {
            int gap = sub(target, countsOf(approx.judgements)).Sum(Math.Abs);
            if (gap < baseGap)
                return new Outcome(approx.sim, approx.judgements, chosen.Count, gap, 0, "approximate");
        }

        return new Outcome(baseline, baseline.Judgements, 0, baseGap, 0, "no candidates");
    }

    private static int[] countsOf(IEnumerable<StableManiaReplaySimulator.Judgement> judgements)
    {
        var counts = new int[counted.Length];
        foreach (var j in judgements) counts[Array.IndexOf(counted, j.Result)]++;
        return counts;
    }

    private static int[] sub(int[] a, int[] b) => a.Select((v, i) => v - b[i]).ToArray();

    private static int[] delta(HitResult from, HitResult to)
    {
        var d = new int[counted.Length];
        d[Array.IndexOf(counted, from)]--;
        d[Array.IndexOf(counted, to)]++;
        return d;
    }

    /// <summary>Result changes within 1 ms of a window edge; changes to or from a miss (which move combo) only when asked.</summary>
    private static IEnumerable<Candidate> timingCandidates(StableManiaReplaySimulator sim, bool includeMiss)
    {
        var best = new Dictionary<(ManiaHitObject, HitResult), Candidate>();

        void consider(StableManiaReplaySimulator.Judgement j, Func<double, HitResult> resultAt, double direction)
        {
            var alt = resultAt(direction);
            if (alt == j.Result || (!includeMiss && (alt == HitResult.Miss || j.Result == HitResult.Miss))) return;

            // distance to the edge: the smallest move that changes the result
            double lo = 0, hi = 1;
            for (int i = 0; i < 24; i++)
            {
                double mid = (lo + hi) / 2;
                if (resultAt(direction * mid) == j.Result) lo = mid;
                else hi = mid;
            }

            var candidate = new Candidate(new Dictionary<ManiaHitObject, HitResult> { [j.Object] = alt }, delta(j.Result, alt), hi);
            if (!best.TryGetValue((j.Object, alt), out var existing) || existing.Margin > candidate.Margin)
                best[(j.Object, alt)] = candidate;
        }

        foreach (var j in sim.Judgements)
        {
            if (j.NoteDelta is double d)
            {
                consider(j, s => sim.NoteResult(d + s), 1);
                consider(j, s => sim.NoteResult(d + s), -1);
            }
            else if (j.Hold is { } h)
            {
                consider(j, s => sim.HoldResult(h with { HeadDelta = h.HeadDelta + s }), 1);
                consider(j, s => sim.HoldResult(h with { HeadDelta = h.HeadDelta + s }), -1);
                if (!h.Missed)
                {
                    consider(j, s => sim.HoldResult(h with { ReleaseDelta = h.ReleaseDelta + s }), 1);
                    consider(j, s => sim.HoldResult(h with { ReleaseDelta = h.ReleaseDelta + s }), -1);
                }
            }
        }

        return best.Values;
    }

    /// <summary>Key events whose rule decision was within 1 ms of its threshold, each moved by ±1 ms and re-simulated.</summary>
    private static IEnumerable<Candidate> ruleCandidates(StableManiaReplaySimulator baseline, Func<IReadOnlyDictionary<int, double>, StableManiaReplaySimulator?> simulate)
    {
        var before = baseline.Judgements.GroupBy(j => j.Object).ToDictionary(g => g.Key, g => g.Last().Result);
        var events = baseline.SensitiveEvents.OrderBy(kv => kv.Value).Take(max_rule_events).ToList();
        var found = new System.Collections.Concurrent.ConcurrentBag<Candidate>();

        Parallel.ForEach(events.SelectMany(e => new[] { (e.Key, e.Value, -1.0), (e.Key, e.Value, 1.0) }), item =>
        {
            var (ev, margin, shift) = item;
            var sim = simulate(new Dictionary<int, double> { [ev] = shift });
            if (sim == null) return;

            var changes = new Dictionary<ManiaHitObject, HitResult>();
            var d = new int[counted.Length];
            foreach (var g in sim.Judgements.GroupBy(j => j.Object))
            {
                var now = g.Last().Result;
                if (!before.TryGetValue(g.Key, out var was) || was == now) continue;
                changes[g.Key] = now;
                d[Array.IndexOf(counted, was)]--;
                d[Array.IndexOf(counted, now)]++;
            }

            if (changes.Count > 0)
                found.Add(new Candidate(changes, d, margin, ev, shift));
        });

        // the same change reached by several moves: keep the closest
        return found.GroupBy(c => string.Join(";", c.Changes.Select(kv => $"{kv.Key.StartTime}:{kv.Key.Column}:{kv.Value}").Order()))
                    .Select(g => g.OrderBy(c => c.Margin).ThenBy(c => c.Event).ThenBy(c => c.Shift).First());
    }

    /// <summary>Sets of <paramref name="size"/> distinct vector groups whose count changes add up to <paramref name="need"/>.</summary>
    private static IEnumerable<List<List<Candidate>>> vectorCombinations(List<List<Candidate>> groups, int size, int[] need)
    {
        IEnumerable<List<List<Candidate>>> rec(int start, int left, int[] remaining, List<List<Candidate>> picked)
        {
            if (left == 0)
            {
                if (remaining.All(v => v == 0)) yield return new List<List<Candidate>>(picked);
                yield break;
            }

            for (int i = start; i < groups.Count; i++)
            {
                // a vector may be used more than once (different objects), so recurse from i, not i + 1
                picked.Add(groups[i]);
                foreach (var r in rec(i, left - 1, sub(remaining, groups[i][0].Delta), picked))
                    yield return r;
                picked.RemoveAt(picked.Count - 1);
            }
        }

        return rec(0, size, need, new List<List<Candidate>>());
    }

    /// <summary>Member choices for a vector combination: distinct candidates, no shared objects, each event moved once.</summary>
    private static IEnumerable<(List<Candidate> set, double margin)> memberCombinations(List<List<Candidate>> combo)
    {
        IEnumerable<(List<Candidate>, double)> rec(int index, int minMember, List<Candidate> picked, double margin)
        {
            if (index == combo.Count)
            {
                yield return (new List<Candidate>(picked), margin);
                yield break;
            }

            var group = combo[index];
            // the same group twice in a row: pick members in increasing order to avoid duplicate sets
            int from = index > 0 && ReferenceEquals(combo[index - 1], group) ? minMember : 0;
            for (int m = from; m < group.Count; m++)
            {
                var c = group[m];
                if (picked.Any(p => ReferenceEquals(p, c) || p.Changes.Keys.Any(c.Changes.ContainsKey) || (c.IsRule && p.Event == c.Event)))
                    continue;
                picked.Add(c);
                foreach (var r in rec(index + 1, m + 1, picked, margin + c.Margin))
                    yield return r;
                picked.RemoveAt(picked.Count - 1);
            }
        }

        return rec(0, 0, new List<Candidate>(), 0);
    }
}
