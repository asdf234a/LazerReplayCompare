using System.Diagnostics;
using System.Globalization;

namespace LazerReplayCompare;

public sealed class MainForm : Form
{
    private readonly ReplayFinder replayFinder = new();
    private readonly TosuClient tosuClient = new();
    private readonly ReplayApiServer apiServer;
    private readonly object replayLock = new();

    private readonly Label statusLabel = new();
    private readonly Label beatmapLabel = new();
    private readonly Label serverLabel = new();
    private readonly TextBox osuLazerBox = new();
    private readonly Button browseOsuLazerButton = new();
    private readonly TextBox tosuBox = new();
    private readonly NumericUpDown portBox = new();
    private readonly Button refreshButton = new();
    private readonly ListView replayList = new();

    private List<ReplayEntry> currentReplays = new();
    private string currentReplaysMd5 = string.Empty;
    private string selectedReplayPath = string.Empty;
    private bool fillingReplayList;
    private TosuSnapshot currentSnapshot = new("lazer", "", "", "", "", "");

    public MainForm()
    {
        apiServer = new ReplayApiServer(GetCurrentReplays);
        InitializeComponent();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        apiServer.Start((int)portBox.Value);
        serverLabel.Text = $"API: http://127.0.0.1:{apiServer.Port}";

        tosuClient.StatusChanged += status => BeginInvoke(() => statusLabel.Text = status);
        tosuClient.SnapshotReceived += OnSnapshotReceived;
        tosuClient.Start(tosuBox.Text);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tosuClient.Dispose();
        apiServer.Dispose();
        base.OnFormClosed(e);
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

    private void InitializeComponent()
    {
        Text = "Lazer Replay Compare";
        Width = 1040;
        Height = 680;
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14),
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(main);

        var title = new Label
        {
            Text = "Lazer Replay Compare",
            Dock = DockStyle.Top,
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            Height = 38,
        };
        main.Controls.Add(title, 0, 0);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 5,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 8),
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        main.Controls.Add(settings, 0, 1);

        osuLazerBox.Text = GetDefaultOsuLazerPath();
        osuLazerBox.Dock = DockStyle.Fill;
        browseOsuLazerButton.Text = "...";
        browseOsuLazerButton.Width = 36;
        browseOsuLazerButton.Click += (_, _) => BrowseOsuLazerFolder();
        tosuBox.Text = "127.0.0.1:24050";
        tosuBox.Width = 130;
        portBox.Minimum = 1024;
        portBox.Maximum = 65535;
        portBox.Value = 24052;
        portBox.Width = 80;

        settings.Controls.Add(new Label { Text = "osu-lazer", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        settings.Controls.Add(osuLazerBox, 1, 0);
        settings.Controls.Add(browseOsuLazerButton, 2, 0);
        settings.Controls.Add(new Label { Text = "tosu", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(10, 0, 0, 0) }, 3, 0);
        settings.Controls.Add(tosuBox, 4, 0);

        var statusBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 8),
        };
        main.Controls.Add(statusBar, 0, 2);

        refreshButton.Text = "Refresh";
        refreshButton.AutoSize = true;
        refreshButton.Click += (_, _) => RefreshReplays();
        statusBar.Controls.Add(refreshButton);

        statusBar.Controls.Add(new Label { Text = "API Port", AutoSize = true, Padding = new Padding(14, 6, 0, 0) });
        statusBar.Controls.Add(portBox);

        statusLabel.AutoSize = true;
        statusLabel.Padding = new Padding(14, 6, 0, 0);
        statusLabel.Text = "Waiting for tosu...";
        statusBar.Controls.Add(statusLabel);

        serverLabel.AutoSize = true;
        serverLabel.Padding = new Padding(14, 6, 0, 0);
        statusBar.Controls.Add(serverLabel);

        beatmapLabel.AutoSize = true;
        beatmapLabel.Padding = new Padding(14, 6, 0, 0);
        beatmapLabel.Text = "Beatmap: -";
        statusBar.Controls.Add(beatmapLabel);

        replayList.Dock = DockStyle.Fill;
        replayList.View = View.Details;
        replayList.CheckBoxes = true;
        replayList.FullRowSelect = true;
        replayList.GridLines = true;
        replayList.Columns.Add("Player", 160);
        replayList.Columns.Add("Date", 170);
        replayList.Columns.Add("Mods", 150);
        replayList.Columns.Add("Score", 110, HorizontalAlignment.Right);
        replayList.Columns.Add("Accuracy", 90, HorizontalAlignment.Right);
        replayList.Columns.Add("Miss", 70, HorizontalAlignment.Right);
        replayList.Columns.Add("Max Combo", 90, HorizontalAlignment.Right);
        replayList.Columns.Add("Rank", 70);
        replayList.Columns.Add("File", 360);
        replayList.ItemChecked += ReplayList_ItemChecked;
        replayList.DoubleClick += (_, _) => OpenSelectedReplayFolder();
        main.Controls.Add(replayList, 0, 3);
    }

    private void BrowseOsuLazerFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select osu-lazer folder",
            SelectedPath = Directory.Exists(osuLazerBox.Text.Trim()) ? osuLazerBox.Text.Trim() : string.Empty,
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            osuLazerBox.Text = dialog.SelectedPath;
    }

    private static string GetDefaultOsuLazerPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultPath = Path.Combine(localAppData, "osulazer");
        return Directory.Exists(defaultPath) ? defaultPath : string.Empty;
    }

    private void OnSnapshotReceived(TosuSnapshot snapshot)
    {
        BeginInvoke(() =>
        {
            var oldChecksum = currentSnapshot.BeatmapChecksum;
            currentSnapshot = snapshot;
            beatmapLabel.Text = string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum)
                ? "Beatmap: -"
                : $"Beatmap: {snapshot.BeatmapChecksum}";

            if (!string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum) && snapshot.BeatmapChecksum != oldChecksum)
                RefreshReplays();
        });
    }

    private void RefreshReplays()
    {
        if (string.IsNullOrWhiteSpace(currentSnapshot.BeatmapChecksum))
        {
            statusLabel.Text = "No selected beatmap from tosu";
            return;
        }

        var md5 = currentSnapshot.BeatmapChecksum;
        var osuLazerRoot = osuLazerBox.Text.Trim();
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
        refreshButton.Enabled = false;

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

                BeginInvoke(() =>
                {
                    FillReplayList(replays);
                    statusLabel.Text = $"{replays.Count} replay(s) loaded";
                    refreshButton.Enabled = true;
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() =>
                {
                    statusLabel.Text = ex.Message;
                    refreshButton.Enabled = true;
                });
            }
        });
    }

    private void FillReplayList(IReadOnlyList<ReplayEntry> replays)
    {
        fillingReplayList = true;
        replayList.BeginUpdate();
        replayList.Items.Clear();

        foreach (var replay in replays)
        {
            var item = new ListViewItem(replay.Player);
            item.SubItems.Add(FormatDate(replay.Date));
            item.SubItems.Add(replay.ModsText);
            item.SubItems.Add(replay.Score.ToString("N0", CultureInfo.InvariantCulture));
            item.SubItems.Add((replay.Accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%");
            item.SubItems.Add(GetStatistic(replay, "Miss").ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(replay.MaxCombo.ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(replay.Rank);
            item.SubItems.Add(replay.FilePath);
            item.Tag = replay;
            item.Checked = string.Equals(replay.FilePath, selectedReplayPath, StringComparison.OrdinalIgnoreCase);
            replayList.Items.Add(item);
        }

        replayList.EndUpdate();
        fillingReplayList = false;
    }

    private void ReplayList_ItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (fillingReplayList || e.Item.Tag is not ReplayEntry replay)
            return;

        if (e.Item.Checked)
        {
            lock (replayLock)
                selectedReplayPath = replay.FilePath;

            try
            {
                fillingReplayList = true;
                foreach (ListViewItem item in replayList.Items)
                {
                    if (!ReferenceEquals(item, e.Item))
                        item.Checked = false;
                }
            }
            finally
            {
                fillingReplayList = false;
            }

            statusLabel.Text = $"Selected replay: {replay.Player} / {replay.Score.ToString("N0", CultureInfo.InvariantCulture)}";
            return;
        }

        lock (replayLock)
        {
            if (string.Equals(selectedReplayPath, replay.FilePath, StringComparison.OrdinalIgnoreCase))
                selectedReplayPath = string.Empty;
        }

        statusLabel.Text = "Selected replay cleared";
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
        if (replayList.SelectedItems.Count == 0 || replayList.SelectedItems[0].Tag is not ReplayEntry replay)
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
