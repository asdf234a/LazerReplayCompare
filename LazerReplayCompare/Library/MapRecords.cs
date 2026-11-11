namespace LazerReplayCompare;

/// <summary>The best play of a beatmap at one rate (and Classic setting, and game).</summary>
public sealed record RateRecord(double Rate, bool Classic, bool Stable, string Rank, double Accuracy, long Score, int MaxCombo, int Plays, long LastPlayedTicks)
{
    public string RateText => Rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
}

/// <summary>What the player has done on a beatmap: the best play of each rate, and the best of all.</summary>
public sealed class MapRecord
{
    public List<RateRecord> ByRate { get; } = new();
    public int Plays { get; set; }
    public long LastPlayedTicks { get; set; }

    /// <summary>The best of every rate: the highest rank, then the highest accuracy.</summary>
    public RateRecord? Best => ByRate.Count == 0 ? null : ByRate.Aggregate((a, b) => Ranks.Better(b, a) ? b : a);

    /// <summary>The best at <paramref name="minRate"/> or faster (null: any rate).</summary>
    public RateRecord? BestAt(double? minRate)
    {
        var list = minRate == null ? ByRate : ByRate.Where(r => r.Rate >= minRate.Value - 0.0001).ToList();
        return list.Count == 0 ? null : list.Aggregate((a, b) => Ranks.Better(b, a) ? b : a);
    }
}

/// <summary>osu!'s grades, as the app orders and names them.</summary>
public static class Ranks
{
    /// <summary>The grades shown, best first ("" is a map never played).</summary>
    public static readonly string[] Letters = ["SS", "S", "A", "B", "C", "D", ""];

    /// <summary>6 for SS down to 1 for D; 0 for a failed or unknown grade.</summary>
    public static int Order(string? rank) => rank switch
    {
        "X" or "XH" or "SS" or "SSH" => 6,
        "S" or "SH" => 5,
        "A" => 4,
        "B" => 3,
        "C" => 2,
        "D" => 1,
        _ => 0,
    };

    /// <summary>"SS", "S", "A" … (osu!lazer names SS "X", and the silver ones "XH" / "SH"); "F" for a failed play.</summary>
    public static string Letter(string? rank) => Order(rank) switch
    {
        6 => "SS",
        5 => "S",
        4 => "A",
        3 => "B",
        2 => "C",
        1 => "D",
        _ => string.IsNullOrEmpty(rank) ? string.Empty : "F",
    };

    public static bool Better(RateRecord a, RateRecord b)
    {
        int byRank = Order(a.Rank).CompareTo(Order(b.Rank));
        if (byRank != 0)
            return byRank > 0;

        return a.Accuracy != b.Accuracy ? a.Accuracy > b.Accuracy : a.Score > b.Score;
    }
}

public static class MapRecords
{
    /// <summary>
    /// Each beatmap's record from every listed score. Only <paramref name="player"/>'s scores count (null: the player
    /// with the most scores, so replays of other players that were imported or downloaded don't count as the user's).
    /// </summary>
    public static (Dictionary<string, MapRecord> Records, string Player) Build(IReadOnlyList<(IReadOnlyList<string> Md5s, ReplayEntry Entry)> scores, string? player = null)
    {
        player ??= scores.GroupBy(s => s.Entry.Player, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(g => g.Count())
                         .Select(g => g.Key)
                         .FirstOrDefault() ?? string.Empty;

        var best = new Dictionary<(string Md5, double Rate, bool Classic, bool Stable), RateRecord>();
        foreach (var (md5s, entry) in scores)
        {
            if (!string.Equals(entry.Player, player, StringComparison.OrdinalIgnoreCase))
                continue;

            var speed = AutoTargetRule.ForReplay(entry);
            foreach (var md5 in md5s)
            {
                var key = (md5.ToLowerInvariant(), Math.Round(speed.Rate, 2), speed.Classic && !entry.IsLegacy, entry.IsLegacy);
                var candidate = new RateRecord(key.Item2, key.Item3, key.Item4, entry.Rank, entry.Accuracy, entry.Score, entry.MaxCombo, 1, entry.Timestamp);
                if (!best.TryGetValue(key, out var known))
                {
                    best[key] = candidate;
                    continue;
                }

                var winner = Ranks.Better(candidate, known) ? candidate : known;
                best[key] = winner with { Plays = known.Plays + 1, LastPlayedTicks = Math.Max(known.LastPlayedTicks, entry.Timestamp) };
            }
        }

        var records = new Dictionary<string, MapRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, record) in best)
        {
            if (!records.TryGetValue(key.Md5, out var map))
                records[key.Md5] = map = new MapRecord();
            map.ByRate.Add(record);
            map.Plays += record.Plays;
            map.LastPlayedTicks = Math.Max(map.LastPlayedTicks, record.LastPlayedTicks);
        }

        foreach (var map in records.Values)
            map.ByRate.Sort((a, b) => a.Rate.CompareTo(b.Rate));
        return (records, player);
    }
}
