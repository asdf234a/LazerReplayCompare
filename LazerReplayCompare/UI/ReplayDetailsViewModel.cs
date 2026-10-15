using System.Collections.ObjectModel;
using System.Globalization;
using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

public sealed class ColumnChip : ObservableObject
{
    private bool isSelected;

    public ColumnChip(int column, string label)
    {
        Column = column;
        Label = label;
    }

    /// <summary>-1: all columns.</summary>
    public int Column { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }
}

/// <param name="Detail">Count and MAX share, e.g. "909 · MAX 48.4%".</param>
public sealed record ShareRow(string Label, string Detail, IReadOnlyList<BarSegment> Segments);

/// <summary>A replay's details page: its recorded result, and the analysis of every press once it is worked out.</summary>
public sealed class ReplayDetailsViewModel : ObservableObject
{
    /// <summary>Up to this many keys every column gets a highlight button; above it only "All".</summary>
    private const int max_column_buttons = 10;

    private static readonly CultureInfo invariant = CultureInfo.InvariantCulture;

    private ReplayEntry? target;
    private bool isPinned;
    private bool isOnCurrentMap = true;
    private bool isLoading;
    private string errorText = string.Empty;
    private ReplayAnalysis? analysis;
    private int highlightColumn = -1;

    /// <param name="scoreV1">The score is osu!stable's own ScoreV1 (listed from osu!stable's scores.db).</param>
    public ReplayDetailsViewModel(ReplayEntry entry, string beatmapTitle, string beatmapSubtitle, ReplayEntry? target, bool isPinned, bool scoreV1)
    {
        Entry = entry;
        ScoreLabel = scoreV1 ? "Score (ScoreV1)" : "Score";
        Title = beatmapTitle;
        Subtitle = beatmapSubtitle;
        this.target = target;
        this.isPinned = isPinned;
        Judgements = MetricDefinition.JudgementNames.Select(key => new JudgementSegment(key, entry.Statistics.GetValueOrDefault(key))).ToList();
    }

    public ReplayEntry Entry { get; }

    // --- Recorded result ---

    public string Title { get; }
    public string Subtitle { get; }
    public string ClientText => Entry.IsLegacy ? "osu!stable" : "osu!lazer";
    public string ModsText => Entry.Mods.Count == 0 ? "NM" : Entry.ModsText;
    public string PlayerDateText => $"{Entry.Player} · {formatDate(Entry.Date)}";
    public string ScoreLabel { get; }
    public string ScoreText => Entry.Score.ToString("N0", invariant);
    public string AccuracyText => (Entry.Accuracy * 100).ToString("0.00", invariant) + "%";
    public string MaxComboText => Entry.MaxCombo.ToString("N0", invariant);

    /// <summary>MAX ÷ 300 to two decimals; "Perfect" when there is no 300.</summary>
    public string MaRatioText
    {
        get
        {
            int great = Entry.Statistics.GetValueOrDefault("Great");
            return great == 0 ? "Perfect" : (Entry.Statistics.GetValueOrDefault("Perfect") / (double)great).ToString("0.00", invariant);
        }
    }

    public string MaRatioSuffix => Entry.Statistics.GetValueOrDefault("Great") == 0 ? string.Empty : " : 1";

    public IReadOnlyList<JudgementSegment> Judgements { get; }
    public string JudgementTotalText => Judgements.Sum(j => j.Count).ToString("N0", invariant);

    public bool HasReplay => Entry.HasReplay;
    public bool ShowNoReplay => !HasReplay;

    public ReplayEntry? Target
    {
        get => target;
        set
        {
            if (!Set(ref target, value))
                return;

            OnPropertyChanged(nameof(HasVsTarget));
            OnPropertyChanged(nameof(VsTargetText));
        }
    }

    public bool IsPinned
    {
        get => isPinned;
        set
        {
            if (Set(ref isPinned, value))
                OnPropertyChanged(nameof(PinText));
        }
    }

    public string PinText => IsPinned ? "Unpin" : "Pin as target";

    /// <summary>Whether the page belongs to the map being played: the comparison target can only be set on its own map.</summary>
    public bool IsOnCurrentMap
    {
        get => isOnCurrentMap;
        set
        {
            if (Set(ref isOnCurrentMap, value))
            {
                OnPropertyChanged(nameof(CanPin));
                OnPropertyChanged(nameof(PinTooltip));
            }
        }
    }

    public bool CanPin => HasReplay && IsOnCurrentMap;

    public string? PinTooltip => HasReplay && !IsOnCurrentMap ? "Select this map in the game to pin its replays" : null;

    public bool HasVsTarget => Target != null && !string.Equals(Target.FilePath, Entry.FilePath, StringComparison.OrdinalIgnoreCase);

    public string VsTargetText
    {
        get
        {
            if (!HasVsTarget)
                return string.Empty;

            long score = Entry.Score - Target!.Score;
            double accuracy = (Entry.Accuracy - Target.Accuracy) * 100;
            int misses = Entry.Statistics.GetValueOrDefault("Miss") - Target.Statistics.GetValueOrDefault("Miss");
            return $"{score.ToString("+#,0;−#,0;±0", invariant)} pts · {accuracy.ToString("+0.00;−0.00;±0.00", invariant)}% acc · {misses.ToString("+0;−0;±0", invariant)} misses";
        }
    }

    // --- Analysis state ---

    public bool IsLoading
    {
        get => isLoading;
        set
        {
            if (Set(ref isLoading, value))
                OnPropertyChanged(nameof(ShowError));
        }
    }

    public string ErrorText
    {
        get => errorText;
        set
        {
            if (Set(ref errorText, value))
                OnPropertyChanged(nameof(ShowError));
        }
    }

    public bool ShowError => !IsLoading && ErrorText.Length > 0;

    public ReplayAnalysis? Analysis
    {
        get => analysis;
        private set
        {
            if (!Set(ref analysis, value))
                return;

            OnPropertyChanged(nameof(HasAnalysis));
            OnPropertyChanged(nameof(HasHolds));
        }
    }

    public bool HasAnalysis => Analysis != null;
    public bool HasHolds => Analysis is { Holds: > 0 };

    public string KeysText { get; private set; } = string.Empty;
    public string OdText { get; private set; } = string.Empty;
    public string RateText { get; private set; } = string.Empty;
    public string StatusText { get; private set; } = string.Empty;
    public bool StatusMatches { get; private set; }
    public bool StatusDiffers => HasAnalysis && !StatusMatches;
    public string FooterText { get; private set; } = string.Empty;

    // hit error distribution
    public IReadOnlyList<HistogramBin> HistogramBins { get; private set; } = Array.Empty<HistogramBin>();
    public IReadOnlyList<double> HistogramTicks { get; private set; } = Array.Empty<double>();
    public string HistogramSubtitle { get; private set; } = string.Empty;
    public string OffChartText { get; private set; } = string.Empty;
    public bool HasOffChart => OffChartText.Length > 0;
    public double PressMean { get; private set; }
    public string PressMeanLabel { get; private set; } = string.Empty;

    public IReadOnlyList<BarSegment> EarlyLateSegments { get; private set; } = Array.Empty<BarSegment>();
    public string EarlyCountText { get; private set; } = string.Empty;
    public string EarlyShareText { get; private set; } = string.Empty;
    public string LateCountText { get; private set; } = string.Empty;
    public string LateShareText { get; private set; } = string.Empty;
    public string MeanText { get; private set; } = string.Empty;
    public string MedianText { get; private set; } = string.Empty;
    public string UnstableRateText { get; private set; } = string.Empty;
    public string OnTimeText { get; private set; } = string.Empty;
    public bool HasOffsetHint { get; private set; }
    public string OffsetHintShift { get; private set; } = string.Empty;
    public string OffsetHintGain { get; private set; } = string.Empty;
    public string OffsetHintCounts { get; private set; } = string.Empty;

    // over the song
    public IReadOnlyList<ScatterPoint> PressPoints { get; private set; } = Array.Empty<ScatterPoint>();
    public IReadOnlyList<ScatterMark> UnpressedMarks { get; private set; } = Array.Empty<ScatterMark>();
    public IReadOnlyList<WindowEdge> WindowEdges { get; private set; } = Array.Empty<WindowEdge>();
    public double PressRange { get; private set; } = 180;
    public double SongLength { get; private set; } = 1;
    public ObservableCollection<ColumnChip> ColumnChips { get; } = new();
    public string MissText { get; private set; } = string.Empty;

    public int HighlightColumn
    {
        get => highlightColumn;
        set
        {
            if (!Set(ref highlightColumn, value))
                return;

            foreach (var chip in ColumnChips)
                chip.IsSelected = chip.Column == value;
        }
    }

    // long notes
    public string LongNotesSubtitle { get; private set; } = string.Empty;
    public string HeadMeanText { get; private set; } = string.Empty;
    public string ReleaseMeanText { get; private set; } = string.Empty;
    public string EarlyReleaseText { get; private set; } = string.Empty;
    public IReadOnlyList<HistogramBin> ReleaseBins { get; private set; } = Array.Empty<HistogramBin>();
    public IReadOnlyList<double> ReleaseTicks { get; private set; } = Array.Empty<double>();
    public string ReleaseSubtitle { get; private set; } = string.Empty;
    public IReadOnlyList<ShareRow> ShareRows { get; private set; } = Array.Empty<ShareRow>();
    public IReadOnlyList<ScatterPoint> ReleasePoints { get; private set; } = Array.Empty<ScatterPoint>();
    public IReadOnlyList<ScatterMark> HeldMarks { get; private set; } = Array.Empty<ScatterMark>();
    public double ReleaseRange { get; private set; } = 130;
    public string ReleaseDotsSubtitle { get; private set; } = string.Empty;
    public string HeldText { get; private set; } = string.Empty;

    public void SetAnalysis(ReplayAnalysis result)
    {
        var stats = ReplayAnalysisStats.From(result);
        string engine = result.StableJudging ? "osu!stable" : "osu!lazer";

        KeysText = $"{result.Columns}K";
        OdText = $"OD {result.OverallDifficulty.ToString("0.#", invariant)}";
        RateText = result.Rate.ToString("0.##", invariant) + "×";
        StatusMatches = result.HitDifference == 0;
        StatusText = StatusMatches
            ? $"Judgements match the replay · {engine} judging"
            : $"{result.HitDifference} judgements differ from the replay · {engine} judging";
        FooterText = $"Hit error = press time − note time · + is late · judged by the {engine} engine";

        // hit error distribution: every press and release, 3 ms bins across the note windows
        var all = new[] { HitKind.Note, HitKind.Head, HitKind.Release };
        HistogramBins = HitHistogram.Build(result, 3, result.MissWindow, all, out int outside);
        HistogramTicks = ticks(result.MissWindow, 50);
        int notesPressed = result.Samples.Count(s => s.Kind == HitKind.Note);
        int holdsPressed = result.Samples.Count(s => s.Kind == HitKind.Head);
        HistogramSubtitle = FormattableString.Invariant($"{notesPressed:N0} notes · {holdsPressed:N0} long notes (head + release) · 3 ms bins · {OdText}")
                            + (result.Rate != 1 ? $" · {RateText}" : string.Empty);
        OffChartText = outside == 0 ? string.Empty : outside == 1 ? "1 point off the chart" : $"{outside} points off the chart";
        PressMean = stats.Mean;
        PressMeanLabel = $"press mean {signed(stats.Mean, "0.0")}";

        double presses = Math.Max(1, stats.Presses);
        EarlyLateSegments = new[]
        {
            new BarSegment(stats.Early, "EarlyBrush", "Early", FormattableString.Invariant($"{stats.Early:N0} ({pct(stats.Early / presses, 0)})")),
            new BarSegment(stats.OnTime, "ChartZeroBrush", "On time", FormattableString.Invariant($"{stats.OnTime:N0} ({pct(stats.OnTime / presses, 0)})")),
            new BarSegment(stats.Late, "LateBrush", "Late", FormattableString.Invariant($"{stats.Late:N0} ({pct(stats.Late / presses, 0)})")),
        };
        EarlyCountText = stats.Early.ToString("N0", invariant);
        EarlyShareText = pct(stats.Early / presses, 0);
        LateCountText = stats.Late.ToString("N0", invariant);
        LateShareText = pct(stats.Late / presses, 0);
        MeanText = JudgementText.Ms(stats.Mean, "0.0");
        MedianText = JudgementText.Ms(stats.Median, "0.0");
        UnstableRateText = stats.UnstableRate.ToString("0", invariant);
        OnTimeText = stats.OnTime.ToString("N0", invariant);
        HasOffsetHint = stats.Offset != null;
        if (stats.Offset is { } hint)
        {
            OffsetHintShift = JudgementText.Ms(hint.Shift);
            OffsetHintGain = $"MAX {(hint.MaxAfter - hint.MaxBefore).ToString("+#,0;−#,0", invariant)}";
            OffsetHintCounts = FormattableString.Invariant($"({hint.MaxBefore:N0} → {hint.MaxAfter:N0})");
        }

        // over the song: presses only (releases have their own chart under long notes)
        PressPoints = result.Samples.Where(s => s.Kind != HitKind.Release).Select(point).ToList();
        UnpressedMarks = result.Unpressed.Select(e => new ScatterMark(e.Time, e.Column, e.Result, "Not pressed")).ToList();
        WindowEdges = result.NoteWindows
            .SelectMany(w => new[] { new WindowEdge(w.Early, w.Result), new WindowEdge(w.Late, w.Result) })
            .GroupBy(e => e.Value).Select(g => g.First()).ToList();
        PressRange = Math.Ceiling(result.MissWindow / 10) * 10 + 10;
        SongLength = Math.Max(1, result.Length);

        ColumnChips.Clear();
        ColumnChips.Add(new ColumnChip(-1, "All") { IsSelected = true });
        if (result.Columns <= max_column_buttons)
        {
            for (int column = 0; column < result.Columns; column++)
                ColumnChips.Add(new ColumnChip(column, (column + 1).ToString(invariant)));
        }

        highlightColumn = -1;
        MissText = stats.MissCount == 0 ? "No misses"
            : stats.Misses is { } cluster
                ? $"{(cluster.Count < stats.MissCount ? $"{cluster.Count} of {stats.MissCount}" : stats.MissCount.ToString(invariant))} misses at {JudgementText.Clock(cluster.From)}–{JudgementText.Clock(cluster.To)}"
                : stats.MissCount == 1 ? "1 miss" : $"{stats.MissCount} misses";

        // long notes
        LongNotesSubtitle = result.StableJudging
            ? FormattableString.Invariant($"{result.Holds:N0} long notes · one judgement each, from head + release")
            : FormattableString.Invariant($"{result.Holds:N0} long notes · head and release judged apart, release windows {result.ReleaseLenience:0.#}× wider");
        HeadMeanText = stats.HeadMean is double head ? JudgementText.Ms(head, "0.0") : "—";
        ReleaseMeanText = stats.ReleaseMean is double release ? JudgementText.Ms(release, "0.0") : "—";
        EarlyReleaseText = stats.EarlyReleaseShare is double early ? pct(early, 0) : "—";

        var releaseErrors = result.Samples.Where(s => s.Kind == HitKind.Release).Select(s => Math.Abs(s.Error)).DefaultIfEmpty(0);
        ReleaseRange = Math.Clamp(Math.Ceiling(releaseErrors.Max() / 10) * 10, 60, 250);
        ReleaseBins = HitHistogram.Releases(result, 10, ReleaseRange, out _);
        ReleaseTicks = new[] { -ReleaseRange, 0, ReleaseRange };
        ReleaseSubtitle = FormattableString.Invariant($"Release error · 10 ms bins · {stats.Releases:N0} releases");
        ReleasePoints = result.Samples.Where(s => s.Kind == HitKind.Release).Select(point).ToList();
        HeldMarks = result.HeldPastEnd.Select(e => new ScatterMark(e.Time, e.Column, e.Result, "Held past the end")).ToList();
        string colouredBy = result.StableJudging ? "the long note's" : "their";
        ReleaseDotsSubtitle = FormattableString.Invariant($"Releases over the song · {stats.Releases:N0} releases, coloured by {colouredBy} judgement");
        HeldText = HeldMarks.Count == 0 ? string.Empty : $"· top edge = held past the end ({HeldMarks.Count})";

        ShareRows = new[]
        {
            shareRow("Notes", result.Notes, result.NoteCounts),
            shareRow("Long notes", result.Holds, result.HoldCounts),
        };

        Analysis = result;
        OnPropertyChanged(string.Empty);   // everything above changed at once
    }

    private static ScatterPoint point(HitSample sample)
        => new(sample.Time, sample.Error, sample.Column, sample.Result, sample.Kind switch
        {
            HitKind.Head => "Hold head",
            HitKind.Release => "Release",
            _ => "Note",
        });

    private static ShareRow shareRow(string label, int objects, IReadOnlyDictionary<HitResult, int> counts)
    {
        double total = Math.Max(1, counts.Values.Sum());
        var segments = ReplayAnalysis.Judgements
            .Select(r => new BarSegment(counts.GetValueOrDefault(r), JudgementText.BrushKey(r), JudgementText.Name(r),
                FormattableString.Invariant($"{counts.GetValueOrDefault(r):N0} ({pct(counts.GetValueOrDefault(r) / total, 1)})")))
            .ToList();
        return new ShareRow(label, FormattableString.Invariant($"{objects:N0} · MAX {pct(counts.GetValueOrDefault(HitResult.Perfect) / total, 1)}"), segments);
    }

    /// <summary>Multiples of <paramref name="step"/> strictly inside ±<paramref name="range"/>.</summary>
    private static IReadOnlyList<double> ticks(double range, double step)
    {
        var values = new List<double>();
        for (double v = -Math.Floor(range / step) * step; v <= range; v += step)
        {
            if (Math.Abs(v) < range)
                values.Add(v);
        }

        return values;
    }

    /// <summary>A share as "19%" (no culture-dependent space before the sign).</summary>
    private static string pct(double share, int decimals) => (share * 100).ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), invariant) + "%";

    private static string signed(double value, string format) => (value > 0 ? "+" : value < 0 ? "−" : "±") + Math.Abs(value).ToString(format, invariant);

    private static string formatDate(string value)
        => DateTimeOffset.TryParse(value, out var date) ? date.LocalDateTime.ToString("yyyy-MM-dd HH:mm", invariant) : value;
}
