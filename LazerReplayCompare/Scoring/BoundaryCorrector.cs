using System.Reflection;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <summary>
/// Reconciles a simulated judgement list with the replay's recorded statistics/score by flipping only judgements whose
/// result can change within the ±0.5ms rounding of .osr frame times. Hit↔hit flips keep combo intact, so any flip set can
/// be scored in closed form from aggregate sums (no re-simulation).
/// </summary>
internal static class BoundaryCorrector
{
    public sealed record Candidate(int Index, HitResult From, HitResult To, double Probability, double DeltaBase, double DeltaCombo, double Time);
    public sealed record Outcome(Dictionary<int, HitResult> Flips, long Score, int ExactSolutions, int Candidates, long ElapsedMs, string Note)
    {
        public bool Exact => Flips.Count > 0 || Note == "already exact";
    }

    /// <summary>
    /// Fraction of the ±0.5ms rounding interval around a user-triggered judgement's offset in which it would have been
    /// judged as <paramref name="to"/> instead (0 when not user-triggered).
    /// </summary>
    public static double FlipProbability(LazerManiaReplaySimulator.Judged r, HitResult to)
    {
        if (r.Offset is not double off) return 0;
        var w = r.HitObject.HitWindows;
        HitResult judge(double o) { var x = w.ResultFor(o / r.Scale); return r.CapAtMeh && x > HitResult.Meh ? HitResult.Meh : x; }
        if (judge(off - 0.4999) != to && judge(off + 0.4999) != to) return 0;
        const int n = 1000;
        int hits = 0;
        for (int s = 0; s < n; s++) if (judge(off - 0.5 + (s + 0.5) / n) == to) hits++;
        return hits / (double)n;
    }

    private static readonly HitResult[] kinds = [HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh];

    public static Outcome Solve(IReadOnlyList<LazerManiaReplaySimulator.Judged> results, IReadOnlyList<JudgementResult> applied,
                                ScoreProcessor processor, IReadOnlyDictionary<HitResult, int> recordedStats, long recordedScore)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double field(string name) => Convert.ToDouble(typeof(ScoreProcessor).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(processor));
        double B = field("currentBaseScore"), Bmax = field("currentMaximumBaseScore"), C = field("currentComboPortion"), Cmax = field("maximumComboPortion");
        double mult = field("scoreMultiplier");
        long scoreOf(double dB, double dC)
        {
            double acc = (B + dB) / Bmax;
            double raw = 150000 * ((C + dC) / Cmax) + 850000 * Math.Pow(acc, 2 + 2 * acc);
            return (long)Math.Round(Math.Round(raw) * mult);
        }
        if (scoreOf(0, 0) != processor.TotalScore.Value)
            return new(new(), processor.TotalScore.Value, 0, 0, sw.ElapsedMilliseconds, "closed form mismatch");

        int comboBase(HitResult r) => r == HitResult.Perfect ? 300 : processor.GetBaseScoreForResult(r);
        double comboFactor(int comboAfter) => Math.Min(Math.Max(0.5, Math.Log(comboAfter, 4)), Math.Log(400, 4));

        // 1. Candidates with their flip probability under a uniform sub-ms prior on the true input time.
        var cands = new List<Candidate>();
        for (int k = 0; k < results.Count; k++)
        {
            var r = results[k];
            if (r.Offset is not double off || !kinds.Contains(r.Type)) continue;
            var w = r.HitObject.HitWindows;
            HitResult judge(double o) { var x = w.ResultFor(o / r.Scale); return r.CapAtMeh && x > HitResult.Meh ? HitResult.Meh : x; }
            // sample the ±0.5ms interval finely; probability = fraction of the interval giving each alternative
            if (judge(off - 0.4999) == r.Type && judge(off + 0.4999) == r.Type) continue;   // windows are wide; endpoints suffice
            var counts = new Dictionary<HitResult, int>(); const int n = 1000;
            for (int s = 0; s < n; s++) { var x = judge(off - 0.5 + (s + 0.5) / n); counts[x] = counts.GetValueOrDefault(x) + 1; }
            foreach (var (alt, c) in counts)
            {
                if (alt == r.Type || !kinds.Contains(alt)) continue;
                int combo = applied[k].ComboAfterJudgement;
                cands.Add(new(k, r.Type, alt, c / (double)n,
                    processor.GetBaseScoreForResult(alt) - processor.GetBaseScoreForResult(r.Type),
                    (comboBase(alt) - comboBase(r.Type)) * comboFactor(combo), r.Time));
            }
        }

        // 2. Transition counts: smallest flip count that closes the per-result count gap (ties: higher likelihood).
        var need = kinds.ToDictionary(k => k, k => recordedStats.GetValueOrDefault(k) - results.Count(r => r.Type == k));
        if (need.Values.All(v => v == 0) && scoreOf(0, 0) == recordedScore)
            return new(new(), recordedScore, 1, cands.Count, sw.ElapsedMilliseconds, "already exact");
        var groups = cands.GroupBy(c => (c.From, c.To)).Select(g => g.ToList()).ToList();
        var bestPlans = new List<int[]>(); int bestTotal = int.MaxValue;
        void plan(int gi, int[] take, Dictionary<HitResult, int> rem, int total)
        {
            if (total > bestTotal || total > 12) return;
            if (gi == groups.Count)
            {
                if (rem.Values.Any(v => v != 0)) return;
                if (total < bestTotal) { bestTotal = total; bestPlans.Clear(); }
                bestPlans.Add((int[])take.Clone());
                return;
            }
            var g = groups[gi];
            for (int t = 0; t <= g.Count && total + t <= 12; t++)
            {
                take[gi] = t;
                rem[g[0].From] += t; rem[g[0].To] -= t;
                plan(gi + 1, take, rem, total + t);
                rem[g[0].From] -= t; rem[g[0].To] += t;
            }
            take[gi] = 0;
        }
        plan(0, new int[groups.Count], new Dictionary<HitResult, int>(need), 0);
        if (bestPlans.Count == 0)
        {
            return solveLargePlan(cands, need, scoreOf, recordedScore, sw)
                   ?? new(new(), processor.TotalScore.Value, 0, cands.Count, sw.ElapsedMilliseconds, "no hit<->hit plan closes the count gap");
        }

        // 3. For each plan, enumerate selections. Groups whose flips do not touch the combo portion (e.g. Perfect->Great)
        //    are score-equivalent, so only combo-affecting groups are enumerated; the rest are filled in afterwards.
        var exact = new List<List<Candidate>>();
        foreach (var take in bestPlans)
        {
            double fixedDB = 0; var free = new List<(List<Candidate> g, int k)>(); var neutral = new List<(List<Candidate> g, int k)>();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                if (take[gi] == 0) continue;
                var g = groups[gi];
                fixedDB += take[gi] * g[0].DeltaBase;
                if (g.All(c => Math.Abs(c.DeltaCombo) < 1e-12)) neutral.Add((g, take[gi])); else free.Add((g, take[gi]));
            }
            foreach (var combo in enumerate(free, 0))
            {
                double dC = combo.Sum(c => c.DeltaCombo);
                if (scoreOf(fixedDB, dC) != recordedScore) continue;
                var sel = new List<Candidate>(combo);
                foreach (var (g, k) in neutral) sel.AddRange(spreadInTime(g, k));
                exact.Add(sel);
                if (exact.Count >= 20000) break;
            }
        }
        if (exact.Count == 0)
            return new(new(), processor.TotalScore.Value, 0, cands.Count, sw.ElapsedMilliseconds, "no selection reproduces the recorded score");

        // 4. Choose the exact solution closest to the posterior mean: marginals over all exact solutions, then the solution
        //    maximising the summed marginal of its flips (ties broken towards an even spread over time).
        var marginal = new Dictionary<(int, HitResult), double>();
        foreach (var sol in exact) foreach (var c in sol) marginal[(c.Index, c.To)] = marginal.GetValueOrDefault((c.Index, c.To)) + 1.0 / exact.Count;
        var chosen = exact.MaxBy(sol => sol.Sum(c => marginal[(c.Index, c.To)]))!;
        return new(chosen.ToDictionary(c => c.Index, c => c.To), recordedScore, exact.Count, cands.Count, sw.ElapsedMilliseconds,
            $"plan flips={bestTotal}: " + string.Join(", ", chosen.GroupBy(c => $"{c.From}->{c.To}").Select(g => $"{g.Key} x{g.Count()}")));
    }

    /// <summary>
    /// Fallback for gaps that need many flips (e.g. replays judged with the unrounded pre-2025.710 windows, where every
    /// judgement near a boundary is a candidate). The minimal flip plan is the net flow along the judgement chain
    /// Perfect↔Great↔Good↔Ok↔Meh; score-neutral flips are taken by probability and the combo-affecting ones are chosen by
    /// local search on the closed-form score.
    /// </summary>
    private static Outcome? solveLargePlan(List<Candidate> cands, Dictionary<HitResult, int> need, Func<double, double, long> scoreOf, long recordedScore,
                                           System.Diagnostics.Stopwatch sw)
    {
        if (kinds.Sum(k => need[k]) != 0)
            return null;

        // flow[i] > 0: that many judgements go kinds[i] -> kinds[i+1]; < 0: the reverse.
        var picks = new List<(List<Candidate> group, int count)>();
        int cumulative = 0;
        for (int i = 0; i < kinds.Length - 1; i++)
        {
            cumulative += need[kinds[i]];
            int flow = -cumulative;
            if (flow == 0) continue;
            var (from, to) = flow > 0 ? (kinds[i], kinds[i + 1]) : (kinds[i + 1], kinds[i]);
            var group = cands.Where(c => c.From == from && c.To == to).OrderByDescending(c => c.Probability).ThenBy(c => c.Time).ToList();
            if (group.Count < Math.Abs(flow)) return null;
            picks.Add((group, Math.Abs(flow)));
        }

        double fixedDB = picks.Sum(p => p.count * p.group[0].DeltaBase);
        var neutral = picks.Where(p => p.group.All(c => Math.Abs(c.DeltaCombo) < 1e-12)).ToList();
        var free = picks.Except(neutral).ToList();

        var chosen = new List<Candidate>();
        foreach (var (group, count) in neutral)
            chosen.AddRange(group.Take(count));

        var used = chosen.Select(c => c.Index).ToHashSet();
        var selection = free.Select(p => p.group.Where(c => !used.Contains(c.Index)).Take(p.count).ToList()).ToList();
        if (selection.Zip(free).Any(x => x.First.Count < x.Second.count)) return null;

        double comboOf() => selection.Sum(sel => sel.Sum(c => c.DeltaCombo));
        long distance(double dC) => Math.Abs(scoreOf(fixedDB, dC) - recordedScore);

        var rng = new Random(1);
        for (int restart = 0; restart < 64 && distance(comboOf()) != 0; restart++)
        {
            if (restart > 0)
            {
                for (int g = 0; g < free.Count; g++)
                    selection[g] = free[g].group.Where(c => !used.Contains(c.Index)).OrderBy(_ => rng.Next()).Take(free[g].count).ToList();
            }

            // Steepest-descent swaps within each group until no swap gets closer to the recorded score.
            bool improved = true;
            while (improved && distance(comboOf()) != 0)
            {
                improved = false;
                double current = comboOf();
                long best = distance(current);
                (int g, int s, Candidate u)? bestSwap = null;
                for (int g = 0; g < free.Count; g++)
                {
                    var selected = selection[g].Select(c => c.Index).ToHashSet();
                    for (int si = 0; si < selection[g].Count; si++)
                    {
                        foreach (var u in free[g].group)
                        {
                            if (selected.Contains(u.Index) || used.Contains(u.Index)) continue;
                            long d = distance(current - selection[g][si].DeltaCombo + u.DeltaCombo);
                            if (d < best) { best = d; bestSwap = (g, si, u); }
                        }
                    }
                }
                if (bestSwap is { } sw2) { selection[sw2.g][sw2.s] = sw2.u; improved = true; }
            }
        }

        if (distance(comboOf()) != 0)
            return null;

        chosen.AddRange(selection.SelectMany(x => x));
        if (chosen.Select(c => c.Index).Distinct().Count() != chosen.Count)
            return null;

        return new(chosen.ToDictionary(c => c.Index, c => c.To), recordedScore, -1, cands.Count, sw.ElapsedMilliseconds,
            "large plan: " + string.Join(", ", chosen.GroupBy(c => $"{c.From}->{c.To}").Select(g => $"{g.Key} x{g.Count()}")));
    }

    private static IEnumerable<List<Candidate>> enumerate(List<(List<Candidate> g, int k)> groups, int gi)
    {
        if (gi == groups.Count) { yield return new List<Candidate>(); yield break; }
        foreach (var pick in choose(groups[gi].g, groups[gi].k, 0))
            foreach (var rest in enumerate(groups, gi + 1))
            { var l = new List<Candidate>(pick); l.AddRange(rest); yield return l; }
    }

    private static IEnumerable<List<Candidate>> choose(List<Candidate> items, int k, int start)
    {
        if (k == 0) { yield return new List<Candidate>(); yield break; }
        for (int i = start; i <= items.Count - k; i++)
            foreach (var rest in choose(items, k - 1, i + 1))
            { rest.Insert(0, items[i]); yield return rest; }
    }

    // Score-neutral flips: pick k candidates at evenly spaced time quantiles (minimises the worst-case timeline drift).
    private static IEnumerable<Candidate> spreadInTime(List<Candidate> g, int k)
    {
        var ordered = g.OrderBy(c => c.Time).ToList();
        for (int j = 0; j < k; j++) yield return ordered[(int)Math.Floor((j + 0.5) * ordered.Count / k)];
    }
}
