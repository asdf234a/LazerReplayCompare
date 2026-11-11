using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LazerReplayCompare;

/// <summary>Short forms of dan names, for the map page's narrow side card.</summary>
public static class DanNames
{
    // the letter dans' own signs: Greek letters in 4K; in 7K γ, Ψ and Ω are Jinjin's (the courses' forum thread).
    // Stellium has none there ("the Epsilon dan of 7K"): a star stands for it.
    private static readonly Dictionary<string, string> symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alpha"] = "α", ["Beta"] = "β", ["Gamma"] = "γ", ["Delta"] = "δ", ["Epsilon"] = "ε",
        ["Zeta"] = "ζ", ["Eta"] = "η", ["Theta"] = "θ", ["Iota"] = "ι", ["Kappa"] = "κ",
        ["Azimuth"] = "Ψ", ["Zenith"] = "Ω", ["Stellium"] = "★",
    };

    /// <summary>The sign of a letter dan ("Regular Azimuth" is Ψ, "Emik Zeta" is ζ), or null for a numbered or otherwise named one.</summary>
    public static string? Symbol(string dan)
    {
        var last = dan.Contains(' ') ? dan[(dan.LastIndexOf(' ') + 1)..] : dan;
        return symbols.GetValueOrDefault(last);
    }

    /// <summary>"Regular 9 low" is "Reg 9 low"; a letter dan is its sign alone ("Regular Gamma mid" is "γ mid", "LN Zenith low" is "LN Ω low").</summary>
    public static string Short(string band)
    {
        if (band.Length == 0)
            return band;

        string lead = band[..(band.Length - band.TrimStart('<', '>', ' ').Length)];
        string body = band[lead.Length..];
        string dan = Dans.Dan(body);
        string part = body[dan.Length..];

        if (Symbol(dan) is { } symbol)
            return lead + (dan.StartsWith("LN ", StringComparison.OrdinalIgnoreCase) ? "LN " : string.Empty) + symbol + part;

        if (dan.StartsWith("Regular ", StringComparison.OrdinalIgnoreCase))
            dan = "Reg " + dan["Regular ".Length..];
        else if (dan.StartsWith("Reform ", StringComparison.OrdinalIgnoreCase))
            dan = "Ref " + dan["Reform ".Length..];

        return lead + dan + part;
    }
}

/// <summary>A filter of the row under the library's search box ("Played", "S", "LN").</summary>
public sealed class QuickFilter : ObservableObject
{
    private readonly Action<QuickFilter> changed;
    private bool isOn;

    public QuickFilter(string key, string name, string tip, Action<QuickFilter> changed, IBrush? brush = null)
    {
        Key = key;
        Name = name;
        Tip = tip;
        Brush = brush;
        this.changed = changed;
    }

    public string Key { get; }
    public string Name { get; }
    public string Tip { get; }
    public IBrush? Brush { get; }

    public bool IsOn
    {
        get => isOn;
        set
        {
            if (Set(ref isOn, value))
                changed(this);
        }
    }
}

/// <summary>One map in the library's list.</summary>
public sealed class MapRow : ObservableObject
{
    public MapRow(MapInfo map, MapDifficulty difficulty, RateRecord? best, int plays, long lastPlayedTicks, string numberKind)
    {
        Map = map;
        Difficulty = difficulty;
        Best = best;
        Plays = plays;
        LastPlayedTicks = lastPlayedTicks;

        (Number, NumberText, _) = number(map, difficulty, numberKind);

        // The colour is the shown number's: a Sunny rating's place on the spectrum; a dan level's as the Sunny rating maps
        // of that level have (surveyed: see DifficultyColors), so the two agree; osu!'s stars as the game colours them.
        double onSpectrum = numberKind switch
        {
            LibraryViewModel.NumberStars => map.Stars >= 0 ? map.Stars : double.NaN,
            LibraryViewModel.NumberDan when difficulty.DanLevel is { } level => DifficultyColors.SunnyOfDanLevel(map.Keys, level) * DifficultyColors.SunnyScale,
            _ => difficulty.Sunny is { } sunny ? sunny * DifficultyColors.SunnyScale : double.NaN,
        };
        NumberBrush = double.IsFinite(onSpectrum) ? DifficultyColors.ForStars(onSpectrum) : DifficultyColors.Neutral;
        NumberBadge = DifficultyColors.Badge(onSpectrum);
        NumberBadgeText = DifficultyColors.BadgeText(onSpectrum);
        Haystack = string.Join('\n', map.Title, map.TitleUnicode, map.Artist, map.ArtistUnicode, map.Version, map.Mapper, map.Tags, map.Source).ToLowerInvariant();
    }

    public MapInfo Map { get; }
    public MapDifficulty Difficulty { get; }
    public RateRecord? Best { get; }
    public int Plays { get; }
    public long LastPlayedTicks { get; }
    public string Haystack { get; }

    public string Md5 => Map.Md5;

    /// <summary>The number the list is led by (Sunny, the dan number or osu!'s stars); NaN when there is none.</summary>
    public double Number { get; }
    public string NumberText { get; }
    public IBrush NumberBrush { get; }

    /// <summary>The box the number sits in, and the number's colour on it (as osu! shows a star rating).</summary>
    public IBrush NumberBadge { get; }
    public IBrush NumberBadgeText { get; }

    public string DanText => Difficulty.Dan ?? string.Empty;

    /// <summary>The dan as the narrow side card writes it ("Reg 9 low", "γ mid").</summary>
    public string ShortDanText => DanNames.Short(DanText);

    /// <summary>The LN dan, for a map of 20 % hold notes or more ("" otherwise).</summary>
    public string LnDanText => Difficulty.Ln ?? string.Empty;
    public string Title => Map.TitleEnglish.Length > 0 ? Map.TitleEnglish : "(no title)";
    public string DiffText => $"[{Map.Version}]";
    public string Sub => string.IsNullOrWhiteSpace(Map.Mapper) ? Map.ArtistEnglish : $"{Map.ArtistEnglish} · mapped by {Map.Mapper}";
    public string ShortSub => $"[{Map.Version}] · {Map.ArtistEnglish}";
    public string KeysText => $"{Map.Keys}K";
    public string StarsText => Map.Stars >= 0 ? Map.Stars.ToString("0.00", CultureInfo.InvariantCulture) : "–";
    public string OdText => Map.Od.ToString("0.#", CultureInfo.InvariantCulture);
    public string BpmText => Map.Bpm > 0 ? Math.Round(Map.Bpm).ToString("0", CultureInfo.InvariantCulture) : "–";
    public string LengthText => FormatLength(Map.LengthMs);
    public string RankText => Best == null ? "–" : Ranks.Letter(Best.Rank);
    public IBrush RankBrush => Best == null ? DifficultyColors.Neutral : DifficultyColors.ForRank(Best.Rank);
    public string AccText => Best == null ? string.Empty : ShownAccuracy.Text(Best.Accuracy, Best.Stable);
    public string PlaysText => Plays.ToString("N0", CultureInfo.InvariantCulture);
    public string BestTooltip => Best == null ? "Not played yet" : $"Best at {Best.RateText}{(Best.Stable ? " in osu!stable" : Best.Classic ? " Classic" : "")} · {Best.Score:N0}";

    private bool isNowPlaying;

    /// <summary>The map the game has selected.</summary>
    public bool IsNowPlaying
    {
        get => isNowPlaying;
        set => Set(ref isNowPlaying, value);
    }

    private Bitmap? background;

    /// <summary>The map's background, loaded while the row is on screen.</summary>
    public Bitmap? Background
    {
        get => background;
        set => Set(ref background, value);
    }

    public static string FormatLength(double milliseconds)
    {
        if (milliseconds <= 0)
            return "–";

        var time = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private static (double, string, IBrush) number(MapInfo map, MapDifficulty difficulty, string kind)
    {
        static string text(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        switch (kind)
        {
            case LibraryViewModel.NumberStars:
                return map.Stars >= 0 ? (map.Stars, text(map.Stars), DifficultyColors.ForStars(map.Stars)) : (double.NaN, "–", DifficultyColors.Neutral);

            case LibraryViewModel.NumberDan when difficulty.DanLevel is { } level:
                return (level, text(level), DifficultyColors.ForDanLevel(level));

            case LibraryViewModel.NumberDan when difficulty.Sunny is { } sunny:
                // no estimator number for this map: its Sunny rating stands in, marked
                return (double.NaN, "*" + text(sunny), DifficultyColors.ForSunny(sunny));

            default:
                return difficulty.Sunny is { } star ? (star, text(star), DifficultyColors.ForSunny(star)) : (double.NaN, "–", DifficultyColors.Neutral);
        }
    }
}

/// <summary>A group of the library (a dan, a Sunny range, a collection …).</summary>
public sealed class LibraryGroup : ObservableObject
{
    public LibraryGroup(string name, List<MapRow> rows, double order)
    {
        Name = name;
        Rows = rows;
        Order = order;

        var counts = new int[Ranks.Letters.Length];
        foreach (var row in rows)
            counts[row.Best == null ? counts.Length - 1 : Math.Clamp(6 - Ranks.Order(row.Best.Rank), 0, counts.Length - 2)]++;
        RankCounts = counts;
    }

    public string Name { get; }
    public List<MapRow> Rows { get; }
    public double Order { get; }
    public IReadOnlyList<int> RankCounts { get; }
    public string CountText => Rows.Count.ToString("N0", CultureInfo.InvariantCulture);

    private string lastWord => Name.Contains(' ') ? Name[(Name.LastIndexOf(' ') + 1)..] : Name;
    private string? symbol => DanNames.Symbol(Name);
    private bool hasShortForm => symbol != null || char.IsDigit(lastWord[^1]);

    /// <summary>
    /// The folded card's label: the dan's number under its kind ("Reform", "LN"), or a letter dan's sign alone ("Emik
    /// Zeta" is ζ, "Regular Azimuth" is Ψ). A name with neither is written whole, small, on as many lines as it needs.
    /// </summary>
    public string RailShort => symbol ?? (hasShortForm ? lastWord : Name);

    public string RailKind
    {
        get
        {
            if (!hasShortForm || !Name.Contains(' '))
                return string.Empty;

            var before = Name[..Name.LastIndexOf(' ')];
            if (symbol == null)
                return before.Replace("Regular", "Reg").Replace("Reform", "Ref");

            // a letter dan is its sign; only what tells two of them apart stays ("7K", "LN")
            return string.Join(' ', before.Split(' ').Where(word => word == "LN" || (word.Length >= 2 && word[^1] == 'K' && char.IsDigit(word[0]))));
        }
    }

    public double RailFontSize => hasShortForm ? 18 : 11;
    public IBrush RailBrush => Rows.Count == 0 ? DifficultyColors.Neutral : Rows[Rows.Count / 2].NumberBrush;

    private bool isCurrent;

    /// <summary>The group the shown map is in.</summary>
    public bool IsCurrent
    {
        get => isCurrent;
        set => Set(ref isCurrent, value);
    }
}

public sealed record LibraryOption(string Key, string Name)
{
    public override string ToString() => Name;
}

public enum LibrarySortKey
{
    Number,
    Title,
    Keys,
    Stars,
    Od,
    Bpm,
    Length,
    Rank,
    Plays,
}

/// <summary>The library: every map, grouped, filtered and sorted. Shown whole (LibraryView) and as the map page's side card.</summary>
public sealed class LibraryViewModel : ObservableObject
{
    public const string NumberSunny = "sunny";
    public const string NumberDan = "dan";
    public const string NumberStars = "stars";

    private readonly LibraryService service;
    private List<MapRow> all = new();

    public LibraryViewModel(LibraryService service)
    {
        this.service = service;
        selectedGroupBy = GroupByOptions[0];
        selectedNumber = NumberOptions[0];
        selectedKeys = KeysOptions[0];
        selectedRate = RateOptions[0];
        selectedCollection = allMaps;
        CollectionOptions.Add(allMaps);

        PlayFilters =
        [
            new QuickFilter("played", "Played", "Maps you have a score on", quickChanged),
            new QuickFilter("unplayed", "Not played", "Maps without a score of yours", quickChanged),
            new QuickFilter("month", "This month", "Played in the last 31 days", quickChanged),
        ];
        RankFilters = Ranks.Letters.Where(letter => letter.Length > 0)
                           .Select(letter => new QuickFilter("rank", letter, $"Maps whose best rank is {letter} (several can be picked)", quickChanged, DifficultyColors.ForRank(letter)))
                           .ToList();
        KindFilters =
        [
            new QuickFilter("rice", "Rice", "Under 20% hold notes", quickChanged),
            new QuickFilter("ln", "LN", "20% hold notes or more", quickChanged),
        ];
    }

    // --- Quick filters (the row under the search box) ---

    public IReadOnlyList<QuickFilter> PlayFilters { get; }
    public IReadOnlyList<QuickFilter> RankFilters { get; }
    public IReadOnlyList<QuickFilter> KindFilters { get; }

    private IEnumerable<QuickFilter> quickFilters => PlayFilters.Concat(RankFilters).Concat(KindFilters);
    public bool HasQuickFilters => quickFilters.Any(f => f.IsOn);

    private bool settingQuick;

    private void quickChanged(QuickFilter filter)
    {
        if (settingQuick)
            return;

        settingQuick = true;
        if (filter.IsOn)
        {
            // what cannot hold together gives way: played or not, a rank needs a play, rice or LN
            QuickFilter play(string key) => PlayFilters.First(f => f.Key == key);
            switch (filter.Key)
            {
                case "played":
                case "month":
                    play("unplayed").IsOn = false;
                    break;

                case "unplayed":
                    play("played").IsOn = false;
                    play("month").IsOn = false;
                    foreach (var rank in RankFilters)
                        rank.IsOn = false;
                    break;

                case "rank":
                    play("unplayed").IsOn = false;
                    break;

                case "rice":
                case "ln":
                    foreach (var kind in KindFilters)
                        kind.IsOn = kind == filter;
                    break;
            }
        }

        settingQuick = false;
        OnPropertyChanged(nameof(HasQuickFilters));
        otherFiltersChanged();
        Rebuild();
    }

    public void ClearQuickFilters()
    {
        settingQuick = true;
        foreach (var filter in quickFilters)
            filter.IsOn = false;
        settingQuick = false;
        OnPropertyChanged(nameof(HasQuickFilters));
        otherFiltersChanged();
        Rebuild();
    }

    /// <summary>What the quick filters let through, as they stand now (read on the UI thread, run on the build's).</summary>
    private Func<MapRow, bool> quickTest()
    {
        if (!HasQuickFilters)
            return _ => true;

        bool on(string key) => quickFilters.Any(f => f.Key == key && f.IsOn);
        bool played = on("played"), unplayed = on("unplayed"), month = on("month"), ln = on("ln"), rice = on("rice");
        var ranks = RankFilters.Where(f => f.IsOn).Select(f => f.Name).ToHashSet();
        long since = DateTime.UtcNow.AddDays(-31).Ticks;

        return row =>
        {
            bool has = row.Plays > 0 || row.Best != null;
            return (!played || has) && (!unplayed || !has) && (!month || row.LastPlayedTicks >= since)
                   && (ranks.Count == 0 || (row.Best != null && ranks.Contains(Ranks.Letter(row.Best.Rank))))
                   && (!ln || row.Difficulty.LnRatio >= MapDifficulty.LnShare) && (!rice || row.Difficulty.LnRatio < MapDifficulty.LnShare);
        };
    }

    /// <summary>A setting the user picked changed (the window saves it).</summary>
    public event Action? OptionsChanged;

    // --- Options ---

    public IReadOnlyList<LibraryOption> GroupByOptions { get; } =
    [
        new("dan", "Dan"),
        new("lndan", "LN dan"),
        new("sunny", "Sunny SR"),
        new("stars", "osu! stars"),
        new("collection", "Collection"),
        new("rank", "Best rank"),
        new("recent", "Recently played"),
        new("none", "No grouping"),
    ];

    public IReadOnlyList<LibraryOption> NumberOptions { get; } =
    [
        new(NumberSunny, "Sunny"),
        new(NumberDan, "Dan level"),
        new(NumberStars, "Stars"),
    ];

    public IReadOnlyList<LibraryOption> KeysOptions { get; } =
    [
        new("0", "All keys"),
        new("4", "4K"),
        new("6", "6K"),
        new("7", "7K"),
        new("others", "Others"),
    ];

    public IReadOnlyList<LibraryOption> RateOptions { get; } =
    [
        new("", "All rates"),
        new("1", "1.0×+"),
        new("1.1", "1.1×+"),
        new("1.2", "1.2×+"),
        new("1.3", "1.3×+"),
        new("1.5", "1.5×+"),
    ];

    private static readonly LibraryOption allMaps = new("", "All maps");
    public ObservableCollection<LibraryOption> CollectionOptions { get; } = new();

    private LibraryOption selectedGroupBy;
    private LibraryOption selectedNumber;
    private LibraryOption selectedKeys;
    private LibraryOption selectedRate;
    private LibraryOption selectedCollection;
    private bool filling;

    public LibraryOption? SelectedGroupBy
    {
        get => selectedGroupBy;
        set => setOption(ref selectedGroupBy, value);
    }

    public LibraryOption? SelectedNumber
    {
        get => selectedNumber;
        set
        {
            if (setOption(ref selectedNumber, value))
            {
                OnPropertyChanged(nameof(NumberHeader));
                OnPropertyChanged(nameof(NumberKind));
                Reload();   // the rows carry the number
            }
        }
    }

    public LibraryOption? SelectedKeys
    {
        get => selectedKeys;
        set => setOption(ref selectedKeys, value);
    }

    public LibraryOption? SelectedRate
    {
        get => selectedRate;
        set
        {
            if (setOption(ref selectedRate, value))
                Reload();   // the rows carry the best play at that rate
        }
    }

    public LibraryOption? SelectedCollection
    {
        get => selectedCollection;
        set
        {
            if (setOption(ref selectedCollection, value))
                otherFiltersChanged();
        }
    }

    // --- What narrows the lists besides the search and the keys (the side card says so, and can undo it) ---

    public bool HasOtherFilters => HasQuickFilters || selectedCollection.Key.Length > 0;

    public string OtherFiltersText
    {
        get
        {
            var parts = quickFilters.Where(f => f.IsOn).Select(f => f.Name).ToList();
            if (selectedCollection.Key.Length > 0)
                parts.Insert(0, selectedCollection.Name);
            return "Only: " + string.Join(", ", parts);
        }
    }

    private void otherFiltersChanged()
    {
        OnPropertyChanged(nameof(HasOtherFilters));
        OnPropertyChanged(nameof(OtherFiltersText));
    }

    public void ClearOtherFilters()
    {
        selectedCollection = allMaps;
        OnPropertyChanged(nameof(SelectedCollection));
        ClearQuickFilters();
    }

    private string nowPlayingText = string.Empty;

    /// <summary>The map the game is on, for the library's "go to it" button ("" when there is none).</summary>
    public string NowPlayingText
    {
        get => nowPlayingText;
        set
        {
            if (Set(ref nowPlayingText, value ?? string.Empty))
                OnPropertyChanged(nameof(HasNowPlaying));
        }
    }

    public bool HasNowPlaying => nowPlayingText.Length > 0;

    public string NumberKind => selectedNumber.Key;

    private bool setOption(ref LibraryOption field, LibraryOption? value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        // a picker briefly loses its selection while its list is rebuilt
        if (value == null || filling || !Set(ref field, value, name))
            return false;

        Rebuild();
        OptionsChanged?.Invoke();
        return true;
    }

    /// <summary>Restores saved choices (unknown ones are left as they are).</summary>
    public void Restore(string groupBy, string number, string keys, string rate)
    {
        filling = true;
        selectedGroupBy = GroupByOptions.FirstOrDefault(o => o.Key == groupBy) ?? selectedGroupBy;
        selectedNumber = NumberOptions.FirstOrDefault(o => o.Key == number) ?? selectedNumber;
        selectedRate = RateOptions.FirstOrDefault(o => o.Key == rate) ?? selectedRate;
        selectedKeys = KeysOptions.FirstOrDefault(o => o.Key == keys) ?? selectedKeys;
        filling = false;
        OnPropertyChanged(nameof(SelectedGroupBy));
        OnPropertyChanged(nameof(SelectedNumber));
        OnPropertyChanged(nameof(SelectedRate));
        OnPropertyChanged(nameof(SelectedKeys));
        OnPropertyChanged(nameof(NumberHeader));
    }

    public string GroupByKey => selectedGroupBy.Key;
    public string KeysKey => selectedKeys.Key;
    public string RateKey => selectedRate.Key;

    private bool glass;

    /// <summary>The window shows the beatmap's background behind the page (the cards are translucent then).</summary>
    public bool Glass
    {
        get => glass;
        set => Set(ref glass, value);
    }

    private bool showRowBackgrounds = true;

    /// <summary>Each map's background across its row.</summary>
    public bool ShowRowBackgrounds
    {
        get => showRowBackgrounds;
        set => Set(ref showRowBackgrounds, value);
    }

    // --- Search ---

    private string searchText = string.Empty;

    public string SearchText
    {
        get => searchText;
        set
        {
            if (Set(ref searchText, value ?? string.Empty))
                Rebuild();
        }
    }

    private string filterNote = string.Empty;

    /// <summary>The filters understood in the search text ("Sunny above 5 · best rank below S").</summary>
    public string FilterNote
    {
        get => filterNote;
        private set
        {
            if (Set(ref filterNote, value))
                OnPropertyChanged(nameof(HasFilterNote));
        }
    }

    public bool HasFilterNote => FilterNote.Length > 0;

    // --- Sorting ---

    private LibrarySortKey sortKey = LibrarySortKey.Number;
    private bool sortDescending;

    public void SortBy(LibrarySortKey key)
    {
        if (sortKey == key)
            sortDescending = !sortDescending;
        else
        {
            sortKey = key;
            sortDescending = key is LibrarySortKey.Rank or LibrarySortKey.Plays;
        }

        foreach (var name in new[] { nameof(NumberHeader), nameof(TitleHeader), nameof(KeysHeader), nameof(StarsHeader), nameof(OdHeader), nameof(BpmHeader), nameof(LengthHeader), nameof(RankHeader), nameof(PlaysHeader) })
            OnPropertyChanged(name);
        showGroup();
    }

    public string NumberHeader => header(selectedNumber.Name, LibrarySortKey.Number);
    public string TitleHeader => header("Map", LibrarySortKey.Title);
    public string KeysHeader => header("Keys", LibrarySortKey.Keys);
    public string StarsHeader => header("Stars", LibrarySortKey.Stars);
    public string OdHeader => header("OD", LibrarySortKey.Od);
    public string BpmHeader => header("BPM", LibrarySortKey.Bpm);
    public string LengthHeader => header("Length", LibrarySortKey.Length);
    public string RankHeader => header("Best", LibrarySortKey.Rank);
    public string PlaysHeader => header("Plays", LibrarySortKey.Plays);

    private string header(string name, LibrarySortKey key) => sortKey == key ? $"{name} {(sortDescending ? "↓" : "↑")}" : name;

    // --- What is shown ---

    public ObservableCollection<LibraryGroup> Groups { get; } = new();

    private LibraryGroup? selectedGroup;

    public LibraryGroup? SelectedGroup
    {
        get => selectedGroup;
        set
        {
            if (value == null && Groups.Count > 0)
                return;   // the list briefly loses its selection while it is rebuilt

            if (Set(ref selectedGroup, value))
                showGroup();
        }
    }

    private IReadOnlyList<MapRow> rows = Array.Empty<MapRow>();

    /// <summary>The selected group's maps, sorted.</summary>
    public IReadOnlyList<MapRow> Rows
    {
        get => rows;
        private set
        {
            if (Set(ref rows, value))
            {
                OnPropertyChanged(nameof(GroupTitle));
                OnPropertyChanged(nameof(GroupCountText));
            }
        }
    }

    public string GroupTitle => SelectedGroup?.Name ?? "Library";
    public string GroupCountText => rows.Count == 1 ? "1 map" : $"{rows.Count:N0} maps";

    private string statusText = string.Empty;

    /// <summary>How the library stands: reading, rating maps in the background, or how many there are.</summary>
    public string StatusText
    {
        get => statusText;
        private set => Set(ref statusText, value);
    }

    private string totalText = "Search maps";

    public string SearchWatermark
    {
        get => totalText;
        private set => Set(ref totalText, value);
    }

    private string nowPlayingMd5 = string.Empty;

    /// <summary>The map the game has selected (marked in the list).</summary>
    public string NowPlayingMd5
    {
        get => nowPlayingMd5;
        set
        {
            if (!Set(ref nowPlayingMd5, value ?? string.Empty))
                return;

            // (the rows on screen can be an earlier, identical set: see showGroup)
            foreach (var row in all.Concat(rows))
                row.IsNowPlaying = string.Equals(row.Md5, nowPlayingMd5, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string shownMd5 = string.Empty;

    /// <summary>The map the map page shows: its group is marked, and selected when the list is opened.</summary>
    public string ShownMd5
    {
        get => shownMd5;
        set
        {
            if (!Set(ref shownMd5, value ?? string.Empty))
                return;

            markCurrentGroup();
            SelectShownGroup();   // the side card follows the shown map
        }
    }

    /// <summary>Selects the group the shown map is in (when it is in one).</summary>
    public void SelectShownGroup()
    {
        if (Groups.FirstOrDefault(g => g.IsCurrent) is { } group)
            SelectedGroup = group;
    }

    /// <summary>A map was found in the lists (the page scrolls to its row).</summary>
    public event Action<MapRow>? Located;

    private string? pendingLocate;

    /// <summary>
    /// Shows a map in the lists: its group is picked. When the search, the keys or a filter hide it,
    /// <paramref name="force"/> drops those first. False when it isn't shown.
    /// </summary>
    public bool Locate(string md5, bool force)
    {
        if (string.IsNullOrEmpty(md5))
            return false;

        bool has(MapRow row) => string.Equals(row.Md5, md5, StringComparison.OrdinalIgnoreCase);
        if (Groups.FirstOrDefault(g => g.Rows.Any(has)) is { } group)
        {
            SelectedGroup = group;
            if (Rows.FirstOrDefault(has) is { } row)
                Located?.Invoke(row);
            return true;
        }

        if (!force || service.Maps.Find(md5) is not { } map)
            return false;

        // hidden by what is picked: the map's own keys, no search, no collection, no quick filter
        pendingLocate = md5;
        string keys = map.Keys is 4 or 6 or 7 ? map.Keys.ToString(CultureInfo.InvariantCulture) : "others";
        selectedKeys = KeysOptions.First(o => o.Key == keys);
        OnPropertyChanged(nameof(SelectedKeys));
        searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        OptionsChanged?.Invoke();
        ClearOtherFilters();   // (builds the lists again)
        return false;
    }

    public MapRow? RowOf(string md5) => all.FirstOrDefault(row => string.Equals(row.Md5, md5, StringComparison.OrdinalIgnoreCase));

    private void markCurrentGroup()
    {
        foreach (var group in Groups)
            group.IsCurrent = shownMd5.Length > 0 && group.Rows.Any(row => string.Equals(row.Md5, shownMd5, StringComparison.OrdinalIgnoreCase));
    }

    // --- Building ---

    private bool building;
    private bool buildAgain;
    private bool buildAgainRows;

    /// <summary>
    /// Reads the service's maps, ratings and records again. Tens of thousands of rows are made, filtered and grouped on a
    /// background thread; the lists follow when they are ready, so the window never waits for them.
    /// </summary>
    public void Reload() => build(rows: true);

    /// <summary>Filters and groups the maps again (the search text or an option changed).</summary>
    public void Rebuild() => build(rows: false);

    private async void build(bool rows)
    {
        if (filling)
            return;

        // one run at a time: a request made meanwhile runs once more afterwards, with the latest text and options
        if (building)
        {
            buildAgain = true;
            buildAgainRows |= rows;
            return;
        }

        building = true;
        try
        {
            do
            {
                bool makeRows = rows || buildAgainRows || all.Count == 0;
                buildAgain = false;
                buildAgainRows = false;
                rows = false;

                var maps = service.Maps.Maps;
                if (makeRows)
                {
                    fillOptions(maps);
                    SearchWatermark = maps.Count == 0 ? "Search maps" : $"Search {maps.Count:N0} maps, or filter: sunny>5 rank<s od>=8";
                }

                var sunny = service.Sunny.Status;
                StatusText = service.Maps.Loading && maps.Count == 0 ? "Reading the games' libraries…"
                    : sunny.Running ? (sunny.Paused ? $"Rating maps paused while you play · {sunny.Done:N0} / {sunny.Total:N0}" : $"Rating maps in the background · {sunny.Done:N0} / {sunny.Total:N0}")
                    : maps.Count == 0 ? service.Maps.Status
                    : string.Empty;

                // what the run works from, read on the UI thread
                string numberKind = NumberKind;
                double? minRate = double.TryParse(selectedRate.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : null;
                string playing = nowPlayingMd5;
                string text = searchText;
                string keys = selectedKeys.Key;
                string collectionName = selectedCollection.Key;
                var existing = all;
                var quick = quickTest();

                var (list, note, groups) = await Task.Run(() =>
                {
                    var made = existing;
                    if (makeRows)
                    {
                        made = new List<MapRow>(maps.Count);
                        foreach (var map in maps.Values)
                        {
                            var record = service.RecordOf(map.Md5);
                            made.Add(new MapRow(map, service.DifficultyOf(map), record?.BestAt(minRate), record?.Plays ?? 0, record?.LastPlayedTicks ?? 0, numberKind)
                            {
                                IsNowPlaying = string.Equals(map.Md5, playing, StringComparison.OrdinalIgnoreCase),
                            });
                        }
                    }

                    var query = LibraryQuery.Parse(text);
                    HashSet<string>? collection = null;
                    if (collectionName.Length > 0)
                    {
                        collection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var osuCollection in service.Maps.Collections.Where(c => string.Equals(c.Name, collectionName, StringComparison.OrdinalIgnoreCase)))
                            collection.UnionWith(osuCollection.Md5s);
                    }

                    var shown = made.Where(row => hasKeys(row.Map.Keys, keys) && (collection == null || collection.Contains(row.Md5)) && quick(row) && query.Matches(row)).ToList();
                    var grouped = group(shown).OrderBy(g => g.Order).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    return (made, query.Note, grouped);
                });

                all = list;
                FilterNote = note;
                apply(groups);
            } while (buildAgain);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
        finally
        {
            building = false;
        }
    }

    private void fillOptions(IReadOnlyDictionary<string, MapInfo> maps)
    {
        filling = true;

        var names = service.Maps.Collections.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (!names.SequenceEqual(CollectionOptions.Skip(1).Select(o => o.Name)))
        {
            var chosen = selectedCollection.Key;
            while (CollectionOptions.Count > 1)
                CollectionOptions.RemoveAt(CollectionOptions.Count - 1);
            foreach (var name in names)
                CollectionOptions.Add(new LibraryOption(name, name));
            selectedCollection = CollectionOptions.FirstOrDefault(o => o.Key == chosen) ?? allMaps;
        }

        filling = false;
        OnPropertyChanged(nameof(SelectedCollection));
    }

    /// <summary>Shows freshly made groups. The same groups as before are replaced in place, so the list keeps its place.</summary>
    private void apply(List<LibraryGroup> groups)
    {
        var previous = selectedGroup?.Name;
        if (groups.Count == Groups.Count && groups.Select(g => g.Name).SequenceEqual(Groups.Select(g => g.Name)))
        {
            for (int i = 0; i < groups.Count; i++)
                Groups[i] = groups[i];
        }
        else
        {
            Groups.Clear();
            foreach (var item in groups)
                Groups.Add(item);
        }

        markCurrentGroup();
        selectedGroup = Groups.FirstOrDefault(g => g.Name == previous) ?? Groups.FirstOrDefault(g => g.IsCurrent)
                        // otherwise the first group that is a real one (not "no dan", "Not rated" …)
                        ?? Groups.FirstOrDefault(g => !g.Name.Contains("no dan", StringComparison.OrdinalIgnoreCase) && !g.Name.StartsWith("No", StringComparison.Ordinal))
                        ?? Groups.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedGroup));
        showGroup();

        if (pendingLocate is { } wanted)
        {
            pendingLocate = null;
            Locate(wanted, force: false);
        }
    }

    private void showGroup()
    {
        var list = selectedGroup?.Rows ?? new List<MapRow>();
        Func<MapRow, IComparable> key = sortKey switch
        {
            LibrarySortKey.Title => row => row.Title,
            LibrarySortKey.Keys => row => row.Map.Keys,
            LibrarySortKey.Stars => row => row.Map.Stars,
            LibrarySortKey.Od => row => row.Map.Od,
            LibrarySortKey.Bpm => row => row.Map.Bpm,
            LibrarySortKey.Length => row => row.Map.LengthMs,
            LibrarySortKey.Rank => row => row.Best == null ? -1 : Ranks.Order(row.Best.Rank) * 10 + row.Best.Accuracy,
            LibrarySortKey.Plays => row => row.Plays,
            // maps without the number (shown with a * or a dash) go last, by their Sunny rating
            _ => row => double.IsNaN(row.Number) ? 1000 + (row.Difficulty.Sunny ?? 99) : row.Number,
        };

        var sorted = (sortDescending ? list.OrderByDescending(key) : list.OrderBy(key)).ToList();

        // the same maps showing the same things: the list on screen stays as it is (its place, its loaded backgrounds)
        if (sorted.Count == rows.Count && sorted.Count > 0)
        {
            bool same = true;
            for (int i = 0; i < sorted.Count && same; i++)
            {
                var (a, b) = (sorted[i], rows[i]);
                same = a.Md5 == b.Md5 && a.NumberText == b.NumberText && a.DanText == b.DanText && a.RankText == b.RankText && a.AccText == b.AccText && a.Plays == b.Plays;
            }

            if (same)
                return;
        }

        Rows = sorted;
    }

    private IEnumerable<LibraryGroup> group(List<MapRow> shown)
    {
        switch (selectedGroupBy.Key)
        {
            case "dan":
            case "lndan":
                bool ln = selectedGroupBy.Key == "lndan";
                bool manyKeys = selectedKeys.Key is "0" or "others";
                return shown.Where(row => !ln || row.Difficulty.LnGroup != null)
                            .GroupBy(row => danGroup(row, manyKeys, ln))
                            .Select(g => new LibraryGroup(g.Key.Name, g.ToList(), g.Key.Order));

            case "sunny":
                return shown.GroupBy(row => row.Difficulty.Sunny is { } s ? (int)Math.Floor(s) : -1)
                            .Select(g => new LibraryGroup(g.Key < 0 ? "Not rated" : $"Sunny {g.Key}", g.ToList(), g.Key < 0 ? 999 : g.Key));

            case "stars":
                return shown.GroupBy(row => row.Map.Stars >= 0 ? (int)Math.Floor(row.Map.Stars) : -1)
                            .Select(g => new LibraryGroup(g.Key < 0 ? "No stars" : $"Stars {g.Key}", g.ToList(), g.Key < 0 ? 999 : g.Key));

            case "rank":
                return shown.GroupBy(row => row.Best == null ? string.Empty : Ranks.Letter(row.Best.Rank))
                            .Select(g => new LibraryGroup(g.Key.Length == 0 ? "Not played" : g.Key, g.ToList(), Array.IndexOf(Ranks.Letters, g.Key) is var i && i >= 0 ? i : 99));

            case "recent":
                var now = DateTime.UtcNow;
                return shown.GroupBy(row => recency(row.LastPlayedTicks, now))
                            .Select(g => new LibraryGroup(g.Key.Name, g.ToList(), g.Key.Order));

            case "collection":
                var byMd5 = shown.ToDictionary(row => row.Md5, StringComparer.OrdinalIgnoreCase);
                var lists = service.Maps.Collections
                                   .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                                   // by name, as the game lists its collections
                                   .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                                   .Select((g, index) => new LibraryGroup(g.Key, g.SelectMany(c => c.Md5s).Distinct(StringComparer.OrdinalIgnoreCase)
                                                                                 .Select(md5 => byMd5.GetValueOrDefault(md5)).Where(row => row != null).Select(row => row!).ToList(), index))
                                   .Where(g => g.Rows.Count > 0)
                                   .ToList();
                var inAny = lists.SelectMany(g => g.Rows).Select(row => row.Md5).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rest = shown.Where(row => !inAny.Contains(row.Md5)).ToList();
                if (rest.Count > 0)
                    lists.Add(new LibraryGroup("Not in a collection", rest, 9999));
                return lists;

            default:
                return [new LibraryGroup("All maps", shown, 0)];
        }
    }

    private static bool hasKeys(int keys, string wanted) => wanted switch
    {
        "0" => true,
        "others" => keys is not (4 or 6 or 7),
        _ => int.TryParse(wanted, out var count) && keys == count,
    };

    private static (string Name, double Order) danGroup(MapRow row, bool manyKeys, bool ln)
    {
        var difficulty = row.Difficulty;
        var name = ln ? difficulty.LnGroup : difficulty.RcGroup;
        if (name == null)
            return (manyKeys && row.Map.Keys > 0 ? $"{row.Map.Keys}K · no dan" : "No dan", row.Map.Keys > 0 ? row.Map.Keys * 1000 + 999 : 99999);

        // in the table's order; what is under the first dan comes first, what is above the last one last
        var order = Dans.DanOrder(row.Map.Keys, ln, extended: true);
        int index = name == MapDifficulty.BelowFirst ? -1 : Array.IndexOf(order, name);
        if (index < 0 && name != MapDifficulty.BelowFirst)
            index = ((ln ? difficulty.Ln : difficulty.Rc) ?? string.Empty).StartsWith('<') ? -1 : order.Length;

        return (manyKeys ? $"{row.Map.Keys}K {name}" : name, row.Map.Keys * 1000 + index + 1);
    }

    private static (string Name, double Order) recency(long ticks, DateTime now)
    {
        if (ticks <= 0)
            return ("Never played", 9);

        double days = (now - new DateTime(ticks, DateTimeKind.Utc)).TotalDays;
        return days < 7 ? ("This week", 0)
            : days < 14 ? ("Last week", 1)
            : days < 31 ? ("This month", 2)
            : days < 365 ? ("This year", 3)
            : ("Earlier", 4);
    }
}

/// <summary>
/// The library's search text: words (found in the title, artist, difficulty name, mapper, tags and source) and filters
/// like <c>sunny&gt;5</c>, <c>rank&lt;s</c>, <c>od&gt;=8</c>, <c>length&lt;2:30</c>.
/// </summary>
public sealed class LibraryQuery
{
    private static readonly Regex filter = new(@"^([a-z]+)(>=|<=|!=|==|=|:|>|<)(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly List<string> words = new();
    private readonly List<Func<MapRow, bool>> tests = new();
    private readonly List<string> notes = new();

    public string Note => string.Join(" · ", notes);

    public bool Matches(MapRow row)
    {
        foreach (var word in words)
        {
            if (!row.Haystack.Contains(word, StringComparison.Ordinal))
                return false;
        }

        foreach (var test in tests)
        {
            if (!test(row))
                return false;
        }

        return true;
    }

    public static LibraryQuery Parse(string? text)
    {
        var query = new LibraryQuery();
        foreach (var token in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = filter.Match(token);
            if (!match.Success || !query.add(match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value, match.Groups[3].Value))
                query.words.Add(token.ToLowerInvariant());
        }

        return query;
    }

    private bool add(string key, string op, string value)
    {
        if (op == ":")
            op = "=";

        Func<MapRow, double?>? number = key switch
        {
            "sunny" or "sr" => row => row.Difficulty.Sunny,
            "stars" or "star" => row => row.Map.Stars >= 0 ? row.Map.Stars : null,
            "dan" => row => row.Difficulty.DanLevel,
            "keys" or "key" or "k" => row => row.Map.Keys,
            "od" => row => row.Map.Od,
            "hp" => row => row.Map.Hp,
            "bpm" => row => row.Map.Bpm,
            "ln" => row => row.Difficulty.LnRatio * 100,
            "length" or "len" => row => row.Map.LengthMs / 1000,
            "notes" or "objects" => row => row.Map.Objects,
            "plays" => row => row.Plays,
            "acc" => row => row.Best == null ? null : row.Best.Accuracy * 100,
            _ => null,
        };

        if (number != null)
        {
            if (!parseNumber(key, value, out var wanted))
                return false;

            tests.Add(row => number(row) is { } actual && compare(actual, op, wanted, key is "keys" or "key" or "k" ? 0 : 0.005));
            notes.Add($"{name(key)} {word(op)} {value}");
            return true;
        }

        if (key == "rank" || key == "grade")
        {
            int wanted = value.ToLowerInvariant() switch
            {
                "ss" or "x" => 6,
                "s" => 5,
                "a" => 4,
                "b" => 3,
                "c" => 2,
                "d" => 1,
                "none" or "noplay" or "-" => 0,
                _ => -1,
            };
            if (wanted < 0)
                return false;

            tests.Add(row => compare(row.Best == null ? 0 : Math.Max(Ranks.Order(row.Best.Rank), 0), op, wanted, 0));
            notes.Add($"best rank {word(op)} {(wanted == 0 ? "none" : value.ToUpperInvariant())}");
            return true;
        }

        Func<MapRow, string>? field = key switch
        {
            "artist" => row => row.Map.Artist + "\n" + row.Map.ArtistUnicode,
            "title" => row => row.Map.Title + "\n" + row.Map.TitleUnicode,
            "creator" or "mapper" => row => row.Map.Mapper,
            "diff" or "version" => row => row.Map.Version,
            "tag" or "tags" => row => row.Map.Tags,
            "source" => row => row.Map.Source,
            "status" => row => row.Map.Status,
            _ => null,
        };

        if (field == null || op is ">" or "<" or ">=" or "<=")
            return false;

        bool negate = op == "!=";
        tests.Add(row => field(row).Contains(value, StringComparison.OrdinalIgnoreCase) != negate);
        notes.Add($"{key} {(negate ? "without" : "has")} “{value}”");
        return true;
    }

    private static bool parseNumber(string key, string value, out double number)
    {
        // a length may be 1:30
        if (key is "length" or "len" && value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                                  && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                number = minutes * 60 + seconds;
                return true;
            }
        }

        return double.TryParse(value.TrimEnd('%', 'k', 'K'), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool compare(double actual, string op, double wanted, double tolerance) => op switch
    {
        ">" => actual > wanted + tolerance,
        ">=" => actual >= wanted - tolerance,
        "<" => actual < wanted - tolerance,
        "<=" => actual <= wanted + tolerance,
        "!=" => Math.Abs(actual - wanted) > tolerance,
        // "sunny=5" means anything shown as 5.xx, a whole number of keys means exactly that
        _ => tolerance == 0 ? actual == wanted : (wanted == Math.Floor(wanted) ? Math.Floor(actual + 0.0001) == wanted : Math.Abs(actual - wanted) <= tolerance),
    };

    private static string name(string key) => key switch
    {
        "sunny" or "sr" => "Sunny",
        "stars" or "star" => "stars",
        "dan" => "dan level",
        "keys" or "key" or "k" => "keys",
        "od" => "OD",
        "hp" => "HP",
        "bpm" => "BPM",
        "ln" => "LN %",
        "length" or "len" => "length",
        "notes" or "objects" => "notes",
        "plays" => "plays",
        "acc" => "accuracy",
        _ => key,
    };

    private static string word(string op) => op switch
    {
        ">" => "above",
        ">=" => "at least",
        "<" => "below",
        "<=" => "at most",
        "!=" => "not",
        _ => "is",
    };
}
