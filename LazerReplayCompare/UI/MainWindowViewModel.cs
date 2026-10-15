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

/// <param name="Key">Judgement name; the window maps it to a theme brush (Judgement{Key}Brush).</param>
public sealed record JudgementSegment(string Key, int Count)
{
    public string CountText => Count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The name shown for it (MAX, 300, …).</summary>
    public string Label => JudgementText.Name(Key);
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

    /// <summary>Raised when the user changes the timeline mode, the theme or the background setting.</summary>
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
            OnPropertyChanged(nameof(ShowBackdrop));
            OnPropertyChanged(nameof(ShowCompareList));
            OnPropertyChanged(nameof(ShowDetailsPage));
        }
    }

    public bool IsComparePage => !IsSettingsPage;

    private ReplayDetailsViewModel? details;

    /// <summary>The replay whose details page is open on the Compare page (null: the replay list).</summary>
    public ReplayDetailsViewModel? Details
    {
        get => details;
        set
        {
            if (!Set(ref details, value))
                return;

            OnPropertyChanged(nameof(ShowCompareList));
            OnPropertyChanged(nameof(ShowDetailsPage));
        }
    }

    public bool ShowCompareList => IsComparePage && Details == null;
    public bool ShowDetailsPage => IsComparePage && Details != null;

    // --- Connections ---

    private bool tosuConnected;
    private bool lazerFound;
    private bool stableFound;
    private bool isStableClient;
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

    public bool StableFound
    {
        get => stableFound;
        set { if (Set(ref stableFound, value)) connectionsChanged(); }
    }

    /// <summary>tosu reports osu!stable: replays come from osu!stable's scores.db instead of osu!lazer's database.</summary>
    public bool IsStableClient
    {
        get => isStableClient;
        set
        {
            if (!Set(ref isStableClient, value))
                return;

            connectionsChanged();
            OnPropertyChanged(nameof(GameName));
        }
    }

    /// <summary>The game tosu follows ("osu!lazer" or "osu!stable"), for texts that name it.</summary>
    public string GameName => IsStableClient ? "osu!stable" : "osu!lazer";

    /// <summary>The replay database of the game being played was found.</summary>
    public bool GameFolderFound => IsStableClient ? StableFound : LazerFound;

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

    public bool AllConnected => TosuConnected && GameFolderFound && ApiRunning;
    public bool ShowTosuBanner => !TosuConnected;
    public bool ShowLazerBanner => !IsStableClient && !LazerFound;
    public bool ShowStableBanner => IsStableClient && !StableFound;
    public bool ShowApiBanner => !ApiRunning;

    public string ConnectionText => AllConnected
        ? "All connected"
        : !TosuConnected ? "tosu not connected"
        : !GameFolderFound ? $"{GameName} folder missing"
        : "Overlay API stopped";

    public string ConnectionDetails =>
        $"tosu: {(TosuConnected ? "connected (:24050)" : "not connected")}\n" +
        (IsStableClient
            ? $"osu!stable: {(StableFound ? "found" : "folder not found")}\n"
            : $"osu!lazer: {(LazerFound ? "found" : "folder not found")}\n") +
        $"Overlay API: {(ApiRunning ? "running (:24052)" : "stopped")}";

    private void connectionsChanged()
    {
        OnPropertyChanged(nameof(GameFolderFound));
        OnPropertyChanged(nameof(AllConnected));
        OnPropertyChanged(nameof(ShowTosuBanner));
        OnPropertyChanged(nameof(ShowLazerBanner));
        OnPropertyChanged(nameof(ShowStableBanner));
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
            OnPropertyChanged(nameof(TargetDetailText));
            OnPropertyChanged(nameof(TargetMods));
            OnPropertyChanged(nameof(TargetHasMods));
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
    public string TargetDetailText => Target == null ? "-" : $"{Target.ModsText} · {TargetDateText}";
    public IReadOnlyList<ReplayMod> TargetMods => Target?.Mods ?? (IReadOnlyList<ReplayMod>)Array.Empty<ReplayMod>();
    public bool TargetHasMods => TargetMods.Count > 0;
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

    public string TargetModeText => IsTargetPinned ? "Pinned" : $"Auto · best {(AutoSpeed.Stable ? "osu!stable replay " : "")}at {AutoSpeed.RateText}{(!AutoSpeed.Stable && AutoSpeed.Classic ? " Classic" : "")}";

    /// <summary>Replays exist, but none at the current rate (and nothing is pinned).</summary>
    public bool ShowNoTarget => HasReplays && Target == null;

    public string NoTargetText => AutoSpeed.Stable
        ? $"No osu!stable replay at {AutoSpeed.RateText}. Playing in osu!stable, only replays set in osu!stable are compared."
        : $"No saved replay at {AutoSpeed.RateText}{(AutoSpeed.Classic ? " with Classic" : "")}. Pin one below to compare against it anyway.";
    public bool ShowPinHint => !IsTargetPinned;
    public string MaxComboText => Target?.MaxCombo.ToString("N0", CultureInfo.InvariantCulture) ?? "-";

    // --- Judgement breakdown ---

    private IReadOnlyList<JudgementSegment> judgements = Array.Empty<JudgementSegment>();

    public IReadOnlyList<JudgementSegment> Judgements
    {
        get => judgements;
        private set
        {
            if (!Set(ref judgements, value))
                return;

            OnPropertyChanged(nameof(JudgementTotalText));
            OnPropertyChanged(nameof(JudgementSummaryText));
        }
    }

    public string JudgementSummaryText => string.Join("\n", Judgements.Select(segment => $"{segment.Label}  {segment.CountText}"));

    public string JudgementTotalText => Judgements.Sum(segment => segment.Count).ToString("N0", CultureInfo.InvariantCulture);

    private static IReadOnlyList<JudgementSegment> buildJudgements(ReplayEntry replay)
        => MetricDefinition.JudgementNames.Select(key => new JudgementSegment(key, replay.Statistics.GetValueOrDefault(key))).ToList();

    // --- Timeline correction ---

    private string correctionTitle = string.Empty;
    private string correctionDetail = string.Empty;
    private string correctionShort = string.Empty;
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

    /// <summary>One short line under the title; the full explanation is the tooltip.</summary>
    public string CorrectionShort
    {
        get => correctionShort;
        private set => Set(ref correctionShort, value);
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
        CorrectionShort = "Working out every note";
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
                CorrectionShort = "Recorded score, used directly";
                break;

            case TimelineCorrectionStatus.Exact:
                CorrectionTone = "good";
                CorrectionTitle = "Exact match";
                CorrectionDetail = correction.StableScoring
                    ? "Scored the way osu!stable does, the timeline reproduces the recorded score and every judgement count, so the overlay's difference is exact at every note."
                    : "The corrected timeline reproduces the recorded score and every judgement count, so the overlay's difference is exact at every note.";
                CorrectionShort = "Score & judgements reproduced";
                break;

            case TimelineCorrectionStatus.Corrected:
                CorrectionTone = "good";
                CorrectionTitle = "Judgements match";
                CorrectionDetail = "Scored the way osu!stable does, the timeline reproduces every recorded judgement count. osu!stable keeps some scoring state that replays don't record, so the small score difference left over is spread across the timeline, which ends exactly on the recorded score.";
                CorrectionShort = "Judgements reproduced, score aligned";
                break;

            case TimelineCorrectionStatus.Approximate when correction.StableScoring:
                CorrectionTone = "warning";
                CorrectionTitle = $"Approximate · {correction.HitDifference} judgements differ";
                CorrectionDetail = $"Scored the way osu!stable does, the replay's frames can't reproduce every recorded judgement ({correction.HitDifference} differ). The timeline still ends on the recorded score, but notes near the differing judgements can be slightly off.";
                CorrectionShort = $"{correction.HitDifference} judgements differ";
                break;

            case TimelineCorrectionStatus.Approximate:
                CorrectionTone = "warning";
                CorrectionTitle = $"Approximate · within {points} points";
                CorrectionDetail = correction.HitDifference == 0
                    ? $"Judgement counts match, but this replay can't reproduce the recorded score exactly. The overlay's difference can be off by up to {points} points."
                    : $"This replay can't be matched exactly ({correction.HitDifference} judgements differ). The overlay's difference can be off by up to {points} points.";
                CorrectionShort = correction.HitDifference == 0 ? "Judgements match" : $"{correction.HitDifference} judgements differ";
                break;

            case TimelineCorrectionStatus.Raw:
                CorrectionTone = "info";
                CorrectionTitle = "Raw timeline";
                var playback = correction.StableScoring ? "osu!stable's judgement of the replay gives" : "osu!lazer's replay playback shows";
                CorrectionDetail = correction.ScoreDifference == 0
                    ? $"Exactly what {playback}, which here matches the recorded score."
                    : $"Exactly what {playback} ({points} points from the recorded score). Choose Corrected in Settings to match the recorded score.";
                CorrectionShort = correction.ScoreDifference == 0 ? "Matches the recorded score" : $"{points} points from the recorded score";
                break;

            case TimelineCorrectionStatus.Scaled:
                CorrectionTone = "info";
                CorrectionTitle = "Scaled timeline";
                CorrectionDetail = $"The raw timeline is stretched so its final {scaleMetric} equals the replay's.";
                CorrectionShort = $"Stretched to the final {scaleMetric}";
                break;

            case TimelineCorrectionStatus.Legacy:
                CorrectionTone = "info";
                CorrectionTitle = "osu!stable replay · scaled";
                CorrectionDetail = $"osu!stable counts hold notes differently, so this replay can't be matched to its recorded judgements in osu!lazer. The timeline is lazer's playback of it, stretched so its final {scaleMetric} equals the recorded one.";
                CorrectionShort = $"Lazer playback, stretched to the final {scaleMetric}";
                break;

            default:
                CorrectionTone = "neutral";
                CorrectionTitle = string.Empty;
                CorrectionDetail = string.Empty;
                CorrectionShort = string.Empty;
                break;
        }
    }

    /// <summary>Informational state when the target isn't compared (e.g. an osu!lazer replay while playing osu!stable).</summary>
    public void ShowCorrectionNotice(string title, string detail, string shortText)
    {
        IsCorrectionLoading = false;
        CorrectionTone = "info";
        CorrectionTitle = title;
        CorrectionDetail = detail;
        CorrectionShort = shortText;
    }

    public void ShowCorrectionError(string message)
    {
        IsCorrectionLoading = false;
        CorrectionTone = "error";
        CorrectionTitle = "Timeline failed";
        CorrectionDetail = message;
        CorrectionShort = "Hover for details";
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

            var oldBlurred = BlurredBackground;
            BlurredBackground = value == null ? null : BackdropImage.CreateBlurred(value);
            OnPropertyChanged(nameof(BlurredBackground));
            oldBlurred?.Dispose();
            old?.Dispose();
        }
    }

    /// <summary>Small blurred copy of the background, stretched behind the whole window (the translucent cards sit on it).</summary>
    public Bitmap? BlurredBackground { get; private set; }

    public bool ShowBackground
    {
        get => showBackground;
        set
        {
            if (!Set(ref showBackground, value))
                return;

            OnPropertyChanged(nameof(ShowBackdrop));
            SettingsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Whether the compare page uses the background layout (banner, translucent cards). It depends only on the setting,
    /// not on whether the image has loaded yet, so the layout never shifts when it arrives.
    /// </summary>
    public bool ShowBackdrop => ShowBackground && IsComparePage;

    // --- osu!lazer folder and overlays ---

    private string osuLazerPath = string.Empty;

    public string OsuLazerPath
    {
        get => osuLazerPath;
        set => Set(ref osuLazerPath, value);
    }

    private string osuStablePath = string.Empty;

    public string OsuStablePath
    {
        get => osuStablePath;
        set => Set(ref osuStablePath, value);
    }

    public string OverlayFolder { get; } = Path.Combine(AppContext.BaseDirectory, "static");
    public bool HasOverlayFolder => Directory.Exists(OverlayFolder);
}
