using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace LazerReplayCompare;

public sealed partial class MainWindow : Window
{
    private const int ApiPort = 24052;
    private const string TosuHost = "127.0.0.1:24050";

    private readonly ReplayFinder replayFinder = new();
    private readonly TosuClient tosuClient = new();
    private readonly ReplayApiServer apiServer;
    private readonly object replayLock = new();
    private readonly AppSettings settings;

    private TextBox osuLazerBox = null!;
    private Button refreshButton = null!;
    private ComboBox themeBox = null!;
    private ListBox replayList = null!;
    private TextBlock statusLabel = null!;
    private TextBlock beatmapLabel = null!;
    private TextBlock selectedReplayBadgeLabel = null!;
    private TextBlock replayCountLabel = null!;
    private TextBlock detailTitleLabel = null!;
    private TextBlock detailSubtitleLabel = null!;
    private TextBlock detailScoreLabel = null!;
    private TextBlock detailAccuracyLabel = null!;
    private TextBlock detailRankLabel = null!;
    private TextBlock detailComboLabel = null!;
    private TextBlock detailMissLabel = null!;
    private TextBlock detailModsLabel = null!;
    private TextBlock detailDateLabel = null!;
    private TextBlock detailFileLabel = null!;
    private TextBlock detailBeatmapLabel = null!;

    private List<ReplayEntry> currentReplays = new();
    private string currentReplaysMd5 = string.Empty;
    private string selectedReplayPath = string.Empty;
    private bool fillingReplayList;
    private TosuSnapshot currentSnapshot = new("lazer", "", "", "", "", "");

    public ObservableCollection<ReplayRow> ReplayRows { get; } = new();

    public MainWindow()
    {
        settings = AppSettings.Load();
        apiServer = new ReplayApiServer(GetCurrentReplays);

        InitializeComponent();
        DataContext = this;
        BindControls();
        ApplySavedSettings();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        apiServer.Start(ApiPort);

        tosuClient.StatusChanged += status => Dispatcher.UIThread.Post(() => statusLabel.Text = status);
        tosuClient.SnapshotReceived += OnSnapshotReceived;
        tosuClient.Start(TosuHost);

        if (string.IsNullOrWhiteSpace(settings.OsuLazerPath) || !Directory.Exists(settings.OsuLazerPath))
            await BrowseOsuLazerFolderCore(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        tosuClient.Dispose();
        apiServer.Dispose();
        base.OnClosed(e);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void BindControls()
    {
        osuLazerBox = this.FindControl<TextBox>("OsuLazerBox")!;
        refreshButton = this.FindControl<Button>("RefreshButton")!;
        themeBox = this.FindControl<ComboBox>("ThemeBox")!;
        replayList = this.FindControl<ListBox>("ReplayList")!;
        statusLabel = this.FindControl<TextBlock>("StatusLabel")!;
        beatmapLabel = this.FindControl<TextBlock>("BeatmapLabel")!;
        selectedReplayBadgeLabel = this.FindControl<TextBlock>("SelectedReplayBadgeLabel")!;
        replayCountLabel = this.FindControl<TextBlock>("ReplayCountLabel")!;
        detailTitleLabel = this.FindControl<TextBlock>("DetailTitleLabel")!;
        detailSubtitleLabel = this.FindControl<TextBlock>("DetailSubtitleLabel")!;
        detailScoreLabel = this.FindControl<TextBlock>("DetailScoreLabel")!;
        detailAccuracyLabel = this.FindControl<TextBlock>("DetailAccuracyLabel")!;
        detailRankLabel = this.FindControl<TextBlock>("DetailRankLabel")!;
        detailComboLabel = this.FindControl<TextBlock>("DetailComboLabel")!;
        detailMissLabel = this.FindControl<TextBlock>("DetailMissLabel")!;
        detailModsLabel = this.FindControl<TextBlock>("DetailModsLabel")!;
        detailDateLabel = this.FindControl<TextBlock>("DetailDateLabel")!;
        detailFileLabel = this.FindControl<TextBlock>("DetailFileLabel")!;
        detailBeatmapLabel = this.FindControl<TextBlock>("DetailBeatmapLabel")!;
    }

    private void ApplySavedSettings()
    {
        osuLazerBox.Text = settings.OsuLazerPath;
        var theme = string.IsNullOrWhiteSpace(settings.Theme) ? "System" : settings.Theme;
        themeBox.SelectedIndex = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            ? 1
            : theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 0;
        ApplyTheme(theme);
        UpdateSelectedReplayDetails(null);
    }

    private (string Md5, IReadOnlyList<ReplayEntry> Replays, ReplayEntry? SelectedReplay) GetCurrentReplays()
    {
        lock (replayLock)
        {
            var replays = currentReplays.ToList();
            var selected = string.IsNullOrWhiteSpace(selectedReplayPath)
                ? null
                : replays.FirstOrDefault(replay => string.Equals(replay.FilePath, selectedReplayPath, StringComparison.OrdinalIgnoreCase));

            return (currentReplaysMd5, replays, selected);
        }
    }

    private async void BrowseOsuLazerFolder(object? sender, RoutedEventArgs e)
    {
        await BrowseOsuLazerFolderCore(false);
    }

    private async Task BrowseOsuLazerFolderCore(bool firstRun)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var startLocation = !string.IsNullOrWhiteSpace(osuLazerBox.Text) && Directory.Exists(osuLazerBox.Text)
            ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(osuLazerBox.Text)
            : null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = firstRun ? "Select your osu!lazer folder" : "Select osu!lazer folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        var folder = folders.FirstOrDefault();
        if (folder == null)
            return;

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        osuLazerBox.Text = path;
        settings.OsuLazerPath = path;
        settings.Save();
        RefreshReplays();
    }

    private void ThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (themeBox.SelectedItem is not ComboBoxItem item || item.Content is not string theme)
            return;

        settings.Theme = theme;
        settings.Save();
        ApplyTheme(theme);
    }

    private void ApplyTheme(string theme)
    {
        var normalized = string.IsNullOrWhiteSpace(theme) ? "System" : theme;
        RequestedThemeVariant = normalized.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Dark
            : normalized.Equals("Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Default;

        var isLight = normalized.Equals("Light", StringComparison.OrdinalIgnoreCase);
        if (normalized.Equals("System", StringComparison.OrdinalIgnoreCase))
            isLight = ActualThemeVariant == ThemeVariant.Light;

        SetPalette(isLight);
    }

    private void SetPalette(bool light)
    {
        Resources["PageBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#F8FAFC") : Color.Parse("#050C19"));
        Resources["CardBackgroundBrush"] = new SolidColorBrush(light ? Colors.White : Color.Parse("#0D192C"));
        Resources["CardAltBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#F1F5F9") : Color.Parse("#111F34"));
        Resources["CardBorderBrush"] = new SolidColorBrush(light ? Color.Parse("#CBD5E1") : Color.Parse("#29436F"));
        Resources["InputBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#FFFFFF") : Color.Parse("#050D1B"));
        Resources["ButtonBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#E2E8F0") : Color.Parse("#16243B"));
        Resources["BadgeBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#EFF6FF") : Color.Parse("#12233A"));
        Resources["RowHoverBrush"] = new SolidColorBrush(light ? Color.Parse("#EFF6FF") : Color.Parse("#12223A"));
        Resources["RowSelectedBrush"] = new SolidColorBrush(light ? Color.Parse("#DBEAFE") : Color.Parse("#1E3A70"));
        Resources["HeadingTextBrush"] = new SolidColorBrush(light ? Color.Parse("#0F172A") : Colors.White);
        Resources["BodyTextBrush"] = new SolidColorBrush(light ? Color.Parse("#1E293B") : Color.Parse("#E8EEF8"));
        Resources["MutedTextBrush"] = new SolidColorBrush(light ? Color.Parse("#64748B") : Color.Parse("#97A4BC"));
    }

    private void OnSnapshotReceived(TosuSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var oldChecksum = currentSnapshot.BeatmapChecksum;
            currentSnapshot = snapshot;
            beatmapLabel.Text = string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum)
                ? "None selected"
                : snapshot.BeatmapChecksum;
            detailBeatmapLabel.Text = string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum) ? "-" : snapshot.BeatmapChecksum;

            if (!string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum) && snapshot.BeatmapChecksum != oldChecksum)
                RefreshReplays();
        });
    }

    private void RefreshReplays(object? sender, RoutedEventArgs e)
    {
        RefreshReplays();
    }

    private void RefreshReplays()
    {
        if (string.IsNullOrWhiteSpace(currentSnapshot.BeatmapChecksum))
        {
            statusLabel.Text = "Select a beatmap in osu!lazer first";
            return;
        }

        var md5 = currentSnapshot.BeatmapChecksum;
        var osuLazerRoot = (osuLazerBox.Text ?? string.Empty).Trim();
        if (!string.Equals(settings.OsuLazerPath, osuLazerRoot, StringComparison.Ordinal))
        {
            settings.OsuLazerPath = osuLazerRoot;
            settings.Save();
        }

        var realmPath = Path.Combine(osuLazerRoot, "client.realm");
        var filesRoot = Path.Combine(osuLazerRoot, "files");

        if (!File.Exists(realmPath))
        {
            statusLabel.Text = $"client.realm not found: {realmPath}";
            return;
        }

        if (!Directory.Exists(filesRoot))
        {
            statusLabel.Text = $"files folder not found: {filesRoot}";
            return;
        }

        statusLabel.Text = "Loading replays...";
        refreshButton.IsEnabled = false;

        _ = Task.Run(() =>
        {
            try
            {
                var replays = replayFinder.FindReplays(md5, realmPath, filesRoot);
                lock (replayLock)
                {
                    currentReplays = replays;
                    currentReplaysMd5 = md5;
                    if (!replays.Any(replay => string.Equals(replay.FilePath, selectedReplayPath, StringComparison.OrdinalIgnoreCase)))
                        selectedReplayPath = string.Empty;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    FillReplayList(replays);
                    statusLabel.Text = $"{replays.Count} replay score(s) found";
                    replayCountLabel.Text = $"{replays.Count} replay{(replays.Count == 1 ? string.Empty : "s")} found";
                    refreshButton.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    statusLabel.Text = ex.Message;
                    refreshButton.IsEnabled = true;
                });
            }
        });
    }

    private void FillReplayList(IReadOnlyList<ReplayEntry> replays)
    {
        fillingReplayList = true;
        ReplayRows.Clear();

        foreach (var replay in replays)
        {
            var row = new ReplayRow(replay)
            {
                IsPinned = string.Equals(replay.FilePath, selectedReplayPath, StringComparison.OrdinalIgnoreCase),
            };
            row.PropertyChanged += ReplayRow_PropertyChanged;
            ReplayRows.Add(row);
        }

        fillingReplayList = false;
        replayList.SelectedIndex = ReplayRows.Count > 0 ? 0 : -1;
        UpdateSelectedReplayDetails(GetDetailReplay());
    }

    private void ReplaySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedReplayDetails(GetDetailReplay());
    }

    private void ReplayRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (fillingReplayList || e.PropertyName != nameof(ReplayRow.IsPinned) || sender is not ReplayRow row)
            return;

        if (row.IsPinned)
        {
            lock (replayLock)
                selectedReplayPath = row.Entry.FilePath;

            fillingReplayList = true;
            foreach (var replayRow in ReplayRows)
            {
                if (!ReferenceEquals(replayRow, row))
                    replayRow.IsPinned = false;
            }
            fillingReplayList = false;

            statusLabel.Text = $"Selected replay: {row.Player} / {row.ScoreText}";
            selectedReplayBadgeLabel.Text = $"{row.Player} / {row.ScoreText} / {row.AccuracyText}";
            UpdateSelectedReplayDetails(row.Entry);
            return;
        }

        lock (replayLock)
        {
            if (string.Equals(selectedReplayPath, row.Entry.FilePath, StringComparison.OrdinalIgnoreCase))
                selectedReplayPath = string.Empty;
        }

        statusLabel.Text = "Selected replay cleared";
        selectedReplayBadgeLabel.Text = "Auto best replay";
        UpdateSelectedReplayDetails(GetDetailReplay());
    }

    private ReplayEntry? GetDetailReplay()
    {
        if (!string.IsNullOrWhiteSpace(selectedReplayPath))
        {
            var pinned = ReplayRows.FirstOrDefault(row => string.Equals(row.Entry.FilePath, selectedReplayPath, StringComparison.OrdinalIgnoreCase));
            if (pinned != null)
                return pinned.Entry;
        }

        if (replayList.SelectedItem is ReplayRow selected)
            return selected.Entry;

        return ReplayRows.FirstOrDefault()?.Entry;
    }

    private void UpdateSelectedReplayDetails(ReplayEntry? replay)
    {
        if (replay is null)
        {
            detailTitleLabel.Text = "No replay selected";
            detailSubtitleLabel.Text = "Select a beatmap, then choose a replay.";
            detailScoreLabel.Text = "-";
            detailAccuracyLabel.Text = "-";
            detailRankLabel.Text = "-";
            detailComboLabel.Text = "-";
            detailMissLabel.Text = "-";
            detailModsLabel.Text = "-";
            detailDateLabel.Text = "-";
            detailFileLabel.Text = "-";
            detailBeatmapLabel.Text = string.IsNullOrWhiteSpace(currentSnapshot.BeatmapChecksum) ? "-" : currentSnapshot.BeatmapChecksum;
            return;
        }

        detailTitleLabel.Text = replay.Player;
        detailSubtitleLabel.Text = replay.ModsText;
        detailScoreLabel.Text = replay.Score.ToString("N0", CultureInfo.InvariantCulture);
        detailAccuracyLabel.Text = (replay.Accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        detailRankLabel.Text = replay.Rank;
        detailComboLabel.Text = replay.MaxCombo.ToString(CultureInfo.InvariantCulture) + " x";
        detailMissLabel.Text = GetStatistic(replay, "Miss").ToString(CultureInfo.InvariantCulture);
        detailModsLabel.Text = replay.ModsText;
        detailDateLabel.Text = FormatDate(replay.Date);
        detailFileLabel.Text = replay.FilePath;
        detailBeatmapLabel.Text = string.IsNullOrWhiteSpace(currentSnapshot.BeatmapChecksum) ? "-" : currentSnapshot.BeatmapChecksum;
    }

    private static int GetStatistic(ReplayEntry replay, string key)
    {
        return replay.Statistics.TryGetValue(key, out var value) ? value : 0;
    }

    private static string FormatDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var date)
            ? date.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }

    private void OpenSelectedReplayFolder()
    {
        var replay = GetDetailReplay();
        if (replay == null)
            return;

        if (File.Exists(replay.FilePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{replay.FilePath}\"",
                UseShellExecute = true,
            });
        }
    }

}

public sealed class ReplayRow : INotifyPropertyChanged
{
    private bool isPinned;

    public ReplayRow(ReplayEntry entry)
    {
        Entry = entry;
    }

    public ReplayEntry Entry { get; }
    public string Player => Entry.Player;
    public string DateText => FormatDate(Entry.Date);
    public string ModsText => Entry.ModsText;
    public string ScoreText => Entry.Score.ToString("N0", CultureInfo.InvariantCulture);
    public string AccuracyText => (Entry.Accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    public string MissText => GetStatistic(Entry, "Miss").ToString(CultureInfo.InvariantCulture);
    public string ComboText => Entry.MaxCombo.ToString(CultureInfo.InvariantCulture) + " x";
    public string Rank => Entry.Rank;
    public IBrush MissBrush => GetStatistic(Entry, "Miss") == 0
        ? new SolidColorBrush(Color.Parse("#74E658"))
        : new SolidColorBrush(Color.Parse("#FFAB40"));

    public bool IsPinned
    {
        get => isPinned;
        set
        {
            if (isPinned == value)
                return;

            isPinned = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static int GetStatistic(ReplayEntry replay, string key)
    {
        return replay.Statistics.TryGetValue(key, out var value) ? value : 0;
    }

    private static string FormatDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var date)
            ? date.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
