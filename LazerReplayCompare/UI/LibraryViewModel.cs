using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LazerReplayCompare;

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

        (Number, NumberText, NumberBrush) = number(map, difficulty, numberKind);
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

    public string DanText => Difficulty.Dan ?? string.Empty;
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

    /// <summary>A short form for the narrow rail: "Reform" over "4", or the whole name.</summary>
    public string RailKind => Name.Contains(' ') && char.IsDigit(Name[^1]) ? Name[..Name.LastIndexOf(' ')] : string.Empty;
    public string RailShort => Name.Contains(' ') && char.IsDigit(Name[^1]) ? Name[(Name.LastIndexOf(' ') + 1)..] : Name;
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
    }

    /// <summary>A setting the user picked changed (the window saves it).</summary>
    public event Action? OptionsChanged;

    // --- Options ---

    public IReadOnlyList<LibraryOption> GroupByOptions { get; } =
    [
        new("dan", "Dan"),
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

    public ObservableCollection<LibraryOption> KeysOptions { get; } = new() { new("0", "All keys") };

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
        set => setOption(ref selectedCollection, value);
    }

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
        wantedKeys = keys;
        filling = false;
        OnPropertyChanged(nameof(SelectedGroupBy));
        OnPropertyChanged(nameof(SelectedNumber));
        OnPropertyChanged(nameof(SelectedRate));
        OnPropertyChanged(nameof(NumberHeader));
    }

    private string wantedKeys = "0";

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

            foreach (var row in all)
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
        }
    }

    /// <summary>Selects the group the shown map is in (when it is in one).</summary>
    public void SelectShownGroup()
    {
        if (Groups.FirstOrDefault(g => g.IsCurrent) is { } group)
            SelectedGroup = group;
    }

    public MapRow? RowOf(string md5) => all.FirstOrDefault(row => string.Equals(row.Md5, md5, StringComparison.OrdinalIgnoreCase));

    private void markCurrentGroup()
    {
        foreach (var group in Groups)
            group.IsCurrent = shownMd5.Length > 0 && group.Rows.Any(row => string.Equals(row.Md5, shownMd5, StringComparison.OrdinalIgnoreCase));
    }

    // --- Building ---

    /// <summary>Reads the service's maps, ratings and records again (on the UI thread; a few thousand maps take a moment).</summary>
    public void Reload()
    {
        var maps = service.Maps.Maps;
        double? minRate = double.TryParse(selectedRate.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : null;
        var built = new List<MapRow>(maps.Count);
        foreach (var map in maps.Values)
        {
            var record = service.RecordOf(map.Md5);
            built.Add(new MapRow(map, service.DifficultyOf(map), record?.BestAt(minRate), record?.Plays ?? 0, record?.LastPlayedTicks ?? 0, NumberKind)
            {
                IsNowPlaying = string.Equals(map.Md5, nowPlayingMd5, StringComparison.OrdinalIgnoreCase),
            });
        }

        all = built;
        fillOptions(maps);
        SearchWatermark = maps.Count == 0 ? "Search maps" : $"Search {maps.Count:N0} maps, or filter: sunny>5 rank<s od>=8";

        var sunny = service.Sunny.Status;
        StatusText = service.Maps.Loading && maps.Count == 0 ? "Reading the games' libraries…"
            : sunny.Running ? (sunny.Paused ? $"Rating maps paused while you play · {sunny.Done:N0} / {sunny.Total:N0}" : $"Rating maps in the background · {sunny.Done:N0} / {sunny.Total:N0}")
            : maps.Count == 0 ? service.Maps.Status
            : string.Empty;

        Rebuild();
    }

    private void fillOptions(IReadOnlyDictionary<string, MapInfo> maps)
    {
        filling = true;

        var keys = maps.Values.Select(map => map.Keys).Where(k => k > 0).Distinct().OrderBy(k => k).ToList();
        var wanted = KeysOptions.Count > 1 ? selectedKeys.Key : wantedKeys;
        if (KeysOptions.Count - 1 != keys.Count)
        {
            while (KeysOptions.Count > 1)
                KeysOptions.RemoveAt(KeysOptions.Count - 1);
            foreach (var count in keys)
                KeysOptions.Add(new LibraryOption(count.ToString(CultureInfo.InvariantCulture), $"{count}K"));
        }

        selectedKeys = KeysOptions.FirstOrDefault(o => o.Key == wanted) ?? KeysOptions[0];

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
        OnPropertyChanged(nameof(SelectedKeys));
        OnPropertyChanged(nameof(SelectedCollection));
    }

    /// <summary>Filters and groups the maps again (the search text or an option changed).</summary>
    public void Rebuild()
    {
        if (filling)
            return;

        var query = LibraryQuery.Parse(searchText);
        FilterNote = query.Note;

        int keys = int.TryParse(selectedKeys.Key, out var k) ? k : 0;
        HashSet<string>? collection = null;
        if (selectedCollection.Key.Length > 0)
        {
            collection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in service.Maps.Collections.Where(c => string.Equals(c.Name, selectedCollection.Key, StringComparison.OrdinalIgnoreCase)))
                collection.UnionWith(list.Md5s);
        }

        var shown = all.Where(row => (keys == 0 || row.Map.Keys == keys) && (collection == null || collection.Contains(row.Md5)) && query.Matches(row)).ToList();
        var groups = group(shown).OrderBy(g => g.Order).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var previous = selectedGroup?.Name;
        Groups.Clear();
        foreach (var item in groups)
            Groups.Add(item);
        markCurrentGroup();

        selectedGroup = Groups.FirstOrDefault(g => g.Name == previous) ?? Groups.FirstOrDefault(g => g.IsCurrent) ?? Groups.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedGroup));
        showGroup();
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

        Rows = (sortDescending ? list.OrderByDescending(key) : list.OrderBy(key)).ToList();
    }

    private IEnumerable<LibraryGroup> group(List<MapRow> shown)
    {
        switch (selectedGroupBy.Key)
        {
            case "dan":
                bool manyKeys = selectedKeys.Key == "0";
                return shown.GroupBy(row => danGroup(row, manyKeys))
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

    private static (string Name, double Order) danGroup(MapRow row, bool manyKeys)
    {
        var difficulty = row.Difficulty;
        if (difficulty.DanGroup == null)
            return (manyKeys && row.Map.Keys > 0 ? $"{row.Map.Keys}K · no dan" : "No dan", row.Map.Keys * 1000 + 999);

        var order = Dans.DanOrder(row.Map.Keys, difficulty.DanIsLn, extended: true);
        int index = Array.IndexOf(order, difficulty.DanGroup);
        if (index < 0)
            index = (difficulty.Dan ?? string.Empty).StartsWith('<') ? -1 : order.Length;

        // RC dans first, then LN dans, each in its table's order
        double position = row.Map.Keys * 1000 + (difficulty.DanIsLn ? 400 : 0) + index + 1;
        return (manyKeys ? $"{row.Map.Keys}K {difficulty.DanGroup}" : difficulty.DanGroup, position);
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
