using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace LazerReplayCompare;

public sealed class MainForm : Form
{
    private const int ApiPort = 24052;
    private const string TosuHost = "127.0.0.1:24050";

    private static readonly Color PageBackground = Color.FromArgb(5, 12, 25);
    private static readonly Color CardBackground = Color.FromArgb(13, 25, 44);
    private static readonly Color CardBackgroundAlt = Color.FromArgb(16, 31, 52);
    private static readonly Color BorderColor = Color.FromArgb(42, 67, 111);
    private static readonly Color MutedText = Color.FromArgb(151, 164, 188);
    private static readonly Color BodyText = Color.FromArgb(232, 238, 248);
    private static readonly Color AccentBlue = Color.FromArgb(59, 130, 246);
    private static readonly Color AccentBlueSoft = Color.FromArgb(32, 82, 170);
    private static readonly Color GoodGreen = Color.FromArgb(116, 230, 88);
    private static readonly Color WarningOrange = Color.FromArgb(255, 171, 64);

    private readonly ReplayFinder replayFinder = new();
    private readonly TosuClient tosuClient = new();
    private readonly ReplayApiServer apiServer;
    private readonly object replayLock = new();

    private readonly Label statusLabel = new();
    private readonly Label beatmapLabel = new();
    private readonly Label selectedReplayBadgeLabel = new();
    private readonly Label replayCountLabel = new();
    private readonly TextBox osuLazerBox = new();
    private readonly RoundedButton browseOsuLazerButton = new();
    private readonly RoundedButton refreshButton = new();
    private readonly ListView replayList = new();

    private readonly Label detailTitleLabel = new();
    private readonly Label detailSubtitleLabel = new();
    private readonly Label detailScoreLabel = new();
    private readonly Label detailAccuracyLabel = new();
    private readonly Label detailRankLabel = new();
    private readonly Label detailComboLabel = new();
    private readonly Label detailMissLabel = new();
    private readonly Label detailModsLabel = new();
    private readonly Label detailFileLabel = new();
    private readonly Label detailBeatmapLabel = new();

    private List<ReplayEntry> currentReplays = new();
    private string currentReplaysMd5 = string.Empty;
    private string selectedReplayPath = string.Empty;
    private bool fillingReplayList;
    private int hoveredReplayIndex = -1;
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
        Width = 1280;
        Height = 780;
        MinimumSize = new Size(1080, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageBackground;
        ForeColor = BodyText;
        Font = new Font("Segoe UI", 9.5f);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(26),
            BackColor = PageBackground,
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(main);

        main.Controls.Add(CreateHeader(), 0, 0);
        main.Controls.Add(CreateFolderCard(), 0, 1);
        main.Controls.Add(CreateContentGrid(), 0, 2);

        UpdateSelectedReplayDetails(null);
    }

    private Control CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 22),
            BackColor = PageBackground,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var icon = new PulseIconControl
        {
            Size = new Size(44, 44),
            Margin = new Padding(0, 4, 16, 0),
        };
        header.Controls.Add(icon, 0, 0);

        var titleBlock = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            BackColor = PageBackground,
            Margin = new Padding(0),
        };
        titleBlock.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleBlock.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(titleBlock, 1, 0);

        titleBlock.Controls.Add(new Label
        {
            Text = "Lazer Replay Compare",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20.5f, FontStyle.Bold),
            ForeColor = Color.White,
            Margin = new Padding(0),
        }, 0, 0);

        titleBlock.Controls.Add(new Label
        {
            Text = "Compare a saved lazer replay with your current osu!lazer play.",
            AutoSize = true,
            Font = new Font("Segoe UI", 10.5f),
            ForeColor = MutedText,
            Margin = new Padding(1, 6, 0, 0),
        }, 0, 1);

        return header;
    }

    private Control CreateFolderCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Radius = 14,
            BorderColor = BorderColor,
            FillColor = Color.FromArgb(11, 23, 43),
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 0, 22),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 2,
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(layout);

        var folderLabel = new Label
        {
            Text = "osu!lazer folder",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            ForeColor = BodyText,
            Margin = new Padding(0, 0, 18, 0),
        };
        layout.Controls.Add(folderLabel, 0, 0);

        var inputShell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Height = 38,
            Radius = 9,
            FillColor = Color.FromArgb(5, 13, 27),
            BorderColor = Color.FromArgb(35, 55, 89),
            Padding = new Padding(12, 8, 12, 6),
            Margin = new Padding(0, 0, 12, 0),
        };
        osuLazerBox.Text = GetDefaultOsuLazerPath();
        osuLazerBox.Dock = DockStyle.Fill;
        osuLazerBox.BorderStyle = BorderStyle.None;
        osuLazerBox.BackColor = inputShell.FillColor;
        osuLazerBox.ForeColor = BodyText;
        osuLazerBox.Font = new Font("Segoe UI", 10.5f);
        inputShell.Controls.Add(osuLazerBox);
        layout.Controls.Add(inputShell, 1, 0);

        browseOsuLazerButton.Text = "Browse";
        browseOsuLazerButton.Width = 112;
        browseOsuLazerButton.Height = 38;
        browseOsuLazerButton.Radius = 9;
        browseOsuLazerButton.FillColor = Color.FromArgb(37, 99, 235);
        browseOsuLazerButton.BorderColor = Color.FromArgb(67, 120, 245);
        browseOsuLazerButton.ForeColor = Color.White;
        browseOsuLazerButton.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        browseOsuLazerButton.Margin = new Padding(0, 0, 12, 0);
        browseOsuLazerButton.Click += (_, _) => BrowseOsuLazerFolder();
        layout.Controls.Add(browseOsuLazerButton, 2, 0);

        refreshButton.Text = "Refresh";
        refreshButton.Width = 112;
        refreshButton.Height = 38;
        refreshButton.Radius = 9;
        refreshButton.FillColor = Color.FromArgb(22, 36, 59);
        refreshButton.BorderColor = Color.FromArgb(48, 68, 104);
        refreshButton.ForeColor = BodyText;
        refreshButton.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        refreshButton.Click += (_, _) => RefreshReplays();
        layout.Controls.Add(refreshButton, 3, 0);

        var badges = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 16, 0, 0),
        };
        layout.SetColumnSpan(badges, 4);
        layout.Controls.Add(badges, 0, 1);

        selectedReplayBadgeLabel.Text = "Selected replay: auto best";
        badges.Controls.Add(CreateBadge(selectedReplayBadgeLabel, true));

        beatmapLabel.Text = "Beatmap: none selected";
        badges.Controls.Add(CreateBadge(beatmapLabel, false));

        statusLabel.Text = "Waiting for osu!lazer selection...";
        badges.Controls.Add(CreateBadge(statusLabel, false));

        return card;
    }

    private Control CreateContentGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = PageBackground,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        grid.Controls.Add(CreateReplayListCard(), 0, 0);
        grid.Controls.Add(CreateDetailCard(), 1, 0);
        return grid;
    }

    private Control CreateReplayListCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 14,
            BorderColor = Color.FromArgb(31, 50, 79),
            FillColor = CardBackground,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 18, 0),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(2, 0, 2, 14),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(header, 0, 0);

        header.Controls.Add(new Label
        {
            Text = "Replay scores",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold),
            ForeColor = BodyText,
            Margin = new Padding(0),
        }, 0, 0);

        replayCountLabel.Text = "0 replays found";
        replayCountLabel.AutoSize = true;
        replayCountLabel.ForeColor = MutedText;
        replayCountLabel.Font = new Font("Segoe UI", 9.5f);
        replayCountLabel.Margin = new Padding(0, 3, 0, 0);
        header.Controls.Add(replayCountLabel, 1, 0);

        replayList.Dock = DockStyle.Fill;
        replayList.View = View.Details;
        replayList.CheckBoxes = true;
        replayList.FullRowSelect = true;
        replayList.HideSelection = false;
        replayList.GridLines = false;
        replayList.BorderStyle = BorderStyle.None;
        replayList.BackColor = CardBackground;
        replayList.ForeColor = BodyText;
        replayList.OwnerDraw = true;
        replayList.SmallImageList = new ImageList { ImageSize = new Size(1, 42) };
        replayList.DrawColumnHeader += ReplayList_DrawColumnHeader;
        replayList.DrawItem += (_, _) => { };
        replayList.DrawSubItem += ReplayList_DrawSubItem;
        replayList.MouseMove += ReplayList_MouseMove;
        replayList.MouseLeave += (_, _) =>
        {
            if (hoveredReplayIndex == -1)
                return;

            hoveredReplayIndex = -1;
            replayList.Invalidate();
        };
        replayList.SelectedIndexChanged += (_, _) => UpdateSelectedReplayDetails(GetDetailReplay());
        replayList.Columns.Add("Player", 130);
        replayList.Columns.Add("Date", 160);
        replayList.Columns.Add("Mods", 140);
        replayList.Columns.Add("Score", 105, HorizontalAlignment.Right);
        replayList.Columns.Add("Accuracy", 90, HorizontalAlignment.Right);
        replayList.Columns.Add("Miss", 70, HorizontalAlignment.Right);
        replayList.Columns.Add("Max Combo", 96, HorizontalAlignment.Right);
        replayList.Columns.Add("Rank", 60);
        replayList.Columns.Add("File", 80);
        replayList.ItemChecked += ReplayList_ItemChecked;
        replayList.DoubleClick += (_, _) => OpenSelectedReplayFolder();
        layout.Controls.Add(replayList, 0, 1);

        return card;
    }

    private Control CreateDetailCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 14,
            BorderColor = Color.FromArgb(31, 50, 79),
            FillColor = CardBackground,
            Padding = new Padding(18),
            Margin = new Padding(4, 0, 0, 0),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Selected replay",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold),
            ForeColor = BodyText,
            Margin = new Padding(0, 0, 0, 16),
        }, 0, 0);

        var identity = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 20),
        };
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(identity, 0, 1);

        identity.Controls.Add(new ReplayArtPanel
        {
            Size = new Size(112, 112),
            Radius = 10,
            Margin = new Padding(0, 0, 18, 0),
        }, 0, 0);

        var identityText = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 0),
        };
        identity.Controls.Add(identityText, 1, 0);

        detailTitleLabel.AutoSize = true;
        detailTitleLabel.Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold);
        detailTitleLabel.ForeColor = Color.White;
        detailTitleLabel.Margin = new Padding(0);
        identityText.Controls.Add(detailTitleLabel, 0, 0);

        detailSubtitleLabel.AutoSize = true;
        detailSubtitleLabel.Font = new Font("Segoe UI", 10f);
        detailSubtitleLabel.ForeColor = MutedText;
        detailSubtitleLabel.Margin = new Padding(1, 4, 0, 0);
        identityText.Controls.Add(detailSubtitleLabel, 0, 1);

        var stats = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 2,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 18),
        };
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        stats.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stats.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(stats, 0, 2);

        stats.Controls.Add(CreateStatCard("Score", detailScoreLabel), 0, 0);
        stats.Controls.Add(CreateStatCard("Accuracy", detailAccuracyLabel), 1, 0);
        stats.Controls.Add(CreateStatCard("Rank", detailRankLabel, AccentBlue), 2, 0);
        stats.Controls.Add(CreateStatCard("Max Combo", detailComboLabel), 0, 1);
        stats.Controls.Add(CreateStatCard("Miss", detailMissLabel, GoodGreen), 1, 1);
        stats.Controls.Add(CreateStatCard("Mods", detailModsLabel), 2, 1);

        var detailInfo = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Radius = 9,
            FillColor = Color.FromArgb(11, 22, 38),
            BorderColor = Color.FromArgb(31, 50, 79),
            Padding = new Padding(14),
            AutoSize = true,
        };
        layout.Controls.Add(detailInfo, 0, 3);

        var infoLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        detailInfo.Controls.Add(infoLayout);
        infoLayout.Controls.Add(CreateInfoLine("File", detailFileLabel), 0, 0);
        infoLayout.Controls.Add(CreateInfoLine("Beatmap ID", detailBeatmapLabel), 0, 1);

        return card;
    }

    private static Control CreateBadge(Label label, bool accent)
    {
        var badge = new RoundedPanel
        {
            AutoSize = true,
            Radius = 9,
            FillColor = accent ? Color.FromArgb(19, 43, 87) : Color.FromArgb(18, 31, 52),
            BorderColor = accent ? AccentBlueSoft : Color.FromArgb(35, 55, 89),
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 12, 0),
        };

        label.AutoSize = true;
        label.ForeColor = accent ? Color.FromArgb(214, 229, 255) : Color.FromArgb(197, 207, 224);
        label.Font = new Font("Segoe UI", 9.5f);
        label.BackColor = Color.Transparent;
        label.Margin = new Padding(0);
        badge.Controls.Add(label);
        return badge;
    }

    private static Control CreateStatCard(string title, Label valueLabel, Color? valueColor = null)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 9,
            FillColor = CardBackgroundAlt,
            BorderColor = Color.FromArgb(33, 55, 88),
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 8, 8),
            MinimumSize = new Size(0, 82),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI", 9f),
            ForeColor = MutedText,
            Margin = new Padding(0),
        }, 0, 0);

        valueLabel.AutoSize = true;
        valueLabel.Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold);
        valueLabel.ForeColor = valueColor ?? BodyText;
        valueLabel.Margin = new Padding(0, 8, 0, 0);
        layout.Controls.Add(valueLabel, 0, 1);

        return card;
    }

    private static Control CreateInfoLine(string title, Label valueLabel)
    {
        var line = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 14),
        };

        line.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = MutedText,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Margin = new Padding(0),
        }, 0, 0);

        valueLabel.AutoSize = false;
        valueLabel.Dock = DockStyle.Top;
        valueLabel.Height = 22;
        valueLabel.AutoEllipsis = true;
        valueLabel.ForeColor = BodyText;
        valueLabel.Font = new Font("Segoe UI", 10f);
        valueLabel.Margin = new Padding(0, 6, 0, 0);
        line.Controls.Add(valueLabel, 0, 1);

        return line;
    }

    private void ReplayList_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(CardBackground);
        using var headerFont = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? string.Empty,
            headerFont,
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 4, e.Bounds.Width - 16, e.Bounds.Height - 4),
            MutedText,
            GetTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left) | TextFormatFlags.VerticalCenter);
    }

    private void ReplayList_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null)
            return;

        var selected = e.Item.Selected || e.Item.Checked;
        var hovered = e.ItemIndex == hoveredReplayIndex;
        var rowBounds = e.Item.Bounds;
        var cellBounds = e.Bounds;
        var rowRect = new Rectangle(rowBounds.X + 6, rowBounds.Y + 3, replayList.ClientSize.Width - 14, rowBounds.Height - 6);
        var cellRect = new Rectangle(cellBounds.X + 10, cellBounds.Y + 3, cellBounds.Width - 16, cellBounds.Height - 6);

        using var baseBrush = new SolidBrush(selected
            ? Color.FromArgb(30, 58, 112)
            : hovered
                ? Color.FromArgb(18, 34, 58)
                : CardBackground);

        if (e.ColumnIndex == 0)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedPanel.CreateRoundedRectangle(rowRect, 7);
            e.Graphics.FillPath(baseBrush, path);

            using var borderPen = new Pen(selected ? AccentBlue : Color.FromArgb(29, 46, 73));
            e.Graphics.DrawPath(borderPen, path);
        }

        var textColor = BodyText;
        if (e.ColumnIndex == 4)
            textColor = Color.FromArgb(91, 166, 255);
        else if (e.ColumnIndex == 5 && int.TryParse(e.SubItem.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var miss))
            textColor = miss == 0 ? GoodGreen : WarningOrange;

        var flags = GetTextFlags(replayList.Columns[e.ColumnIndex].TextAlign) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

        if (e.ColumnIndex == 0)
        {
            DrawReplayCheckBox(e.Graphics, e.Item.Checked, new Point(cellRect.X, cellRect.Y + 8));
            cellRect.X += 34;
            cellRect.Width -= 34;
        }

        var text = e.SubItem.Text;
        if (e.ColumnIndex == 8)
            text = "Open";

        TextRenderer.DrawText(e.Graphics, text, replayList.Font, cellRect, textColor, flags);
    }

    private static TextFormatFlags GetTextFlags(HorizontalAlignment alignment)
    {
        var flags = TextFormatFlags.NoPrefix;
        return alignment switch
        {
            HorizontalAlignment.Right => flags | TextFormatFlags.Right,
            HorizontalAlignment.Center => flags | TextFormatFlags.HorizontalCenter,
            _ => flags | TextFormatFlags.Left,
        };
    }

    private static void DrawReplayCheckBox(Graphics graphics, bool isChecked, Point location)
    {
        var rect = new Rectangle(location.X, location.Y, 20, 20);
        using var box = RoundedPanel.CreateRoundedRectangle(rect, 4);
        using var fill = new SolidBrush(isChecked ? AccentBlue : Color.FromArgb(9, 18, 33));
        using var border = new Pen(isChecked ? Color.FromArgb(94, 160, 255) : Color.FromArgb(92, 111, 142), 1.5f);
        graphics.FillPath(fill, box);
        graphics.DrawPath(border, box);

        if (!isChecked)
            return;

        using var checkPen = new Pen(Color.White, 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLines(checkPen, new[]
        {
            new Point(rect.X + 5, rect.Y + 10),
            new Point(rect.X + 9, rect.Y + 14),
            new Point(rect.X + 15, rect.Y + 6),
        });
    }

    private void ReplayList_MouseMove(object? sender, MouseEventArgs e)
    {
        var item = replayList.GetItemAt(e.X, e.Y);
        var index = item?.Index ?? -1;
        if (index == hoveredReplayIndex)
            return;

        hoveredReplayIndex = index;
        replayList.Invalidate();
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
            detailBeatmapLabel.Text = string.IsNullOrWhiteSpace(snapshot.BeatmapChecksum) ? "-" : snapshot.BeatmapChecksum;

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
                    replayCountLabel.Text = $"{replays.Count} replay{(replays.Count == 1 ? string.Empty : "s")} found";
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
            item.SubItems.Add(replay.MaxCombo.ToString(CultureInfo.InvariantCulture) + " x");
            item.SubItems.Add(replay.Rank);
            item.SubItems.Add(replay.FilePath);
            item.Tag = replay;
            item.Checked = string.Equals(replay.FilePath, selectedReplayPath, StringComparison.OrdinalIgnoreCase);
            replayList.Items.Add(item);
        }

        replayList.EndUpdate();
        fillingReplayList = false;
        UpdateSelectedReplayDetails(GetDetailReplay());
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
            selectedReplayBadgeLabel.Text = $"Selected replay: {replay.Player} / {replay.Score.ToString("N0", CultureInfo.InvariantCulture)} / {(replay.Accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture)}%";
            UpdateSelectedReplayDetails(replay);
            replayList.Invalidate();
            return;
        }

        lock (replayLock)
        {
            if (string.Equals(selectedReplayPath, replay.FilePath, StringComparison.OrdinalIgnoreCase))
                selectedReplayPath = string.Empty;
        }

        statusLabel.Text = "Selected replay cleared";
        selectedReplayBadgeLabel.Text = "Selected replay: auto best";
        UpdateSelectedReplayDetails(GetDetailReplay());
        replayList.Invalidate();
    }

    private ReplayEntry? GetDetailReplay()
    {
        if (!string.IsNullOrWhiteSpace(selectedReplayPath))
        {
            foreach (ListViewItem item in replayList.Items)
            {
                if (item.Tag is ReplayEntry replay && string.Equals(replay.FilePath, selectedReplayPath, StringComparison.OrdinalIgnoreCase))
                    return replay;
            }
        }

        if (replayList.SelectedItems.Count > 0 && replayList.SelectedItems[0].Tag is ReplayEntry selected)
            return selected;

        return replayList.Items.Count > 0 && replayList.Items[0].Tag is ReplayEntry first ? first : null;
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
            detailFileLabel.Text = "-";
            detailBeatmapLabel.Text = string.IsNullOrWhiteSpace(currentSnapshot.BeatmapChecksum) ? "-" : currentSnapshot.BeatmapChecksum;
            return;
        }

        detailTitleLabel.Text = replay.Player;
        detailSubtitleLabel.Text = FormatDate(replay.Date);
        detailScoreLabel.Text = replay.Score.ToString("N0", CultureInfo.InvariantCulture);
        detailAccuracyLabel.Text = (replay.Accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        detailRankLabel.Text = replay.Rank;
        detailComboLabel.Text = replay.MaxCombo.ToString(CultureInfo.InvariantCulture) + " x";
        detailMissLabel.Text = GetStatistic(replay, "Miss").ToString(CultureInfo.InvariantCulture);
        detailModsLabel.Text = replay.ModsText;
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

    private sealed class RoundedPanel : Panel
    {
        public int Radius { get; set; } = 12;
        public Color FillColor { get; set; } = CardBackground;
        public Color BorderColor { get; set; } = Color.FromArgb(30, 48, 77);

        public RoundedPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = CreateRoundedRectangle(rect, Radius);
            using var fill = new SolidBrush(FillColor);
            using var border = new Pen(BorderColor);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }

        public static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(1, radius * 2);
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class RoundedButton : Button
    {
        public int Radius { get; set; } = 9;
        public Color FillColor { get; set; } = AccentBlue;
        public Color BorderColor { get; set; } = AccentBlueSoft;

        public RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            var fillColor = Enabled ? FillColor : Color.FromArgb(28, 38, 55);

            using var path = RoundedPanel.CreateRoundedRectangle(rect, Radius);
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(BorderColor);
            pevent.Graphics.FillPath(fill, path);
            pevent.Graphics.DrawPath(border, path);
            TextRenderer.DrawText(pevent.Graphics, Text, Font, rect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private sealed class PulseIconControl : Control
    {
        public PulseIconControl()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(AccentBlue, 2.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            var y = Height / 2;
            e.Graphics.DrawLines(pen, new[]
            {
                new Point(1, y),
                new Point(11, y),
                new Point(16, y - 13),
                new Point(22, y + 15),
                new Point(29, y - 5),
                new Point(36, y - 5),
                new Point(43, y - 5),
            });
        }
    }

    private sealed class ReplayArtPanel : Control
    {
        public int Radius { get; set; } = 10;

        public ReplayArtPanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedPanel.CreateRoundedRectangle(rect, Radius);
            using var brush = new LinearGradientBrush(rect, Color.FromArgb(44, 89, 220), Color.FromArgb(150, 59, 210), 45f);
            e.Graphics.FillPath(brush, path);

            using var glow = new Pen(Color.FromArgb(190, 214, 236, 255), 3f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            e.Graphics.SetClip(path);
            e.Graphics.DrawLine(glow, Width - 18, 8, 24, Height - 14);

            using var soft = new Pen(Color.FromArgb(95, 255, 255, 255), 1.5f);
            e.Graphics.DrawLine(soft, Width - 48, 18, 50, Height - 26);
            e.Graphics.ResetClip();

            using var border = new Pen(Color.FromArgb(58, 90, 144));
            e.Graphics.DrawPath(border, path);
        }
    }
}
