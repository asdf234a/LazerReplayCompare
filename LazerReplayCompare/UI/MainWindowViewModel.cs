using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LazerReplayCompare;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record MetricOption(string Key, string Name)
{
    public override string ToString() => Name;
}

/// <param name="Key">Judgement name; the window maps it to a theme brush (Judgement{Key}Brush).</param>
public sealed record JudgementSegment(string Key, int Count)
{
    public string CountText => Count.ToString("N0", CultureInfo.InvariantCulture);
}

public enum ReplaySortKey
{
    Score,
    Date,
    Accuracy,
    Miss,
    Combo,
}

/// <summary>State shown by <see cref="MainWindow"/> and <see cref="CompactWindow"/>.</summary>
public sealed class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<ReplayRow> ReplayRows { get; } = new();

    /// <summary>Raised when the user changes an overlay metric, the timeline mode or the theme.</summary>
    public event Action? SettingsChanged;

    // --- Pages ---

    private bool isSettingsPage;

    public bool IsSettingsPage
    {
        get => isSettingsPage;
        set
        {
            if (!Set(ref isSettingsPage, value))
                return;

            OnPropertyChanged(nameof(IsComparePage));
            OnPropertyChanged(nameof(ShowBannerOnCompare));
        }
    }

    public bool IsComparePage => !IsSettingsPage;

    // --- Connections ---

    private bool tosuConnected;
    private bool lazerFound;
    private bool apiRunning = true;
    private string apiError = string.Empty;

    public bool TosuConnected
    {
        get => tosuConnected;
        set { if (Set(ref tosuConnected, value)) connectionsChanged(); }
    }

    public bool LazerFound
    {
        get => lazerFound;
        set { if (Set(ref lazerFound, value)) connectionsChanged(); }
    }

    public bool ApiRunning
    {
        get => apiRunning;
        set { if (Set(ref apiRunning, value)) connectionsChanged(); }
    }

    public string ApiError
    {
        get => apiError;
        set => Set(ref apiError, value);
    }

    public bool AllConnected => TosuConnected && LazerFound && ApiRunning;
    public bool ShowTosuBanner => !TosuConnected;
    public bool ShowLazerBanner => !LazerFound;
    public bool ShowApiBanner => !ApiRunning;

    public string ConnectionText => AllConnected
        ? "All connected"
        : !TosuConnected ? "tosu not connected"
        : !LazerFound ? "osu!lazer folder missing"
        : "Overlay API stopped";

    public string ConnectionDetails =>
        $"tosu: {(TosuConnected ? "connected (:24050)" : "not connected")}\n" +
        $"osu!lazer: {(LazerFound ? "found" : "folder not found")}\n" +
        $"Overlay API: {(ApiRunning ? "running (:24052)" : "stopped")}";

    private void connectionsChanged()
    {
        OnPropertyChanged(nameof(AllConnected));
        OnPropertyChanged(nameof(ShowTosuBanner));
        OnPropertyChanged(nameof(ShowLazerBanner));
        OnPropertyChanged(nameof(ShowApiBanner));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(ConnectionDetails));
        OnPropertyChanged(nameof(ShowCompareContent));
        OnPropertyChanged(nameof(ShowDisconnectedPlaceholder));
        OnPropertyChanged(nameof(ShowNoReplays));
        OnPropertyChanged(nameof(ShowNoBeatmap));
    }

    // --- Beatmap and replays ---

    private string beatmapTitle = "No beatmap selected";
    private string beatmapSubtitle = "Select a beatmap in osu!lazer";
    private bool hasBeatmap;
    private bool isLoadingReplays;
    private string statusText = string.Empty;

    public string BeatmapTitle
    {
        get => beatmapTitle;
        set => Set(ref beatmapTitle, value);
    }

    public string BeatmapSubtitle
    {
        get => beatmapSubtitle;
        set => Set(ref beatmapSubtitle, value);
    }

    public bool HasBeatmap
    {
        get => hasBeatmap;
        set { if (Set(ref hasBeatmap, value)) replaysChanged(); }
    }

    public bool IsLoadingReplays
    {
        get => isLoadingReplays;
        set { if (Set(ref isLoadingReplays, value)) replaysChanged(); }
    }

    /// <summary>Last notable event (shown small under the beatmap).</summary>
    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }

    public bool HasReplays => ReplayRows.Count > 0;
    public string ReplayCountText => ReplayRows.Count == 1 ? "1 saved" : $"{ReplayRows.Count} saved";
    public bool ShowCompareContent => AllConnected && HasReplays;
    public bool ShowDisconnectedPlaceholder => !AllConnected;
    public bool ShowNoReplays => AllConnected && HasBeatmap && !IsLoadingReplays && !HasReplays;
    public bool ShowNoBeatmap => AllConnected && !HasBeatmap;

    public void NotifyReplaysChanged() => replaysChanged();

    private void replaysChanged()
    {
        OnPropertyChanged(nameof(HasReplays));
        OnPropertyChanged(nameof(ShowNoTarget));
        OnPropertyChanged(nameof(ReplayCountText));
        OnPropertyChanged(nameof(ShowCompareContent));
        OnPropertyChanged(nameof(ShowNoReplays));
        OnPropertyChanged(nameof(ShowNoBeatmap));
    }

    // --- Sorting ---

    private ReplaySortKey sortKey = ReplaySortKey.Score;
    private bool sortDescending = true;

    public ReplaySortKey SortKey
    {
        get => sortKey;
        set { if (Set(ref sortKey, value)) headersChanged(); }
    }

    public bool SortDescending
    {
        get => sortDescending;
        set { if (Set(ref sortDescending, value)) headersChanged(); }
    }

    public string ScoreHeader => header("Score", ReplaySortKey.Score);
    public string DateHeader => header("Date", ReplaySortKey.Date);
    public string AccuracyHeader => header("Acc", ReplaySortKey.Accuracy);
    public string MissHeader => header("Miss", ReplaySortKey.Miss);
    public string ComboHeader => header("Combo", ReplaySortKey.Combo);

    private string header(string name, ReplaySortKey key) => SortKey == key ? $"{name} {(SortDescending ? "↓" : "↑")}" : name;

    private void headersChanged()
    {
        OnPropertyChanged(nameof(ScoreHeader));
        OnPropertyChanged(nameof(DateHeader));
        OnPropertyChanged(nameof(AccuracyHeader));
        OnPropertyChanged(nameof(MissHeader));
        OnPropertyChanged(nameof(ComboHeader));
    }

    // --- Comparison target ---

    private ReplayEntry? target;
    private bool isTargetPinned;

    public ReplayEntry? Target
    {
        get => target;
        set
        {
            if (!Set(ref target, value))
                return;

            OnPropertyChanged(nameof(HasTarget));
            OnPropertyChanged(nameof(ShowNoTarget));
            OnPropertyChanged(nameof(TargetPlayer));
            OnPropertyChanged(nameof(TargetSummary));
            OnPropertyChanged(nameof(TargetScoreText));
            OnPropertyChanged(nameof(TargetAccuracyText));
            OnPropertyChanged(nameof(TargetDateText));
            OnPropertyChanged(nameof(MaxComboText));
            Judgements = value == null ? Array.Empty<JudgementSegment>() : buildJudgements(value);

            foreach (var row in ReplayRows)
                row.UpdateTarget(value);
        }
    }

    public bool IsTargetPinned
    {
        get => isTargetPinned;
        set
        {
            if (!Set(ref isTargetPinned, value))
                return;

            OnPropertyChanged(nameof(TargetModeText));
            OnPropertyChanged(nameof(ShowPinHint));
        }
    }

    public bool HasTarget => Target != null;
    public string TargetPlayer => Target?.Player ?? "-";
    public string TargetScoreText => Target?.Score.ToString("N0", CultureInfo.InvariantCulture) ?? "-";
    public string TargetAccuracyText => Target == null ? "-" : (Target.Accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    public string TargetDateText => Target == null ? "-" : ReplayRow.FormatDateOnly(Target.Date);
    public string TargetSummary => Target == null ? "-" : $"{TargetAccuracyText} · {Target.ModsText} · {TargetDateText}";
    private PlaySpeed autoSpeed = new(1, false);

    /// <summary>Rate/Classic the auto target must match (the current play's).</summary>
    public PlaySpeed AutoSpeed
    {
        get => autoSpeed;
        set
        {
            if (!Set(ref autoSpeed, value))
                return;

            OnPropertyChanged(nameof(TargetModeText));
            OnPropertyChanged(nameof(NoTargetText));
        }
    }

    public string TargetModeText => IsTargetPinned ? "Pinned" : $"Auto · best at {AutoSpeed.RateText}{(AutoSpeed.Classic ? " Classic" : "")}";

    /// <summary>Replays exist, but none at the current rate (and nothing is pinned).</summary>
    public bool ShowNoTarget => HasReplays && Target == null;

    public string NoTargetText => $"No saved replay at {AutoSpeed.RateText}{(AutoSpeed.Classic ? " with Classic" : "")}. Pin one below to compare against it anyway.";
    public bool ShowPinHint => !IsTargetPinned;
    public string MaxComboText => Target?.MaxCombo.ToString("N0", CultureInfo.InvariantCulture) ?? "-";

    // --- Judgement breakdown ---

    private IReadOnlyList<JudgementSegment> judgements = Array.Empty<JudgementSegment>();

    public IReadOnlyList<JudgementSegment> Judgements
    {
        get => judgements;
        private set
        {
            if (Set(ref judgements, value))
                OnPropertyChanged(nameof(JudgementTotalText));
        }
    }

    public string JudgementTotalText => Judgements.Sum(segment => segment.Count).ToString("N0", CultureInfo.InvariantCulture);

    private static IReadOnlyList<JudgementSegment> buildJudgements(ReplayEntry replay)
        => MetricDefinition.JudgementNames.Select(key => new JudgementSegment(key, replay.Statistics.GetValueOrDefault(key))).ToList();

    // --- Timeline correction ---

    private string correctionTitle = string.Empty;
    private string correctionDetail = string.Empty;
    private string correctionTone = "neutral";
    private bool isCorrectionLoading;

    public string CorrectionTitle
    {
        get => correctionTitle;
        private set => Set(ref correctionTitle, value);
    }

    public string CorrectionDetail
    {
        get => correctionDetail;
        private set => Set(ref correctionDetail, value);
    }

    /// <summary>good | warning | info | error | neutral — picks the status colours.</summary>
    public string CorrectionTone
    {
        get => correctionTone;
        private set
        {
            if (!Set(ref correctionTone, value))
                return;

            OnPropertyChanged(nameof(IsToneGood));
            OnPropertyChanged(nameof(IsToneWarning));
            OnPropertyChanged(nameof(IsToneInfo));
            OnPropertyChanged(nameof(IsToneError));
            OnPropertyChanged(nameof(CorrectionGlyph));
        }
    }

    public bool IsCorrectionLoading
    {
        get => isCorrectionLoading;
        private set => Set(ref isCorrectionLoading, value);
    }

    public bool IsToneGood => CorrectionTone == "good";
    public bool IsToneWarning => CorrectionTone == "warning";
    public bool IsToneInfo => CorrectionTone == "info";
    public bool IsToneError => CorrectionTone == "error";

    /// <summary>Path data for the status icon (check, dash, info, cross, dots).</summary>
    public Geometry CorrectionGlyph => Geometry.Parse(CorrectionTone switch
    {
        "good" => "M5,12.5 L9.5,17 L19,7.5",
        "warning" => "M5,12 L19,12",
        "info" => "M12,11 L12,17 M12,7 L12,7.5",
        "error" => "M7,7 L17,17 M17,7 L7,17",
        _ => "M6,12 L6.5,12 M12,12 L12.5,12 M18,12 L18.5,12",
    });

    public void ShowCorrectionLoading()
    {
        IsCorrectionLoading = true;
        CorrectionTone = "neutral";
        CorrectionTitle = "Checking the replay timeline…";
        CorrectionDetail = "Working out the replay's score at every note.";
    }

    public void ShowCorrection(TimelineCorrection? correction, string scaleMetric)
    {
        IsCorrectionLoading = false;
        var points = Math.Abs(correction?.ScoreDifference ?? 0).ToString("N0", CultureInfo.InvariantCulture);

        switch (correction?.Status)
        {
            case TimelineCorrectionStatus.Recorded:
                CorrectionTone = "good";
                CorrectionTitle = "Exact match";
                CorrectionDetail = "This replay stores its score at every frame, so the overlay uses it directly.";
                break;

            case TimelineCorrectionStatus.Exact:
                CorrectionTone = "good";
                CorrectionTitle = "Exact match";
                CorrectionDetail = "The corrected timeline reproduces the recorded score and every judgement count, so the overlay's difference is exact at every note.";
                break;

            case TimelineCorrectionStatus.Approximate:
                CorrectionTone = "warning";
                CorrectionTitle = $"Approximate · within {points} points";
                CorrectionDetail = correction.HitDifference == 0
                    ? $"Judgement counts match, but this replay can't reproduce the recorded score exactly. The overlay's difference can be off by up to {points} points."
                    : $"This replay can't be matched exactly ({correction.HitDifference} judgements differ). The overlay's difference can be off by up to {points} points.";
                break;

            case TimelineCorrectionStatus.Raw:
                CorrectionTone = "info";
                CorrectionTitle = "Raw timeline";
                CorrectionDetail = correction.ScoreDifference == 0
                    ? "Exactly what osu!lazer's replay playback shows, which here matches the recorded score."
                    : $"Exactly what osu!lazer's replay playback shows ({points} points from the recorded score). Choose Corrected in Settings to match the recorded score.";
                break;

            case TimelineCorrectionStatus.Scaled:
                CorrectionTone = "info";
                CorrectionTitle = "Scaled timeline";
                CorrectionDetail = $"The raw timeline is stretched so its final {scaleMetric} equals the replay's.";
                break;

            case TimelineCorrectionStatus.Legacy:
                CorrectionTone = "info";
                CorrectionTitle = "Shown as osu!lazer plays it back";
                CorrectionDetail = "osu!stable counts hold notes differently, so this replay can't be matched to its recorded judgements. The timeline is exactly what lazer's replay playback shows.";
                break;

            default:
                CorrectionTone = "neutral";
                CorrectionTitle = string.Empty;
                CorrectionDetail = string.Empty;
                break;
        }
    }

    public void ShowCorrectionError(string message)
    {
        IsCorrectionLoading = false;
        CorrectionTone = "error";
        CorrectionTitle = "Couldn't build the replay timeline";
        CorrectionDetail = message;
    }

    // --- Overlay metrics ---

    private MetricOption? selectedMainMetric;
    private MetricOption? selectedSubMetric;

    public IReadOnlyList<MetricOption> MainMetricOptions { get; } =
        MetricRegistry.All.Select(metric => new MetricOption(metric.Key, $"Main: {metric.DisplayName}")).ToList();

    public IReadOnlyList<MetricOption> SubMetricOptions { get; } =
        MetricRegistry.All.Select(metric => new MetricOption(metric.Key, $"Sub: {metric.DisplayName}"))
                      .Append(new MetricOption(MetricRegistry.Off, $"Sub: {MetricRegistry.Off}")).ToList();

    public MetricOption? SelectedMainMetric
    {
        get => selectedMainMetric;
        set
        {
            if (value != null && Set(ref selectedMainMetric, value))
                SettingsChanged?.Invoke();
        }
    }

    public MetricOption? SelectedSubMetric
    {
        get => selectedSubMetric;
        set
        {
            if (value != null && Set(ref selectedSubMetric, value))
                SettingsChanged?.Invoke();
        }
    }

    public void SetMetrics(string main, string sub)
    {
        selectedMainMetric = MainMetricOptions.FirstOrDefault(option => option.Key == main) ?? MainMetricOptions[0];
        selectedSubMetric = SubMetricOptions.FirstOrDefault(option => option.Key == sub) ?? SubMetricOptions[^1];
        OnPropertyChanged(nameof(SelectedMainMetric));
        OnPropertyChanged(nameof(SelectedSubMetric));
    }

    // --- Timeline mode and theme (radio groups) ---

    private CorrectionMode correctionMode = CorrectionMode.Corrected;
    private string theme = "System";

    public CorrectionMode CorrectionMode
    {
        get => correctionMode;
        set
        {
            if (!Set(ref correctionMode, value))
                return;

            OnPropertyChanged(nameof(IsCorrectedMode));
            OnPropertyChanged(nameof(IsRawMode));
            OnPropertyChanged(nameof(IsScaledMode));
            SettingsChanged?.Invoke();
        }
    }

    public bool IsCorrectedMode
    {
        get => CorrectionMode == CorrectionMode.Corrected;
        set { if (value) CorrectionMode = CorrectionMode.Corrected; }
    }

    public bool IsRawMode
    {
        get => CorrectionMode == CorrectionMode.Raw;
        set { if (value) CorrectionMode = CorrectionMode.Raw; }
    }

    public bool IsScaledMode
    {
        get => CorrectionMode == CorrectionMode.Scaled;
        set { if (value) CorrectionMode = CorrectionMode.Scaled; }
    }

    public string Theme
    {
        get => theme;
        set
        {
            if (!Set(ref theme, value))
                return;

            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            SettingsChanged?.Invoke();
        }
    }

    public bool IsSystemTheme
    {
        get => Theme == "System";
        set { if (value) Theme = "System"; }
    }

    public bool IsDarkTheme
    {
        get => Theme == "Dark";
        set { if (value) Theme = "Dark"; }
    }

    public bool IsLightTheme
    {
        get => Theme == "Light";
        set { if (value) Theme = "Light"; }
    }

    // --- Beatmap background ---

    private Bitmap? backgroundImage;
    private bool showBackground = true;

    /// <summary>The current beatmap's background, decoded at a reduced size (null when there is none).</summary>
    public Bitmap? BackgroundImage
    {
        get => backgroundImage;
        set
        {
            var old = backgroundImage;
            if (!Set(ref backgroundImage, value))
                return;

            // Anchored to the top: most backgrounds keep their subject in the upper half, which a centred crop cuts off.
            BackgroundBrush = value == null ? null : new ImageBrush(value) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
            OnPropertyChanged(nameof(BackgroundBrush));
            old?.Dispose();
            OnPropertyChanged(nameof(ShowBanner));
            OnPropertyChanged(nameof(ShowBannerOnCompare));
        }
    }

    public IBrush? BackgroundBrush { get; private set; }

    public bool ShowBackground
    {
        get => showBackground;
        set
        {
            if (!Set(ref showBackground, value))
                return;

            OnPropertyChanged(nameof(ShowBanner));
            OnPropertyChanged(nameof(ShowBannerOnCompare));
            SettingsChanged?.Invoke();
        }
    }

    /// <summary>Whether the beatmap background banner is drawn.</summary>
    public bool ShowBanner => ShowBackground && BackgroundImage != null;

    public bool ShowBannerOnCompare => ShowBanner && IsComparePage;

    // --- osu!lazer folder and overlays ---

    private string osuLazerPath = string.Empty;

    public string OsuLazerPath
    {
        get => osuLazerPath;
        set => Set(ref osuLazerPath, value);
    }

    public string OverlayFolder { get; } = Path.Combine(AppContext.BaseDirectory, "static");
    public bool HasOverlayFolder => Directory.Exists(OverlayFolder);
}
