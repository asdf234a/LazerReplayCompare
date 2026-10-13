using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using LazerReplayCompare;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using RawScoreBatchAnalyzer.Realm;
using RawScoreBatchAnalyzer.Reports;

namespace RawScoreBatchAnalyzer.Analysis;

public sealed class RawReplayAnalyzer
{
    public async Task<AnalysisSummary> AnalyzeAsync(
        AnalysisOptions options,
        IProgress<AnalysisProgress> progress,
        CancellationToken cancellationToken)
    {
        Validate(options);

        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var replayFiles = Directory
            .EnumerateFiles(options.ReplayFolder, "*.osr", searchOption)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var total = replayFiles.Length;
        var stopwatch = Stopwatch.StartNew();
        var success = 0;
        var skipped = 0;
        var failed = 0;
        var processed = 0;
        var lastReport = Stopwatch.StartNew();
        var index = BeatmapRealmIndex.Build(options.RealmPath, options.FilesRoot);

        progress.Report(new AnalysisProgress(total, 0, 0, 0, 0, 0, string.Empty, $"Indexed {index.Count:N0} beatmaps from realm."));

        using var writer = new CsvReportWriter(options.OutputCsvPath);
        using var timer = new Timer(_ =>
        {
            var currentProcessed = Volatile.Read(ref processed);
            var filesPerSecond = stopwatch.Elapsed.TotalSeconds > 0 ? currentProcessed / stopwatch.Elapsed.TotalSeconds : 0;
            progress.Report(new AnalysisProgress(
                total,
                currentProcessed,
                Volatile.Read(ref success),
                Volatile.Read(ref skipped),
                Volatile.Read(ref failed),
                filesPerSecond,
                string.Empty,
                "Analyzing..."));
        }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
        };

        await Parallel.ForEachAsync(replayFiles, parallelOptions, (path, token) =>
        {
            var result = AnalyzeReplay(path, index, token);
            writer.Write(result);

            switch (result.Status)
            {
                case "ok":
                    Interlocked.Increment(ref success);
                    break;
                case "skipped":
                    Interlocked.Increment(ref skipped);
                    break;
                default:
                    Interlocked.Increment(ref failed);
                    break;
            }

            var done = Interlocked.Increment(ref processed);
            if (lastReport.ElapsedMilliseconds >= 100)
            {
                lastReport.Restart();
                var filesPerSecond = stopwatch.Elapsed.TotalSeconds > 0 ? done / stopwatch.Elapsed.TotalSeconds : 0;
                progress.Report(new AnalysisProgress(
                    total,
                    done,
                    Volatile.Read(ref success),
                    Volatile.Read(ref skipped),
                    Volatile.Read(ref failed),
                    filesPerSecond,
                    Path.GetFileName(path),
                    "Analyzing..."));
            }

            return ValueTask.CompletedTask;
        });

        stopwatch.Stop();

        var summary = new AnalysisSummary(total, success, skipped, failed, stopwatch.Elapsed, options.OutputCsvPath);
        progress.Report(new AnalysisProgress(
            total,
            processed,
            success,
            skipped,
            failed,
            stopwatch.Elapsed.TotalSeconds > 0 ? processed / stopwatch.Elapsed.TotalSeconds : 0,
            string.Empty,
            "Done."));

        return summary;
    }

    private ReplayAnalysisResult AnalyzeReplay(string replayPath, BeatmapRealmIndex index, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var header = OsrHeaderReader.Read(replayPath);
            var beatmapHash = header.BeatmapHash;
            if (string.IsNullOrWhiteSpace(beatmapHash))
                return Error(replayPath, "skipped", "Replay has no beatmap hash.");

            if (header.Ruleset != 3)
                return Error(replayPath, "skipped", "Only osu!mania replays are supported.");

            if (!index.TryGetBeatmapPath(beatmapHash, out var beatmapPath))
                return Error(replayPath, "skipped", $"Beatmap not found in realm index: {beatmapHash}");

            var source = Decode(replayPath, beatmapPath);
            if (!IsMania(source))
                return Error(replayPath, "skipped", "Only osu!mania replays are supported.");

            var replayMods = GetReplayMods(source);
            var rate = ModUtility.GetPlaybackRate(source);

            // One decode + one simulation per replay: the lazer port gives the raw result and the boundary-corrected one.
            var simulation = ManiaReplaySimulation.Run(source, beatmapPath);
            var raw = simulation.RawFinal;
            var corrected = simulation.CorrectedFinal;

            var scoreInfo = source.ScoreInfo;
            // Recorded lazer score (for stable replays this is lazer's conversion of the stable score).
            var osrScore = scoreInfo.TotalScore;
            var osrAccuracy = scoreInfo.Accuracy;
            var osrHits = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Perfect"] = header.CountGeki,
                ["Great"] = header.Count300,
                ["Good"] = header.CountKatu,
                ["Ok"] = header.Count100,
                ["Meh"] = header.Count50,
                ["Miss"] = header.CountMiss,
            };

            return new ReplayAnalysisResult(
                ReplayFile: replayPath,
                BeatmapHash: beatmapHash,
                BeatmapPath: beatmapPath,
                Player: scoreInfo.User?.Username ?? header.Player,
                Mods: ModUtility.FormatModsText(replayMods),
                Rate: rate,
                Ruleset: "mania",
                OsrScore: osrScore,
                RawScore: raw.Score,
                ScoreDelta: raw.Score - osrScore,
                ScoreDeltaRatio: DeltaRatio(raw.Score, osrScore),
                CorrectedScore: corrected.Score,
                CorrectedScoreDelta: corrected.Score - osrScore,
                CorrectedScoreDeltaRatio: DeltaRatio(corrected.Score, osrScore),
                OsrAccuracy: osrAccuracy,
                RawAccuracy: raw.Accuracy,
                AccuracyDelta: raw.Accuracy - osrAccuracy,
                CorrectedAccuracy: corrected.Accuracy,
                CorrectedAccuracyDelta: corrected.Accuracy - osrAccuracy,
                OsrPerfect: header.CountGeki,
                RawPerfect: Hit(raw.Hits, "Perfect"),
                OsrGreat: header.Count300,
                RawGreat: Hit(raw.Hits, "Great"),
                OsrGood: header.CountKatu,
                RawGood: Hit(raw.Hits, "Good"),
                OsrOk: header.Count100,
                RawOk: Hit(raw.Hits, "Ok"),
                OsrMeh: header.Count50,
                RawMeh: Hit(raw.Hits, "Meh"),
                OsrMiss: header.CountMiss,
                RawMiss: Hit(raw.Hits, "Miss"),
                MissDelta: Hit(raw.Hits, "Miss") - header.CountMiss,
                OsrMaxCombo: header.MaxCombo,
                RawMaxCombo: raw.MaxCombo,
                MaxComboDelta: raw.MaxCombo - header.MaxCombo,
                CorrectedMaxCombo: corrected.MaxCombo,
                CorrectedMaxComboDelta: corrected.MaxCombo - header.MaxCombo,
                RawHitDelta: HitDelta(raw.Hits, osrHits),
                CorrectedHitDelta: HitDelta(corrected.Hits, osrHits),
                IsLegacy: scoreInfo.LegacyTotalScore != null,
                Correction: simulation.Correction,
                NoteCount: simulation.JudgementCount,
                Status: "ok",
                Error: string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(replayPath, "failed", ex.ToString());
        }
    }

    private static Score Decode(string replayPath, string? beatmapPath)
    {
        using var stream = File.OpenRead(replayPath);
        return new LazerReplayScoreDecoder(beatmapPath).Parse(stream);
    }

    private static bool IsMania(Score score)
    {
        try
        {
            return score.ScoreInfo.Ruleset.OnlineID == 3;
        }
        catch
        {
            return false;
        }
    }

    private static int Hit(IReadOnlyDictionary<string, int> hits, string key)
    {
        return hits.TryGetValue(key, out var value) ? value : 0;
    }

    private static int HitDelta(IReadOnlyDictionary<string, int> simulated, IReadOnlyDictionary<string, int> recorded)
    {
        return recorded.Keys.Sum(key => Math.Abs(Hit(simulated, key) - Hit(recorded, key)));
    }

    private static double DeltaRatio(long value, long reference)
    {
        return reference != 0 ? Math.Abs(value - reference) / (double)Math.Abs(reference) : 0;
    }

    private static string? GetStringProperty(object source, string name)
    {
        return source.GetType().GetProperty(name)?.GetValue(source)?.ToString();
    }

    private static List<ReplayMod> GetReplayMods(Score score)
    {
        try
        {
            var scoreInfo = score.ScoreInfo;
            var modsValue = scoreInfo.GetType().GetProperty("APIMods")?.GetValue(scoreInfo)
                ?? scoreInfo.GetType().GetProperty("Mods")?.GetValue(scoreInfo);

            if (modsValue is not IEnumerable modsEnumerable)
                return new List<ReplayMod>();

            return modsEnumerable
                .Cast<object>()
                .Select(mod =>
                {
                    var acronym = GetStringProperty(mod, "Acronym") ?? string.Empty;
                    var settings = GetSettings(mod);
                    var display = settings.Count == 0 ? acronym : $"{acronym}({FormatSettings(settings)})";
                    var matchKey = settings.Count == 0 ? acronym : $"{acronym}({BuildSettingsKey(settings)})";

                    return new ReplayMod(acronym, settings, display, matchKey);
                })
                .Where(mod => !string.IsNullOrWhiteSpace(mod.Acronym))
                .OrderBy(mod => mod.Acronym, StringComparer.Ordinal)
                .ThenBy(mod => mod.MatchKey, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return new List<ReplayMod>();
        }
    }

    private static Dictionary<string, string> GetSettings(object mod)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var settingsValue = mod.GetType().GetProperty("Settings")?.GetValue(mod);
        if (settingsValue == null)
            return settings;

        if (settingsValue is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
                AddSetting(settings, entry.Key, entry.Value);

            return settings;
        }

        if (settingsValue is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item == null)
                    continue;

                var key = item.GetType().GetProperty("Key")?.GetValue(item);
                var value = item.GetType().GetProperty("Value")?.GetValue(item);
                AddSetting(settings, key, value);
            }
        }

        return settings;
    }

    private static void AddSetting(Dictionary<string, string> settings, object? key, object? value)
    {
        var name = key?.ToString();
        if (string.IsNullOrWhiteSpace(name) || value == null)
            return;

        settings[name] = ConvertSettingValue(value);
    }

    private static string ConvertSettingValue(object value)
    {
        var nestedValue = value.GetType().GetProperty("Value")?.GetValue(value);
        if (nestedValue != null && !ReferenceEquals(nestedValue, value))
            value = nestedValue;

        return value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string FormatSettings(Dictionary<string, string> settings)
    {
        return string.Join(", ", settings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}: {pair.Value}"));
    }

    private static string BuildSettingsKey(Dictionary<string, string> settings)
    {
        return string.Join(",", settings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static ReplayAnalysisResult Error(string replayPath, string status, string error)
    {
        return new ReplayAnalysisResult(
            ReplayFile: replayPath,
            BeatmapHash: string.Empty,
            BeatmapPath: string.Empty,
            Player: string.Empty,
            Mods: string.Empty,
            Rate: 0,
            Ruleset: string.Empty,
            OsrScore: 0,
            RawScore: 0,
            ScoreDelta: 0,
            ScoreDeltaRatio: 0,
            CorrectedScore: 0,
            CorrectedScoreDelta: 0,
            CorrectedScoreDeltaRatio: 0,
            OsrAccuracy: 0,
            RawAccuracy: 0,
            AccuracyDelta: 0,
            CorrectedAccuracy: 0,
            CorrectedAccuracyDelta: 0,
            OsrPerfect: 0,
            RawPerfect: 0,
            OsrGreat: 0,
            RawGreat: 0,
            OsrGood: 0,
            RawGood: 0,
            OsrOk: 0,
            RawOk: 0,
            OsrMeh: 0,
            RawMeh: 0,
            OsrMiss: 0,
            RawMiss: 0,
            MissDelta: 0,
            OsrMaxCombo: 0,
            RawMaxCombo: 0,
            MaxComboDelta: 0,
            CorrectedMaxCombo: 0,
            CorrectedMaxComboDelta: 0,
            RawHitDelta: 0,
            CorrectedHitDelta: 0,
            IsLegacy: false,
            Correction: string.Empty,
            NoteCount: 0,
            Status: status,
            Error: error);
    }

    private static void Validate(AnalysisOptions options)
    {
        if (!File.Exists(options.RealmPath))
            throw new FileNotFoundException("client.realm was not found.", options.RealmPath);

        if (!Directory.Exists(options.FilesRoot))
            throw new DirectoryNotFoundException($"files folder was not found: {options.FilesRoot}");

        if (!Directory.Exists(options.ReplayFolder))
            throw new DirectoryNotFoundException($"OSR folder was not found: {options.ReplayFolder}");

        if (string.IsNullOrWhiteSpace(options.OutputCsvPath))
            throw new ArgumentException("Output CSV path is required.", nameof(options));
    }
}
