namespace LazerReplayCompare;

/// <summary>How hard a map is, as the app shows it: its Sunny star rating and, for 4K, 6K and 7K, its dans.</summary>
/// <param name="Rc">The regular dan with its part ("Reform 4 mid"), or null when there is none.</param>
/// <param name="Ln">The LN dan ("LN 8 high"), only for a map of 20 % hold notes or more.</param>
/// <param name="DanLevel">
/// The estimator's number, moved so that a dan runs from x.00 to x.99 (Reform 3 is 3.00 to 3.99; the estimators put it
/// from 2.51 to 3.50), when an estimator gave one.
/// </param>
public sealed record MapDifficulty(double? Sunny, double LnRatio, string? Rc, string? Ln, double? DanLevel, int Keys)
{
    /// <summary>A map of this share of hold notes or more is listed under its LN dan too.</summary>
    public const double LnShare = 0.2;

    /// <summary>The dan written under a map's number: the regular one (the LN dan when a map has no regular one).</summary>
    public string? Dan => Rc ?? Ln;

    /// <summary>The regular dan without its part ("Reform 4"), which maps are grouped by. In 4K everything under Reform 1 is one group.</summary>
    public string? RcGroup
    {
        get
        {
            if (Rc == null)
                return null;

            var dan = Dans.Dan(Rc.TrimStart('<', '>', ' '));
            return Keys == 4 && (Rc.StartsWith('<') || dan.StartsWith("Intro", StringComparison.OrdinalIgnoreCase)) ? BelowFirst : dan;
        }
    }

    public string? LnGroup => Ln == null ? null : Dans.Dan(Ln.TrimStart('<', '>', ' '));

    public const string BelowFirst = "< Reform 1";
}

/// <summary>A dan estimate from the analyser's estimators (the dan engine), when it has one for a map.</summary>
public sealed record DanLookupResult(string? Rc, string? Ln, double? Numeric);

/// <summary>
/// What the library knows: the games' maps, their Sunny ratings and dans, and the player's best play of each. Everything
/// is worked out in the background; <see cref="Changed"/> says when something new is in.
/// </summary>
public sealed class LibraryService
{
    public MapLibrary Maps { get; } = new();
    public SunnyCache Sunny { get; } = new();

    /// <summary>The dan engine's estimate for a map (set once the engine is there; null results fall back to the Sunny table).</summary>
    public Func<string, DanLookupResult?>? DanLookup { get; set; }

    private Dictionary<string, MapRecord> records = new(StringComparer.OrdinalIgnoreCase);
    private Func<List<(IReadOnlyList<string> Md5s, ReplayEntry Entry)>>? scoreSource;
    private int recordsRunning;
    private bool recordsAgain;

    /// <summary>The player the records are of (the one with the most local scores).</summary>
    public string Player { get; private set; } = string.Empty;

    /// <summary>Maps, ratings or records changed. Raised on a background thread, at most a few times a second.</summary>
    public event Action? Changed;

    public LibraryService()
    {
        Maps.Changed += () =>
        {
            Sunny.Update(Maps.Maps);
            Changed?.Invoke();
        };
        Sunny.Changed += () => Changed?.Invoke();
    }

    public MapRecord? RecordOf(string md5) => Volatile.Read(ref records).GetValueOrDefault(md5);

    // ratings worked out ahead of the background run, for maps that are looked at
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SunnyResult?> early = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rates a map now when the background run hasn't reached it yet (once; <see cref="Changed"/> follows).</summary>
    public void EnsureRated(MapInfo map)
    {
        if (!SunnyCache.Rated(map.Keys) || map.ChartPath.Length == 0 || Sunny.Get(map.Md5) != null || !early.TryAdd(map.Md5, null))
            return;

        Sunny.AtRateAsync(map, 1.0).ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
            {
                early[map.Md5] = task.Result;
                Changed?.Invoke();
            }
        });
    }

    /// <summary>Works the records out again from every local score, in the background (one run at a time).</summary>
    public void UpdateRecords(Func<List<(IReadOnlyList<string> Md5s, ReplayEntry Entry)>> scores)
    {
        scoreSource = scores;
        if (Interlocked.Exchange(ref recordsRunning, 1) == 1)
        {
            recordsAgain = true;
            return;
        }

        Task.Run(() =>
        {
            try
            {
                do
                {
                    recordsAgain = false;
                    var (built, player) = MapRecords.Build(scoreSource!());
                    Player = player;
                    Volatile.Write(ref records, built);
                } while (recordsAgain);
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
            }
            finally
            {
                Interlocked.Exchange(ref recordsRunning, 0);
                Changed?.Invoke();
            }
        });
    }

    /// <summary>A dan label that says something ("Unknown" and empty ones don't).</summary>
    private static string? known(string? label)
        => string.IsNullOrWhiteSpace(label) || label.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ? null : label;

    public MapDifficulty DifficultyOf(MapInfo map)
    {
        var sunny = Sunny.Get(map.Md5) ?? early.GetValueOrDefault(map.Md5);
        double? star = sunny is { } s && double.IsFinite(s.Star) ? s.Star : null;
        double lnRatio = sunny?.LnRatio ?? (map.Objects > 0 ? (double)map.Holds / map.Objects : 0);

        string? rc = null, ln = null;
        double? level = null;
        if (DanLookup?.Invoke(map.Md5) is { } estimate)
        {
            rc = known(estimate.Rc);
            ln = known(estimate.Ln);
            level = estimate.Numeric is { } number && double.IsFinite(number) ? number : null;
        }

        // what the estimators didn't give comes from the Sunny rating's tables
        if (star is { } value)
        {
            var (tableRc, tableLn) = Dans.Split(value, lnRatio, map.Keys, extended: true);
            rc ??= known(tableRc);
            ln ??= known(tableLn);
        }

        rc = known(rc);
        ln = lnRatio >= MapDifficulty.LnShare ? known(ln) : null;

        // the estimators put a dan from x-0.49 to x+0.50: half a step up, it runs from x.00 to x.99
        if (level is { } raw)
            level = raw + 0.4999;

        return new MapDifficulty(star, lnRatio, rc, ln, level, map.Keys);
    }
}
