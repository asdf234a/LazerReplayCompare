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

/// <param name="Judgements">The judgements the timeline was built from, with the correction's result changes applied.</param>
/// <param name="Playable">The beatmap they were judged on (with the replay's mods and hit windows).</param>
internal sealed record ManiaTimelineResult(List<ReplayTimelineFrame> Frames, List<ReplayTimelineFrame> RawFrames, string Label, double ScoreMultiplier,
                                           IReadOnlyList<LazerManiaReplaySimulator.Judged> Judgements, IBeatmap Playable);

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
        simulationCache.Value = new System.Collections.Concurrent.ConcurrentDictionary<(long frames, string forced), IReadOnlyList<LazerManiaReplaySimulator.Judged>>();
        try
        {
            return build(score, beatmapPath, correctionMode);
        }
        finally
        {
            simulationCache.Value = null;
        }
    }

    /// <summary>
    /// Re-simulations requested by the correction stages, keyed by frame content and forced results. The widened and
    /// approximate stages revisit the same Meh↔Miss / timing variants many times.
    /// </summary>
    private static readonly AsyncLocal<System.Collections.Concurrent.ConcurrentDictionary<(long frames, string forced), IReadOnlyList<LazerManiaReplaySimulator.Judged>>?> simulationCache = new();

    /// <summary>Runs independent re-simulations in parallel so later <see cref="simulate"/> calls hit the cache.</summary>
    private static void prefetch(IBeatmap playable, int columns, double gameplayStartTime, ScoreInfo info,
                                 IEnumerable<(List<ManiaReplayFrame> frames, IReadOnlyDictionary<osu.Game.Rulesets.Objects.HitObject, HitResult>? forced)> requests)
    {
        if (simulationCache.Value == null) return;
        Parallel.ForEach(requests, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            request => simulate(playable, columns, request.frames, gameplayStartTime, info, request.forced));
    }

    private static IReadOnlyList<LazerManiaReplaySimulator.Judged> simulate(IBeatmap playable, int columns, List<ManiaReplayFrame> frames, double gameplayStartTime, ScoreInfo info,
                                                                            IReadOnlyDictionary<osu.Game.Rulesets.Objects.HitObject, HitResult>? forced = null)
    {
        long framesKey = frames.Count;
        foreach (var frame in frames)
        {
            framesKey = framesKey * 31 + BitConverter.DoubleToInt64Bits(frame.Time);
            foreach (var action in frame.Actions) framesKey = framesKey * 7 + (int)action + 1;
        }

        var forcedKey = forced == null ? "" : string.Join(";", forced.Select(kv => $"{kv.Key.StartTime}:{((ManiaHitObject)kv.Key).Column}:{kv.Key.GetType().Name}:{kv.Value}").Order());
        var cache = simulationCache.Value;
        if (cache != null && cache.TryGetValue((framesKey, forcedKey), out var cached))
            return cached;

        var simulator = new LazerManiaReplaySimulator(playable, columns) { LiveFrameInterval = LiveFrameIntervalFor(info), ForcedResults = forced };
        simulator.Run(frames, gameplayStartTime);
        if (cache != null)
            cache[(framesKey, forcedKey)] = simulator.Results;
        return simulator.Results;
    }

    private ManiaTimelineResult build(Score score, string beatmapPath, CorrectionMode correctionMode)
    {
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("A .osu beatmap path is required to simulate mania timelines.");

        var info = score.ScoreInfo;
        if (info.Ruleset.OnlineID != 3)
            throw new NotSupportedException("The replay does not contain score headers, and only osu!mania timeline simulation is currently supported.");

        var ruleset = new ManiaRuleset();
        var playable = new FlatWorkingBeatmap(beatmapPath).GetPlayableBeatmap(ruleset.RulesetInfo, info.Mods);
        var preRoundingWindows = PreRoundingManiaHitWindows.AppliesTo(info);
        var uncertainty = preRoundingWindows ? OldReplayUncertainty : BoundaryCorrector.TimingUncertainty.Rounding;
        if (preRoundingWindows)
            PreRoundingManiaHitWindows.Apply(playable, info.Mods);

        if (playable.HitObjects.Count == 0)
            return new ManiaTimelineResult(new List<ReplayTimelineFrame>(), new List<ReplayTimelineFrame>(), "lazer-port(empty)", 1, Array.Empty<LazerManiaReplaySimulator.Judged>(), playable);

        var columns = ((ManiaBeatmap)playable).TotalColumns;
        var replayFrames = score.Replay.Frames.OfType<ManiaReplayFrame>().OrderBy(frame => frame.Time).ToList();
        var gameplayStartTime = playable.HitObjects[0].StartTime - 2000;
        var simulator = new LazerManiaReplaySimulator(playable, columns) { LiveFrameInterval = LiveFrameIntervalFor(info) };
        simulator.Run(replayFrames, gameplayStartTime);
        var results = simulator.Results;

        var processor = ruleset.CreateScoreProcessor();
        processor.Mods.Value = info.Mods;
        processor.ApplyBeatmap(playable);
        var applied = new List<JudgementResult>(results.Count);
        var rawFrames = applyResults(processor, results, applied, new Dictionary<int, HitResult>());
        var frames = rawFrames;
        var scoreMultiplier = getScoreMultiplier(processor);
        IReadOnlyList<LazerManiaReplaySimulator.Judged> judged = results;
        IReadOnlyDictionary<int, HitResult> overrides = new Dictionary<int, HitResult>();

        var label = "raw";
        if (correctionMode == CorrectionMode.Corrected)
        {
            if (info.LegacyTotalScore != null)
            {
                // Stable replays store stable statistics (one judgement per hold note), which cannot be reconciled
                // with lazer judgements. The raw port already matches lazer's own playback for these; ReplayTimelineBuilder
                // scales it onto the recorded result (or scores it with the stable engine against a live osu!stable play).
                label = "raw(legacy-replay)";
            }
            else
            {
                (IReadOnlyList<LazerManiaReplaySimulator.Judged> results, BoundaryCorrector.Outcome outcome, int mehMiss, int timing) correct(BoundaryCorrector.TimingUncertainty u)
                {
                    var outcome = BoundaryCorrector.Solve(simulator.Results, applied, processor, info.Statistics, info.TotalScore, u);
                    if (!outcome.Exact &&
                        reconcileWithMehMissFlips(playable, columns, replayFrames, gameplayStartTime, simulator.Results, processor, applied, info, u) is { } reconciled)
                        return (reconciled.results, reconciled.outcome, reconciled.flips, 0);
                    if (!outcome.Exact &&
                        reconcileWithTimingAdjustments(playable, columns, replayFrames, gameplayStartTime, simulator.Results, processor, applied, info, u) is { } adjusted)
                        return adjusted;
                    return (simulator.Results, outcome, 0, 0);
                }

                var corrected = correct(uncertainty);
                var widened = 0.0;

                // Not enough boundary candidates: widen the assumed timing error step by step (exact solutions only).
                foreach (var width in widened_uncertainties)
                {
                    if (corrected.outcome.Exact) break;
                    applyResults(processor, simulator.Results, applied, new Dictionary<int, HitResult>(), buildFrames: false);
                    corrected = correct(new BoundaryCorrector.TimingUncertainty(width, width));
                    widened = width;
                }

                if (corrected.outcome.Exact && (corrected.outcome.Flips.Count > 0 || corrected.mehMiss > 0 || corrected.timing > 0))
                {
                    frames = applyResults(processor, corrected.results, applied, corrected.outcome.Flips);
                    (judged, overrides) = (corrected.results, corrected.outcome.Flips);
                    label = $"boundary-corrected(flips={corrected.outcome.Flips.Count + corrected.mehMiss},meh-miss={corrected.mehMiss},timing={corrected.timing},solutions={corrected.outcome.ExactSolutions}"
                            + (corrected.outcome.Exact && widened > 0 ? $",widened=±{widened}ms" : "") + ")";
                }
                else if (corrected.outcome.Exact)
                {
                    label = $"raw(boundary:{corrected.outcome.Note})";
                }
                else
                {
                    // Give up on an exact match: close as much of the count gap as the widest candidate set allows, keeping
                    // the score as close to the recorded one as possible.
                    var approx = approximateCorrection(playable, columns, replayFrames, gameplayStartTime, simulator.Results, processor, applied, info);
                    if (approx is { } best)
                    {
                        frames = applyResults(processor, best.results, applied, best.flips);
                        (judged, overrides) = (best.results, best.flips);
                        var dS = processor.TotalScore.Value - info.TotalScore;
                        label = best.gap == 0 && dS == 0
                            ? $"boundary-corrected(flips={best.flips.Count + best.changes},meh-miss={best.changes},widened=±{best.width}ms)"
                            : $"approx-corrected(flips={best.flips.Count + best.changes},hitΔ={best.gap},dS={dS},±{best.width}ms)";
                    }
                    else
                    {
                        frames = applyResults(processor, simulator.Results, applied, new Dictionary<int, HitResult>());
                        label = $"raw(boundary:{corrected.outcome.Note})";
                    }
                }
            }
        }

        if (preRoundingWindows)
            label += ",pre-2025.710-windows";

        var judgements = judged.Select((result, i) => overrides.TryGetValue(i, out var type) ? result with { Type = type } : result).ToList();
        return new ManiaTimelineResult(frames, rawFrames, label, scoreMultiplier, judgements, playable);
    }

    /// <summary>
    /// Replays recorded before osu!lazer 2025.530 only contain frames where the input changed (plus a 60Hz heartbeat),
    /// so playing them back skips the live updates in which expired objects were missed before the next input arrived.
    /// </summary>
    internal static double? LiveFrameIntervalFor(ScoreInfo info)
        => info.LegacyTotalScore == null && PreRoundingManiaHitWindows.IsBefore(info, (2025, 530)) ? LiveFrameInterval : null;

    /// <summary>
    /// How long before an input the emulated live update runs. Results are identical for anything up to ~0.25ms;
    /// LRC_LIVE_FRAME overrides it ("off" disables the emulation).
    /// </summary>
    internal static double? LiveFrameInterval = Environment.GetEnvironmentVariable("LRC_LIVE_FRAME") switch
    {
        "off" => null,
        { } value when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var live) => live,
        _ => 0.1,
    };

    /// <summary>Timing uncertainty assumed for replays set before osu!lazer 2025.710 (experimental).</summary>
    internal static BoundaryCorrector.TimingUncertainty OldReplayUncertainty = parseUncertainty(Environment.GetEnvironmentVariable("LRC_OLD_UNCERTAINTY")) ?? BoundaryCorrector.TimingUncertainty.Rounding;

    private static BoundaryCorrector.TimingUncertainty? parseUncertainty(string? value)
    {
        var parts = value?.Split(',');
        return parts?.Length == 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var before) && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var after)
            ? new BoundaryCorrector.TimingUncertainty(before, after)
            : null;
    }

    /// <summary>Timing errors (±ms) tried when the rounding-only candidates admit no exact solution.</summary>
    private static readonly double[] widened_uncertainties = { 1.0, 1.5, 2.0 };

    /// <summary>Timing errors (±ms) the approximate correction may assume, nearest judgements to a boundary first.</summary>
    private static readonly double[] approximate_uncertainties = { 2, 4, 8, 16 };

    private const int max_approximate_miss_variants = 12;

    /// <summary>
    /// Last resort when no exact solution exists: tries the raw simulation and Meh↔Miss / edge-press variants, each with
    /// increasingly wide flip candidates, and keeps the result with the smallest statistics gap, then the smallest score
    /// difference. Returns null when nothing improves on the raw simulation.
    /// </summary>
    private static (IReadOnlyList<LazerManiaReplaySimulator.Judged> results, Dictionary<int, HitResult> flips, int changes, int gap, double width)? approximateCorrection(
        IBeatmap playable,
        int columns,
        List<ManiaReplayFrame> replayFrames,
        double gameplayStartTime,
        IReadOnlyList<LazerManiaReplaySimulator.Judged> raw,
        ScoreProcessor processor,
        List<JudgementResult> applied,
        ScoreInfo info)
    {
        int gapNow() => gap_statistics.Sum(r => Math.Abs(processor.Statistics.GetValueOrDefault(r) - info.Statistics.GetValueOrDefault(r)));

        applyResults(processor, raw, applied, new Dictionary<int, HitResult>(), buildFrames: false);
        (int gap, long dS) bestKey = (gapNow(), Math.Abs(processor.TotalScore.Value - info.TotalScore));
        (IReadOnlyList<LazerManiaReplaySimulator.Judged> results, Dictionary<int, HitResult> flips, int changes, int gap, double width)? best = null;

        void consider(IReadOnlyList<LazerManiaReplaySimulator.Judged> results, int changes, double width)
        {
            // The processor holds `results` here.
            var outcome = BoundaryCorrector.Solve(results, applied, processor, info.Statistics, info.TotalScore, new BoundaryCorrector.TimingUncertainty(width, width), approximate: true);
            var flips = outcome.Flips;
            applyResults(processor, results, applied, flips, buildFrames: false);
            var key = (gapNow(), Math.Abs(processor.TotalScore.Value - info.TotalScore));
            if (key.CompareTo(bestKey) < 0)
            {
                bestKey = key;
                best = (results, flips, changes, key.Item1, width);
            }
        }

        foreach (var width in approximate_uncertainties)
        {
            applyResults(processor, raw, applied, new Dictionary<int, HitResult>(), buildFrames: false);
            consider(raw, 0, width);
            if (bestKey == (0, 0)) break;

            var variants = mehMissVariants(playable, columns, replayFrames, gameplayStartTime, raw, processor, applied, info,
                new BoundaryCorrector.TimingUncertainty(width, width), max_approximate_miss_variants).ToList();
            foreach (var (results, changes) in variants)
            {
                applyResults(processor, results, applied, new Dictionary<int, HitResult>(), buildFrames: false);
                consider(results, changes, width);
            }

            if (bestKey == (0, 0)) break;
        }

        return best;
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
        ScoreInfo info,
        BoundaryCorrector.TimingUncertainty uncertainty)
    {
        foreach (var (variant, changes) in mehMissVariants(playable, columns, replayFrames, gameplayStartTime, results, processor, applied, info, uncertainty, max_meh_miss_attempts))
        {
            if (processor.HighestCombo.Value != info.MaxCombo)
                continue;
            if (hold_statistics.Any(r => processor.Statistics.GetValueOrDefault(r) != info.Statistics.GetValueOrDefault(r)))
                continue;

            var outcome = BoundaryCorrector.Solve(variant, applied, processor, info.Statistics, info.TotalScore, uncertainty);
            if (outcome.Exact)
                return (variant, outcome, changes);
        }

        return null;
    }

    /// <summary>
    /// Re-simulations with Meh↔Miss flips (and ignored edge presses) forced, most likely first. The processor holds each
    /// yielded variant's results while the caller inspects it.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<LazerManiaReplaySimulator.Judged> results, int changes)> mehMissVariants(
        IBeatmap playable,
        int columns,
        List<ManiaReplayFrame> replayFrames,
        double gameplayStartTime,
        IReadOnlyList<LazerManiaReplaySimulator.Judged> results,
        ScoreProcessor processor,
        List<JudgementResult> applied,
        ScoreInfo info,
        BoundaryCorrector.TimingUncertainty uncertainty,
        int maxAttempts)
    {
        var missGap = info.Statistics.GetValueOrDefault(HitResult.Miss) - results.Count(r => r.Type == HitResult.Miss);
        var count = Math.Abs(missGap);
        if (count == 0 || count > max_meh_miss_flips)
            yield break;

        var from = missGap > 0 ? HitResult.Meh : HitResult.Miss;
        var to = missGap > 0 ? HitResult.Miss : HitResult.Meh;
        var candidates = results
            .Where(r => r.Type == from)
            .Select(r => (judged: r, to, probability: BoundaryCorrector.FlipProbability(r, to, uncertainty)))
            .Where(c => c.probability > 0)
            .ToList();

        // An early press stored on the miss window's edge may really have come just before the window opened. Lazer then
        // ignored it, and a later press could still hit the note.
        if (missGap < 0)
        {
            candidates.AddRange(results
                                .Where(r => r.Type == HitResult.Miss && r.Offset < 0)
                                .Select(r => (judged: r, to: HitResult.None, probability: BoundaryCorrector.FlipProbability(r, HitResult.None, uncertainty)))
                                .Where(c => c.probability > 0));
        }

        if (candidates.Count == 0)
            yield break;

        // Ignoring an early press can cascade (the freed later press changes which object the next press reaches), so
        // fewer changes than the miss gap may already close it.
        var attempts = Enumerable.Range(1, count)
            .SelectMany(k => combinations(candidates, k)
                             .Where(set => set.Select(c => c.judged.HitObject).Distinct().Count() == set.Count)
                             .OrderByDescending(set => set.Aggregate(1.0, (p, c) => p * c.probability))
                             .Take(maxAttempts));

        var attemptList = attempts.ToList();
        for (int i = 0; i < attemptList.Count; i++)
        {
            var set = attemptList[i];
            if (i % Environment.ProcessorCount == 0)
            {
                prefetch(playable, columns, gameplayStartTime, info, attemptList.Skip(i).Take(Environment.ProcessorCount)
                    .Select(a => (replayFrames, (IReadOnlyDictionary<osu.Game.Rulesets.Objects.HitObject, HitResult>?)a.ToDictionary(c => c.judged.HitObject, c => c.to))));
            }

            var variant = simulate(playable, columns, replayFrames, gameplayStartTime, info, set.ToDictionary(c => c.judged.HitObject, c => c.to));
            applyResults(processor, variant, applied, new Dictionary<int, HitResult>(), buildFrames: false);
            yield return (variant, set.Count);
        }
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
        ScoreInfo info,
        BoundaryCorrector.TimingUncertainty uncertainty)
    {
        var candidates = findTimingCandidates(playable, replayFrames);
        if (candidates.Count == 0)
            return null;

        IReadOnlyList<LazerManiaReplaySimulator.Judged> simulate(List<ManiaReplayFrame> frames)
            => ManiaTimelineCalculator.simulate(playable, columns, frames, gameplayStartTime, info);

        (int gap, bool comboOk) evaluate(IReadOnlyList<LazerManiaReplaySimulator.Judged> results)
        {
            applyResults(processor, results, applied, new Dictionary<int, HitResult>(), buildFrames: false);
            var gap = gap_statistics.Sum(r => Math.Abs(processor.Statistics.GetValueOrDefault(r) - info.Statistics.GetValueOrDefault(r)));
            return (gap, processor.HighestCombo.Value == info.MaxCombo);
        }

        var currentGap = evaluate(rawResults).gap;

        prefetch(playable, columns, gameplayStartTime, info,
            candidates.Select(c => (nudge(replayFrames, c.index, c.direction), (IReadOnlyDictionary<osu.Game.Rulesets.Objects.HitObject, HitResult>?)null)));

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

            var outcome = BoundaryCorrector.Solve(results, applied, processor, info.Statistics, info.TotalScore, uncertainty);
            if (outcome.Exact)
                return (results, outcome, 0, 1);

            if (reconcileWithMehMissFlips(playable, columns, single, gameplayStartTime, results, processor, applied, info, uncertainty) is { } reconciled)
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

            var outcome = BoundaryCorrector.Solve(results, applied, processor, info.Statistics, info.TotalScore, uncertainty);
            if (outcome.Exact)
                return (results, outcome, 0, accepted);

            if (reconcileWithMehMissFlips(playable, columns, current, gameplayStartTime, results, processor, applied, info, uncertainty) is { } reconciled)
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
        IReadOnlyDictionary<int, HitResult> overrides,
        bool buildFrames = true)
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

            // Correction stages only need the processor's totals.
            if (!buildFrames || !counted_results.Contains(type))
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
