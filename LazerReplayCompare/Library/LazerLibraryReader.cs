using osu.Game.Beatmaps;
using osu.Game.Collections;
using Realms;

namespace LazerReplayCompare;

/// <summary>osu!lazer's mania difficulties and collections, read from its database (a copy, opened read-only).</summary>
public static class LazerLibraryReader
{
    public static (List<MapInfo> Maps, List<OsuCollection> Collections) Read(Realm realm, string filesRoot)
    {
        var maps = new List<MapInfo>();
        foreach (var set in realm.All<BeatmapSetInfo>().AsEnumerable())
        {
            if (set.DeletePending)
                continue;

            Dictionary<string, string>? files = null;
            foreach (var beatmap in set.Beatmaps)
            {
                try
                {
                    if (beatmap.Hidden || beatmap.Ruleset?.ShortName != "mania" || string.IsNullOrWhiteSpace(beatmap.MD5Hash))
                        continue;

                    files ??= set.Files.AsEnumerable()
                                 .Where(file => file.Filename != null && file.File?.Hash != null)
                                 .GroupBy(file => file.Filename!, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(group => group.Key, group => group.First().File!.Hash!, StringComparer.OrdinalIgnoreCase);
                    maps.Add(read(beatmap, set, files, filesRoot));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // one odd entry doesn't stop the rest
                    InternalLogger.Log(ex);
                }
            }
        }

        var collections = new List<OsuCollection>();
        try
        {
            foreach (var collection in realm.All<BeatmapCollection>().AsEnumerable())
                collections.Add(new OsuCollection(collection.Name, "lazer", collection.BeatmapMD5Hashes.ToList()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            InternalLogger.Log(ex);
        }

        return (maps, collections);
    }

    private static MapInfo read(BeatmapInfo beatmap, BeatmapSetInfo set, Dictionary<string, string> files, string filesRoot)
    {
        var metadata = beatmap.Metadata;
        var background = metadata?.BackgroundFile;
        var backgroundHash = string.IsNullOrWhiteSpace(background) ? string.Empty : files.GetValueOrDefault(background) ?? string.Empty;

        return new MapInfo
        {
            Md5 = beatmap.MD5Hash,
            Title = metadata?.Title ?? string.Empty,
            TitleUnicode = metadata?.TitleUnicode ?? string.Empty,
            Artist = metadata?.Artist ?? string.Empty,
            ArtistUnicode = metadata?.ArtistUnicode ?? string.Empty,
            Source = metadata?.Source ?? string.Empty,
            Tags = metadata?.Tags ?? string.Empty,
            Mapper = metadata?.Author?.Username ?? string.Empty,
            Version = beatmap.DifficultyName ?? string.Empty,
            Keys = (int)Math.Round(beatmap.Difficulty?.CircleSize ?? 0),
            Od = beatmap.Difficulty?.OverallDifficulty ?? 0,
            Hp = beatmap.Difficulty?.DrainRate ?? 0,
            Stars = beatmap.StarRating,
            LengthMs = beatmap.Length,
            Bpm = beatmap.BPM,
            Objects = Math.Max(0, beatmap.TotalObjectCount),
            Holds = Math.Max(0, beatmap.EndTimeObjectCount),
            BeatmapId = beatmap.OnlineID,
            SetId = set.OnlineID,
            Clients = "lazer",
            Status = MapStatus.FromLazer((int)beatmap.Status),
            ChartPath = ReplayFinder.GetLazerFilePath(filesRoot, beatmap.Hash),
            BackgroundPath = ReplayFinder.GetLazerFilePath(filesRoot, backgroundHash),
        };
    }
}
