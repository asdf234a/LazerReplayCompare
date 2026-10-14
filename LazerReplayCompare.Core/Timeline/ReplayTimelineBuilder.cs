using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

public sealed class ReplayTimelineBuilder
{
    private const int cache_capacity = 8;

    private readonly ManiaTimelineCalculator maniaTimelineCalculator = new();
    private readonly object cacheLock = new();
    private readonly LinkedList<(string Key, Lazy<ReplayTimelineResponse> Value)> cache = new();

    /// <summary>
    /// <see cref="Build"/> through a small most-recently-used cache, so the app's correction display and the overlay
    /// API share one computation per replay/mode. A changed replay file (size or write time) misses the cache.
    /// </summary>
    public ReplayTimelineResponse GetOrBuild(
        string replayPath,
        string? beatmapPath,
        double rate = 0,
        CorrectionMode correctionMode = CorrectionMode.Corrected,
        string scaleMetric = "Score",
        LiveClient client = LiveClient.Lazer)
    {
        var file = new FileInfo(replayPath);
        // Scaled timelines depend on the metric, and so do corrected osu!stable replays compared against osu!lazer (scaled too).
        var scaled = correctionMode == CorrectionMode.Scaled || (correctionMode == CorrectionMode.Corrected && client == LiveClient.Lazer && IsStableReplay(replayPath));
        var metricKey = scaled ? TimelineScaler.NormalizeMetric(scaleMetric) : "";
        // `rate` is not part of the key: playback rate comes from the replay's mods (see Build).
        var key = string.Join("|", replayPath, file.Exists ? file.Length : -1, file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
            beatmapPath, correctionMode, metricKey, client);

        Lazy<ReplayTimelineResponse> entry;
        lock (cacheLock)
        {
            var node = cache.First;
            while (node != null && node.Value.Key != key)
                node = node.Next;

            if (node != null)
            {
                cache.Remove(node);
                cache.AddFirst(node);
            }
            else
            {
                node = cache.AddFirst((key, new Lazy<ReplayTimelineResponse>(() => Build(replayPath, beatmapPath, rate, correctionMode, scaleMetric: scaleMetric, client: client))));
                while (cache.Count > cache_capacity)
                    cache.RemoveLast();
            }

            entry = node.Value.Value;
        }

        try
        {
            return entry.Value;
        }
        catch
        {
            // Do not keep failures (e.g. the beatmap file was not ready yet).
            lock (cacheLock)
            {
                var node = cache.First;
                while (node != null && !ReferenceEquals(node.Value.Value, entry))
                    node = node.Next;
                if (node != null)
                    cache.Remove(node);
            }

            throw;
        }
    }

    public ReplayTimelineResponse Build(
        string replayPath,
        string? beatmapPath,
        double rate = 0,
        CorrectionMode correctionMode = CorrectionMode.Corrected,
        JudgementScoreOrder scoreOrder = JudgementScoreOrder.JudgementTime,
        string scaleMetric = "Score",
        LiveClient client = LiveClient.Lazer)
    {
        if (string.IsNullOrWhiteSpace(replayPath))
            throw new ArgumentException("Missing osr path.", nameof(replayPath));

        if (!File.Exists(replayPath))
            throw new FileNotFoundException("Replay file was not found.", replayPath);

        using var stream = File.OpenRead(replayPath);
        var score = new LazerReplayScoreDecoder(beatmapPath).Parse(stream);

        var totalFrames = score.Replay.Frames.Count;
        var framesWithHeader = score.Replay.Frames.Count(f => f.Header != null);
        var frames = new List<ReplayTimelineFrame>();
        var lastHitCount = 0;

        foreach (var replayFrame in score.Replay.Frames.Where(frame => frame.Header != null).OrderBy(frame => frame.Time))
        {
            var header = replayFrame.Header!;
            var hits = ConvertStatistics(header.Statistics);
            var hitCount = GetHitCount(hits);

            if (hitCount <= lastHitCount)
                continue;

            frames.Add(new ReplayTimelineFrame(
                Time: replayFrame.Time,
                Index: hitCount,
                Score: header.TotalScore,
                Combo: header.Combo,
                MaxCombo: header.MaxCombo,
                Accuracy: header.Accuracy,
                Hits: hits));

            lastHitCount = hitCount;
        }

        var source = $"frame-header(total={totalFrames},withHeader={framesWithHeader})";
        var correction = new TimelineCorrection(TimelineCorrectionStatus.Recorded, 0, 0);
        string? appliedScaleMetric = null;
        var scaleFactor = 1.0;
        var info = score.ScoreInfo;
        var (finalScore, finalAccuracy, finalMaxCombo) = (info.TotalScore, info.Accuracy, info.MaxCombo);

        if (frames.Count == 0 && client == LiveClient.Stable && StableTimelineCalculator.Supports(info))
        {
            // osu!stable replay against a live osu!stable play: score it the way osu!stable does (ScoreV1, one judgement
            // per hold note), so it lines up with what osu!stable shows.
            var stable = StableTimelineCalculator.Build(score, beatmapPath ?? string.Empty, correctionMode);
            frames = stable.Frames;
            correction = stable.Correction;
            (finalScore, finalAccuracy, finalMaxCombo) = (stable.FinalScore, stable.FinalAccuracy, stable.FinalMaxCombo);
            var label = correction.Label;

            if (correctionMode == CorrectionMode.Scaled)
            {
                appliedScaleMetric = TimelineScaler.NormalizeMetric(scaleMetric);
                scaleFactor = TimelineScaler.Apply(frames, appliedScaleMetric, stable.FinalScore, stable.FinalAccuracy,
                    key => Enum.TryParse<HitResult>(key, out var result) ? info.Statistics.GetValueOrDefault(result) : 0);
                label += $",scaled(metric={appliedScaleMetric},factor={scaleFactor:F6})";
                correction = correction with { ScoreDifference = frames.Count > 0 ? frames[^1].Score - stable.FinalScore : 0, Label = label };
            }

            source = $"stable-simulated(engine=stable-port,rate={ModUtility.GetPlaybackRate(score):F4},{label},totalFrames={totalFrames})";
        }
        else if (frames.Count == 0)
        {
            // Hit windows and playback rate come from the replay's own mods (as in osu!lazer); `rate` is kept for API
            // compatibility only.
            var rateFromMods = ModUtility.GetPlaybackRate(score);
            var simulated = maniaTimelineCalculator.Build(score, beatmapPath ?? string.Empty, correctionMode);
            frames = simulated.Frames;
            var correctionLabel = simulated.Label;

            if (info.LegacyTotalScore != null && correctionMode != CorrectionMode.Raw && frames.Count > 0)
            {
                // osu!stable replay against osu!lazer: lazer's playback, scaled onto the recorded result. osu!stable counts
                // one judgement per hold note and lazer two (head and tail), so recorded counts are brought to lazer's total.
                appliedScaleMetric = TimelineScaler.NormalizeMetric(scaleMetric);
                var last = frames.MaxBy(frame => frame.Index)!;
                double lazerJudgements = counted_results.Sum(result => last.Hits.GetValueOrDefault(result.ToString()));
                double stableJudgements = counted_results.Sum(result => info.Statistics.GetValueOrDefault(result));
                double holdNoteCorrection = stableJudgements > 0 ? lazerJudgements / stableJudgements : 1;
                scaleFactor = TimelineScaler.Apply(frames, appliedScaleMetric, info.TotalScore, info.Accuracy,
                    key => Enum.TryParse<HitResult>(key, out var result) ? info.Statistics.GetValueOrDefault(result) * holdNoteCorrection : 0);
                correctionLabel += $",scaled(metric={appliedScaleMetric},factor={scaleFactor:F6},holdNotes=x{holdNoteCorrection:F4})";
            }
            else if (correctionMode == CorrectionMode.Scaled)
            {
                appliedScaleMetric = TimelineScaler.NormalizeMetric(scaleMetric);
                scaleFactor = TimelineScaler.Apply(frames, score, appliedScaleMetric);
                correctionLabel += $",scaled(metric={appliedScaleMetric},factor={scaleFactor:F6})";
            }
            correction = describeCorrection(info, frames, correctionMode, correctionLabel);
            source = $"mania-simulated(engine=lazer-port,rate={rateFromMods:F4},scoreMultiplier={simulated.ScoreMultiplier:F4},{correctionLabel},totalFrames={totalFrames},withHeader={framesWithHeader})";
        }

        if (frames.Count == 0)
            throw new InvalidOperationException("Could not build a replay timeline from this replay.");

        return new ReplayTimelineResponse(
            ReplayPath: replayPath,
            BeatmapPath: beatmapPath ?? string.Empty,
            Source: source,
            Estimated: false,
            TotalNotes: frames.Last().Index,
            FinalScore: finalScore,
            FinalAccuracy: finalAccuracy,
            FinalMaxCombo: finalMaxCombo,
            Timeline: frames.Select(frame => new object[] { frame.Time, frame.Score, frame.Combo }).ToList(),
            Frames: frames,
            ScaleMetric: appliedScaleMetric,
            ScaleFactor: scaleFactor,
            Correction: correction);
    }

    private static readonly HitResult[] counted_results = [HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss];

    /// <summary>
    /// Whether the .osr was saved by osu!stable, from its header alone: osu!stable writes its build date (yyyymmdd) as the
    /// version, osu!lazer 30000000 or later.
    /// </summary>
    public static bool IsStableReplay(string replayPath)
    {
        try
        {
            using var stream = File.OpenRead(replayPath);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            return reader.ReadInt32() < 30000000;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }

    private static TimelineCorrection describeCorrection(ScoreInfo info, List<ReplayTimelineFrame> frames, CorrectionMode mode, string label)
    {
        var last = frames.MaxBy(frame => frame.Index)!;
        var scoreDifference = last.Score - info.TotalScore;
        // osu!stable's recorded counts (one per hold note) cannot be compared one to one with lazer's judgements.
        var hitDifference = info.LegacyTotalScore != null ? 0
            : counted_results.Sum(result => Math.Abs(last.Hits.GetValueOrDefault(result.ToString()) - info.Statistics.GetValueOrDefault(result)));

        var status = mode == CorrectionMode.Raw ? TimelineCorrectionStatus.Raw
            : info.LegacyTotalScore != null && mode == CorrectionMode.Corrected ? TimelineCorrectionStatus.Legacy
            : mode == CorrectionMode.Scaled ? TimelineCorrectionStatus.Scaled
            : scoreDifference == 0 && hitDifference == 0 ? TimelineCorrectionStatus.Exact
            : TimelineCorrectionStatus.Approximate;

        return new TimelineCorrection(status, scoreDifference, hitDifference, label);
    }

    private static Dictionary<string, int> ConvertStatistics(IReadOnlyDictionary<HitResult, int> statistics)
    {
        return statistics.ToDictionary(
            pair => pair.Key.ToString(),
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    private static int GetHitCount(Dictionary<string, int> hits)
    {
        return hits
            .Where(pair => IsJudgementResult(pair.Key))
            .Sum(pair => pair.Value);
    }

    private static bool IsJudgementResult(string key)
    {
        return key is
            "Miss" or
            "Meh" or
            "Ok" or
            "Good" or
            "Great" or
            "Perfect";
    }
}

public sealed record ReplayTimelineResponse(
    string ReplayPath,
    string BeatmapPath,
    string Source,
    bool Estimated,
    int TotalNotes,
    long FinalScore,
    double FinalAccuracy,
    int FinalMaxCombo,
    List<object[]> Timeline,
    List<ReplayTimelineFrame> Frames,
    string? ScaleMetric = null,
    double ScaleFactor = 1.0,
    TimelineCorrection? Correction = null);

/// <summary>How the timeline relates to the replay's recorded result.</summary>
public enum TimelineCorrectionStatus
{
    /// <summary>The replay carries per-frame score headers; the timeline is the recorded one.</summary>
    Recorded,

    /// <summary>Corrected timeline reproducing the recorded score and judgement counts.</summary>
    Exact,

    /// <summary>Corrected as closely as possible, but not exactly onto the recorded result.</summary>
    Approximate,

    /// <summary>The simulator's result exactly as osu!lazer plays the replay back (no correction requested).</summary>
    Raw,

    /// <summary>Raw timeline linearly scaled onto the replay's final value.</summary>
    Scaled,

    /// <summary>
    /// An osu!stable replay compared against osu!lazer: lazer's playback of it, scaled onto its recorded result (stable
    /// counts hold notes differently, so its judgements cannot be reconciled with lazer's).
    /// </summary>
    Legacy,

    /// <summary>
    /// osu!stable replay compared against osu!stable: the recorded judgement counts are reproduced, and the score left
    /// over is spread across the timeline so it ends on the recorded score.
    /// </summary>
    Corrected,
}

/// <param name="Status">Overall result.</param>
/// <param name="ScoreDifference">Timeline final score minus the recorded score.</param>
/// <param name="HitDifference">Sum of absolute per-judgement count differences against the recorded statistics.</param>
/// <param name="Label">The simulator's detailed correction label.</param>
/// <param name="StableScoring">The timeline is scored the way osu!stable does (ScoreV1, one judgement per hold note).</param>
public sealed record TimelineCorrection(TimelineCorrectionStatus Status, long ScoreDifference, int HitDifference, string Label = "", bool StableScoring = false);

public sealed record ReplayTimelineFrame(
    double Time,
    int Index,
    long Score,
    int Combo,
    int MaxCombo,
    double Accuracy,
    Dictionary<string, int> Hits,
    ReplayFrameDebugInfo? DebugInfo = null);

public sealed record ReplayFrameDebugInfo(
    int Column,
    string Kind,
    double ObjectTime,
    double Offset,
    string Result);
