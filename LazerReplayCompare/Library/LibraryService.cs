namespace LazerReplayCompare;

/// <summary>How hard a map is, as the app shows it: its Sunny star rating and, for 4K, 6K and 7K, its dan.</summary>
/// <param name="Dan">The dan with its part ("Reform 4 mid", "LN 8 high"), or null when there is none.</param>
/// <param name="DanGroup">The dan without its part ("Reform 4"), which maps are grouped by.</param>
/// <param name="DanLevel">The estimator's numeric difficulty, when an estimator gave one.</param>
public sealed record MapDifficulty(double? Sunny, double LnRatio, string? Dan, string? DanGroup, bool DanIsLn, double? DanLevel);

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

    public MapDifficulty DifficultyOf(MapInfo map)
    {
        var sunny = Sunny.Get(map.Md5);
        double? star = sunny is { } s && double.IsFinite(s.Star) ? s.Star : null;
        double lnRatio = sunny?.LnRatio ?? (map.Objects > 0 ? (double)map.Holds / map.Objects : 0);

        string? rc = null, ln = null;
        double? level = null;
        if (DanLookup?.Invoke(map.Md5) is { } estimate)
        {
            rc = estimate.Rc;
            ln = estimate.Ln;
            level = estimate.Numeric;
        }

        if (rc == null && ln == null && star is { } value)
            (rc, ln) = Dans.Split(value, lnRatio, map.Keys, extended: true);

        // a map of mostly holds shows its LN dan, the rest their RC dan
        bool useLn = ln != null && lnRatio >= 0.15;
        string? dan = useLn ? ln : rc ?? ln;
        return new MapDifficulty(star, lnRatio, dan, dan == null ? null : Dans.Dan(dan.TrimStart('<', '>', ' ')), useLn, level);
    }
}
