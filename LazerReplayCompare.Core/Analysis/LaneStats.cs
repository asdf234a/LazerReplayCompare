using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <summary>One lane (column) of a replay.</summary>
/// <param name="Lane">0-based, left to right.</param>
/// <param name="LeftHand">Played by the left hand (see <see cref="LaneStats.From"/>).</param>
/// <param name="Objects">Judgements in the lane (one per note; per hold note one for osu!stable, two for osu!lazer).</param>
/// <param name="Counts">How many of each judgement (<see cref="ReplayAnalysis.Judgements"/>; all six are present).</param>
/// <param name="Accuracy">In percent, by the formula of the game that recorded the replay; null for a lane with nothing in it.</param>
/// <param name="Presses">Note and hold-head presses that hit: what <paramref name="Mean"/> and <paramref name="UnstableRate"/> are taken over.</param>
/// <param name="Mean">Mean press error in real-time ms (positive = late); null without presses.</param>
/// <param name="UnstableRate">10 × the standard deviation of the press errors; null without presses.</param>
public sealed record LaneStat(
    int Lane,
    bool LeftHand,
    int Objects,
    IReadOnlyDictionary<HitResult, int> Counts,
    int Misses,
    double? Accuracy,
    int Presses,
    double? Mean,
    double? UnstableRate);

/// <summary>The lanes of one hand together.</summary>
/// <param name="Lanes">How many lanes the hand plays.</param>
/// <param name="Accuracy">In percent, over every judgement of the hand's lanes; null when it has none.</param>
public sealed record HandStat(int Lanes, int Objects, int Misses, double? Accuracy);

/// <summary>A replay's judgements and timing lane by lane, and by hand.</summary>
/// <param name="HasMiddleLane">An odd key count: the middle lane goes to the hand <paramref name="MiddleLaneLeft"/> says.</param>
public sealed record LaneStats(
    int KeyCount,
    bool HasMiddleLane,
    bool MiddleLaneLeft,
    IReadOnlyList<LaneStat> Lanes,
    HandStat Left,
    HandStat Right)
{
    /// <summary>
    /// Per-lane statistics of <paramref name="analysis"/> for a <paramref name="keyCount"/>-key map. Judgements come from
    /// <see cref="ReplayAnalysis.Judged"/>; the timing figures from the same presses as <see cref="ReplayAnalysisStats.Mean"/>
    /// (notes and hold-note heads that hit, inside the note windows). The lower half of the lanes is the left hand; with
    /// an odd key count the middle lane is the left hand's when <paramref name="middleLaneLeft"/>, else the right's.
    /// Anything in a column outside 0..keyCount-1 is left out.
    /// </summary>
    public static LaneStats From(ReplayAnalysis analysis, int keyCount, bool middleLaneLeft)
    {
        keyCount = Math.Max(0, keyCount);
        var counts = Enumerable.Range(0, keyCount).Select(_ => new int[ReplayAnalysis.Judgements.Length]).ToArray();
        foreach (var judged in analysis.Judged)
        {
            int result = Array.IndexOf(ReplayAnalysis.Judgements, judged.Result);
            if (judged.Column >= 0 && judged.Column < keyCount && result >= 0)
                counts[judged.Column][result]++;
        }

        var errors = Enumerable.Range(0, keyCount).Select(_ => new List<double>()).ToArray();
        double window = analysis.MissWindow;
        foreach (var sample in analysis.Samples)
        {
            // (as ReplayAnalysisStats: hits only, and inside the note windows)
            if (sample.Kind != HitKind.Release && sample.Result != HitResult.Miss && Math.Abs(sample.Error) <= window
                && sample.Column >= 0 && sample.Column < keyCount)
                errors[sample.Column].Add(sample.Error);
        }

        int leftLanes = LeftLanes(keyCount, middleLaneLeft);
        var lanes = new List<LaneStat>(keyCount);
        for (int lane = 0; lane < keyCount; lane++)
        {
            var presses = errors[lane];
            double? mean = presses.Count == 0 ? null : presses.Average();
            double? unstableRate = mean is double m ? Math.Sqrt(presses.Sum(e => (e - m) * (e - m)) / presses.Count) * 10 : null;
            lanes.Add(new LaneStat(lane, lane < leftLanes, counts[lane].Sum(), toDictionary(counts[lane]), counts[lane][^1],
                Accuracy(counts[lane], analysis.StableJudging), presses.Count, mean, unstableRate));
        }

        return new LaneStats(keyCount, keyCount % 2 == 1, middleLaneLeft, lanes,
            hand(counts.Take(leftLanes).ToList(), analysis.StableJudging), hand(counts.Skip(leftLanes).ToList(), analysis.StableJudging));
    }

    /// <summary>How many lanes, from the left, the left hand plays.</summary>
    public static int LeftLanes(int keyCount, bool middleLaneLeft) => keyCount / 2 + (keyCount % 2 == 1 && middleLaneLeft ? 1 : 0);

    /// <summary>
    /// Accuracy in percent of judgement counts in <see cref="ReplayAnalysis.Judgements"/> order (MAX, 300, 200, 100, 50,
    /// Miss), with the weights the app's "Stable acc" (V1Acc: MAX and 300 alike) or "Lazer acc" (MAX 305) metric uses.
    /// Null without judgements.
    /// </summary>
    public static double? Accuracy(IReadOnlyList<int> counts, bool stable)
    {
        if (counts.Sum() == 0)
            return null;

        var metric = MetricRegistry.Find(stable ? "V1Acc" : "LazerAcc") ?? throw new InvalidOperationException("The accuracy metric is missing.");
        return metric.Evaluate(0, 0, name => Enum.TryParse<HitResult>(name, out var result) && Array.IndexOf(ReplayAnalysis.Judgements, result) is >= 0 and var i ? counts[i] : 0);
    }

    private static HandStat hand(IReadOnlyList<int[]> lanes, bool stable)
    {
        var total = new int[ReplayAnalysis.Judgements.Length];
        foreach (var lane in lanes)
        {
            for (int i = 0; i < total.Length; i++)
                total[i] += lane[i];
        }

        return new HandStat(lanes.Count, total.Sum(), total[^1], Accuracy(total, stable));
    }

    private static IReadOnlyDictionary<HitResult, int> toDictionary(int[] counts)
        => ReplayAnalysis.Judgements.Select((result, i) => (result, count: counts[i])).ToDictionary(p => p.result, p => p.count);
}
