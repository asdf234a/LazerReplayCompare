using System.Globalization;
using Avalonia.Media;

namespace LazerReplayCompare;

/// <summary>One of the map card's numbers with its bar (notes, hold notes, keys, OD, HP).</summary>
public sealed record MapStat(string Name, string Value, double Fraction)
{
    /// <summary>The bar's fill, in percent.</summary>
    public double Percent => Math.Clamp(Fraction, 0, 1) * 100;
}

/// <summary>
/// The map page's card: what the map is and how hard it is, laid out the way osu! shows a beatmap (status, title and
/// artist, length and BPM, the difficulty line, then the numbers with bars), led by the Sunny rating and the dan.
/// </summary>
public sealed class MapCardViewModel : ObservableObject
{
    /// <param name="map">The library's entry, or null when the library doesn't know the map (the game's own texts are shown).</param>
    public MapCardViewModel(MapInfo? map, MapDifficulty? difficulty, MapRecord? record, string numberKind, string fallbackTitle, string fallbackSubtitle, bool browsing, string liveMapText)
    {
        Map = map;
        IsBrowsing = browsing;
        LiveMapText = liveMapText;
        HasMap = map != null;
        Title = map?.TitleEnglish is { Length: > 0 } title ? title : fallbackTitle;
        Artist = map?.ArtistEnglish is { Length: > 0 } artist ? artist : fallbackSubtitle;

        if (map == null || difficulty == null)
            return;

        var row = new MapRow(map, difficulty, record?.Best, record?.Plays ?? 0, record?.LastPlayedTicks ?? 0, numberKind);
        NumberText = row.NumberText;
        NumberBrush = row.NumberBrush;
        NumberName = numberKind switch
        {
            LibraryViewModel.NumberDan => "Dan level",
            LibraryViewModel.NumberStars => "Stars",
            _ => "Sunny",
        };
        DanText = difficulty.Dan ?? (map.Keys is 4 or 6 or 7 ? string.Empty : $"{map.Keys}K has no dans");

        StatusLabel = string.IsNullOrEmpty(map.Status) ? string.Empty : MapStatus.Label(map.Status).ToUpperInvariant();
        LengthText = MapRow.FormatLength(map.LengthMs);
        BpmText = map.Bpm > 0 ? $"{Math.Round(map.Bpm):0} BPM" : string.Empty;
        PlaysText = (record?.Plays ?? 0) == 1 ? "1 play" : $"{record?.Plays ?? 0:N0} plays";
        LastPlayedText = record is { LastPlayedTicks: > 0 } ? "last played " + new DateTime(record.LastPlayedTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

        StarsText = map.Stars >= 0 ? map.Stars.ToString("0.00", CultureInfo.InvariantCulture) : "–";
        StarsBrush = map.Stars >= 0 ? DifficultyColors.ForStars(map.Stars) : DifficultyColors.Neutral;
        VersionText = map.Version;
        Mapper = map.Mapper;

        int notes = Math.Max(map.Objects, 0);
        double lnRatio = difficulty.LnRatio;
        int holds = map.Holds > 0 ? map.Holds : (int)Math.Round(notes * lnRatio);
        ContentStats =
        [
            new MapStat("Notes", notes.ToString("N0", CultureInfo.InvariantCulture), notes / 3000.0),
            new MapStat("Hold notes", holds.ToString("N0", CultureInfo.InvariantCulture), notes > 0 ? (double)holds / notes : 0),
            new MapStat("LN ratio", (lnRatio * 100).ToString("0", CultureInfo.InvariantCulture) + "%", lnRatio),
        ];
        SettingStats =
        [
            new MapStat("Keys", map.Keys.ToString(CultureInfo.InvariantCulture), map.Keys / 10.0),
            new MapStat("Accuracy (OD)", map.Od.ToString("0.#", CultureInfo.InvariantCulture), map.Od / 10.0),
            new MapStat("HP drain", map.Hp.ToString("0.#", CultureInfo.InvariantCulture), map.Hp / 10.0),
        ];

        if (record?.Best is { } best)
        {
            BestRank = Ranks.Letter(best.Rank);
            BestBrush = DifficultyColors.ForRank(best.Rank);
            BestText = $"{ShownAccuracy.Text(best.Accuracy, best.Stable)} at {best.RateText}";
        }
    }

    public MapInfo? Map { get; }
    public bool HasMap { get; }

    /// <summary>The page shows a map picked in the library, not the one the game has selected.</summary>
    public bool IsBrowsing { get; }
    public bool IsNowPlaying => !IsBrowsing;

    /// <summary>The map the game is on, while another one is shown.</summary>
    public string LiveMapText { get; }

    public string Title { get; }
    public string Artist { get; }
    public string NumberText { get; } = "–";
    public IBrush NumberBrush { get; } = DifficultyColors.Neutral;
    public string NumberName { get; } = "Sunny";
    public string DanText { get; } = string.Empty;
    public string StatusLabel { get; } = string.Empty;
    public bool HasStatus => StatusLabel.Length > 0;
    public string LengthText { get; } = string.Empty;
    public string BpmText { get; } = string.Empty;
    public string PlaysText { get; } = string.Empty;
    public string LastPlayedText { get; } = string.Empty;
    public string StarsText { get; } = string.Empty;
    public IBrush StarsBrush { get; } = DifficultyColors.Neutral;
    public string VersionText { get; } = string.Empty;
    public string Mapper { get; } = string.Empty;
    public bool HasMapper => Mapper.Length > 0;
    public IReadOnlyList<MapStat> ContentStats { get; } = [];
    public IReadOnlyList<MapStat> SettingStats { get; } = [];
    public string BestRank { get; } = string.Empty;
    public IBrush BestBrush { get; } = DifficultyColors.Neutral;
    public string BestText { get; } = string.Empty;
    public bool HasBest => BestRank.Length > 0;
}

/// <summary>One score of a beatmap's online leaderboard, as the Online ranking tab lists it.</summary>
public sealed class OnlineScoreRow : ObservableObject
{
    public OnlineScoreRow(OnlineScore score, bool isUser)
    {
        Score = score;
        IsUser = isUser;
    }

    public OnlineScore Score { get; }

    /// <summary>The user's own score (by player name).</summary>
    public bool IsUser { get; }

    public string PositionText => Score.Position.ToString(CultureInfo.InvariantCulture);
    public string Player => Score.Player;
    public string DateText => Score.Date is { } date ? date.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
    public string ModsText => Score.Mods.Count == 0 ? "NM" : string.Join(" ", Score.Mods);
    public string ScoreText => Score.TotalScore.ToString("N0", CultureInfo.InvariantCulture);
    public string AccuracyText => ShownAccuracy.Text(Score.Accuracy, stable: false);
    public string MissText => Score.Miss.ToString(CultureInfo.InvariantCulture);
    public string ComboText => Score.MaxCombo.ToString("N0", CultureInfo.InvariantCulture);
    public string RankText => Ranks.Letter(Score.Rank);
    public IBrush RankBrush => DifficultyColors.ForRank(Score.Rank);
    public bool CanDownload => Score.HasReplay && !IsSaved && !IsBusy;

    private bool isSaved;

    /// <summary>Its replay is in the app's replays folder already.</summary>
    public bool IsSaved
    {
        get => isSaved;
        set
        {
            if (Set(ref isSaved, value))
                changed();
        }
    }

    private bool isBusy;

    public bool IsBusy
    {
        get => isBusy;
        set
        {
            if (Set(ref isBusy, value))
                changed();
        }
    }

    private bool isTarget;

    public bool IsTarget
    {
        get => isTarget;
        set => Set(ref isTarget, value);
    }

    private string note = string.Empty;

    /// <summary>Why the replay couldn't be had ("no replay online", "couldn't download").</summary>
    public string Note
    {
        get => note;
        set
        {
            if (Set(ref note, value))
                changed();
        }
    }

    public string Tooltip => Note.Length > 0 ? Note
        : IsSaved ? "Downloaded: click to compare against it"
        : !Score.HasReplay ? "osu! has no replay of this score"
        : "Click to download this replay and compare against it";

    private void changed()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(ShowDownload));
        OnPropertyChanged(nameof(ShowSaved));
    }

    public bool ShowDownload => !IsSaved && Score.HasReplay;
    public bool ShowSaved => IsSaved;
}
