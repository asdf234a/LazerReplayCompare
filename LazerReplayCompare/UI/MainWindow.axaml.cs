using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
    private ReplayDetailsView detailsView = null!;
    private LayoutTransformControl pageZoom = null!;
    private Panel banner = null!;
    private Panel bannerColumn = null!;
    private Panel bannerSides = null!;
    private Control bannerSidesImage = null!;
    // how far the page is scrolled (page units): the banner and its blurred sides move up and fade with it
    private double bannerScroll;
    private Grid targetRow = null!;
    private Grid replayHeader = null!;
    private bool narrowPage;

    // the banner's least height and the page content's width (StackPanel MaxWidth + margins), before the zoom
    private const double banner_min_height = 460;
    private const double page_content_width = 1160 + 2 * 28;
    // The banner's fade into the blurred copy, from its top (0) to its bottom (1): sharp down to a bit under half way,
    // then gradually blurred. The blurred sides fade the same way.
    private static readonly (double Offset, byte Alpha)[] banner_fade =
        [(0.42, 255), (0.52, 242), (0.62, 210), (0.72, 160), (0.82, 100), (0.91, 45), (1, 0)];
    // how far the sharp banner fades into the blurred sides, before the zoom
    private const double banner_side_fade = 120;

    // the score a results screen shows: its replay's details open once it is found in the list
    private ResultsScreenScore? pendingResults;
    private ResultsScreenScore? openedResults;

    // The map (and game) the open details page belongs to, and whether a results screen opened it: a page opened by
    // hand stays open when the map changes, one opened by a results screen closes when the results screen does.
    private string detailsBeatmap = string.Empty;
    private bool detailsStable;
    private bool detailsFromResults;
    private int resultsRefreshes;
    // rescans while a results screen's replay is not listed yet: every 2 s for half a minute (osu!stable's Data/r is also
    // watched, see watchStableReplays)
    private const int max_results_refreshes = 15;

    private static readonly double[] zoom_steps = [0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2];

    // analyses of the replays whose details were opened, by replay and beatmap file (most recent last)
    private readonly List<(string Key, ReplayAnalysis Analysis)> analyses = new();
    private const int kept_analyses = 12;

    private List<ReplayEntry> currentReplays = new();
    private string currentReplaysMd5 = string.Empty;
    private bool fillingReplayList;
    private TosuSnapshot currentSnapshot = new("lazer", "", "", "", "", "", "", "", "");
    private CompactWindow? compactWindow;
    private bool closing;
    private bool started;
    private CancellationTokenSource? correctionCancellation;
    private (CorrectionMode Mode, string ScaleMetric) lastTimelineSettings;
    private FileSystemWatcher? customItemsWatcher;

    // osu!stable's Data/r, watched while osu!stable is played: a play it has just saved is listed (and its results screen
    // opened) as soon as its replay is written, however long that takes
    private FileSystemWatcher? stableReplaysWatcher;
    private string stableReplaysWatched = string.Empty;
    private DispatcherTimer? stableReplaysQuiet;

    public MainWindow()
    {
        apiServer = new ReplayApiServer(GetCurrentReplays, GetCurrentCorrectionMode, () => settingsService.OverlayDisplay, timelineBuilder);

        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        judgementDonut = this.FindControl<JudgementDonut>("JudgementDonut")!;
        judgementLegend = this.FindControl<Avalonia.Controls.Primitives.UniformGrid>("JudgementLegend")!;
        replayList = this.FindControl<ListBox>("ReplayList")!;
        detailsView = this.FindControl<ReplayDetailsView>("DetailsView")!;
        detailsView.BackRequested += CloseDetails;
        detailsView.ShowFileRequested += entry => ShowReplayFile(entry);
        detailsView.PinRequested += TogglePin;
        pageZoom = this.FindControl<LayoutTransformControl>("PageZoom")!;

        // back from a details page, and zoom, from anywhere in the window (before controls handle the input)
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnWindowPointerWheel, RoutingStrategies.Tunnel);
        setUpTitleBar(this.FindControl<Border>("TitleBar")!);

        // The sharp banner scrolls with the page, so it stays behind "now playing" (the blurred copy stays put).
        banner = this.FindControl<Panel>("Banner")!;
        bannerColumn = this.FindControl<Panel>("BannerColumn")!;
        bannerSides = this.FindControl<Panel>("BannerSides")!;
        bannerSidesImage = this.FindControl<Control>("BannerSidesImage")!;
        var bannerOffset = new TranslateTransform();
        banner.RenderTransform = bannerOffset;
        SizeChanged += (_, _) => layoutBanner();
        var compareScroll = this.FindControl<ScrollViewer>("CompareScroll")!;
        var detailsScroll = detailsView.FindControl<ScrollViewer>("Scroll")!;
        void followScroll(ScrollViewer scroll)
        {
            // (the scroll offset is in page units; the banner sits outside the zoomed page)
            var offset = scroll.Offset.Y;
            bannerScroll = offset;
            bannerOffset.Y = -offset * viewModel.Zoom;
            // ... and gradually gives way to the blurred copy as the page scrolls (gone once its sharp part is scrolled past).
            banner.Opacity = Math.Clamp(1 - offset / Math.Max(1, banner.Height / viewModel.Zoom * banner_fade[0].Offset), 0, 1);
            fadeBannerSides();
        }

        compareScroll.ScrollChanged += (_, _) =>
        {
            followScroll(compareScroll);

            // Fade the content's top edge under the top bar once there is something scrolled past it.
            if (compareScroll.OpacityMask is LinearGradientBrush { GradientStops: [var top, _] })
                top.Color = Color.FromArgb((byte)Math.Round(255 * (1 - Math.Clamp(compareScroll.Offset.Y / 28, 0, 1))), 0, 0, 0);
        };
        detailsScroll.ScrollChanged += (_, _) => followScroll(detailsScroll);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ShowDetailsPage))
                followScroll(viewModel.ShowDetailsPage ? detailsScroll : compareScroll);
            else if (e.PropertyName == nameof(MainWindowViewModel.BackgroundImage))
                layoutBanner();   // its height follows the image's shape
        };

        // In a narrow window the legend stacks into one column so the target's name keeps its room.
        this.FindControl<Border>("TargetCard")!.SizeChanged += (_, e) => judgementLegend.Columns = e.NewSize.Width >= 1000 ? 2 : 1;

        // A narrow page (small window or large zoom): the target card goes two by two and the replay list drops
        // its least needed columns.
        targetRow = this.FindControl<Grid>("TargetRow")!;
        replayHeader = this.FindControl<Grid>("ReplayHeader")!;
        replayList.ContainerPrepared += (_, e) =>
        {
            if (e.Container is Control container)
                Dispatcher.UIThread.Post(() => applyReplayColumns(container), DispatcherPriority.Loaded);
        };
        this.FindControl<StackPanel>("CompareContent")!.SizeChanged += (_, e) => setNarrowPage(e.NewSize.Width < ResponsiveLayout.NarrowWidth);

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
        customItemsWatcher?.Dispose();
        stableReplaysWatcher?.Dispose();
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
        viewModel.OpenDetailsOnResults = current.OpenDetailsOnResults;
        applyZoom(current.Zoom);
        viewModel.CorrectionMode = ParseCorrectionMode(current.CorrectionMode);

        ThemeService.Apply(this, viewModel.Theme);

        // custom overlay items: metrics.json next to the settings, watched so new items appear in the pickers at once
        OverlayItems.CustomPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LazerReplayCompare", "metrics.json");
        viewModel.SetOverlayItems(OverlayItems.All, settingsService.OverlayDisplay);
        watchCustomItems();
        lastTimelineSettings = (viewModel.CorrectionMode, OverlayItems.ScaleMetric(viewModel.OverlayDisplay.MainItem));
    }

    private void OnSettingsChanged()
    {
        settingsService.SetTheme(viewModel.Theme);
        settingsService.SetShowBackground(viewModel.ShowBackground);
        settingsService.SetOpenDetailsOnResults(viewModel.OpenDetailsOnResults);
        settingsService.SetCorrectionMode(viewModel.CorrectionMode);
        var display = viewModel.OverlayDisplay;
        settingsService.SetOverlayDisplay(display.MainItem, display.MainShow, display.SubItem, display.SubShow);

        ThemeService.Apply(this, viewModel.Theme);
        if (compactWindow != null)
            ThemeService.Apply(compactWindow, viewModel.Theme);
        RebuildJudgementBar();

        // The correction shown for the target depends on the timeline mode (and on the Main item when scaled).
        var timelineSettings = (viewModel.CorrectionMode, OverlayItems.ScaleMetric(display.MainItem));
        var affectsTimeline = timelineSettings.Item1 != lastTimelineSettings.Mode ||
                              (timelineSettings.Item1 == CorrectionMode.Scaled && timelineSettings.Item2 != lastTimelineSettings.ScaleMetric);
        lastTimelineSettings = timelineSettings;
        if (affectsTimeline)
            UpdateTarget();
    }

    private CorrectionMode GetCurrentCorrectionMode() => ParseCorrectionMode(settingsService.Current.CorrectionMode);

    /// <summary>Refreshes the overlay pickers when metrics.json changes (the overlays get the new items through /state).</summary>
    private void watchCustomItems()
    {
        var path = OverlayItems.CustomPath;
        var folder = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        try
        {
            customItemsWatcher = new FileSystemWatcher(folder, System.IO.Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            void changed(object? sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(150);   // editors write in several steps
                viewModel.SetOverlayItems(OverlayItems.All, settingsService.OverlayDisplay);
            });
            customItemsWatcher.Changed += changed;
            customItemsWatcher.Created += changed;
            customItemsWatcher.Renamed += (sender, e) => changed(sender, e);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }

    private void OpenCustomItems(object? sender, RoutedEventArgs e)
    {
        OverlayItems.CustomPath = OverlayItems.CustomPath;   // recreates the example file if it was deleted
        try
        {
            Process.Start(new ProcessStartInfo { FileName = OverlayItems.CustomPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            OpenInExplorer($"/select,\"{OverlayItems.CustomPath}\"");
        }
    }

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
        watchStableReplays();
    }

    /// <summary>Watches osu!stable's Data/r while osu!stable is played, and rescans when a replay is written there.</summary>
    private void watchStableReplays()
    {
        var folder = currentSnapshot.IsStable && viewModel.StableFound
            ? System.IO.Path.Combine((viewModel.OsuStablePath ?? string.Empty).Trim(), "Data", "r")
            : string.Empty;
        if (string.Equals(folder, stableReplaysWatched, StringComparison.OrdinalIgnoreCase))
            return;

        stableReplaysWatcher?.Dispose();
        stableReplaysWatcher = null;
        stableReplaysWatched = folder;
        if (folder.Length == 0 || !Directory.Exists(folder))
            return;

        try
        {
            stableReplaysWatcher = new FileSystemWatcher(folder, "*.osr")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            // a replay is written in several steps: rescan once the folder has been quiet for a moment
            void written(object? sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(() =>
            {
                if (stableReplaysQuiet == null)
                {
                    stableReplaysQuiet = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
                    stableReplaysQuiet.Tick += (_, _) =>
                    {
                        stableReplaysQuiet.Stop();
                        if (currentSnapshot.IsStable)
                            RefreshReplays();
                    };
                }

                stableReplaysQuiet.Stop();
                stableReplaysQuiet.Start();
            });
            stableReplaysWatcher.Created += written;
            stableReplaysWatcher.Changed += written;
            stableReplaysWatcher.Renamed += (sender, e) => written(sender, e);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
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
            watchStableReplays();

            // A details page opened by hand stays open on another map; one a results screen opened belongs to it
            // (and closes with it, see OnResultsScreen).
            if ((snapshot.BeatmapChecksum != oldChecksum || snapshot.IsStable != oldStable) && detailsFromResults)
                CloseDetails();
            if (viewModel.Details is { } openDetails)
                openDetails.IsOnCurrentMap = detailsOnCurrentMap;

            var hasBeatmap = !string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum);
            viewModel.HasBeatmap = hasBeatmap;
            viewModel.BeatmapTitle = hasBeatmap ? FirstNonEmpty(snapshot.BeatmapTitle, snapshot.BeatmapChecksum) : "No beatmap selected";
            viewModel.BeatmapSubtitle = hasBeatmap
                ? string.Join(" · ", new[] { snapshot.BeatmapArtist, snapshot.BeatmapVersion }.Where(part => !string.IsNullOrWhiteSpace(part)))
                : $"Select a beatmap in {viewModel.GameName}";

            // (a details page opened by hand keeps its own map's background while it stays open on another map)
            if (snapshot.BeatmapChecksum != backgroundBeatmap && !(viewModel.Details != null && !detailsOnCurrentMap))
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

            OnResultsScreen(snapshot);
        });
    }

    private const string TosuBackgroundUrl = "http://" + TosuHost + "/files/beatmap/background";
    private static readonly HttpClient tosuHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Beatmap the shown (or loading) background belongs to, and where it is coming from ("" = not chosen yet).</summary>
    private string backgroundBeatmap = string.Empty;
    private string backgroundSource = string.Empty;
    // the osu!lazer database lookup's last answer, for a background chosen after it (a details page kept it waiting)
    private (string Beatmap, string Path)? databaseBackground;

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
        else if (databaseBackground is { } found && found.Beatmap == snapshot.BeatmapChecksum)
            LoadBackground(snapshot.BeatmapChecksum, string.IsNullOrWhiteSpace(found.Path) ? TosuBackgroundUrl : found.Path);
        else if (!viewModel.LazerFound)
            LoadBackground(snapshot.BeatmapChecksum, TosuBackgroundUrl);
    }

    /// <summary>Called with the database lookup's result for <paramref name="beatmap"/>.</summary>
    private void OnDatabaseBackground(string beatmap, string path)
    {
        databaseBackground = (beatmap, path);
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

        foreach (var replay in Sort(replays.Select(replay => new ReplayRow(replay, currentSnapshot.IsStable) { IsPinned = replaySelection.IsSelected(replay) })))
        {
            replay.PropertyChanged += ReplayRow_PropertyChanged;
            viewModel.ReplayRows.Add(replay);
        }

        fillingReplayList = false;
        viewModel.NotifyReplaysChanged();
        UpdateTarget();
        OpenPendingResults();
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
        if (viewModel.Details is { } details && detailsOnCurrentMap)
        {
            details.Target = target;
            details.IsPinned = pinned && target != null && string.Equals(target.FilePath, details.Entry.FilePath, StringComparison.OrdinalIgnoreCase);
        }

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
        // a scaled timeline is stretched by the Main overlay's item
        var mainItem = viewModel.OverlayDisplay.MainItem;
        var metric = MetricRegistry.Find(OverlayItems.ScaleMetric(mainItem)) ?? MetricRegistry.Default;
        var metricName = OverlayItems.NameOf(mainItem == OverlayItems.Off ? "score" : mainItem).ToLowerInvariant();
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
            var name = new TextBlock { Text = segment.Label, FontSize = 11, Classes = { "secondary" } };
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

    private void ShowComparePage(object? sender, RoutedEventArgs e)
    {
        // Compare again while on it: back to the replay list
        if (!viewModel.IsSettingsPage)
            CloseDetails();
        viewModel.IsSettingsPage = false;
    }

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

    // --- Replay details ---

    // (also on DoubleTapped: a quick second click on a row, e.g. right after going back, is a double tap)
    private void ReplayTapped(object? sender, TappedEventArgs e)
    {
        // the pin button keeps its own job
        if (e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) != null)
            return;

        if (source is StyledElement { DataContext: ReplayRow row })
            OpenDetails(row.Entry);
    }

    private void ReplayListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && replayList.SelectedItem is ReplayRow row)
            OpenDetails(row.Entry);
    }

    /// <summary>Whether the open details page belongs to the map (and game) being played.</summary>
    private bool detailsOnCurrentMap => detailsBeatmap == currentSnapshot.BeatmapChecksum && detailsStable == currentSnapshot.IsStable;

    private void OpenDetails(ReplayEntry entry, bool fromResults = false)
    {
        replayList.SelectedItem = null;
        detailsBeatmap = currentSnapshot.BeatmapChecksum;
        detailsStable = currentSnapshot.IsStable;
        detailsFromResults = fromResults;
        bool pinned;
        lock (replayLock)
            pinned = replaySelection.IsSelected(entry);
        var details = new ReplayDetailsViewModel(entry, viewModel.BeatmapTitle, viewModel.BeatmapSubtitle, viewModel.Target, pinned, scoreV1: currentSnapshot.IsStable);
        viewModel.Details = details;
        detailsView.ScrollToTop();

        if (!entry.HasReplay)
            return;

        var beatmapPath = BuildBeatmapPath(currentSnapshot);
        var key = $"{entry.FilePath}|{beatmapPath}";
        var cached = analyses.FindIndex(a => a.Key == key);
        if (cached >= 0)
        {
            details.SetAnalysis(analyses[cached].Analysis);
            return;
        }

        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
        {
            details.ErrorText = "The beatmap file isn't available yet, so the replay can't be analysed.";
            return;
        }

        details.IsLoading = true;
        _ = Task.Run(() =>
        {
            try
            {
                var analysis = ReplayAnalyzer.Analyze(entry.FilePath, beatmapPath);
                Dispatcher.UIThread.Post(() =>
                {
                    analyses.RemoveAll(a => a.Key == key);
                    analyses.Add((key, analysis));
                    if (analyses.Count > kept_analyses)
                        analyses.RemoveAt(0);

                    details.IsLoading = false;
                    details.SetAnalysis(analysis);
                });
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
                Dispatcher.UIThread.Post(() =>
                {
                    details.IsLoading = false;
                    details.ErrorText = $"Couldn't analyse the replay: {ex.Message}";
                });
            }
        });
    }

    private void CloseDetails()
    {
        viewModel.Details = null;
        detailsFromResults = false;

        // back to the map being played, if the page kept another map's background
        if (currentSnapshot.BeatmapChecksum != backgroundBeatmap)
            OnBackgroundBeatmapChanged(currentSnapshot);
    }

    /// <summary>
    /// A results screen (after a play, or a score opened from the leaderboard): opens its replay's details once the
    /// replay is in the list. A play that just ended may not be saved yet, so the list is rescanned a few times.
    /// </summary>
    private void OnResultsScreen(TosuSnapshot snapshot)
    {
        if (!snapshot.IsResultsScreen || snapshot.Results is not { } results)
        {
            pendingResults = null;
            if (!snapshot.IsResultsScreen)
            {
                openedResults = null;
                // leaving the results screen goes back to the replays, if the results screen opened the page
                if (detailsFromResults)
                    CloseDetails();
            }

            return;
        }

        if (!viewModel.OpenDetailsOnResults || results == openedResults || results == pendingResults)
            return;

        pendingResults = results;
        resultsRefreshes = 0;
        if (!OpenPendingResults())
            refreshForResults();
    }

    /// <summary>Opens the pending results screen's replay when it is listed; false when it is not (yet).</summary>
    private bool OpenPendingResults()
    {
        if (pendingResults is not { } results)
            return false;

        ReplayEntry? entry;
        lock (replayLock)
            entry = results.FindIn(currentReplays);

        if (entry == null)
            return false;

        pendingResults = null;
        openedResults = results;
        if (viewModel.Details?.Entry.FilePath != entry.FilePath || !detailsOnCurrentMap)
            OpenDetails(entry, fromResults: true);
        return true;
    }

    private void refreshForResults()
    {
        if (pendingResults == null || resultsRefreshes++ >= max_results_refreshes)
            return;

        RefreshReplays();
        DispatcherTimer.RunOnce(() =>
        {
            if (pendingResults != null && !OpenPendingResults())
                refreshForResults();
        }, TimeSpan.FromSeconds(2));
    }

    // --- Back and zoom ---

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key is Key.OemPlus or Key.Add)
                stepZoom(1);
            else if (e.Key is Key.OemMinus or Key.Subtract)
                stepZoom(-1);
            else if (e.Key is Key.D0 or Key.NumPad0)
                applyZoom(1, save: true);
            else
                return;

            e.Handled = true;
            return;
        }

        if (!viewModel.ShowDetailsPage)
            return;

        // typing in a box or choosing in an open list keeps its keys
        var focused = FocusManager?.GetFocusedElement();
        bool editing = focused is TextBox || focused is ComboBox { IsDropDownOpen: true };
        bool back = e.Key == Key.BrowserBack
                    || (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt)
                    || (!editing && e.Key is Key.Back or Key.Escape);
        if (!back)
            return;

        CloseDetails();
        e.Handled = true;
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // the mouse's back button
        if (viewModel.ShowDetailsPage && e.GetCurrentPoint(this).Properties.IsXButton1Pressed)
        {
            CloseDetails();
            e.Handled = true;
        }
    }

    private void OnWindowPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0)
            return;

        stepZoom(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void ZoomIn(object? sender, RoutedEventArgs e) => stepZoom(1);

    private void ZoomOut(object? sender, RoutedEventArgs e) => stepZoom(-1);

    private void ResetZoom(object? sender, RoutedEventArgs e) => applyZoom(1, save: true);

    private void stepZoom(int direction)
    {
        var current = viewModel.Zoom;
        var next = direction > 0
            ? zoom_steps.FirstOrDefault(step => step > current + 0.001, zoom_steps[^1])
            : zoom_steps.LastOrDefault(step => step < current - 0.001, zoom_steps[0]);
        applyZoom(next, save: true);
    }

    private void applyZoom(double zoom, bool save = false)
    {
        zoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 1, zoom_steps[0], zoom_steps[^1]);
        viewModel.Zoom = zoom;
        pageZoom.LayoutTransform = Math.Abs(zoom - 1) < 0.001 ? null : new ScaleTransform(zoom, zoom);
        layoutBanner();
        if (save)
            settingsService.SetZoom(zoom);
    }

    /// <summary>
    /// Sizes the banner to the page zoom. The sharp image is only as wide as the page's content (and a fade); in a
    /// wider window the sides show the page's blurred copy, less dimmed, fading out towards the window's edges.
    /// </summary>
    private void layoutBanner()
    {
        if (banner == null)
            return;

        var zoom = viewModel.Zoom;
        var width = Bounds.Width;
        var fade = banner_side_fade * zoom;
        var column = Math.Min(width, page_content_width * zoom + fade);
        bannerColumn.Width = column;

        // tall enough for the whole image across the column (no taller than 4:3, nor than the window)
        var aspect = viewModel.BackgroundImage is { Size: { Width: > 0 } size } ? Math.Min(size.Height / size.Width, 0.75) : 9 / 16.0;
        banner.Height = Math.Max(banner_min_height * zoom, Math.Min(column * aspect, Bounds.Height));
        banner.OpacityMask = fadeDown(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative));

        var side = (width - column) / 2;
        bannerSides.IsVisible = side > 1;
        if (side <= 1 || column <= 0)
        {
            bannerColumn.OpacityMask = null;
            return;
        }

        var edge = Math.Min(fade / column, 0.25);
        bannerColumn.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, edge),
                new GradientStop(Colors.Black, 1 - edge),
                new GradientStop(Colors.Transparent, 1),
            },
        };

        // the blurred sides: full behind the sharp image's fade, gone at the window's edges
        var inner = (side + fade / 2) / width;
        bannerSidesImage.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, inner),
                new GradientStop(Colors.Black, 1 - inner),
                new GradientStop(Colors.Transparent, 1),
            },
        };
        fadeBannerSides();
    }

    /// <summary>
    /// Fades the blurred sides the way the banner fades (they sit in the page's backdrop, outside the banner): out
    /// towards its bottom edge, which moves up as the page scrolls, and away as it scrolls further.
    /// </summary>
    private void fadeBannerSides()
    {
        if (!bannerSides.IsVisible)
            return;

        // the banner's own fade, at the banner's place in the window
        var top = -bannerScroll * viewModel.Zoom;
        bannerSides.Opacity = banner.Opacity;
        bannerSides.OpacityMask = fadeDown(new RelativePoint(0, top, RelativeUnit.Absolute), new RelativePoint(0, top + banner.Height, RelativeUnit.Absolute));
    }

    /// <summary>The banner's fade (<see cref="banner_fade"/>) from <paramref name="top"/> down to <paramref name="bottom"/>.</summary>
    private static LinearGradientBrush fadeDown(RelativePoint top, RelativePoint bottom)
    {
        var brush = new LinearGradientBrush { StartPoint = top, EndPoint = bottom };
        foreach (var (offset, alpha) in banner_fade)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 0, 0, 0), offset));
        return brush;
    }

    private void setNarrowPage(bool narrow)
    {
        if (narrow == narrowPage)
            return;

        narrowPage = narrow;
        // target card: name | score over judgements | timeline status
        ResponsiveLayout.Apply(targetRow, narrow, "*,Auto", (0, 0), (0, 1), (1, 0), (1, 1));
        applyReplayColumns(replayHeader);
        foreach (var container in replayList.GetRealizedContainers())
            applyReplayColumns(container);
    }

    // The replay list's columns (header and rows): pin, player, date, mods, score, vs target, accuracy, miss, combo,
    // rank, chevron. A narrow page drops date, vs target and combo.
    private const string replay_columns = "52,1.2*,0.9*,1.5*,1*,0.9*,0.8*,0.55*,0.65*,0.55*,36";
    private const string narrow_replay_columns = "52,1.5*,0,1.4*,1*,0,0.9*,0.6*,0,0.6*,36";
    private static readonly int[] narrow_hidden_columns = [2, 5, 8];

    private void applyReplayColumns(Control control)
    {
        var grid = control as Grid ?? control.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Classes.Contains("replayCells"));
        if (grid == null)
            return;

        grid.ColumnDefinitions = new ColumnDefinitions(narrowPage ? narrow_replay_columns : replay_columns);
        foreach (var cell in grid.Children)
            cell.IsVisible = !narrowPage || !narrow_hidden_columns.Contains(Grid.GetColumn(cell));
    }

    /// <summary>Pins the replay as the comparison target, or unpins it when it already is.</summary>
    private void TogglePin(ReplayEntry entry)
    {
        // (by file: a rescan lists the same replays as new entries)
        var row = viewModel.ReplayRows.FirstOrDefault(r => string.Equals(r.Entry.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
        if (row is { HasReplay: true })
            row.IsPinned = !row.IsPinned;   // the row's handler updates the target (and the open details page)
    }

    private static void ShowReplayFile(ReplayEntry entry)
    {
        if (File.Exists(entry.FilePath))
            OpenInExplorer($"/select,\"{entry.FilePath}\"");
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
