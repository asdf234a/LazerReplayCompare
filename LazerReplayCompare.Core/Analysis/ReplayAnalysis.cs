using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

public enum HitKind
{
    Note,
    Head,
    Release,
}

/// <param name="Time">The object's time (the hold note's end for a release), in real-time ms.</param>
/// <param name="Error">How far off the press or release was, in real-time ms (positive = late).</param>
/// <param name="Result">The judgement it led to; for an osu!stable hold note, the one judgement of the whole hold note.</param>
public sealed record HitSample(double Time, int Column, HitKind Kind, double Error, HitResult Result);

/// <summary>An object judged without a press or release time (a note never pressed, a hold note held past its end).</summary>
public sealed record ObjectEvent(double Time, int Column, HitResult Result);

/// <summary>Errors (real-time ms) a note press may have to get <paramref name="Result"/>.</summary>
public sealed record JudgementWindow(HitResult Result, double Early, double Late);

/// <summary>
/// Every judged press and release of a replay, as the engine of the client that recorded it judges them: osu!stable's
/// (one judgement per hold note, from its head and release together) for osu!stable replays, osu!lazer's (head and
/// release judged separately, the release with wider windows) for osu!lazer replays. Corrected onto the replay's
/// recorded judgement counts where the engines allow it.
/// </summary>
/// <param name="StableJudging">Judged by the osu!stable engine.</param>
/// <param name="Rate">Playback rate; every time here is real time.</param>
/// <param name="Length">End of the last object.</param>
/// <param name="ReleaseLenience">How much wider than the note windows osu!lazer's release windows are (0 for osu!stable).</param>
/// <param name="MissTimes">When each miss happened.</param>
/// <param name="HoldCounts">Hold note judgements: one per hold note for osu!stable, head and release for osu!lazer.</param>
/// <param name="HitDifference">How many judgements differ from the replay's recorded counts.</param>
/// <param name="HeadCounts">osu!lazer: the hold notes' head judgements (judged like notes). Empty for osu!stable.</param>
/// <param name="ReleaseCounts">osu!lazer: the hold notes' release judgements. Empty for osu!stable.</param>
public sealed record ReplayAnalysis(
    bool StableJudging,
    int Columns,
    double OverallDifficulty,
    double Rate,
    double Length,
    IReadOnlyList<JudgementWindow> NoteWindows,
    double ReleaseLenience,
    IReadOnlyList<HitSample> Samples,
    IReadOnlyList<ObjectEvent> Unpressed,
    IReadOnlyList<ObjectEvent> HeldPastEnd,
    IReadOnlyList<double> MissTimes,
    IReadOnlyDictionary<HitResult, int> NoteCounts,
    IReadOnlyDictionary<HitResult, int> HoldCounts,
    int Notes,
    int Holds,
    int HitDifference,
    IReadOnlyDictionary<HitResult, int> HeadCounts,
    IReadOnlyDictionary<HitResult, int> ReleaseCounts)
{
    public static readonly HitResult[] Judgements = [HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss];

    /// <summary>What a note pressed <paramref name="error"/> ms off gets.</summary>
    public HitResult NoteResultAt(double error)
    {
        foreach (var window in NoteWindows)
        {
            if (window.Result != HitResult.Miss && error >= window.Early && error <= window.Late)
                return window.Result;
        }

        return HitResult.Miss;
    }

    /// <summary>
    /// Whether a hold-note sample got another judgement than a note pressed at the same error would (osu!lazer's wider
    /// release windows, osu!stable's head + release rule). Such samples are drawn on top of the notes in charts.
    /// </summary>
    public bool JudgedUnlikeNote(HitSample sample) => sample.Kind != HitKind.Note && sample.Result != NoteResultAt(sample.Error);

    /// <summary>The widest note window (the miss window), for symmetric chart ranges.</summary>
    public double MissWindow => NoteWindows.Count == 0 ? 0 : NoteWindows.Max(w => Math.Max(-w.Early, w.Late));
}

public static class ReplayAnalyzer
{
    private static readonly HashSet<HitResult> counted = ReplayAnalysis.Judgements.ToHashSet();

    public static ReplayAnalysis Analyze(string replayPath, string beatmapPath)
    {
        if (!File.Exists(replayPath))
            throw new FileNotFoundException("Replay file was not found.", replayPath);
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("The beatmap file isn't available.");

        Score score;
        using (var stream = File.OpenRead(replayPath))
            score = new LazerReplayScoreDecoder(beatmapPath).Parse(stream);

        var info = score.ScoreInfo;
        if (info.Ruleset.OnlineID != 3)
            throw new NotSupportedException("Only osu!mania replays can be analysed.");

        double rate = info.Mods.OfType<ModRateAdjust>().FirstOrDefault()?.SpeedChange.Value ?? 1;
        return StableTimelineCalculator.Supports(info) ? stable(score, beatmapPath, rate) : lazer(score, beatmapPath, rate);
    }

    private static ReplayAnalysis stable(Score score, string beatmapPath, double rate)
    {
        var result = StableTimelineCalculator.Build(score, beatmapPath, CorrectionMode.Corrected);
        var judgements = result.Judgements ?? [];
        var windows = result.Simulation?.NoteWindows.Select(w => new JudgementWindow(w.Result, w.Early / rate, w.Late / rate)).ToList() ?? new List<JudgementWindow>();

        var collector = new Collector();
        foreach (var j in judgements)
        {
            var hitObject = j.Object;
            double time = hitObject.StartTime / rate;
            if (j.Result == HitResult.Miss)
                collector.MissTimes.Add(j.Time / rate);

            if (hitObject is HoldNote hold)
            {
                collector.HoldCounts[j.Result] = collector.HoldCounts.GetValueOrDefault(j.Result) + 1;
                if (j.Hold is not { } timing)
                {
                    collector.Unpressed.Add(new ObjectEvent(time, hitObject.Column, j.Result));
                    continue;
                }

                if (timing.MissedHead)
                    collector.Unpressed.Add(new ObjectEvent(time, hitObject.Column, j.Result));
                else
                    collector.Samples.Add(new HitSample(time, hitObject.Column, HitKind.Head, timing.HeadDelta / rate, j.Result));

                double end = hold.EndTime / rate;
                if (timing.Overhold)
                    collector.HeldPastEnd.Add(new ObjectEvent(end, hitObject.Column, j.Result));
                else if (!timing.Missed)
                    collector.Samples.Add(new HitSample(end, hitObject.Column, HitKind.Release, timing.ReleaseDelta / rate, j.Result));
            }
            else
            {
                collector.NoteCounts[j.Result] = collector.NoteCounts.GetValueOrDefault(j.Result) + 1;
                if (j.NoteDelta is double delta)
                    collector.Samples.Add(new HitSample(time, hitObject.Column, HitKind.Note, delta / rate, j.Result));
                else
                    collector.Unpressed.Add(new ObjectEvent(time, hitObject.Column, j.Result));
            }
        }

        var objects = judgements.Select(j => j.Object).Distinct().ToList();
        return collector.Build(true, result.Simulation?.Columns ?? 0, result.Simulation?.OverallDifficulty ?? 0, rate, windows, 0,
            objects.Count(o => o is not HoldNote), objects.Count(o => o is HoldNote), objects.Select(o => o.GetEndTime()).DefaultIfEmpty(0).Max() / rate,
            result.Correction.HitDifference);
    }

    private static ReplayAnalysis lazer(Score score, string beatmapPath, double rate)
    {
        var result = new ManiaTimelineCalculator().Build(score, beatmapPath, CorrectionMode.Corrected);
        var playable = result.Playable;
        var reference = playable.HitObjects.OfType<Note>().Cast<HitObject>().Concat(playable.HitObjects.OfType<HoldNote>().Select(h => (HitObject)h.Head)).FirstOrDefault();
        var windows = reference == null
            ? new List<JudgementWindow>()
            : ReplayAnalysis.Judgements.Select(r =>
            {
                double w = reference.HitWindows.WindowFor(r) / rate;
                return new JudgementWindow(r, -w, w);
            }).ToList();

        var collector = new Collector();
        foreach (var j in result.Judgements)
        {
            HitKind kind;
            switch (j.HitObject)
            {
                case HeadNote:
                    kind = HitKind.Head;
                    break;

                case TailNote:
                    kind = HitKind.Release;
                    break;

                case Note:
                    kind = HitKind.Note;
                    break;

                default:
                    continue;   // the hold note itself and its body: no timing of their own
            }

            if (!counted.Contains(j.Type))
                continue;

            var counts = kind == HitKind.Note ? collector.NoteCounts : collector.HoldCounts;
            counts[j.Type] = counts.GetValueOrDefault(j.Type) + 1;
            if (kind != HitKind.Note)
            {
                var part = kind == HitKind.Head ? collector.HeadCounts : collector.ReleaseCounts;
                part[j.Type] = part.GetValueOrDefault(j.Type) + 1;
            }

            if (j.Type == HitResult.Miss)
                collector.MissTimes.Add(j.Time / rate);

            int column = ((ManiaHitObject)j.HitObject).Column;
            double time = j.HitObject.StartTime / rate;
            if (j.Offset is double offset)
                collector.Samples.Add(new HitSample(time, column, kind, offset / rate, j.Type));
            else if (kind != HitKind.Release)
                collector.Unpressed.Add(new ObjectEvent(time, column, j.Type));
            else if (j.Type != HitResult.Miss)
                collector.HeldPastEnd.Add(new ObjectEvent(time, column, j.Type));
        }

        var info = score.ScoreInfo;
        int hitDifference = ReplayAnalysis.Judgements.Sum(r => Math.Abs(collector.NoteCounts.GetValueOrDefault(r) + collector.HoldCounts.GetValueOrDefault(r) - info.Statistics.GetValueOrDefault(r)));
        return collector.Build(false, ((osu.Game.Rulesets.Mania.Beatmaps.ManiaBeatmap)playable).TotalColumns, playable.Difficulty.OverallDifficulty, rate, windows, TailNote.RELEASE_WINDOW_LENIENCE,
            playable.HitObjects.Count(o => o is Note), playable.HitObjects.Count(o => o is HoldNote),
            playable.HitObjects.Select(o => o.GetEndTime()).DefaultIfEmpty(0).Max() / rate, hitDifference);
    }

    private sealed class Collector
    {
        public readonly List<HitSample> Samples = new();
        public readonly List<ObjectEvent> Unpressed = new();
        public readonly List<ObjectEvent> HeldPastEnd = new();
        public readonly List<double> MissTimes = new();
        public readonly Dictionary<HitResult, int> NoteCounts = new();
        public readonly Dictionary<HitResult, int> HoldCounts = new();
        public readonly Dictionary<HitResult, int> HeadCounts = new();
        public readonly Dictionary<HitResult, int> ReleaseCounts = new();

        public ReplayAnalysis Build(bool stable, int columns, double overallDifficulty, double rate, List<JudgementWindow> windows, double lenience, int notes, int holds, double length, int hitDifference)
        {
            return new ReplayAnalysis(stable, columns, overallDifficulty, rate, length, windows, lenience,
                Samples.OrderBy(s => s.Time).ToList(), Unpressed.OrderBy(e => e.Time).ToList(), HeldPastEnd.OrderBy(e => e.Time).ToList(),
                MissTimes.Order().ToList(), NoteCounts, HoldCounts, notes, holds, hitDifference, HeadCounts, ReleaseCounts);
        }
    }
}
