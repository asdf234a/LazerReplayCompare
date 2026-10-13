using System.Reflection;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mania.Replays;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

internal sealed record ManiaTimelineResult(List<ReplayTimelineFrame> Frames, List<ReplayTimelineFrame> RawFrames, string Label, double ScoreMultiplier);

/// <summary>
/// Builds a score timeline for an osu!mania replay without frame headers by replaying it through
/// <see cref="LazerManiaReplaySimulator"/> and scoring the results with osu!lazer's own <see cref="ScoreProcessor"/>.
/// </summary>
internal sealed class ManiaTimelineCalculator
{
    private static readonly HitResult[] counted_results =
    {
        HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss,
    };

    public ManiaTimelineResult Build(Score score, string beatmapPath, CorrectionMode correctionMode = CorrectionMode.Corrected)
    {
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("A .osu beatmap path is required to simulate mania timelines.");

        var info = score.ScoreInfo;
        if (info.Ruleset.OnlineID != 3)
            throw new NotSupportedException("The replay does not contain score headers, and only osu!mania timeline simulation is currently supported.");

        var ruleset = new ManiaRuleset();
        var playable = new FlatWorkingBeatmap(beatmapPath).GetPlayableBeatmap(ruleset.RulesetInfo, info.Mods);
        var preRoundingWindows = PreRoundingManiaHitWindows.AppliesTo(info);
        if (preRoundingWindows)
            PreRoundingManiaHitWindows.Apply(playable, info.Mods);

        if (playable.HitObjects.Count == 0)
            return new ManiaTimelineResult(new List<ReplayTimelineFrame>(), new List<ReplayTimelineFrame>(), "lazer-port(empty)", 1);

        var columns = ((ManiaBeatmap)playable).TotalColumns;
        var replayFrames = score.Replay.Frames.OfType<ManiaReplayFrame>().OrderBy(frame => frame.Time).ToList();
        var gameplayStartTime = playable.HitObjects[0].StartTime - 2000;
        var simulator = new LazerManiaReplaySimulator(playable, columns);
        simulator.Run(replayFrames, gameplayStartTime);
        var results = simulator.Results;

        var processor = ruleset.CreateScoreProcessor();
        processor.Mods.Value = info.Mods;
        processor.ApplyBeatmap(playable);
        var applied = new List<JudgementResult>(results.Count);
        var rawFrames = applyResults(processor, results, applied, new Dictionary<int, HitResult>());
        var frames = rawFrames;
        var scoreMultiplier = getScoreMultiplier(processor);

        var label = "raw";
        if (correctionMode == CorrectionMode.Corrected)
        {
            if (info.LegacyTotalScore != null)
            {
                // Stable replays store stable statistics (one judgement per hold note), which cannot be reconciled
                // with lazer judgements. The raw port already matches lazer's own playback for these.
                label = "raw(legacy-replay)";
            }
            else
            {
                var outcome = BoundaryCorrector.Solve(results, applied, processor, info.Statistics, info.TotalScore);
                var mehMissFlips = 0;
                var timingAdjustments = 0;

                if (!outcome.Exact &&
                    reconcileWithMehMissFlips(playable, columns, replayFrames, gameplayStartTime, results, processor, applied, info) is { } reconciled)
                {
                    (results, outcome, mehMissFlips) = reconciled;
                }

                if (!outcome.Exact &&
                    reconcileWithTimingAdjustments(playable, columns, replayFrames, gameplayStartTime, results, processor, applied, info) is { } adjusted)
                {
                    (results, outcome, mehMissFlips, timingAdjustments) = adjusted;
                }

                if (outcome.Exact && (outcome.Flips.Count > 0 || mehMissFlips > 0 || timingAdjustments > 0))
                {
                    frames = applyResults(processor, results, applied, outcome.Flips);
                    label = $"boundary-corrected(flips={outcome.Flips.Count + mehMissFlips},meh-miss={mehMissFlips},timing={timingAdjustments},solutions={outcome.ExactSolutions})";
                }
                else if (!outcome.Exact)
                {
                    // Leave the processor holding the raw results.
                    frames = applyResults(processor, simulator.Results, applied, new Dictionary<int, HitResult>());
                    label = $"raw(boundary:{outcome.Note})";
                }
                else
                {
                    label = $"raw(boundary:{outcome.Note})";
                }
            }
        }

        if (preRoundingWindows)
            label += ",pre-2025.710-windows";

        return new ManiaTimelineResult(frames, rawFrames, label, scoreMultiplier);
    }

    private const int max_meh_miss_flips = 3;
    private const int max_meh_miss_attempts = 48;

    private static readonly HitResult[] hold_statistics = { HitResult.Miss, HitResult.IgnoreHit, HitResult.IgnoreMiss, HitResult.ComboBreak };

    /// <summary>
    /// Meh↔Miss flips change combo and cascade through hold notes (tail capping, hold/body results), so they cannot be
    /// applied after the fact. Each candidate set is forced into a fresh simulation instead, and only accepted when the
    /// highest combo stays equal to the recorded one, the miss/hold statistics match, and the remaining gap can be closed
    /// by hit↔hit boundary flips.
    /// </summary>
    private static (IReadOnlyList<LazerManiaReplaySimulator.Judged> results, BoundaryCorrector.Outcome outcome, int flips)? reconcileWithMehMissFlips(
        IBeatmap playable,
        int columns,
        List<ManiaReplayFrame> replayFrames,
        double gameplayStartTime,
        IReadOnlyList<LazerManiaReplaySimulator.Judged> results,
        ScoreProcessor processor,
        List<JudgementResult> applied,
        ScoreInfo info)
    {
        var missGap = info.Statistics.GetValueOrDefault(HitResult.Miss) - results.Count(r => r.Type == HitResult.Miss);
        var count = Math.Abs(missGap);
        if (count == 0 || count > max_meh_miss_flips)
            return null;

        var from = missGap > 0 ? HitResult.Meh : HitResult.Miss;
        var to = missGap > 0 ? HitResult.Miss : HitResult.Meh;
        var candidates = results
            .Where(r => r.Type == from)
            .Select(r => (judged: r, probability: BoundaryCorrector.FlipProbability(r, to)))
            .Where(c => c.probability > 0)
            .ToList();
        if (candidates.Count < count)
            return null;

        var attempts = combinations(candidates, count)
            .OrderByDescending(set => set.Aggregate(1.0, (p, c) => p * c.probability))
            .Take(max_meh_miss_attempts);

        foreach (var set in attempts)
        {
            var simulator = new LazerManiaReplaySimulator(playable, columns)
            {
                ForcedResults = set.ToDictionary(c => c.judged.HitObject, _ => to),
            };
            simulator.Run(replayFrames, gameplayStartTime);
            applyResults(processor, simulator.Results, applied, new Dictionary<int, HitResult>());

            if (processor.HighestCombo.Value != info.MaxCombo)
                continue;
            if (hold_statistics.Any(r => processor.Statistics.GetValueOrDefault(r) != info.Statistics.GetValueOrDefault(r)))
                continue;

            var outcome = BoundaryCorrector.Solve(simulator.Results, applied, processor, info.Statistics, info.TotalScore);
            if (outcome.Exact)
                return (simulator.Results, outcome, count);
        }

        return null;
    }

    private const double timing_nudge = 0.4;
    private const int max_timing_adjustments = 6;

    private static readonly HitResult[] gap_statistics =
    {
        HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss,
        HitResult.IgnoreHit, HitResult.IgnoreMiss, HitResult.ComboBreak,
    };

    /// <summary>
    /// Besides hit windows (whose boundaries sit on x.5ms), OrderedHitPolicy compares the input time against the next
    /// object's start time (`time &lt; next.StartTime`). Object times are whole milliseconds, so a press stored exactly on
    /// the next object's start may really have happened up to 0.5ms earlier and gone to the previous object. Such presses
    /// (together with any frames stored at the same millisecond, keeping their order) are nudged by 0.4ms and re-simulated;
    /// nudges that bring the statistics closer without changing the max combo are accepted greedily, and after each one the
    /// remaining gap is handed to the boundary flips.
    /// </summary>
    private static (IReadOnlyList<LazerManiaReplaySimulator.Judged> results, BoundaryCorrector.Outcome outcome, int mehMiss, int timing)? reconcileWithTimingAdjustments(
        IBeatmap playable,
        int columns,
        List<ManiaReplayFrame> replayFrames,
        double gameplayStartTime,
        IReadOnlyList<LazerManiaReplaySimulator.Judged> rawResults,
        ScoreProcessor processor,
        List<JudgementResult> applied,
        ScoreInfo info)
    {
        var candidates = findTimingCandidates(playable, replayFrames);
        if (candidates.Count == 0)
            return null;

        IReadOnlyList<LazerManiaReplaySimulator.Judged> simulate(List<ManiaReplayFrame> frames)
        {
            var simulator = new LazerManiaReplaySimulator(playable, columns);
            simulator.Run(frames, gameplayStartTime);
            return simulator.Results;
        }

        (int gap, bool comboOk) evaluate(IReadOnlyList<LazerManiaReplaySimulator.Judged> results)
        {
            applyResults(processor, results, applied, new Dictionary<int, HitResult>());
            var gap = gap_statistics.Sum(r => Math.Abs(processor.Statistics.GetValueOrDefault(r) - info.Statistics.GetValueOrDefault(r)));
            return (gap, processor.HighestCombo.Value == info.MaxCombo);
        }

        var currentGap = evaluate(rawResults).gap;

        // Score each candidate on its own.
        var improving = new List<(int index, int direction, int gap)>();
        foreach (var (index, direction) in candidates)
        {
            var (gap, comboOk) = evaluate(simulate(nudge(replayFrames, index, direction)));
            if (comboOk && gap < currentGap)
                improving.Add((index, direction, gap));
        }

        // Each improving nudge on its own may already be the explanation (the greedy accumulation below would otherwise
        // commit to the first-best nudge and never revisit the others in isolation).
        foreach (var (index, direction, _) in improving.OrderBy(c => c.gap))
        {
            var single = nudge(replayFrames, index, direction);
            var results = simulate(single);
            evaluate(results);

            var outcome = BoundaryCorrector.Solve(results, applied, processor, info.Statistics, info.TotalScore);
            if (outcome.Exact)
                return (results, outcome, 0, 1);

            if (reconcileWithMehMissFlips(playable, columns, single, gameplayStartTime, results, processor, applied, info) is { } reconciled)
                return (reconciled.results, reconciled.outcome, reconciled.flips, 1);
        }

        var current = replayFrames;
        var accepted = 0;
        foreach (var (index, direction, _) in improving.OrderBy(c => c.gap).Take(max_timing_adjustments))
        {
            var trial = nudge(current, index, direction);
            var results = simulate(trial);
            var (gap, comboOk) = evaluate(results);
            if (!comboOk || gap >= currentGap)
                continue;

            current = trial;
            currentGap = gap;
            accepted++;

            var outcome = BoundaryCorrector.Solve(results, applied, processor, info.Statistics, info.TotalScore);
            if (outcome.Exact)
                return (results, outcome, 0, accepted);

            if (reconcileWithMehMissFlips(playable, columns, current, gameplayStartTime, results, processor, applied, info) is { } reconciled)
                return (reconciled.results, reconciled.outcome, reconciled.flips, accepted);
        }

        return null;
    }

    /// <summary>Presses stored exactly on the start time of the next object in their column while the previous object could still take them.</summary>
    private static List<(int index, int direction)> findTimingCandidates(IBeatmap playable, List<ManiaReplayFrame> frames)
    {
        var byColumn = playable.HitObjects.Cast<ManiaHitObject>()
                               .GroupBy(h => h.Column)
                               .ToDictionary(g => g.Key, g => g.OrderBy(h => h.StartTime).ToList());
        var candidates = new List<(int, int)>();

        for (var i = 0; i < frames.Count; i++)
        {
            var previous = i > 0 ? frames[i - 1].Actions : new List<ManiaAction>();
            foreach (var column in frames[i].Actions.Except(previous).Select(a => (int)a))
            {
                if (!byColumn.TryGetValue(column, out var objects))
                    continue;

                var k = objects.FindIndex(h => h.StartTime == frames[i].Time);
                if (k <= 0)
                    continue;

                var prev = objects[k - 1];
                var missWindow = (prev is HoldNote hold ? hold.Head : prev).HitWindows.WindowFor(HitResult.Miss);
                if (prev is not HoldNote && frames[i].Time - prev.StartTime > missWindow)
                    continue;

                // The press may really have been earlier (previous object takes it) – and, symmetrically, later.
                candidates.Add((i, -1));
                candidates.Add((i, 1));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Moves frame <paramref name="index"/> by 0.4ms in <paramref name="direction"/>, together with the frames stored at the
    /// same millisecond on that side (chords are recorded as consecutive frames), keeping their relative order.
    /// </summary>
    private static List<ManiaReplayFrame> nudge(List<ManiaReplayFrame> frames, int index, int direction)
    {
        var result = frames.ToList();
        var time = frames[index].Time;
        var group = new List<int> { index };
        for (var j = index + direction; j >= 0 && j < frames.Count && frames[j].Time == time; j += direction)
            group.Add(j);

        // group[0] is the candidate; frames further out on the nudge side move a little further to keep the order.
        for (var g = 0; g < group.Count; g++)
        {
            var f = frames[group[g]];
            result[group[g]] = new ManiaReplayFrame(time + direction * (timing_nudge + g * 0.001), f.Actions.ToArray());
        }

        // Keep the neighbour on the far side of the nudge from being overtaken.
        var limit = group.Max(j => j * direction) * direction + direction;
        if (limit >= 0 && limit < frames.Count && direction * (frames[limit].Time - result[group[^1]].Time) <= 0)
            return frames;

        return result;
    }

    private static IEnumerable<List<T>> combinations<T>(IReadOnlyList<T> items, int k, int start = 0)
    {
        if (k == 0)
        {
            yield return new List<T>();
            yield break;
        }

        for (var i = start; i <= items.Count - k; i++)
        {
            foreach (var rest in combinations(items, k - 1, i + 1))
            {
                rest.Insert(0, items[i]);
                yield return rest;
            }
        }
    }

    /// <summary>
    /// (Re)applies all results to <paramref name="processor"/> (reverting previously <paramref name="applied"/> ones) and
    /// records a timeline frame after every result that counts as a hit judgement.
    /// </summary>
    private static List<ReplayTimelineFrame> applyResults(
        ScoreProcessor processor,
        IReadOnlyList<LazerManiaReplaySimulator.Judged> results,
        List<JudgementResult> applied,
        IReadOnlyDictionary<int, HitResult> overrides)
    {
        for (var i = applied.Count - 1; i >= 0; i--)
            processor.RevertResult(applied[i]);
        applied.Clear();

        var hits = counted_results.ToDictionary(result => result.ToString(), _ => 0, StringComparer.Ordinal);
        var frames = new List<ReplayTimelineFrame>();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var type = overrides.TryGetValue(i, out var overridden) ? overridden : result.Type;
            var judgementResult = new JudgementResult(result.HitObject, result.HitObject.Judgement) { Type = type };
            processor.ApplyResult(judgementResult);
            applied.Add(judgementResult);

            if (!counted_results.Contains(type))
                continue;

            hits[type.ToString()]++;
            var objectTime = result.HitObject.StartTime;
            frames.Add(new ReplayTimelineFrame(
                Time: result.Time,
                Index: frames.Count + 1,
                Score: processor.TotalScore.Value,
                Combo: processor.Combo.Value,
                MaxCombo: processor.HighestCombo.Value,
                Accuracy: processor.Accuracy.Value,
                Hits: new Dictionary<string, int>(hits, StringComparer.Ordinal),
                DebugInfo: new ReplayFrameDebugInfo(
                    Column: ((ManiaHitObject)result.HitObject).Column,
                    Kind: result.HitObject switch
                    {
                        HeadNote => "HoldHead",
                        TailNote => "HoldTail",
                        _ => "Note",
                    },
                    ObjectTime: objectTime,
                    Offset: result.Offset ?? result.Time - objectTime,
                    Result: type.ToString())));
        }

        return frames;
    }

    private static double getScoreMultiplier(ScoreProcessor processor)
    {
        var field = typeof(ScoreProcessor).GetField("scoreMultiplier", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(processor) is double value ? value : 1;
    }
}
