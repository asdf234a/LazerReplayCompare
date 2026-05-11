using System.Diagnostics;
using System.Globalization;

namespace LazerReplayCompare;

public sealed class MainForm : Form
{
    private const int ApiPort = 24052;
    private const string TosuHost = "127.0.0.1:24050";

    private readonly ReplayFinder replayFinder = new();
    private readonly TosuClient tosuClient = new();
    private readonly ReplayApiServer apiServer;
    private readonly object replayLock = new();

    private readonly Label statusLabel = new();
    private readonly Label beatmapLabel = new();
    private readonly TextBox osuLazerBox = new();
    private readonly Button browseOsuLazerButton = new();
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
        apiServer.Start(ApiPort);

        tosuClient.StatusChanged += status => BeginInvoke(() => statusLabel.Text = status);
        tosuClient.SnapshotReceived += OnSnapshotReceived;
        tosuClient.Start(TosuHost);
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
        Width = 1120;
        Height = 720;
        MinimumSize = new Size(940, 590);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(14, 18, 24);
        ForeColor = Color.FromArgb(231, 236, 245);
        Font = new Font("Segoe UI", 9.5f);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(18),
            BackColor = BackColor,
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(main);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 12),
            BackColor = BackColor,
        };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.Controls.Add(header, 0, 0);

        var title = new Label
        {
            Text = "Lazer Replay Compare",
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI Semibold", 22, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
        };
        header.Controls.Add(title, 0, 0);

        var subtitle = new Label
        {
            Text = "Choose a saved replay and compare it with your current osu!lazer play.",
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 10.5f),
            ForeColor = Color.FromArgb(151, 162, 179),
            AutoSize = true,
            Padding = new Padding(1, 4, 0, 0),
        };
        header.Controls.Add(subtitle, 0, 1);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            AutoSize = true,
            Padding = new Padding(14, 12, 14, 12),
            BackColor = Color.FromArgb(21, 27, 36),
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        main.Controls.Add(settings, 0, 1);

        osuLazerBox.Text = GetDefaultOsuLazerPath();
        osuLazerBox.Dock = DockStyle.Fill;
        osuLazerBox.BorderStyle = BorderStyle.FixedSingle;
        osuLazerBox.BackColor = Color.FromArgb(9, 12, 17);
        osuLazerBox.ForeColor = Color.FromArgb(231, 236, 245);
        osuLazerBox.Margin = new Padding(10, 2, 8, 2);
        browseOsuLazerButton.Text = "Browse";
        browseOsuLazerButton.Width = 82;
        browseOsuLazerButton.Height = 30;
        browseOsuLazerButton.FlatStyle = FlatStyle.Flat;
        browseOsuLazerButton.BackColor = Color.FromArgb(43, 99, 235);
        browseOsuLazerButton.ForeColor = Color.White;
        browseOsuLazerButton.FlatAppearance.BorderSize = 0;
        browseOsuLazerButton.Margin = new Padding(0, 0, 8, 0);
        browseOsuLazerButton.Click += (_, _) => BrowseOsuLazerFolder();

        refreshButton.Text = "Refresh";
        refreshButton.Width = 82;
        refreshButton.Height = 30;
        refreshButton.FlatStyle = FlatStyle.Flat;
        refreshButton.BackColor = Color.FromArgb(36, 44, 58);
        refreshButton.ForeColor = Color.FromArgb(231, 236, 245);
        refreshButton.FlatAppearance.BorderColor = Color.FromArgb(55, 66, 84);
        refreshButton.Click += (_, _) => RefreshReplays();

        settings.Controls.Add(new Label
        {
            Text = "osu!lazer folder",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(185, 195, 211),
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
        }, 0, 0);
        settings.Controls.Add(osuLazerBox, 1, 0);
        settings.Controls.Add(browseOsuLazerButton, 2, 0);
        settings.Controls.Add(refreshButton, 3, 0);

        var statusBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 12),
            BackColor = BackColor,
        };
        main.Controls.Add(statusBar, 0, 2);

        statusLabel.AutoSize = true;
        statusLabel.Padding = new Padding(12, 7, 12, 7);
        statusLabel.Margin = new Padding(0, 0, 10, 0);
        statusLabel.BackColor = Color.FromArgb(30, 38, 50);
        statusLabel.ForeColor = Color.FromArgb(198, 207, 221);
        statusLabel.Text = "Waiting for osu!lazer selection...";
        statusBar.Controls.Add(statusLabel);

        beatmapLabel.AutoSize = true;
        beatmapLabel.Padding = new Padding(12, 7, 12, 7);
        beatmapLabel.Margin = new Padding(0);
        beatmapLabel.BackColor = Color.FromArgb(30, 38, 50);
        beatmapLabel.ForeColor = Color.FromArgb(198, 207, 221);
        beatmapLabel.Text = "Beatmap: none selected";
        statusBar.Controls.Add(beatmapLabel);

        var listHeader = new Label
        {
            Text = "Replay scores",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(1, 0, 0, 8),
            ForeColor = Color.FromArgb(231, 236, 245),
            Font = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold),
        };
        main.Controls.Add(listHeader, 0, 3);

        replayList.Dock = DockStyle.Fill;
        replayList.View = View.Details;
        replayList.CheckBoxes = true;
        replayList.FullRowSelect = true;
        replayList.GridLines = false;
        replayList.BorderStyle = BorderStyle.None;
        replayList.BackColor = Color.FromArgb(11, 15, 21);
        replayList.ForeColor = Color.FromArgb(231, 236, 245);
        replayList.OwnerDraw = true;
        replayList.DrawColumnHeader += ReplayList_DrawColumnHeader;
        replayList.DrawItem += (_, e) => e.DrawDefault = true;
        replayList.DrawSubItem += (_, e) => e.DrawDefault = true;
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
        main.Controls.Add(replayList, 0, 4);
    }

    private void ReplayList_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(Color.FromArgb(22, 29, 39));
        using var headerFont = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? string.Empty,
            headerFont,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
            Color.FromArgb(185, 195, 211),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
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
                ? "Beatmap: none selected"
                : $"Beatmap: {snapshot.BeatmapChecksum}";

            if (!string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum) && snapshot.BeatmapChecksum != oldChecksum)
                RefreshReplays();
        });
    }

    private void RefreshReplays()
    {
        if (string.IsNullOrWhiteSpace(currentSnapshot.BeatmapChecksum))
        {
            statusLabel.Text = "Select a beatmap in osu!lazer first";
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
                    statusLabel.Text = $"{replays.Count} replay score(s) found";
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
