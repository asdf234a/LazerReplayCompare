using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <summary>Moving every note press by <paramref name="Shift"/> ms would turn <paramref name="MaxBefore"/> MAX into <paramref name="MaxAfter"/>.</summary>
public sealed record OffsetHint(int Shift, int MaxBefore, int MaxAfter);

/// <summary><paramref name="Count"/> of the replay's misses fall between <paramref name="From"/> and <paramref name="To"/> (real-time ms).</summary>
public sealed record MissCluster(int Count, double From, double To);

/// <summary>One bar of a hit error histogram: errors in [From, To).</summary>
/// <param name="Base">Judgement counts of the samples judged like a note at their error, best first.</param>
/// <param name="Stacked">Hold-note samples judged otherwise (drawn on top), best first.</param>
public sealed record HistogramBin(double From, double To, IReadOnlyList<(HitResult Result, int Count)> Base, IReadOnlyList<(HitResult Result, int Count)> Stacked)
{
    public int Total => Base.Sum(b => b.Count) + Stacked.Sum(s => s.Count);
}

/// <param name="Presses">Note and hold-head presses that hit (what the timing figures are taken over).</param>
/// <param name="UnstableRate">10 × the standard deviation of the press errors.</param>
/// <param name="OnTime">Presses within half a millisecond of the note.</param>
/// <param name="EarlyReleaseShare">Share of releases before the hold note's end.</param>
public sealed record ReplayAnalysisStats(
    int Presses,
    double Mean,
    double Median,
    double UnstableRate,
    int Early,
    int Late,
    int OnTime,
    double? HeadMean,
    double? ReleaseMean,
    double? EarlyReleaseShare,
    int Releases,
    OffsetHint? Offset,
    MissCluster? Misses,
    int MissCount)
{
    private const double on_time = 0.5;

    public static ReplayAnalysisStats From(ReplayAnalysis analysis)
    {
        // hits only, and inside the note windows (a hold note's head can be re-pressed far from it)
        bool pressed(HitSample s) => s.Result != HitResult.Miss && Math.Abs(s.Error) <= analysis.MissWindow;
        var presses = analysis.Samples.Where(s => s.Kind != HitKind.Release && pressed(s)).Select(s => s.Error).ToList();
        var heads = analysis.Samples.Where(s => s.Kind == HitKind.Head && pressed(s)).Select(s => s.Error).ToList();
        var releases = analysis.Samples.Where(s => s.Kind == HitKind.Release).Select(s => s.Error).ToList();

        double mean = presses.Count == 0 ? 0 : presses.Average();
        double variance = presses.Count == 0 ? 0 : presses.Sum(e => (e - mean) * (e - mean)) / presses.Count;

        return new ReplayAnalysisStats(
            presses.Count,
            mean,
            median(presses),
            Math.Sqrt(variance) * 10,
            presses.Count(e => e <= -on_time),
            presses.Count(e => e >= on_time),
            presses.Count(e => Math.Abs(e) < on_time),
            heads.Count == 0 ? null : heads.Average(),
            releases.Count == 0 ? null : releases.Average(),
            releases.Count == 0 ? null : releases.Count(e => e < 0) / (double)releases.Count,
            releases.Count,
            offsetHint(analysis),
            missCluster(analysis.MissTimes),
            analysis.MissTimes.Count);
    }

    /// <summary>
    /// The shift (5 ms steps, up to ±40 ms) that would give the most MAX if every note press moved by it, when that is
    /// more than today. Notes only: hold notes are judged with other rules.
    /// </summary>
    private static OffsetHint? offsetHint(ReplayAnalysis analysis)
    {
        var notes = analysis.Samples.Where(s => s.Kind == HitKind.Note).Select(s => s.Error).ToList();
        if (notes.Count == 0)
            return null;

        int maxAt(int shift) => notes.Count(e => analysis.NoteResultAt(e + shift) == HitResult.Perfect);
        int now = maxAt(0);
        var best = Enumerable.Range(-8, 17).Select(step => step * 5).Where(shift => shift != 0)
                             .Select(shift => (Shift: shift, Max: maxAt(shift)))
                             .OrderByDescending(c => c.Max).ThenBy(c => Math.Abs(c.Shift)).First();

        // only worth showing when it is clearly better
        return best.Max - now >= Math.Max(5, notes.Count / 100) ? new OffsetHint(best.Shift, now, best.Max) : null;
    }

    /// <summary>The 10 seconds with the most misses, when they hold at least 3 and half of them.</summary>
    private static MissCluster? missCluster(IReadOnlyList<double> times)
    {
        const double span = 10_000;
        MissCluster? best = null;
        for (int start = 0, end = 0; start < times.Count; start++)
        {
            while (end < times.Count && times[end] - times[start] <= span) end++;
            int count = end - start;
            if (best == null || count > best.Count)
                best = new MissCluster(count, times[start], times[end - 1]);
        }

        return best != null && best.Count >= 3 && best.Count * 2 >= times.Count ? best : null;
    }

    private static double median(List<double> values)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

public static class HitHistogram
{
    /// <summary>
    /// Presses and releases in bins of <paramref name="width"/> ms centred on 0, across ±<paramref name="range"/> ms.
    /// Hold-note samples that a note at the same error would not share a judgement with are kept apart (Stacked).
    /// </summary>
    /// <param name="kinds">Which samples to count.</param>
    /// <param name="outside">Samples beyond the range.</param>
    public static List<HistogramBin> Build(ReplayAnalysis analysis, double width, double range, IReadOnlyCollection<HitKind> kinds, out int outside)
    {
        int half = (int)Math.Ceiling((range - width / 2) / width);
        var bins = Enumerable.Range(-half, 2 * half + 1).Select(_ => (Base: new int[6], Stacked: new int[6])).ToArray();
        outside = 0;

        foreach (var sample in analysis.Samples)
        {
            if (!kinds.Contains(sample.Kind))
                continue;

            int index = (int)Math.Floor(sample.Error / width + 0.5) + half;
            if (index < 0 || index >= bins.Length)
            {
                outside++;
                continue;
            }

            int result = Array.IndexOf(ReplayAnalysis.Judgements, sample.Result);
            if (result < 0)
                continue;

            (analysis.JudgedUnlikeNote(sample) ? bins[index].Stacked : bins[index].Base)[result]++;
        }

        static List<(HitResult, int)> parts(int[] counts)
            => counts.Select((count, i) => (ReplayAnalysis.Judgements[i], count)).Where(p => p.count > 0).ToList();

        return bins.Select((bin, i) =>
        {
            double centre = (i - half) * width;
            return new HistogramBin(centre - width / 2, centre + width / 2, parts(bin.Base), parts(bin.Stacked));
        }).ToList();
    }

    /// <summary>Release errors in bins of <paramref name="width"/> ms from 0 outwards, each split by the hold note's judgement.</summary>
    public static List<HistogramBin> Releases(ReplayAnalysis analysis, double width, double range, out int outside)
    {
        int half = (int)Math.Ceiling(range / width);
        var bins = Enumerable.Range(0, 2 * half).Select(_ => new int[6]).ToArray();
        outside = 0;

        foreach (var sample in analysis.Samples.Where(s => s.Kind == HitKind.Release))
        {
            int index = (int)Math.Floor(sample.Error / width) + half;
            int result = Array.IndexOf(ReplayAnalysis.Judgements, sample.Result);
            if (index < 0 || index >= bins.Length || result < 0)
            {
                outside++;
                continue;
            }

            bins[index][result]++;
        }

        return bins.Select((counts, i) => new HistogramBin((i - half) * width, (i - half + 1) * width,
            counts.Select((count, r) => (ReplayAnalysis.Judgements[r], count)).Where(p => p.count > 0).ToList(),
            Array.Empty<(HitResult, int)>())).ToList();
    }
}
