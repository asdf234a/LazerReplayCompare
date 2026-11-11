using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LazerReplayCompare;

// The library, browsing a map the game hasn't selected, the online ranking and the pacemaker's rivals (3.0).
public sealed partial class MainWindow
{
    private readonly LibraryService library = new();
    private LibraryViewModel libraryModel = null!;
    private LibraryView libraryPage = null!;
    private DanEngine? danEngine;
    private DispatcherTimer? libraryRefresh;

    // the map the game has selected; currentSnapshot is the map the page shows, which is another one while browsing
    private TosuSnapshot liveSnapshot = new("lazer", "", "", "", "", "", "", "", "");
    private bool browsing;

    // the online ranking of the shown map, and a downloaded replay to pin once it is listed
    private IReadOnlyList<OnlineScore> onlineScores = [];
    private string onlineScoresMd5 = string.Empty;
    private string pendingPin = string.Empty;

    // downloaded replays' scores by file (read once)
    private readonly Dictionary<string, ReplayEntry> downloadedEntries = new(StringComparer.OrdinalIgnoreCase);

    private void setUpLibrary()
    {
        libraryModel = new LibraryViewModel(library);
        viewModel.Library = libraryModel;
        libraryPage = this.FindControl<LibraryView>("LibraryPage")!;
        // the library goes to the map the game is on (whatever hides it is dropped)
        libraryPage.NowPlayingChosen += () => libraryModel.Locate(liveSnapshot.BeatmapChecksum, force: true);
        libraryPage.MapChosen += row =>
        {
            BrowseTo(row.Map);
            mapOpenedFromLibrary = true;   // Back returns to the library
        };

        // a page arrives from the side it is on: going in (library → map → replay) it rises into place, going back it
        // comes down; Settings, beside them all, just rises a little
        var pages = new (string Property, Control Page, int Depth)[]
        {
            (nameof(MainWindowViewModel.ShowLibraryPage), this.FindControl<Control>("LibraryPanel")!, 0),
            (nameof(MainWindowViewModel.ShowCompareList), this.FindControl<Control>("ComparePage")!, 1),
            (nameof(MainWindowViewModel.ShowDetailsPage), this.FindControl<Control>("DetailsPanel")!, 2),
            (nameof(MainWindowViewModel.IsSettingsPage), this.FindControl<Control>("SettingsPage")!, -1),
        };
        int shownDepth = 1;
        viewModel.PropertyChanged += (_, e) =>
        {
            foreach (var (property, page, depth) in pages)
            {
                if (e.PropertyName != property || !page.IsVisible)
                    continue;

                if (depth < 0 || shownDepth < 0)
                    Motion.Enter(page, 14, 1, 220);
                else if (depth > shownDepth)
                    Motion.Enter(page, 26, 0.98, 260);
                else if (depth < shownDepth)
                    Motion.Enter(page, -18, 1.015, 260);
                shownDepth = depth;
            }

            if (e.PropertyName == nameof(MainWindowViewModel.MapCard))
                rollNumber();
            else if (e.PropertyName == nameof(MainWindowViewModel.IsRailExpanded) && viewModel.IsRailExpanded)
            {
                // (opened: its list was not laid out while it was folded)
                railCentred = string.Empty;
                centreRail();
                moveRail();
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsRailExpanded))
                moveRail();
        };
        rollNumber();

        libraryModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.Rows))
                centreRail();
        };

        var settings = settingsService.Current;
        libraryModel.Restore(settings.LibraryGroupBy, settings.LibraryNumber, settings.LibraryKeys, settings.LibraryRate);
        libraryModel.ShowRowBackgrounds = settings.RowBackgrounds;
        libraryModel.OptionsChanged += () =>
        {
            settings.LibraryGroupBy = libraryModel.GroupByKey;
            settings.LibraryNumber = libraryModel.NumberKind;
            settings.LibraryKeys = libraryModel.KeysKey;
            settings.LibraryRate = libraryModel.RateKey;
            settingsService.Save();
            updateMapCard();
        };

        // maps, ratings and records come in from background threads: the lists follow a moment later, all at once
        library.Changed += () => Dispatcher.UIThread.Post(scheduleLibraryRefresh);
        library.ModdedChanged += () => Dispatcher.UIThread.Post(updateMapCard);
        library.Sunny.Busy = () => isPlaying(Volatile.Read(ref liveSnapshot));
        // progress only moves the numbers in the top bar; the lists are made again when new ratings are in
        library.Sunny.Progress += scheduleWorkStatus;
        applyBackgroundSpeed();
        library.Maps.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            updateRecords();
            startDanEngine();
        });
    }

    private static bool isPlaying(TosuSnapshot snapshot) => snapshot.State.Equals("play", StringComparison.OrdinalIgnoreCase);

    private void scheduleLibraryRefresh()
    {
        if (libraryRefresh == null)
        {
            libraryRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            libraryRefresh.Tick += (_, _) =>
            {
                libraryRefresh.Stop();
                refreshLibraryNow();
            };
        }

        if (!libraryRefresh.IsEnabled)
        {
            // while thousands of maps are being rated, the lists are made again only every few seconds
            bool working = library.Sunny.Status.Running || danEngine?.Status.Running == true;
            libraryRefresh.Interval = TimeSpan.FromSeconds(working ? 12 : 0.5);
            libraryRefresh.Start();
        }
    }

    private int workStatusQueued;

    /// <summary>Shows how the background work stands, at most a few times a second (called from the workers' threads).</summary>
    private void scheduleWorkStatus()
    {
        if (Interlocked.Exchange(ref workStatusQueued, 1) == 1)
            return;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(400);
                Volatile.Write(ref workStatusQueued, 0);
                updateWorkStatus();
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
            }
        }, DispatcherPriority.Background);
    }

    private void updateWorkStatus()
    {
        var sunny = library.Sunny.Status;
        var dans = danEngine?.Status ?? WorkProgress.Idle;
        bool reading = library.Maps.Loading && library.Maps.Maps.Count == 0;

        static string remaining(WorkProgress work) => work.Remaining is { } left && left > TimeSpan.Zero
            ? left.TotalMinutes >= 1 ? $" · about {Math.Ceiling(left.TotalMinutes):0} min left" : " · under a minute left"
            : string.Empty;

        if (reading)
        {
            viewModel.WorkText = "Reading the libraries…";
            viewModel.WorkPercent = 0;
            viewModel.WorkDetails = "Reading osu!lazer's and osu!stable's beatmap lists (once; they are kept for the next start).";
        }
        else if (sunny.Running)
        {
            viewModel.WorkText = sunny.Checking ? "Checking maps…" : $"Rating maps {sunny.Done:N0} / {sunny.Total:N0}{(sunny.Paused ? " (paused)" : "")}";
            viewModel.WorkPercent = sunny.Fraction * 100;
            viewModel.WorkDetails = $"Working out each map's Sunny star rating in the background{remaining(sunny)}.\nIt pauses while you play, and what is done is kept, so it only happens once per map."
                                    + (dans.Running ? $"\nDan estimates: {dans.Done:N0} / {dans.Total:N0}." : string.Empty)
                                    + "\nSettings → Library sets how much of the CPU it may take.";
        }
        else if (dans.Running)
        {
            viewModel.WorkText = $"Estimating dans {dans.Done:N0} / {dans.Total:N0}{(dans.Paused ? " (paused)" : "")}";
            viewModel.WorkPercent = dans.Fraction * 100;
            viewModel.WorkDetails = $"Estimating each 4K, 6K and 7K map's dan with the analyser, in a hidden browser{remaining(dans)}.\nIt pauses while you play, and what is done is kept.\nSettings → Library sets how much of the CPU it may take.";
        }
        else
        {
            viewModel.WorkText = string.Empty;
            viewModel.WorkPercent = 0;
        }
    }

    /// <summary>How many threads the ratings and the dan estimates may use, from the setting (applied to runs that start after it).</summary>
    private void applyBackgroundSpeed()
    {
        int cores = Environment.ProcessorCount;
        int threads = viewModel.BackgroundSpeed switch
        {
            "fast" => cores,
            "normal" => Math.Max(1, cores / 2),
            _ => Math.Clamp(cores / 4, 1, 6),
        };

        library.Sunny.Threads = threads;
        if (danEngine != null)
            danEngine.Workers = threads;
    }

    private void refreshLibraryNow()
    {
        updateWorkStatus();
        libraryModel.Glass = viewModel.ShowBackdrop;
        libraryModel.Reload();
        viewModel.ShowRail = library.Maps.Maps.Count > 0;
        updateMapCard();

        var sunny = library.Sunny.Status;
        var parts = new List<string> { library.Maps.Status };
        if (sunny.Running)
            parts.Add($"Sunny ratings {sunny.Done:N0} / {sunny.Total:N0}{(sunny.Paused ? " (paused while you play)" : "")}");
        if (danEngine != null)
        {
            var dans = danEngine.Status;
            parts.Add(danEngine.Unavailable is { } why ? $"Dan estimates unavailable: {why}"
                : dans.Running ? $"Dan estimates {dans.Done:N0} / {dans.Total:N0}{(dans.Paused ? " (paused while you play)" : "")}"
                : "Dan estimates up to date");
        }

        viewModel.LibraryStatus = string.Join(" · ", parts);
    }

    /// <summary>Reads the games' libraries (the kept list shows first). Called once the folders are known.</summary>
    private void startLibrary()
    {
        library.Maps.Start((viewModel.OsuLazerPath ?? string.Empty).Trim(), (viewModel.OsuStablePath ?? string.Empty).Trim());
        updateRecords();
    }

    private void RefreshLibrary(object? sender, RoutedEventArgs e)
    {
        library.Maps.Refresh((viewModel.OsuLazerPath ?? string.Empty).Trim(), (viewModel.OsuStablePath ?? string.Empty).Trim());
        updateRecords();
    }

    /// <summary>The player's best play of each map, from every local score (osu!lazer's kept replays and osu!stable's scores.db).</summary>
    private void updateRecords()
    {
        var stableRoot = (viewModel.OsuStablePath ?? string.Empty).Trim();
        bool stable = OsuStablePathResolver.IsOsuStableRoot(stableRoot);
        library.UpdateRecords(() =>
        {
            var scores = lazerReplays.All();
            if (stable)
            {
                try
                {
                    scores.AddRange(stableReplayFinder.AllScores(stableRoot));
                }
                catch (Exception ex)
                {
                    InternalLogger.Log(ex);
                }
            }

            return scores;
        });
    }

    private void startDanEngine()
    {
        if (!viewModel.UseDanEngine)
            return;

        try
        {
            if (danEngine == null)
            {
                danEngine = new DanEngine { Busy = () => isPlaying(Volatile.Read(ref liveSnapshot)) };
                danEngine.Changed += () => Dispatcher.UIThread.Post(scheduleLibraryRefresh);
                danEngine.Progress += scheduleWorkStatus;
                applyBackgroundSpeed();
                var engine = danEngine;
                library.DanLookup = md5 => engine.Get(md5) is { } estimate ? new DanLookupResult(estimate.Rc, estimate.Ln, estimate.Numeric) : null;
                library.DanAtRate = async (map, rate, odFlag) =>
                    engine.Unavailable == null && await engine.AtRateAsync(map, rate, odFlag) is { } estimate ? new DanLookupResult(estimate.Rc, estimate.Ln, estimate.Numeric) : null;
            }

            if (danEngine.Unavailable == null)
                danEngine.Update(library.Maps.Maps);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }

    // --- Pages ---

    private void OpenLibrary(object? sender, RoutedEventArgs e) => openLibrary();

    private void openLibrary(bool focusSearch = false)
    {
        viewModel.IsSettingsPage = false;
        viewModel.IsLibraryOpen = true;
        libraryModel.Glass = viewModel.ShowBackdrop;
        // it opens on the shown map's group; from there it only moves when asked to (Now playing)
        libraryModel.SelectShownGroup();
        if (libraryModel.RowOf(currentSnapshot.BeatmapChecksum) is { } row)
            Dispatcher.UIThread.Post(() => libraryPage.ScrollTo(row), DispatcherPriority.Loaded);
        if (focusSearch)
            Dispatcher.UIThread.Post(libraryPage.FocusSearch, DispatcherPriority.Loaded);
    }

    private void closeLibrary() => viewModel.IsLibraryOpen = false;

    private void ToggleRail(object? sender, RoutedEventArgs e) => viewModel.IsRailExpanded = !viewModel.IsRailExpanded;

    private void RailGroupClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LibraryGroup group })
        {
            libraryModel.SelectedGroup = group;
            viewModel.IsRailExpanded = true;
        }
    }

    private void ClearLibraryFilters(object? sender, RoutedEventArgs e) => libraryModel.ClearOtherFilters();

    private void RailMapTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is StyledElement { DataContext: MapRow row })
        {
            BrowseTo(row.Map);
            mapOpenedFromLibrary = false;
        }
    }

    // --- Browsing: the map page on a map picked in the library ---

    /// <summary>
    /// A snapshot from tosu. False while the page stays on a map picked in the library; a play that starts (or a results
    /// screen that opens) takes the page back to the game's map.
    /// </summary>
    private bool onLiveSnapshot(TosuSnapshot snapshot)
    {
        var previous = liveSnapshot;
        Volatile.Write(ref liveSnapshot, snapshot);
        libraryModel.NowPlayingMd5 = snapshot.BeatmapChecksum;
        libraryModel.NowPlayingText = string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum) ? string.Empty
            : string.Join(" ", new[] { snapshot.BeatmapTitle, string.IsNullOrWhiteSpace(snapshot.BeatmapVersion) ? string.Empty : $"[{snapshot.BeatmapVersion}]" }.Where(part => part.Length > 0));

        // other rate or OD mods in the game: the shown map's difficulty follows
        if (Environment.GetEnvironmentVariable("LRC_DEBUG_MODS") is not { Length: > 0 }
            && (Math.Abs(snapshot.PlaySpeed.Rate - shownMods.Rate) >= 0.005 || snapshot.OdFlag != shownMods.OdFlag))
            Dispatcher.UIThread.Post(updateMapCard);

        if (!browsing)
            return true;

        bool started = (isPlaying(snapshot) && !isPlaying(previous)) || (snapshot.IsResultsScreen && !previous.IsResultsScreen);
        if (!started)
        {
            if (snapshot.BeatmapChecksum != previous.BeatmapChecksum)
                updateMapCard();
            return false;
        }

        browsing = false;
        viewModel.IsBrowsingMap = false;
        closeLibrary();
        return true;
    }

    private void BrowseTo(MapInfo map)
    {
        closeLibrary();
        viewModel.Details = null;
        detailsFromResults = false;

        if (string.Equals(map.Md5, liveSnapshot.BeatmapChecksum, StringComparison.OrdinalIgnoreCase))
        {
            backToLiveMap();
            return;
        }

        browsing = true;
        viewModel.IsBrowsingMap = true;
        showSnapshot(snapshotOf(map));
    }

    private void BackToLiveMap(object? sender, RoutedEventArgs e) => backToLiveMap();

    private void backToLiveMap()
    {
        if (!browsing && currentSnapshot.BeatmapChecksum == liveSnapshot.BeatmapChecksum)
            return;

        browsing = false;
        viewModel.IsBrowsingMap = false;
        showSnapshot(liveSnapshot);
    }

    /// <summary>A map of the library in the shape tosu reports the game's map, so the page shows both the same way.</summary>
    private TosuSnapshot snapshotOf(MapInfo map)
    {
        bool stable = !map.Clients.Contains("lazer", StringComparison.OrdinalIgnoreCase);
        var folder = System.IO.Path.GetDirectoryName(map.ChartPath) ?? string.Empty;
        var file = System.IO.Path.GetFileName(map.ChartPath);

        // osu!lazer keeps files flat under their hashes; osu!stable keeps each set in its own folder under Songs
        var songs = stable ? System.IO.Path.GetDirectoryName(folder) ?? string.Empty : folder;
        return new TosuSnapshot(
            Client: stable ? "stable" : "lazer",
            State: "menu",
            BeatmapChecksum: map.Md5,
            BeatmapArtist: map.ArtistEnglish,
            BeatmapTitle: map.TitleEnglish,
            BeatmapVersion: map.Version,
            SongsFolder: songs,
            BeatmapFolder: stable ? System.IO.Path.GetFileName(folder) : string.Empty,
            BeatmapFile: file,
            DirectBackgroundPath: map.BackgroundPath,
            Speed: new PlaySpeed(1, false, stable));
    }

    // the library a map was opened from: Back (Backspace, Esc, the mouse's back button) returns to it
    private bool mapOpenedFromLibrary;
    private string fadedMap = string.Empty;

    private double shownNumber = double.NaN;

    /// <summary>The map card's big number rolls from the value it showed to the new one, its colour with it.</summary>
    private void rollNumber()
    {
        var label = this.FindControl<TextBlock>("MapNumber")!;
        var box = this.FindControl<Border>("MapNumberBox")!;
        var card = viewModel.MapCard;
        string text = card?.NumberText ?? "–";
        var boxBrush = card?.NumberBadge ?? DifficultyColors.Badge(double.NaN);
        var textBrush = card?.NumberBadgeText ?? DifficultyColors.Neutral;
        string mark = text.StartsWith('*') ? "*" : string.Empty;

        bool number = double.TryParse(text.TrimStart('*'), NumberStyles.Float, CultureInfo.InvariantCulture, out var target);
        double from = double.IsNaN(shownNumber) ? 0 : shownNumber;
        if (!number || !viewModel.ShowCompareList || Math.Abs(from - target) < 0.005)
        {
            Motion.Run(label, "roll", 1, _ => { });   // (stops one that is rolling)
            label.Text = text;
            label.Foreground = textBrush;
            box.Background = boxBrush;
            shownNumber = number ? target : double.NaN;
            return;
        }

        static Avalonia.Media.Color colorOf(Avalonia.Media.IBrush? brush, Avalonia.Media.Color fallback) => (brush as Avalonia.Media.ISolidColorBrush)?.Color ?? fallback;
        var toBox = colorOf(boxBrush, Avalonia.Media.Colors.Gray);
        var fromBox = colorOf(box.Background, toBox);
        var toText = colorOf(textBrush, Avalonia.Media.Colors.Black);
        var fromText = colorOf(label.Foreground, toText);
        Motion.Run(label, "roll", 420, t =>
        {
            shownNumber = from + (target - from) * t;
            label.Text = t >= 1 ? text : mark + shownNumber.ToString("0.00", CultureInfo.InvariantCulture);
            label.Foreground = t >= 1 ? textBrush : new Avalonia.Media.SolidColorBrush(Motion.Mix(fromText, toText, t));
            box.Background = t >= 1 ? boxBrush : new Avalonia.Media.SolidColorBrush(Motion.Mix(fromBox, toBox, t));
        });
    }

    /// <summary>The map card's bars fill from nothing, one after the other.</summary>
    private void growBars()
    {
        int index = 0;
        foreach (var name in new[] { "ContentStatsList", "SettingStatsList" })
        {
            if (this.FindControl<Control>(name) is not { } list)
                continue;

            foreach (var bar in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<ProgressBar>())
            {
                if (bar.DataContext is not MapStat stat)
                    continue;

                double target = stat.Percent;
                Motion.Run(bar, "grow", 420, t => bar.SetCurrentValue(Avalonia.Controls.Primitives.RangeBase.ValueProperty, target * t), delay: 40 * index++);
            }
        }
    }

    /// <summary>The side card widens or narrows to its other form instead of jumping, what it holds fading in.</summary>
    private void moveRail()
    {
        var rail = this.FindControl<Border>("LibraryRail")!;
        var shown = this.FindControl<Control>(viewModel.IsRailExpanded ? "RailOpened" : "RailFolded")!;
        double from = rail.Bounds.Width, to = viewModel.IsRailExpanded ? 304 : 92;
        if (!rail.IsVisible || from <= 0 || !viewModel.ShowCompareList)
            return;

        Motion.Run(rail, "width", 260, t => rail.Width = from + (to - from) * t, () => rail.Width = double.NaN);
        Motion.Run(shown, "fade", 300, t => shown.Opacity = t);
    }

    private void OpenInOsu(object? sender, RoutedEventArgs e)
    {
        if (viewModel.MapCard is not { BeatmapId: > 0 } card)
            return;

        try
        {
            // osu!lazer shows the beatmap's info, osu!stable opens it in osu!direct
            Process.Start(new ProcessStartInfo { FileName = $"osu://b/{card.BeatmapId}", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }

    /// <summary>The shown map changed (the game's, or one picked in the library): the card, the tabs and the online ranking follow.</summary>
    private void onShownMapChanged()
    {
        var md5 = currentSnapshot.BeatmapChecksum;
        if (md5 != fadedMap)
        {
            // another map: its page settles in (from part way, so browsing song select doesn't flicker) and its bars fill
            fadedMap = md5;
            if (viewModel.ShowCompareList && this.FindControl<Control>("CompareContent") is { } content)
            {
                Motion.Enter(content, 8, 1, 260, 0.3);
                Dispatcher.UIThread.Post(growBars, DispatcherPriority.Loaded);
            }
        }

        if (!string.IsNullOrEmpty(md5) && library.Maps.Find(md5) == null && !library.Maps.Loading)
        {
            // a map the library hasn't read yet (just downloaded): read the games' libraries again, not more than twice a minute
            library.Maps.Refresh((viewModel.OsuLazerPath ?? string.Empty).Trim(), (viewModel.OsuStablePath ?? string.Empty).Trim(), TimeSpan.FromSeconds(30));
        }

        updateMapCard();
        viewModel.NotifyTabsChanged();

        if (md5 != onlineScoresMd5)
        {
            onlineScores = [];
            onlineScoresMd5 = md5;
            viewModel.OnlineRows.Clear();
            viewModel.OnlineSummary = string.Empty;
            viewModel.OnlineNote = string.Empty;
            viewModel.NotifyTabsChanged();
            if (viewModel.IsOnlineTab || viewModel.RivalsMode != "local")
                loadOnlineSoon();
        }
    }

    private DispatcherTimer? onlineTimer;

    /// <summary>
    /// Asks for the shown map's ranking once it has stayed shown a moment, so going through song select doesn't ask
    /// osu! about every map passed on the way.
    /// </summary>
    private void loadOnlineSoon()
    {
        onlineTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        if (onlineTimer.Tag == null)
        {
            onlineTimer.Tag = this;
            onlineTimer.Tick += (_, _) =>
            {
                onlineTimer.Stop();
                loadOnline();
            };
        }

        onlineTimer.Stop();
        onlineTimer.Start();
    }

    private (double Rate, string? OdFlag) shownMods = (1, null);

    private void updateMapCard()
    {
        var md5 = currentSnapshot.BeatmapChecksum;
        var map = string.IsNullOrEmpty(md5) ? null : library.Maps.Find(md5);
        if (map != null)
            library.EnsureRated(map);   // the shown map doesn't wait for its turn among thousands
        var live = string.IsNullOrWhiteSpace(liveSnapshot.BeatmapChecksum) ? "nothing yet"
            : string.Join(" ", new[] { liveSnapshot.BeatmapTitle, string.IsNullOrWhiteSpace(liveSnapshot.BeatmapVersion) ? string.Empty : $"[{liveSnapshot.BeatmapVersion}]" }.Where(part => part.Length > 0));

        // the rate and OD mods selected in the game change how hard the map is; until that is worked out the plain
        // difficulty stands in
        double rate = liveSnapshot.PlaySpeed.Rate;
        string? odFlag = liveSnapshot.OdFlag;
        if (Environment.GetEnvironmentVariable("LRC_DEBUG_MODS") is { Length: > 0 } debugMods)
        {
            // (for looking at the card without the game: "1.2|9", "1.5|HR", "1.1|")
            var parts = debugMods.Split('|');
            rate = double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var debugRate) ? debugRate : 1;
            odFlag = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        }

        var modded = map == null ? null : library.DifficultyAt(map, rate, odFlag);
        shownMods = (rate, odFlag);

        viewModel.MapCard = new MapCardViewModel(
            map,
            map == null ? null : modded ?? library.DifficultyOf(map),
            map == null ? null : library.RecordOf(md5),
            libraryModel.NumberKind,
            viewModel.BeatmapTitle,
            viewModel.BeatmapSubtitle,
            browsing,
            live,
            rate,
            odFlag,
            modsPending: map != null && modded == null && (Math.Abs(rate - 1) >= 0.005 || odFlag != null),
            waiting: map != null && modded == null);
        libraryModel.ShownMd5 = md5;
        centreRail();
    }

    private string railCentred = string.Empty;

    /// <summary>The side card's list goes to the shown map, mid-list (once per map and group, so it is not pulled back while it is scrolled).</summary>
    private void centreRail()
    {
        var md5 = libraryModel.ShownMd5;
        int index = libraryModel.Rows.ToList().FindIndex(row => string.Equals(row.Md5, md5, StringComparison.OrdinalIgnoreCase));
        string key = $"{md5}|{libraryModel.GroupTitle}|{index >= 0}";
        if (index < 0 || key == railCentred)
            return;

        railCentred = key;
        Motion.CentreRow(this.FindControl<ListBox>("RailList")!, index, 46);
    }

    // --- Local replays: downloaded ones, places ---

    private static string downloadPath(string md5, long scoreId) => System.IO.Path.Combine(AppPaths.Downloads, md5.ToLowerInvariant(), $"{scoreId}.osr");

    /// <summary>The replays downloaded for a beatmap (their scores are read from the files, once).</summary>
    private List<ReplayEntry> downloadedReplays(string md5, string beatmapPath)
    {
        var result = new List<ReplayEntry>();
        var folder = System.IO.Path.Combine(AppPaths.Downloads, md5.ToLowerInvariant());
        if (string.IsNullOrWhiteSpace(md5) || !Directory.Exists(folder) || !File.Exists(beatmapPath))
            return result;

        foreach (var file in Directory.EnumerateFiles(folder, "*.osr"))
        {
            try
            {
                ReplayEntry? entry;
                lock (downloadedEntries)
                    downloadedEntries.TryGetValue(file, out entry);

                if (entry == null)
                {
                    entry = ReplayFinder.ReadReplay(file, beatmapPath);
                    lock (downloadedEntries)
                        downloadedEntries[file] = entry;
                }

                result.Add(entry);
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
            }
        }

        return result;
    }

    /// <summary>Numbers the listed replays by score and says where the player's best stands.</summary>
    private void updateLocalPlaces()
    {
        var byScore = viewModel.ReplayRows.OrderByDescending(row => row.Entry.Score).ToList();
        for (int i = 0; i < byScore.Count; i++)
            byScore[i].Position = i + 1;

        var mine = byScore.FirstOrDefault(row => string.Equals(row.Player, library.Player, StringComparison.OrdinalIgnoreCase));
        viewModel.LocalSummary = mine == null || byScore.Count < 2 ? string.Empty
            : mine.Position == 1 ? $"Your best is #1 of {byScore.Count} here"
            : $"Your best is #{mine.Position} of {byScore.Count} here · {(byScore[0].Entry.Score - mine.Entry.Score).ToString("N0", CultureInfo.InvariantCulture)} behind #1";
        viewModel.NotifyTabsChanged();

        // a replay that was just downloaded becomes the target
        if (pendingPin.Length > 0 && viewModel.ReplayRows.FirstOrDefault(row => string.Equals(row.Entry.FilePath, pendingPin, StringComparison.OrdinalIgnoreCase)) is { HasReplay: true } downloaded)
        {
            pendingPin = string.Empty;
            if (!downloaded.IsPinned)
                downloaded.IsPinned = true;
        }

        markOnlineTarget();
    }

    // --- Online ranking ---

    private void ShowLocalTab(object? sender, RoutedEventArgs e) => viewModel.IsOnlineTab = false;

    private void ShowOnlineTab(object? sender, RoutedEventArgs e)
    {
        viewModel.IsOnlineTab = true;
        if (viewModel.OnlineRows.Count == 0)
            loadOnline();
    }

    private async void loadOnline()
    {
        try
        {
            var md5 = currentSnapshot.BeatmapChecksum;
            var map = string.IsNullOrEmpty(md5) ? null : library.Maps.Find(md5);
            if (string.IsNullOrEmpty(md5))
                return;

            if (!leaderboard.HasClient)
            {
                viewModel.OnlineNote = "Enter an osu! API client in Settings → osu! API to see this map's online ranking.";
                return;
            }

            if (map == null || map.BeatmapId <= 0)
            {
                viewModel.OnlineNote = map == null ? "The library hasn't read this map yet." : "This map isn't on osu!'s website, so it has no online ranking.";
                return;
            }

            viewModel.OnlineNote = "Asking osu! for the ranking…";
            var scores = await leaderboard.GetDetailedAsync(map.BeatmapId);
            if (md5 != currentSnapshot.BeatmapChecksum)
                return;

            onlineScores = scores;
            onlineScoresMd5 = md5;
            viewModel.OnlineRows.Clear();
            foreach (var score in scores)
            {
                viewModel.OnlineRows.Add(new OnlineScoreRow(score, string.Equals(score.Player, library.Player, StringComparison.OrdinalIgnoreCase))
                {
                    IsSaved = File.Exists(downloadPath(md5, score.ScoreId)),
                });
            }

            viewModel.OnlineNote = scores.Count == 0 ? "No scores came back (no leaderboard, or osu! couldn't be reached). It is asked again in a minute." : string.Empty;

            var mine = viewModel.OnlineRows.FirstOrDefault(row => row.IsUser);
            viewModel.OnlineSummary = mine == null ? (scores.Count > 0 ? $"You aren't in the top {scores.Count}" : string.Empty)
                : mine.Score.Position == 1 ? "You are #1"
                : $"You are #{mine.Score.Position} · {(scores[mine.Score.Position - 2].TotalScore - mine.Score.TotalScore).ToString("N0", CultureInfo.InvariantCulture)} behind #{mine.Score.Position - 1}";
            viewModel.NotifyTabsChanged();
            markOnlineTarget();
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            viewModel.OnlineNote = $"Couldn't read the ranking: {ex.Message}";
        }
    }

    private void markOnlineTarget()
    {
        var target = viewModel.Target?.FilePath ?? string.Empty;
        var md5 = currentSnapshot.BeatmapChecksum;
        foreach (var row in viewModel.OnlineRows)
            row.IsTarget = target.Length > 0 && string.Equals(target, downloadPath(md5, row.Score.ScoreId), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A click on an online score: its replay is downloaded (that one only, once) and becomes the comparison target.</summary>
    private async void OnlineTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not StyledElement { DataContext: OnlineScoreRow row } || row.IsBusy)
            return;

        try
        {
            var md5 = currentSnapshot.BeatmapChecksum;
            var path = downloadPath(md5, row.Score.ScoreId);
            if (currentSnapshot.IsStable)
            {
                row.Note = "Downloaded replays are compared in osu!lazer: this map is shown from osu!stable.";
                return;
            }

            if (!row.IsSaved)
            {
                if (!row.Score.HasReplay)
                {
                    row.Note = "osu! has no replay of this score";
                    return;
                }

                row.IsBusy = true;
                bool ok = await leaderboard.DownloadReplayAsync(row.Score.ScoreId, path);
                row.IsBusy = false;
                if (!ok)
                {
                    row.Note = string.IsNullOrEmpty(leaderboard.LastError) ? "Couldn't download the replay" : leaderboard.LastError;
                    return;
                }

                row.Note = string.Empty;
                row.IsSaved = true;
            }

            if (md5 != currentSnapshot.BeatmapChecksum)
                return;

            pendingPin = path;
            RefreshReplays();
        }
        catch (Exception ex)
        {
            row.IsBusy = false;
            row.Note = ex.Message;
            InternalLogger.Log(ex);
        }
    }

    // --- Rivals for the pacemaker overlay ---

    private const int max_rivals = 12;

    /// <summary>The opponents for the game's map: local replays at the play's rate, and leaderboard scores without a replay here.</summary>
    private RivalSet buildRivals()
    {
        string mode = settingsService.Current.RivalsMode is "online" or "both" ? settingsService.Current.RivalsMode : "local";
        var live = Volatile.Read(ref liveSnapshot);

        List<ReplayEntry> replays;
        ReplayEntry? selected;
        string md5;
        lock (replayLock)
        {
            replays = currentReplays.ToList();
            md5 = currentReplaysMd5;
            selected = replaySelection.GetSelected(replays);
        }

        // the list belongs to the game's map only while the page shows it
        if (string.IsNullOrEmpty(md5) || !string.Equals(md5, live.BeatmapChecksum, StringComparison.OrdinalIgnoreCase))
            return new RivalSet(live.BeatmapChecksum, mode, library.Player, []);

        var target = selected ?? replays.FirstOrDefault(replay => AutoTargetRule.IsCompatible(replay, live.PlaySpeed));
        var rivals = new List<Rival>();
        if (mode != "online")
        {
            foreach (var replay in replays.Where(replay => replay.HasReplay && (AutoTargetRule.IsCompatible(replay, live.PlaySpeed) || ReferenceEquals(replay, selected))))
            {
                rivals.Add(new Rival(replay.FilePath, replay.Player, "replay", replay.FilePath, replay.Score, replay.Accuracy, replay.MaxCombo, Ranks.Letter(replay.Rank), replay.ModsText,
                    replay.Date, target != null && string.Equals(target.FilePath, replay.FilePath, StringComparison.OrdinalIgnoreCase),
                    string.Equals(replay.Player, library.Player, StringComparison.OrdinalIgnoreCase)));
            }
        }

        if (mode != "local" && string.Equals(onlineScoresMd5, md5, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var score in onlineScores)
            {
                var path = downloadPath(md5, score.ScoreId);
                if (rivals.Any(rival => string.Equals(rival.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                bool saved = File.Exists(path);
                rivals.Add(new Rival($"online:{score.ScoreId}", score.Player, saved ? "replay" : "online", saved ? path : string.Empty, score.TotalScore, score.Accuracy, score.MaxCombo,
                    Ranks.Letter(score.Rank), score.Mods.Count == 0 ? "NM" : string.Join(" ", score.Mods), score.Date?.ToString("O") ?? string.Empty,
                    false, string.Equals(score.Player, library.Player, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return new RivalSet(md5, mode, library.Player, rivals.OrderByDescending(rival => rival.Score).Take(max_rivals).ToList());
    }

    /// <summary>
    /// For looking at a page without the game: LRC_DEBUG_PAGE=library opens the library, =map shows the most played map,
    /// =online its online ranking, =settings the settings. Nothing happens without the variable.
    /// </summary>
    private async void openDebugPage()
    {
        try
        {
            var page = Environment.GetEnvironmentVariable("LRC_DEBUG_PAGE");
            if (string.IsNullOrWhiteSpace(page))
                return;

            // the kept library and the records need a moment
            for (int i = 0; i < 40 && (library.Maps.Maps.Count == 0 || library.Player.Length == 0); i++)
                await Task.Delay(500);
            await Task.Delay(1500);
            refreshLibraryNow();

            if (page.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            {
                openLibrary();
                await Task.Delay(1500);
                if (page.Contains("high", StringComparison.OrdinalIgnoreCase) && libraryModel.Groups.Count > 4)
                    libraryModel.SelectedGroup = libraryModel.Groups[libraryModel.Groups.Count * 4 / 5];
                if (page.Contains("locate", StringComparison.OrdinalIgnoreCase)
                    && library.Maps.Maps.Values.OrderByDescending(map => library.RecordOf(map.Md5)?.Plays ?? 0).FirstOrDefault() is { } played)
                    libraryModel.Locate(played.Md5, force: true);
            }
            else if (page.StartsWith("set", StringComparison.OrdinalIgnoreCase))
                viewModel.IsSettingsPage = true;
            else
            {
                var best = library.Maps.Maps.Values.OrderByDescending(map => library.RecordOf(map.Md5)?.Plays ?? 0).FirstOrDefault();
                if (best != null)
                    BrowseTo(best);
                if (page.StartsWith("rail", StringComparison.OrdinalIgnoreCase))
                    viewModel.IsRailExpanded = true;
                if (page.StartsWith("narrow", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.IsRailExpanded = true;
                    WindowState = Avalonia.Controls.WindowState.Normal;
                    Width = Math.Max(MinWidth, 700);
                }
                if (page.StartsWith("pick", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(2500);
                    var button = this.FindControl<Button>("MainPickerButton")!;
                    button.Flyout?.ShowAt(button);
                }
                if (page.StartsWith("online", StringComparison.OrdinalIgnoreCase))
                    ShowOnlineTab(this, new RoutedEventArgs());
                if (page.StartsWith("detail", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(2500);
                    if (viewModel.ReplayRows.FirstOrDefault(row => row.HasReplay) is { } row)
                        OpenDetails(row.Entry);
                }
            }
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }

    private void OpenDataFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{AppPaths.Root}\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }
}
