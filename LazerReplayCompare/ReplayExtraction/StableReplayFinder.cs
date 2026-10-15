using System.Text;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets.Mania;

namespace LazerReplayCompare;

/// <summary>
/// osu!stable's own local scores for a beatmap: the local leaderboard in <c>scores.db</c>, with each score's replay
/// from the hidden <c>Data/r</c> folder (full .osr files). Replays are matched to scores by their replay hash (or
/// timestamp), as their file names are not documented; replays of plays that are not in <c>scores.db</c> yet (osu!stable
/// may write it later) are listed from <c>Data/r</c> directly. Scores whose replay is missing are listed without one
/// (<see cref="ReplayEntry.HasReplay"/>).
/// </summary>
public sealed class StableReplayFinder
{
    private const byte mania_mode = 3;
    private const int target_practice = 1 << 23;

    /// <summary>One score as osu!stable stores it, in scores.db and in the .osr header alike.</summary>
    private sealed record StableScore(
        byte Mode, string BeatmapMd5, string Player, string ReplayMd5,
        int Count300, int Count100, int Count50, int CountGeki, int CountKatu, int CountMiss,
        int Score, int MaxCombo, int Mods, long Timestamp);

    private readonly object sync = new();

    // .osr headers by path, refreshed when a file's size or write time changes
    private readonly Dictionary<string, (long Length, DateTime Written, StableScore? Score)> headers = new(StringComparer.OrdinalIgnoreCase);

    public List<ReplayEntry> FindReplays(string beatmapMd5, string stableRoot)
    {
        lock (sync)
        {
            var replayFolder = Path.Combine(stableRoot, "Data", "r");
            var scoresDb = Path.Combine(stableRoot, "scores.db");
            var scores = readScoresDb(scoresDb, beatmapMd5);

            // Replays named after the beatmap first (the usual "{beatmap md5}-{time}.osr"); the whole folder only when
            // a score's replay is not among them.
            var replays = Directory.Exists(replayFolder)
                ? headersOf(Directory.EnumerateFiles(replayFolder, beatmapMd5 + "-*.osr"))
                : new List<(string Path, StableScore Score)>();
            if (Directory.Exists(replayFolder) && scores.Any(score => find(replays, score) == null))
                replays = headersOf(Directory.EnumerateFiles(replayFolder, "*.osr"));

            var entries = new List<ReplayEntry>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var score in scores)
            {
                if (find(replays, score) is { } path)
                {
                    if (used.Add(path)) entries.Add(toEntry(score, path, hasReplay: true));
                    continue;
                }

                // in the local leaderboard, but its replay is gone: listed, not comparable
                var key = $"{scoresDb}#{(string.IsNullOrEmpty(score.ReplayMd5) ? score.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) : score.ReplayMd5)}";
                if (used.Add(key)) entries.Add(toEntry(score, key, hasReplay: false));
            }

            // plays not written to scores.db yet
            foreach (var (path, score) in replays)
            {
                if (score.Mode != mania_mode || !string.Equals(score.BeatmapMd5, beatmapMd5, StringComparison.OrdinalIgnoreCase) || !used.Add(path)) continue;
                entries.Add(toEntry(score, path, hasReplay: true));
            }

            return entries.OrderByDescending(entry => entry.Score).ToList();
        }
    }

    private static string? find(List<(string Path, StableScore Score)> replays, StableScore score)
    {
        foreach (var (path, replay) in replays)
        {
            if (!string.Equals(replay.BeatmapMd5, score.BeatmapMd5, StringComparison.OrdinalIgnoreCase)) continue;
            if ((!string.IsNullOrEmpty(score.ReplayMd5) && string.Equals(replay.ReplayMd5, score.ReplayMd5, StringComparison.OrdinalIgnoreCase))
                || replay.Timestamp == score.Timestamp)
                return path;
        }

        return null;
    }

    private List<(string Path, StableScore Score)> headersOf(IEnumerable<string> paths)
    {
        var result = new List<(string, StableScore)>();
        foreach (var path in paths)
        {
            try
            {
                var file = new FileInfo(path);
                if (!headers.TryGetValue(path, out var cached) || cached.Length != file.Length || cached.Written != file.LastWriteTimeUtc)
                {
                    StableScore? header = null;
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new BinaryReader(stream, Encoding.UTF8);
                        header = readScore(reader, replayHeader: true);
                    }
                    catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException)
                    {
                        // not a readable replay (yet): cached as such until the file changes
                    }

                    headers[path] = cached = (file.Length, file.LastWriteTimeUtc, header);
                }

                if (cached.Score != null)
                    result.Add((path, cached.Score));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                InternalLogger.Log(e);
            }
        }

        return result;
    }

    /// <summary>The beatmap's osu!mania scores in scores.db (empty when there is no scores.db).</summary>
    private static List<StableScore> readScoresDb(string path, string beatmapMd5)
    {
        var result = new List<StableScore>();
        if (!File.Exists(path))
            return result;

        // osu!stable may be writing it: read a snapshot
        byte[] data;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            data = memory.ToArray();
        }

        using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        reader.ReadInt32(); // version
        int beatmaps = reader.ReadInt32();
        for (int b = 0; b < beatmaps; b++)
        {
            string md5 = readString(reader);
            int count = reader.ReadInt32();
            bool wanted = string.Equals(md5, beatmapMd5, StringComparison.OrdinalIgnoreCase);
            for (int s = 0; s < count; s++)
            {
                var score = readScore(reader, replayHeader: false);
                if (wanted && score.Mode == mania_mode)
                    result.Add(score);
            }
        }

        return result;
    }

    /// <summary>
    /// A score record: the .osr header (<paramref name="replayHeader"/>, followed by the life bar graph) or a scores.db
    /// entry (followed by an empty string, -1, the online score id and, with Target Practice, its accuracy).
    /// </summary>
    private static StableScore readScore(BinaryReader reader, bool replayHeader)
    {
        byte mode = reader.ReadByte();
        reader.ReadInt32(); // game version
        string beatmapMd5 = readString(reader);
        string player = readString(reader);
        string replayMd5 = readString(reader);
        int count300 = reader.ReadUInt16(), count100 = reader.ReadUInt16(), count50 = reader.ReadUInt16();
        int countGeki = reader.ReadUInt16(), countKatu = reader.ReadUInt16(), countMiss = reader.ReadUInt16();
        int score = reader.ReadInt32();
        int maxCombo = reader.ReadUInt16();
        reader.ReadBoolean(); // perfect combo
        int mods = reader.ReadInt32();
        readString(reader); // life bar graph (always empty in scores.db)
        long timestamp = reader.ReadInt64();

        if (!replayHeader)
        {
            reader.ReadInt32(); // always -1
            reader.ReadInt64(); // online score id
            if ((mods & target_practice) != 0) reader.ReadDouble();
        }

        return new StableScore(mode, beatmapMd5, player, replayMd5, count300, count100, count50, countGeki, countKatu, countMiss, score, maxCombo, mods, timestamp);
    }

    private static string readString(BinaryReader reader)
    {
        byte flag = reader.ReadByte();
        if (flag == 0x00) return string.Empty;
        if (flag != 0x0b) throw new InvalidDataException($"Unexpected string flag 0x{flag:x2}.");

        int length = 0, shift = 0;
        byte part;
        do
        {
            part = reader.ReadByte();
            length |= (part & 0x7f) << shift;
            shift += 7;
        } while ((part & 0x80) != 0);

        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static ReplayEntry toEntry(StableScore score, string path, bool hasReplay)
    {
        var lazerMods = new ManiaRuleset().ConvertFromLegacyMods((LegacyMods)score.Mods).ToList();
        var mods = ReplayFinder.FromLazerMods(lazerMods);
        var statistics = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Perfect"] = score.CountGeki,
            ["Great"] = score.Count300,
            ["Good"] = score.CountKatu,
            ["Ok"] = score.Count100,
            ["Meh"] = score.Count50,
            ["Miss"] = score.CountMiss,
        };

        // osu!stable's osu!mania accuracy: MAX and 300 both count fully
        int total = statistics.Values.Sum();
        double accuracy = total == 0 ? 1
            : (300.0 * (score.CountGeki + score.Count300) + 200.0 * score.CountKatu + 100.0 * score.Count100 + 50.0 * score.Count50) / (300.0 * total);
        var date = new DateTime(score.Timestamp, DateTimeKind.Utc);

        return new ReplayEntry(
            FilePath: path,
            Player: string.IsNullOrWhiteSpace(score.Player) ? "unknown" : score.Player,
            Score: score.Score,
            Mods: mods,
            ModsText: ModUtility.FormatModsText(mods),
            ModsKey: ModUtility.BuildModsKey(mods),
            Accuracy: accuracy,
            MaxCombo: score.MaxCombo,
            Rank: rank(accuracy, lazerMods.Any(mod => mod.Acronym is "HD" or "FI" or "FL")),
            Statistics: statistics,
            Timestamp: date.Ticks,
            Date: date.ToString("O"),
            IsLegacy: true,
            HasReplay: hasReplay);
    }

    /// <summary>osu!stable's osu!mania grade (silver with Hidden, Fade In or Flashlight), named like osu!lazer's ScoreRank.</summary>
    private static string rank(double accuracy, bool silver) => accuracy switch
    {
        >= 1 => silver ? "XH" : "X",
        > 0.95 => silver ? "SH" : "S",
        > 0.9 => "A",
        > 0.8 => "B",
        > 0.7 => "C",
        _ => "D",
    };
}
