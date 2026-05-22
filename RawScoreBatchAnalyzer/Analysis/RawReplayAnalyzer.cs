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
    private readonly ReplayTimelineBuilder timelineBuilder = new();

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
            var rate = ModUtility.GetPlaybackRate(replayMods);
            if (Math.Abs(rate - 1.0) < 0.0001)
                rate = ModUtility.GetPlaybackRate(source);
            var raw = timelineBuilder.Build(replayPath, beatmapPath, rate, CorrectionMode.Raw);
            var commitRaw = timelineBuilder.Build(replayPath, beatmapPath, rate, CorrectionMode.Raw, JudgementScoreOrder.CommitTime);
            var lastFrame = raw.Frames.LastOrDefault();
            var lastCommitFrame = commitRaw.Frames.LastOrDefault();
            if (lastFrame == null)
                return Error(replayPath, "failed", "Raw timeline is empty.");
            if (lastCommitFrame == null)
                return Error(replayPath, "failed", "Commit-order raw timeline is empty.");

            var scoreInfo = source.ScoreInfo;
            var rawHits = lastFrame.Hits;
            var rawScore = lastFrame.Score;
            var osrAccuracy = scoreInfo.Accuracy;
            var rawAccuracy = lastFrame.Accuracy;

            return new ReplayAnalysisResult(
                ReplayFile: replayPath,
                BeatmapHash: beatmapHash,
                BeatmapPath: beatmapPath,
                Player: scoreInfo.User?.Username ?? header.Player,
                Mods: ModUtility.FormatModsText(replayMods),
                Rate: rate,
                Ruleset: "mania",
                OsrScore: header.TotalScore,
                RawScore: rawScore,
                ScoreDelta: rawScore - header.TotalScore,
                ScoreDeltaRatio: header.TotalScore != 0 ? Math.Abs(rawScore - header.TotalScore) / (double)Math.Abs(header.TotalScore) : 0,
                CommitRawScore: lastCommitFrame.Score,
                CommitScoreDelta: lastCommitFrame.Score - header.TotalScore,
                CommitScoreDeltaRatio: header.TotalScore != 0 ? Math.Abs(lastCommitFrame.Score - header.TotalScore) / (double)Math.Abs(header.TotalScore) : 0,
                OsrAccuracy: osrAccuracy,
                RawAccuracy: rawAccuracy,
                AccuracyDelta: rawAccuracy - osrAccuracy,
                CommitRawAccuracy: lastCommitFrame.Accuracy,
                CommitAccuracyDelta: lastCommitFrame.Accuracy - osrAccuracy,
                OsrPerfect: header.CountGeki,
                RawPerfect: Hit(rawHits, "Perfect"),
                OsrGreat: header.Count300,
                RawGreat: Hit(rawHits, "Great"),
                OsrGood: header.CountKatu,
                RawGood: Hit(rawHits, "Good"),
                OsrOk: header.Count100,
                RawOk: Hit(rawHits, "Ok"),
                OsrMeh: header.Count50,
                RawMeh: Hit(rawHits, "Meh"),
                OsrMiss: header.CountMiss,
                RawMiss: Hit(rawHits, "Miss"),
                MissDelta: Hit(rawHits, "Miss") - header.CountMiss,
                OsrMaxCombo: header.MaxCombo,
                RawMaxCombo: lastFrame.MaxCombo,
                MaxComboDelta: lastFrame.MaxCombo - header.MaxCombo,
                CommitRawMaxCombo: lastCommitFrame.MaxCombo,
                CommitMaxComboDelta: lastCommitFrame.MaxCombo - header.MaxCombo,
                NoteCount: raw.TotalNotes,
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
            CommitRawScore: 0,
            CommitScoreDelta: 0,
            CommitScoreDeltaRatio: 0,
            OsrAccuracy: 0,
            RawAccuracy: 0,
            AccuracyDelta: 0,
            CommitRawAccuracy: 0,
            CommitAccuracyDelta: 0,
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
            CommitRawMaxCombo: 0,
            CommitMaxComboDelta: 0,
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
