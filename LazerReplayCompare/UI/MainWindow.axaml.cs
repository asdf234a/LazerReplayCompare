using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LazerReplayCompare;

public sealed partial class MainWindow : Window
{
    private const int ApiPort = 24052;
    private const string TosuHost = "127.0.0.1:24050";

    private readonly ReplayFinder replayFinder = new();
    private readonly TosuClient tosuClient = new();
    private readonly ReplayTimelineBuilder timelineBuilder = new();
    private readonly ReplayApiServer apiServer;
    private readonly object replayLock = new();
    private readonly AppSettingsService settingsService = new();
    private readonly ReplaySelectionService replaySelection = new();
    private readonly MainWindowViewModel viewModel = new();

    private Grid judgementBar = null!;
    private WrapPanel judgementLegend = null!;
    private ListBox replayList = null!;

    private List<ReplayEntry> currentReplays = new();
    private string currentReplaysMd5 = string.Empty;
    private bool fillingReplayList;
    private TosuSnapshot currentSnapshot = new("lazer", "", "", "", "", "", "", "", "");
    private CompactWindow? compactWindow;
    private bool closing;
    private bool started;
    private CancellationTokenSource? correctionCancellation;
    private (CorrectionMode Mode, string MainMetric) lastTimelineSettings;

    public MainWindow()
    {
        apiServer = new ReplayApiServer(GetCurrentReplays, GetCurrentCorrectionMode, GetOverlayMetrics, timelineBuilder);

        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        judgementBar = this.FindControl<Grid>("JudgementBar")!;
        judgementLegend = this.FindControl<WrapPanel>("JudgementLegend")!;
        replayList = this.FindControl<ListBox>("ReplayList")!;

        ApplySavedSettings();
        viewModel.SettingsChanged += OnSettingsChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Opened is raised again whenever the window is shown after Hide() (e.g. back from compact mode); the API
        // server, tosu connection and first-run folder check must only start once.
        if (started)
            return;
        started = true;

        try
        {
            apiServer.Start(ApiPort);
            viewModel.ApiRunning = true;
        }
        catch (Exception ex)
        {
            viewModel.ApiRunning = false;
            viewModel.ApiError = $"Port {ApiPort} is unavailable: {ex.Message}";
        }

        tosuClient.StatusChanged += status => Dispatcher.UIThread.Post(() =>
        {
            if (status.StartsWith("Connected", StringComparison.Ordinal))
                viewModel.TosuConnected = true;
            else if (status.StartsWith("tosu disconnected", StringComparison.Ordinal))
                viewModel.TosuConnected = false;
        });
        tosuClient.SnapshotReceived += OnSnapshotReceived;
        tosuClient.Start(TosuHost);

        if (!OsuLazerPathResolver.IsOsuLazerRoot(settingsService.Current.OsuLazerPath) &&
            OsuLazerPathResolver.Resolve(null) is { } detectedPath)
        {
            SetOsuLazerPath(detectedPath);
            return;
        }

        await Task.Delay(2500);
        if (!OsuLazerPathResolver.IsOsuLazerRoot(viewModel.OsuLazerPath))
            await BrowseOsuLazerFolderCore(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        closing = true;
        compactWindow?.Close();
        tosuClient.Dispose();
        apiServer.Dispose();
        base.OnClosed(e);
    }

    // --- Settings ---

    private void ApplySavedSettings()
    {
        var current = settingsService.Current;
        viewModel.OsuLazerPath = OsuLazerPathResolver.IsOsuLazerRoot(current.OsuLazerPath)
            ? current.OsuLazerPath
            : OsuLazerPathResolver.Resolve(null) ?? current.OsuLazerPath;
        viewModel.LazerFound = OsuLazerPathResolver.IsOsuLazerRoot(viewModel.OsuLazerPath);

        viewModel.Theme = NormalizeTheme(current.Theme);
        viewModel.ShowBackground = current.ShowBackground;
        viewModel.CorrectionMode = ParseCorrectionMode(current.CorrectionMode);
        viewModel.SetMetrics(MetricRegistry.Normalize(current.MainMetric, MetricRegistry.Default.Key),
            MetricRegistry.Normalize(current.SubMetric, "Acc", allowOff: true));

        ThemeService.Apply(this, viewModel.Theme);
        lastTimelineSettings = (viewModel.CorrectionMode, viewModel.SelectedMainMetric?.Key ?? MetricRegistry.Default.Key);
    }

    private void OnSettingsChanged()
    {
        settingsService.SetTheme(viewModel.Theme);
        settingsService.SetShowBackground(viewModel.ShowBackground);
        settingsService.SetCorrectionMode(viewModel.CorrectionMode);
        if (viewModel.SelectedMainMetric is { } main)
            settingsService.SetMainMetric(main.Key);
        if (viewModel.SelectedSubMetric is { } sub)
            settingsService.SetSubMetric(sub.Key);

        ThemeService.Apply(this, viewModel.Theme);
        if (compactWindow != null)
            ThemeService.Apply(compactWindow, viewModel.Theme);
        RebuildJudgementBar();

        // The correction shown for the target depends on the timeline mode (and on the main metric when scaled).
        var timelineSettings = (viewModel.CorrectionMode, viewModel.SelectedMainMetric?.Key ?? MetricRegistry.Default.Key);
        var affectsTimeline = timelineSettings.Item1 != lastTimelineSettings.Mode ||
                              (timelineSettings.Item1 == CorrectionMode.Scaled && timelineSettings.Item2 != lastTimelineSettings.MainMetric);
        lastTimelineSettings = timelineSettings;
        if (affectsTimeline)
            UpdateTarget();
    }

    private CorrectionMode GetCurrentCorrectionMode() => ParseCorrectionMode(settingsService.Current.CorrectionMode);

    private (string MainMetric, string SubMetric) GetOverlayMetrics()
        => (MetricRegistry.Normalize(settingsService.Current.MainMetric, MetricRegistry.Default.Key),
            MetricRegistry.Normalize(settingsService.Current.SubMetric, "Acc", allowOff: true));

    private (string Md5, IReadOnlyList<ReplayEntry> Replays, ReplayEntry? SelectedReplay) GetCurrentReplays()
    {
        lock (replayLock)
        {
            var replays = currentReplays.ToList();
            return (currentReplaysMd5, replays, replaySelection.GetSelected(replays));
        }
    }

    // --- osu!lazer folder ---

    private async void BrowseOsuLazerFolder(object? sender, RoutedEventArgs e) => await BrowseOsuLazerFolderCore(false);

    private async Task BrowseOsuLazerFolderCore(bool firstRun)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return;

        var startLocation = !string.IsNullOrWhiteSpace(viewModel.OsuLazerPath) && Directory.Exists(viewModel.OsuLazerPath)
            ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(viewModel.OsuLazerPath)
            : null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = firstRun ? "Select your osu!lazer folder" : "Select osu!lazer folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        SetOsuLazerPath(path);
        RefreshReplays();
    }

    private void OsuLazerPathCommitted(object? sender, RoutedEventArgs e) => CommitOsuLazerPath();

    private void OsuLazerPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitOsuLazerPath();
    }

    private void CommitOsuLazerPath()
    {
        var path = (viewModel.OsuLazerPath ?? string.Empty).Trim();
        if (string.Equals(path, settingsService.Current.OsuLazerPath, StringComparison.Ordinal))
            return;

        SetOsuLazerPath(path);
        RefreshReplays();
    }

    private void SetOsuLazerPath(string path)
    {
        viewModel.OsuLazerPath = path;
        viewModel.LazerFound = OsuLazerPathResolver.IsOsuLazerRoot(path);
        settingsService.SetOsuLazerPath(path);
    }

    // --- tosu ---

    private void OnSnapshotReceived(TosuSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            viewModel.TosuConnected = true;
            var oldChecksum = currentSnapshot.BeatmapChecksum;
            var oldSpeed = currentSnapshot.PlaySpeed;
            currentSnapshot = snapshot;

            var hasBeatmap = !string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum);
            viewModel.HasBeatmap = hasBeatmap;
            viewModel.BeatmapTitle = hasBeatmap ? FirstNonEmpty(snapshot.BeatmapTitle, snapshot.BeatmapChecksum) : "No beatmap selected";
            viewModel.BeatmapSubtitle = hasBeatmap
                ? string.Join(" · ", new[] { snapshot.BeatmapArtist, snapshot.BeatmapVersion }.Where(part => !string.IsNullOrWhiteSpace(part)))
                : "Select a beatmap in osu!lazer";

            if (snapshot.BeatmapChecksum != backgroundBeatmap)
                OnBackgroundBeatmapChanged(snapshot);

            if (!viewModel.LazerFound && OsuLazerPathResolver.Resolve(snapshot) is { } detectedPath)
                SetOsuLazerPath(detectedPath);

            if (hasBeatmap && snapshot.BeatmapChecksum != oldChecksum)
                RefreshReplays();
            else if (snapshot.PlaySpeed != oldSpeed)
                UpdateTarget();   // the auto target depends on the current rate
        });
    }

    private const string TosuBackgroundUrl = "http://" + TosuHost + "/files/beatmap/background";
    private static readonly HttpClient tosuHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Beatmap the shown (or loading) background belongs to, and where it is coming from ("" = not chosen yet).</summary>
    private string backgroundBeatmap = string.Empty;
    private string backgroundSource = string.Empty;

    /// <summary>
    /// Background sources, best first: the file tosu points at (when it exists — for lazer tosu reports the original
    /// file name, which is not how lazer stores files), the file found through the osu!lazer database once the replay
    /// lookup has run (see RefreshReplays), and finally tosu's own background endpoint.
    /// </summary>
    private void OnBackgroundBeatmapChanged(TosuSnapshot snapshot)
    {
        backgroundBeatmap = snapshot.BeatmapChecksum;
        backgroundSource = string.Empty;
        viewModel.BackgroundImage = null;

        if (string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum))
            return;

        if (File.Exists(snapshot.BackgroundPath))
            LoadBackground(snapshot.BeatmapChecksum, snapshot.BackgroundPath);
        else if (!viewModel.LazerFound)
            LoadBackground(snapshot.BeatmapChecksum, TosuBackgroundUrl);
    }

    /// <summary>Called with the database lookup's result for <paramref name="beatmap"/>.</summary>
    private void OnDatabaseBackground(string beatmap, string path)
    {
        if (beatmap != backgroundBeatmap || backgroundSource.Length > 0)
            return;

        LoadBackground(beatmap, string.IsNullOrWhiteSpace(path) ? TosuBackgroundUrl : path);
    }

    /// <summary>Decodes a background file or URL off the UI thread (reduced to banner size).</summary>
    private void LoadBackground(string beatmap, string source)
    {
        backgroundSource = source;

        _ = Task.Run(async () =>
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            try
            {
                await using var stream = source.StartsWith("http", StringComparison.Ordinal)
                    ? (Stream)new MemoryStream(await tosuHttp.GetByteArrayAsync(source))
                    : File.OpenRead(source);
                bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 1600);
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (beatmap != backgroundBeatmap || source != backgroundSource)
                {
                    bitmap?.Dispose();
                    return;
                }

                if (bitmap == null && source != TosuBackgroundUrl)
                {
                    // The file could not be read; tosu may still be able to serve it.
                    LoadBackground(beatmap, TosuBackgroundUrl);
                    return;
                }

                viewModel.BackgroundImage = bitmap;
            });
        });
    }

    /// <summary>The beatmap path exactly as the overlays send it to /timeline, so both share the timeline cache.</summary>
    private static string BuildBeatmapPath(TosuSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SongsFolder) || string.IsNullOrWhiteSpace(snapshot.BeatmapFile))
            return string.Empty;

        return snapshot.Client == "lazer"
            ? $"{snapshot.SongsFolder}\\{snapshot.BeatmapFile}"
            : $"{snapshot.SongsFolder}\\{snapshot.BeatmapFolder}\\{snapshot.BeatmapFile}";
    }

    // --- Replays ---

    private void RefreshReplays(object? sender, RoutedEventArgs e) => RefreshReplays();

    private void RefreshReplays()
    {
        if (string.IsNullOrWhiteSpace(currentSnapshot.BeatmapChecksum))
        {
            viewModel.StatusText = "Select a beatmap in osu!lazer first";
            return;
        }

        var md5 = currentSnapshot.BeatmapChecksum;
        var osuLazerRoot = (viewModel.OsuLazerPath ?? string.Empty).Trim();
        var realmPath = System.IO.Path.Combine(osuLazerRoot, "client.realm");
        var filesRoot = System.IO.Path.Combine(osuLazerRoot, "files");

        if (!File.Exists(realmPath) || !Directory.Exists(filesRoot))
        {
            viewModel.LazerFound = false;
            return;
        }

        viewModel.LazerFound = true;
        viewModel.IsLoadingReplays = true;
        viewModel.StatusText = string.Empty;

        _ = Task.Run(() =>
        {
            try
            {
                var (replays, backgroundPath) = replayFinder.FindReplaysAndBackground(md5, realmPath, filesRoot);
                lock (replayLock)
                {
                    currentReplays = replays;
                    currentReplaysMd5 = md5;
                    replaySelection.ClearIfMissing(replays);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    viewModel.IsLoadingReplays = false;
                    FillReplayList(replays);
                    OnDatabaseBackground(md5, backgroundPath);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    viewModel.IsLoadingReplays = false;
                    viewModel.StatusText = $"Couldn't read replays: {ex.Message}";
                    OnDatabaseBackground(md5, string.Empty);
                });
            }
        });
    }

    private void FillReplayList(IReadOnlyList<ReplayEntry> replays)
    {
        fillingReplayList = true;
        foreach (var row in viewModel.ReplayRows)
            row.PropertyChanged -= ReplayRow_PropertyChanged;
        viewModel.ReplayRows.Clear();

        foreach (var replay in Sort(replays.Select(replay => new ReplayRow(replay) { IsPinned = replaySelection.IsSelected(replay) })))
        {
            replay.PropertyChanged += ReplayRow_PropertyChanged;
            viewModel.ReplayRows.Add(replay);
        }

        fillingReplayList = false;
        viewModel.NotifyReplaysChanged();
        UpdateTarget();
    }

    private void ReplayRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (fillingReplayList || e.PropertyName != nameof(ReplayRow.IsPinned) || sender is not ReplayRow row)
            return;

        fillingReplayList = true;
        lock (replayLock)
        {
            if (row.IsPinned)
                replaySelection.Select(row.Entry);
            else if (replaySelection.IsSelected(row.Entry))
                replaySelection.Clear();
        }

        foreach (var other in viewModel.ReplayRows)
        {
            if (!ReferenceEquals(other, row))
                other.IsPinned = false;
        }

        fillingReplayList = false;
        UpdateTarget();
    }

    private void UnpinReplay(object? sender, RoutedEventArgs e)
    {
        fillingReplayList = true;
        lock (replayLock)
            replaySelection.Clear();
        foreach (var row in viewModel.ReplayRows)
            row.IsPinned = false;
        fillingReplayList = false;
        UpdateTarget();
    }

    // --- Sorting ---

    private void SortReplays(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<ReplaySortKey>(tag, out var key))
            return;

        if (viewModel.SortKey == key)
            viewModel.SortDescending = !viewModel.SortDescending;
        else
        {
            viewModel.SortKey = key;
            viewModel.SortDescending = key != ReplaySortKey.Miss;
        }

        var sorted = Sort(viewModel.ReplayRows).ToList();
        fillingReplayList = true;
        viewModel.ReplayRows.Clear();
        foreach (var row in sorted)
            viewModel.ReplayRows.Add(row);
        fillingReplayList = false;
    }

    private IEnumerable<ReplayRow> Sort(IEnumerable<ReplayRow> rows)
    {
        Func<ReplayRow, IComparable> keySelector = viewModel.SortKey switch
        {
            ReplaySortKey.Date => row => DateTimeOffset.TryParse(row.Entry.Date, out var date) ? date : DateTimeOffset.MinValue,
            ReplaySortKey.Accuracy => row => row.Entry.Accuracy,
            ReplaySortKey.Miss => row => row.Miss,
            ReplaySortKey.Combo => row => row.Entry.MaxCombo,
            _ => row => row.Entry.Score,
        };

        return viewModel.SortDescending ? rows.OrderByDescending(keySelector) : rows.OrderBy(keySelector);
    }

    // --- Comparison target ---

    /// <summary>
    /// The pinned replay, otherwise the best score at the current play's rate and Classic mod (the same rule as the
    /// overlay's auto mode). Also starts working out how the target's timeline matches its recorded result.
    /// </summary>
    private void UpdateTarget()
    {
        ReplayEntry? target;
        bool pinned;
        var speed = currentSnapshot.PlaySpeed;
        lock (replayLock)
        {
            var selected = replaySelection.GetSelected(currentReplays);
            pinned = selected != null;
            target = selected ?? currentReplays.FirstOrDefault(replay => AutoTargetRule.IsCompatible(replay, speed));
        }

        viewModel.AutoSpeed = speed;
        viewModel.IsTargetPinned = pinned;
        viewModel.Target = target;

        correctionCancellation?.Cancel();
        if (target == null)
        {
            viewModel.ShowCorrection(null, string.Empty);
            return;
        }

        var beatmapPath = BuildBeatmapPath(currentSnapshot);
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
        {
            viewModel.ShowCorrectionError("The beatmap file isn't available yet, so the replay can't be checked.");
            return;
        }

        var cancellation = correctionCancellation = new CancellationTokenSource();
        var mode = viewModel.CorrectionMode;
        var metric = viewModel.SelectedMainMetric ?? viewModel.MainMetricOptions[0];
        var metricName = MetricRegistry.Find(metric.Key)?.DisplayName ?? metric.Key;
        viewModel.ShowCorrectionLoading();

        _ = Task.Run(() =>
        {
            try
            {
                var timeline = timelineBuilder.GetOrBuild(target.FilePath, beatmapPath, 0, mode, metric.Key);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cancellation.IsCancellationRequested)
                        viewModel.ShowCorrection(timeline.Correction, metricName);
                });
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cancellation.IsCancellationRequested)
                        viewModel.ShowCorrectionError(ex.Message);
                });
            }
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Judgements))
            RebuildJudgementBar();
    }

    /// <summary>Proportional judgement bar and its legend (theme brushes, so rebuilt when the theme changes).</summary>
    private void RebuildJudgementBar()
    {
        judgementBar.Children.Clear();
        judgementBar.ColumnDefinitions.Clear();
        judgementLegend.Children.Clear();

        foreach (var segment in viewModel.Judgements)
        {
            var brush = this.TryFindResource($"Judgement{segment.Key}Brush", ActualThemeVariant, out var resource) && resource is IBrush found
                ? found
                : Brushes.Gray;

            if (segment.Count > 0)
            {
                judgementBar.ColumnDefinitions.Add(new ColumnDefinition(segment.Count, GridUnitType.Star));
                var bar = new Border { Background = brush, Margin = new Thickness(0, 0, 2, 0) };
                Grid.SetColumn(bar, judgementBar.ColumnDefinitions.Count - 1);
                judgementBar.Children.Add(bar);
            }

            var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 18, 4) };
            legend.Children.Add(new Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = segment.Key, FontSize = 13, Classes = { "secondary" } });
            legend.Children.Add(new TextBlock { Text = segment.CountText, FontSize = 13, Classes = { "mono" } });
            judgementLegend.Children.Add(legend);
        }
    }

    // --- Pages, compact mode, files ---

    private void ShowComparePage(object? sender, RoutedEventArgs e) => viewModel.IsSettingsPage = false;

    private void ShowSettingsPage(object? sender, RoutedEventArgs e) => viewModel.IsSettingsPage = true;

    private void OpenCompactMode(object? sender, RoutedEventArgs e)
    {
        if (compactWindow == null)
        {
            compactWindow = new CompactWindow(viewModel, viewModel.Theme, ShowFullWindow);
            compactWindow.Closed += (_, _) =>
            {
                compactWindow = null;
                // Closing the compact window while the full one is hidden would leave the app running invisibly.
                if (!closing && !IsVisible)
                    ShowFullWindow();
            };
        }

        compactWindow.Show();
        Hide();
    }

    private void ShowFullWindow()
    {
        Show();
        Activate();
    }

    private void ReplayDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (replayList.SelectedItem is ReplayRow row && File.Exists(row.Entry.FilePath))
            OpenInExplorer($"/select,\"{row.Entry.FilePath}\"");
    }

    private void OpenOverlayFolder(object? sender, RoutedEventArgs e)
    {
        if (viewModel.HasOverlayFolder)
            OpenInExplorer($"\"{viewModel.OverlayFolder}\"");
    }

    private static void OpenInExplorer(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = arguments, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }

    // --- Helpers ---

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string NormalizeTheme(string? theme)
        => string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark"
            : string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
            : "System";

    private static CorrectionMode ParseCorrectionMode(string? value)
    {
        if (string.Equals(value, "Raw", StringComparison.OrdinalIgnoreCase))
            return CorrectionMode.Raw;
        if (string.Equals(value, "Scaled", StringComparison.OrdinalIgnoreCase))
            return CorrectionMode.Scaled;
        return CorrectionMode.Corrected;
    }
}
