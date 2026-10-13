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
        if (playable.HitObjects.Count == 0)
            return new ManiaTimelineResult(new List<ReplayTimelineFrame>(), new List<ReplayTimelineFrame>(), "lazer-port(empty)", 1);

        var simulator = new LazerManiaReplaySimulator(playable, ((ManiaBeatmap)playable).TotalColumns);
        simulator.Run(
            score.Replay.Frames.OfType<ManiaReplayFrame>().OrderBy(frame => frame.Time).ToList(),
            gameplayStartTime: playable.HitObjects[0].StartTime - 2000);
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
                if (outcome.Flips.Count > 0)
                {
                    frames = applyResults(processor, results, applied, outcome.Flips);
                    label = $"boundary-corrected(flips={outcome.Flips.Count},solutions={outcome.ExactSolutions})";
                }
                else
                {
                    label = $"raw(boundary:{outcome.Note})";
                }
            }
        }

        return new ManiaTimelineResult(frames, rawFrames, label, scoreMultiplier);
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
