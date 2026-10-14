using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mania.Replays;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

/// <param name="Frames">One frame per stable judgement (one per hold note), scored in ScoreV1 with stable's accuracy.</param>
/// <param name="FinalScore">The replay's recorded ScoreV1.</param>
/// <param name="FinalAccuracy">The replay's stable accuracy (from its recorded judgement counts).</param>
internal sealed record StableTimelineResult(List<ReplayTimelineFrame> Frames, TimelineCorrection Correction, long FinalScore, double FinalAccuracy, int FinalMaxCombo);

/// <summary>
/// Timeline of an osu!stable replay as osu!stable itself scores it, for comparing against a live osu!stable play:
/// judged by <see cref="StableManiaReplaySimulator"/>, scored with <see cref="LegacyManiaScoreV1"/>.
/// Corrected timelines end on the replay's recorded result: judgement counts are matched by <see cref="StableCorrector"/>,
/// the ScoreV1 left over (stable keeps state the .osr does not record) is spread over the judgements in proportion to
/// how many have been made, and the max combo is the recorded one.
/// </summary>
internal static class StableTimelineCalculator
{
    private static readonly HitResult[] counted = [HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss];

    /// <summary>
    /// Whether <see cref="Build"/> applies: an osu!mania replay from osu!stable scored with ScoreV1 (ScoreV2 judges hold
    /// note heads and tails separately).
    /// </summary>
    public static bool Supports(ScoreInfo info)
        => info.LegacyTotalScore != null && info.Ruleset.OnlineID == 3 && !info.Mods.Any(m => m is ModScoreV2);

    /// <summary>
    /// Raw: the engine's own result. Corrected: brought onto the recorded result. Scaled: raw (the caller scales it onto
    /// <see cref="StableTimelineResult.FinalScore"/> / <see cref="StableTimelineResult.FinalAccuracy"/>).
    /// </summary>
    public static StableTimelineResult Build(Score score, string beatmapPath, CorrectionMode correctionMode)
    {
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("A .osu beatmap path is required to simulate mania timelines.");

        var info = score.ScoreInfo;
        if (info.Ruleset.OnlineID != 3)
            throw new NotSupportedException("Only osu!mania stable replays can be simulated.");

        var working = new FlatWorkingBeatmap(beatmapPath);
        var mods = info.Mods;
        var playable = working.GetPlayableBeatmap(new ManiaRuleset().RulesetInfo, mods);
        long recordedScore = info.LegacyTotalScore ?? info.TotalScore;
        double recordedAccuracy = LegacyManiaScoreV1.Accuracy(r => info.Statistics.GetValueOrDefault(r));
        if (playable.HitObjects.Count == 0)
            return new StableTimelineResult(new List<ReplayTimelineFrame>(), new TimelineCorrection(TimelineCorrectionStatus.Raw, 0, 0, "stable(empty)", StableScoring: true), recordedScore, recordedAccuracy, info.MaxCombo);

        int columns = ((ManiaBeatmap)playable).TotalColumns;
        double rate = mods.OfType<ModRateAdjust>().FirstOrDefault()?.SpeedChange.Value ?? 1;
        int hardRockEasy = mods.Any(m => m is ModHardRock) ? 1 : mods.Any(m => m is ModEasy) ? -1 : 0;
        int objectCount = playable.HitObjects.Count;
        double multiplier = LegacyManiaScoreV1.ModMultiplier(working.Beatmap, mods);
        double divider = LegacyManiaScoreV1.ModDivider(mods);

        var keyFrames = StableManiaReplaySimulator.ToKeyFrames(score.Replay.Frames.OfType<ManiaReplayFrame>(), columns);

        StableManiaReplaySimulator run(IReadOnlyList<StableManiaReplaySimulator.KeyFrame> frames)
        {
            var simulator = new StableManiaReplaySimulator(playable, columns, playable.Difficulty.OverallDifficulty, hardRockEasy, rate);
            simulator.Run(frames);
            return simulator;
        }

        double scoreOf(IReadOnlyList<StableManiaReplaySimulator.Judgement> judgements)
        {
            var v1 = new LegacyManiaScoreV1(objectCount, multiplier, divider);
            foreach (var j in judgements) v1.Add(j.Result);
            return v1.Total;
        }

        var baseline = run(keyFrames);
        var simulation = baseline;
        IReadOnlyList<StableManiaReplaySimulator.Judgement> judgements = baseline.Judgements;
        Func<double, int, double> adjust = (v1, _) => v1;
        int? recordedMaxCombo = null;
        string label = "stable-raw";

        if (correctionMode == CorrectionMode.Corrected)
        {
            var outcome = StableCorrector.Correct(baseline, shifts =>
            {
                var moved = StableManiaReplaySimulator.Nudge(keyFrames, shifts);
                return moved == null ? null : run(moved);
            }, info.Statistics, recordedScore, scoreOf);

            simulation = outcome.Simulation;
            judgements = outcome.Judgements;
            recordedMaxCombo = info.MaxCombo;

            // What the judgements cannot explain is spread in proportion to the judgements made so far.
            double total = scoreOf(judgements);
            double residual = recordedScore - total;
            int count = judgements.Count;
            adjust = (v1, n) => v1 + residual * n / count;
            label = $"stable-corrected({outcome.Note},changes={outcome.Changes},solutions={outcome.Solutions},hitΔ={outcome.Gap},v1Δ={total - recordedScore:F0})";
        }

        var frames = buildFrames(judgements, adjustCombo(simulation, judgements), objectCount, multiplier, divider, adjust, recordedMaxCombo);
        if (frames.Count == 0)
            return new StableTimelineResult(frames, new TimelineCorrection(TimelineCorrectionStatus.Raw, 0, 0, label, StableScoring: true), recordedScore, recordedAccuracy, info.MaxCombo);

        var last = frames[^1];
        int hitDifference = counted.Sum(r => Math.Abs(last.Hits.GetValueOrDefault(r.ToString()) - info.Statistics.GetValueOrDefault(r)));
        long scoreDifference = last.Score - recordedScore;
        var status = correctionMode switch
        {
            CorrectionMode.Raw => TimelineCorrectionStatus.Raw,
            CorrectionMode.Scaled => TimelineCorrectionStatus.Scaled,
            _ when hitDifference != 0 => TimelineCorrectionStatus.Approximate,
            _ when Math.Round(scoreOf(judgements)) == recordedScore => TimelineCorrectionStatus.Exact,
            _ => TimelineCorrectionStatus.Corrected,
        };

        return new StableTimelineResult(frames, new TimelineCorrection(status, scoreDifference, hitDifference, label, StableScoring: true), recordedScore, recordedAccuracy, info.MaxCombo);
    }

    /// <summary>
    /// The simulation's combo with notes that the correction turned into a miss (or out of one) breaking (or keeping) it.
    /// Hold notes keep the simulated combo: their judgement gives no combo, and a missed one has usually broken it already.
    /// </summary>
    private static List<StableManiaReplaySimulator.ComboEvent> adjustCombo(StableManiaReplaySimulator simulation, IReadOnlyList<StableManiaReplaySimulator.Judgement> judgements)
    {
        var events = simulation.Combo.ToList();
        bool changed = false;
        for (int i = 0; i < judgements.Count && i < simulation.Judgements.Count; i++)
        {
            var was = simulation.Judgements[i];
            var now = judgements[i];
            if (now.Object is HoldNote || (was.Result == HitResult.Miss) == (now.Result == HitResult.Miss)) continue;

            bool nowMiss = now.Result == HitResult.Miss;
            int index = events.FindIndex(e => e.Time == now.Time && e.Increment == nowMiss);
            if (index < 0) continue;
            events[index] = new StableManiaReplaySimulator.ComboEvent(now.Time, !nowMiss);
            changed = true;
        }

        if (changed)
            events = events.OrderBy(e => e.Time).ThenBy(e => e.Increment ? 0 : 1).ToList();
        return events;
    }

    private static List<ReplayTimelineFrame> buildFrames(
        IReadOnlyList<StableManiaReplaySimulator.Judgement> judgements,
        IReadOnlyList<StableManiaReplaySimulator.ComboEvent> combo,
        int objectCount,
        double multiplier,
        double divider,
        Func<double, int, double> adjust,
        int? recordedMaxCombo)
    {
        // combo and highest combo after each combo event (events are in time order, breaks after increments)
        var comboTimes = new double[combo.Count];
        var comboValues = new int[combo.Count];
        var comboMax = new int[combo.Count];
        int current = 0, highest = 0;
        for (int i = 0; i < combo.Count; i++)
        {
            current = combo[i].Increment ? current + 1 : 0;
            highest = Math.Max(highest, current);
            comboTimes[i] = combo[i].Time;
            comboValues[i] = current;
            comboMax[i] = highest;
        }

        (int combo, int max) comboAt(double time)
        {
            // last event at or before `time`
            int lo = 0, hi = comboTimes.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (comboTimes[mid] <= time) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            return found < 0 ? (0, 0) : (comboValues[found], comboMax[found]);
        }

        var v1 = new LegacyManiaScoreV1(objectCount, multiplier, divider);
        var counts = counted.ToDictionary(r => r, _ => 0);
        var hits = counted.ToDictionary(r => r.ToString(), _ => 0, StringComparer.Ordinal);
        var frames = new List<ReplayTimelineFrame>(judgements.Count);

        for (int n = 0; n < judgements.Count; n++)
        {
            var j = judgements[n];
            v1.Add(j.Result);
            counts[j.Result]++;
            hits[j.Result.ToString()]++;

            var (comboNow, maxNow) = comboAt(j.Time);
            if (recordedMaxCombo is int recorded)
                maxNow = n == judgements.Count - 1 ? recorded : Math.Min(maxNow, recorded);

            frames.Add(new ReplayTimelineFrame(
                Time: j.Time,
                Index: n + 1,
                Score: (long)Math.Round(adjust(v1.Total, n + 1)),
                Combo: comboNow,
                MaxCombo: maxNow,
                Accuracy: LegacyManiaScoreV1.Accuracy(r => counts[r]),
                Hits: new Dictionary<string, int>(hits, StringComparer.Ordinal),
                DebugInfo: new ReplayFrameDebugInfo(
                    Column: j.Object.Column,
                    Kind: j.Object is HoldNote ? "Hold" : "Note",
                    ObjectTime: j.Object.StartTime,
                    Offset: j.NoteDelta ?? j.Hold?.ReleaseDelta ?? j.Time - j.Object.StartTime,
                    Result: j.Result.ToString())));
        }

        return frames;
    }
}
