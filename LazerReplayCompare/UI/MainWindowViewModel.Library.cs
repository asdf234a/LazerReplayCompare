using System.Collections.ObjectModel;

namespace LazerReplayCompare;

// The library, the map card, the side card and the replay tabs (2.0).
public sealed partial class MainWindowViewModel
{
    // --- Library page ---

    private LibraryViewModel? library;

    public LibraryViewModel? Library
    {
        get => library;
        set => Set(ref library, value);
    }

    private bool isLibraryOpen;

    /// <summary>The whole library is shown instead of the map page.</summary>
    public bool IsLibraryOpen
    {
        get => isLibraryOpen;
        set
        {
            if (!Set(ref isLibraryOpen, value))
                return;

            OnPropertyChanged(nameof(ShowLibraryPage));
            OnPropertyChanged(nameof(ShowCompareList));
            OnPropertyChanged(nameof(ShowDetailsPage));
        }
    }

    public bool ShowLibraryPage => IsComparePage && IsLibraryOpen;

    // --- Map card ---

    private MapCardViewModel? mapCard;

    public MapCardViewModel? MapCard
    {
        get => mapCard;
        set => Set(ref mapCard, value);
    }

    // --- Side card (the library beside the map page) ---

    private bool isRailExpanded;

    public bool IsRailExpanded
    {
        get => isRailExpanded;
        set
        {
            if (Set(ref isRailExpanded, value))
                SettingsChanged?.Invoke();
        }
    }

    private bool showRail;

    /// <summary>The library has groups to show beside the map page.</summary>
    public bool ShowRail
    {
        get => showRail;
        set => Set(ref showRail, value);
    }

    // --- Replay tabs: local replays, online ranking ---

    private bool isOnlineTab;

    public bool IsOnlineTab
    {
        get => isOnlineTab;
        set
        {
            if (!Set(ref isOnlineTab, value))
                return;

            OnPropertyChanged(nameof(IsLocalTab));
            OnPropertyChanged(nameof(ShowOnlineList));
            OnPropertyChanged(nameof(ShowOnlineNote));
            OnPropertyChanged(nameof(RankSummary));
        }
    }

    public bool IsLocalTab => !IsOnlineTab;

    private bool isBrowsingMap;

    /// <summary>The page shows a map picked in the library (its replays are listed without tosu, too).</summary>
    public bool IsBrowsingMap
    {
        get => isBrowsingMap;
        set
        {
            if (Set(ref isBrowsingMap, value))
                connectionsChanged();
        }
    }

    /// <summary>The map's replays can be shown: everything is connected, or the map was picked in the library.</summary>
    public bool CanShowMap => AllConnected || (IsBrowsingMap && GameFolderFound);

    /// <summary>The tabs show once there is a map whose replays can be shown.</summary>
    public bool ShowReplayTabs => CanShowMap && HasBeatmap;

    public ObservableCollection<OnlineScoreRow> OnlineRows { get; } = new();

    private string onlineNote = string.Empty;

    /// <summary>Why the online ranking is empty (no API client, loading, no leaderboard).</summary>
    public string OnlineNote
    {
        get => onlineNote;
        set
        {
            if (Set(ref onlineNote, value))
                OnPropertyChanged(nameof(ShowOnlineNote));
        }
    }

    public bool ShowOnlineList => IsOnlineTab && ShowReplayTabs;
    public bool ShowOnlineNote => OnlineNote.Length > 0;

    public string LocalTabText => $"Local replays  {ReplayRows.Count}";
    public string OnlineTabText => OnlineRows.Count > 0 ? $"Online ranking  {OnlineRows.Count}" : "Online ranking";

    private string localSummary = string.Empty;
    private string onlineSummary = string.Empty;

    /// <summary>"Your best is #2 of 5 here · 111,336 behind #1".</summary>
    public string LocalSummary
    {
        get => localSummary;
        set
        {
            if (Set(ref localSummary, value))
                OnPropertyChanged(nameof(RankSummary));
        }
    }

    /// <summary>"You are #32 · 4,210 behind #31".</summary>
    public string OnlineSummary
    {
        get => onlineSummary;
        set
        {
            if (Set(ref onlineSummary, value))
                OnPropertyChanged(nameof(RankSummary));
        }
    }

    public string RankSummary => IsOnlineTab ? OnlineSummary : LocalSummary;

    public void NotifyTabsChanged()
    {
        OnPropertyChanged(nameof(ShowReplayTabs));
        OnPropertyChanged(nameof(ShowOnlineList));
        OnPropertyChanged(nameof(LocalTabText));
        OnPropertyChanged(nameof(OnlineTabText));
    }

    // --- Rivals (who the pacemaker overlay races against) ---

    public IReadOnlyList<OverlayOption> RivalsOptions { get; } =
    [
        new("local", "Local replays"),
        new("online", "Online ranking"),
        new("both", "Both"),
    ];

    private OverlayOption? selectedRivals;

    public OverlayOption? SelectedRivals
    {
        get => selectedRivals ?? RivalsOptions[0];
        set
        {
            if (value != null && Set(ref selectedRivals, value))
                SettingsChanged?.Invoke();
        }
    }

    public string RivalsMode => SelectedRivals?.Key ?? "local";

    public void RestoreRivals(string mode) => selectedRivals = RivalsOptions.FirstOrDefault(o => o.Key == mode) ?? RivalsOptions[0];

    // --- Library settings ---

    private bool showRowBackgrounds = true;

    public bool ShowRowBackgrounds
    {
        get => showRowBackgrounds;
        set
        {
            if (Set(ref showRowBackgrounds, value))
                SettingsChanged?.Invoke();
        }
    }

    private bool useDanEngine = true;

    /// <summary>Estimate dans with the analyser's estimators (a headless browser in the background).</summary>
    public bool UseDanEngine
    {
        get => useDanEngine;
        set
        {
            if (Set(ref useDanEngine, value))
                SettingsChanged?.Invoke();
        }
    }

    private string libraryStatus = string.Empty;

    /// <summary>How many maps were read, and how the ratings and dan estimates stand.</summary>
    public string LibraryStatus
    {
        get => libraryStatus;
        set => Set(ref libraryStatus, value);
    }

    public string DataFolder => AppPaths.Root;

    // --- Background work (rating maps, estimating dans), shown in the top bar while it runs ---

    private string workText = string.Empty;
    private string workDetails = string.Empty;
    private double workPercent;

    /// <summary>"Rating maps 1,234 / 45,270" while maps are rated or dans estimated; empty when nothing runs.</summary>
    public string WorkText
    {
        get => workText;
        set
        {
            if (Set(ref workText, value))
                OnPropertyChanged(nameof(ShowWork));
        }
    }

    public string WorkDetails
    {
        get => workDetails;
        set => Set(ref workDetails, value);
    }

    public double WorkPercent
    {
        get => workPercent;
        set => Set(ref workPercent, value);
    }

    public bool ShowWork => WorkText.Length > 0;

    public IReadOnlyList<OverlayOption> SpeedOptions { get; } =
    [
        new("light", "Light (a quarter of the CPU)"),
        new("normal", "Normal (half of the CPU)"),
        new("fast", "Fast (all of the CPU)"),
    ];

    private OverlayOption? selectedSpeed;

    public OverlayOption? SelectedSpeed
    {
        get => selectedSpeed ?? SpeedOptions[0];
        set
        {
            if (value != null && Set(ref selectedSpeed, value))
                SettingsChanged?.Invoke();
        }
    }

    public string BackgroundSpeed => SelectedSpeed?.Key ?? "light";

    public void RestoreSpeed(string speed) => selectedSpeed = SpeedOptions.FirstOrDefault(o => o.Key == speed) ?? SpeedOptions[0];
}
