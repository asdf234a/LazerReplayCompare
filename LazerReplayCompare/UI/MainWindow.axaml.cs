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
    private readonly StableReplayFinder stableReplayFinder = new();
    private readonly TosuClient tosuClient = new();
    private readonly ReplayTimelineBuilder timelineBuilder = new();
    private readonly ReplayApiServer apiServer;
    private readonly object replayLock = new();
    private readonly AppSettingsService settingsService = new();
    private readonly ReplaySelectionService replaySelection = new();
    private readonly MainWindowViewModel viewModel = new();

    private JudgementDonut judgementDonut = null!;
    private Avalonia.Controls.Primitives.UniformGrid judgementLegend = null!;
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
        judgementDonut = this.FindControl<JudgementDonut>("JudgementDonut")!;
        judgementLegend = this.FindControl<Avalonia.Controls.Primitives.UniformGrid>("JudgementLegend")!;
        replayList = this.FindControl<ListBox>("ReplayList")!;
        setUpTitleBar(this.FindControl<Border>("TitleBar")!);

        // The sharp banner scrolls with the page, so it stays behind "now playing" (the blurred copy stays put).
        var banner = this.FindControl<Panel>("Banner")!;
        var bannerOffset = new TranslateTransform();
        banner.RenderTransform = bannerOffset;
        var compareScroll = this.FindControl<ScrollViewer>("CompareScroll")!;
        compareScroll.ScrollChanged += (_, _) =>
        {
            var offset = compareScroll.Offset.Y;
            bannerOffset.Y = -offset;
            // ... and gradually gives way to the blurred copy as the page scrolls.
            banner.Opacity = Math.Clamp(1 - offset / 320, 0, 1);

            // Fade the content's top edge under the top bar once there is something scrolled past it.
            if (compareScroll.OpacityMask is LinearGradientBrush { GradientStops: [var top, _] })
                top.Color = Color.FromArgb((byte)Math.Round(255 * (1 - Math.Clamp(offset / 28, 0, 1))), 0, 0, 0);
        };

        // In a narrow window the legend stacks into one column so the target's name keeps its room.
        this.FindControl<Border>("TargetCard")!.SizeChanged += (_, e) => judgementLegend.Columns = e.NewSize.Width >= 1000 ? 2 : 1;

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
        // Playing osu!stable needs osu!stable's folder instead (found through tosu, or chosen on its banner).
        if (!OsuLazerPathResolver.IsOsuLazerRoot(viewModel.OsuLazerPath) && !viewModel.IsStableClient)
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
        viewModel.OsuStablePath = OsuStablePathResolver.Resolve(null, current.OsuStablePath) ?? current.OsuStablePath;
        viewModel.StableFound = OsuStablePathResolver.IsOsuStableRoot(viewModel.OsuStablePath);

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

    // --- osu!stable folder ---

    private async void BrowseOsuStableFolder(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return;

        var startLocation = !string.IsNullOrWhiteSpace(viewModel.OsuStablePath) && Directory.Exists(viewModel.OsuStablePath)
            ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(viewModel.OsuStablePath)
            : null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select osu! (stable) folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        SetOsuStablePath(path);
        RefreshReplays();
    }

    private void OsuStablePathCommitted(object? sender, RoutedEventArgs e) => CommitOsuStablePath();

    private void OsuStablePathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitOsuStablePath();
    }

    private void CommitOsuStablePath()
    {
        var path = (viewModel.OsuStablePath ?? string.Empty).Trim();
        if (string.Equals(path, settingsService.Current.OsuStablePath, StringComparison.Ordinal))
            return;

        SetOsuStablePath(path);
        RefreshReplays();
    }

    private void SetOsuStablePath(string path)
    {
        viewModel.OsuStablePath = path;
        viewModel.StableFound = OsuStablePathResolver.IsOsuStableRoot(path);
        settingsService.SetOsuStablePath(path);
    }

    // --- tosu ---

    private void OnSnapshotReceived(TosuSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            viewModel.TosuConnected = true;
            var oldChecksum = currentSnapshot.BeatmapChecksum;
            var oldSpeed = currentSnapshot.PlaySpeed;
            var oldStable = currentSnapshot.IsStable;
            currentSnapshot = snapshot;
            viewModel.IsStableClient = snapshot.IsStable;

            var hasBeatmap = !string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum);
            viewModel.HasBeatmap = hasBeatmap;
            viewModel.BeatmapTitle = hasBeatmap ? FirstNonEmpty(snapshot.BeatmapTitle, snapshot.BeatmapChecksum) : "No beatmap selected";
            viewModel.BeatmapSubtitle = hasBeatmap
                ? string.Join(" · ", new[] { snapshot.BeatmapArtist, snapshot.BeatmapVersion }.Where(part => !string.IsNullOrWhiteSpace(part)))
                : $"Select a beatmap in {viewModel.GameName}";

            if (snapshot.BeatmapChecksum != backgroundBeatmap)
                OnBackgroundBeatmapChanged(snapshot);

            if (!viewModel.LazerFound && !snapshot.IsStable && OsuLazerPathResolver.Resolve(snapshot) is { } detectedPath)
                SetOsuLazerPath(detectedPath);
            if (!viewModel.StableFound && snapshot.IsStable && OsuStablePathResolver.Resolve(snapshot) is { } stablePath)
                SetOsuStablePath(stablePath);

            // the replay list comes from the game being played: osu!lazer's database or osu!stable's scores.db
            if (hasBeatmap && (snapshot.BeatmapChecksum != oldChecksum || snapshot.IsStable != oldStable))
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
            viewModel.StatusText = $"Select a beatmap in {viewModel.GameName} first";
            return;
        }

        var md5 = currentSnapshot.BeatmapChecksum;
        if (currentSnapshot.IsStable)
        {
            RefreshStableReplays(md5);
            return;
        }

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

    /// <summary>Playing in osu!stable: the beatmap's scores from osu!stable's scores.db, with their replays from Data/r.</summary>
    private void RefreshStableReplays(string md5)
    {
        var stableRoot = (viewModel.OsuStablePath ?? string.Empty).Trim();
        if (!OsuStablePathResolver.IsOsuStableRoot(stableRoot))
        {
            viewModel.StableFound = false;
            return;
        }

        viewModel.StableFound = true;
        viewModel.IsLoadingReplays = true;
        viewModel.StatusText = string.Empty;

        _ = Task.Run(() =>
        {
            try
            {
                var replays = stableReplayFinder.FindReplays(md5, stableRoot);
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
                    OnDatabaseBackground(md5, string.Empty);
                });
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
                Dispatcher.UIThread.Post(() =>
                {
                    viewModel.IsLoadingReplays = false;
                    viewModel.StatusText = $"Couldn't read osu!stable replays: {ex.Message}";
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

        if (!target.HasReplay)
        {
            viewModel.ShowCorrectionNotice("No replay to compare",
                "osu!stable lists this score in scores.db, but its replay file is missing from Data/r, so there is nothing to compare against.",
                "No replay file");
            return;
        }

        var client = LiveClients.Parse(currentSnapshot.Client);
        if (client == LiveClient.Stable && !target.IsLegacy)
        {
            viewModel.ShowCorrectionNotice("Not compared in osu!stable",
                "This replay was set in osu!lazer. Playing in osu!stable, the overlay only compares against replays set in osu!stable.",
                "osu!lazer replay");
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
                var timeline = timelineBuilder.GetOrBuild(target.FilePath, beatmapPath, 0, mode, metric.Key, client);
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

    /// <summary>Legend next to the judgement ring (theme brushes, so rebuilt when the theme changes).</summary>
    private void RebuildJudgementBar()
    {
        judgementDonut.Refresh();
        judgementLegend.Children.Clear();

        foreach (var segment in viewModel.Judgements)
        {
            var brush = this.TryFindResource($"Judgement{segment.Key}Brush", ActualThemeVariant, out var resource) && resource is IBrush found
                ? found
                : Brushes.Gray;

            var legend = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6, Margin = new Thickness(0, 1, 16, 1) };
            legend.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = segment.Key, FontSize = 11, Classes = { "secondary" } };
            Grid.SetColumn(name, 1);
            legend.Children.Add(name);
            var count = new TextBlock { Text = segment.CountText, FontSize = 11, Classes = { "mono" }, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(count, 2);
            legend.Children.Add(count);
            judgementLegend.Children.Add(legend);
        }
    }

    /// <summary>The window has no system title bar: the top bar's empty space drags it and the app draws the window buttons.</summary>
    private void setUpTitleBar(Border titleBar)
    {
        Opened += (_, _) => WindowCorners.Round(this);

        var maximizeGlyph = titleBar.FindControl<Avalonia.Controls.Shapes.Path>("MaximizeGlyph")!;
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty)
                return;

            maximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
                ? "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z"
                : "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z");
        };
    }

    private void DragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
            ToggleMaximizeWindow(sender, e);
        else
            BeginMoveDrag(e);
    }

    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximizeWindow(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    // --- Pages, compact mode, files ---

    private void ShowComparePage(object? sender, RoutedEventArgs e) => viewModel.IsSettingsPage = false;

    private void ShowSettingsPage(object? sender, RoutedEventArgs e) => viewModel.IsSettingsPage = true;

    private void OpenCompactMode(object? sender, RoutedEventArgs e)
    {
        if (compactWindow == null)
        {
            compactWindow = new CompactWindow(viewModel, viewModel.Theme, ShowFullWindow, Close);
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
