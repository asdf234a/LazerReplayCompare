using System.Globalization;

namespace LazerReplayCompare;

public sealed class DebugComparisonService
{
    private readonly ReplayTimelineBuilder timelineBuilder = new();
    private readonly object sync = new();
    private ReplayTimelineResponse? cachedTimeline;
    private string cacheKey = string.Empty;
    private string? currentLogPath;
    private string? currentMajorLogPath;
    private int mismatchCount;
    private readonly HashSet<int> recordedIndexes = new();
    private readonly List<DebugComparisonRow> comparisonRows = new();

    public string? CurrentLogPath
    {
        get
        {
            lock (sync)
                return currentLogPath;
        }
    }

    public string? CurrentMajorLogPath
    {
        get
        {
            lock (sync)
                return currentMajorLogPath;
        }
    }

    public int MismatchCount
    {
        get
        {
            lock (sync)
                return mismatchCount;
        }
    }

    public DebugRunResult StartRun(DebugRunRequest request)
    {
        lock (sync)
        {
            try
            {
                cacheKey = string.Empty;
                cachedTimeline = null;
                currentLogPath = CreateLogPath();
                currentMajorLogPath = CreateMajorLogPath(currentLogPath);
                mismatchCount = 0;
                recordedIndexes.Clear();
                comparisonRows.Clear();
                WriteHeader(currentLogPath);
                WriteHeader(currentMajorLogPath);
                return new DebugRunResult(true, "started", currentLogPath, mismatchCount);
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
                return new DebugRunResult(false, ex.Message, currentLogPath, mismatchCount);
            }
        }
    }

    public DebugHitResult RecordHit(DebugHitRequest request)
    {
        lock (sync)
        {
            try
            {
                var timeline = GetTimeline(request);
                if (!recordedIndexes.Add(request.Index))
                    return new DebugHitResult(true, "duplicate ignored", null, request.Result, false, currentLogPath, mismatchCount);

                var frame = timeline.Frames.FirstOrDefault(f => f.Index >= request.Index);
                if (frame == null)
                    return new DebugHitResult(false, "timeline frame not found", null, request.Result, false, currentLogPath, mismatchCount);

                var previous = timeline.Frames.LastOrDefault(f => f.Index < frame.Index);
                var predicted = GetNewJudgement(previous?.Hits, frame.Hits);
                var mismatch = !string.Equals(predicted, request.Result, StringComparison.OrdinalIgnoreCase);
                var row = DebugComparisonRow.From(request, timeline, frame, predicted, mismatch);
                comparisonRows.Add(row);

                var adjustedRows = BuildBlockAdjustedRows(comparisonRows).ToArray();
                mismatchCount = adjustedRows.Count(r => r.Mismatch);
                WriteFullComparisonLog(adjustedRows);
                WriteMajorComparisonLog(adjustedRows);

                var adjustedRow = adjustedRows.LastOrDefault(r => r.Index == request.Index) ?? row;

                return new DebugHitResult(true, adjustedRow.Mismatch ? "mismatch logged" : "matched", predicted, request.Result, adjustedRow.Mismatch, currentLogPath, mismatchCount);
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
                return new DebugHitResult(false, ex.Message, null, request.Result, false, currentLogPath, mismatchCount);
            }
        }
    }

    private ReplayTimelineResponse GetTimeline(DebugHitRequest request)
    {
        var key = string.Join("|",
            request.ReplayPath,
            request.BeatmapPath,
            request.Rate.ToString("0.0000", CultureInfo.InvariantCulture),
            request.CorrectionMode);

        if (cachedTimeline != null && key == cacheKey)
            return cachedTimeline;

        cachedTimeline = timelineBuilder.Build(request.ReplayPath, request.BeatmapPath, request.Rate, request.CorrectionMode);
        cacheKey = key;
        currentLogPath ??= CreateLogPath();
        currentMajorLogPath ??= CreateMajorLogPath(currentLogPath);
        mismatchCount = 0;
        recordedIndexes.Clear();
        comparisonRows.Clear();
        if (!File.Exists(currentLogPath))
            WriteHeader(currentLogPath);
        if (!File.Exists(currentMajorLogPath))
            WriteHeader(currentMajorLogPath);
        return cachedTimeline;
    }

    private static string? GetNewJudgement(IReadOnlyDictionary<string, int>? previous, IReadOnlyDictionary<string, int> current)
    {
        foreach (var key in new[] { "Perfect", "Great", "Good", "Ok", "Meh", "Miss" })
        {
            var before = previous != null && previous.TryGetValue(key, out var oldValue) ? oldValue : 0;
            var after = current.TryGetValue(key, out var newValue) ? newValue : 0;
            if (after > before)
                return key;
        }

        return null;
    }

    private void WriteFullComparisonLog(IReadOnlyList<DebugComparisonRow> rows)
    {
        if (currentLogPath == null)
            return;

        File.WriteAllText(currentLogPath, HeaderLine + Environment.NewLine);
        if (rows.Count > 0)
            File.AppendAllLines(currentLogPath, rows.Select(ToCsv));
    }

    private void WriteMajorComparisonLog(IReadOnlyList<DebugComparisonRow> adjustedRows)
    {
        if (currentMajorLogPath == null)
            return;

        var majorRows = FilterMajorRows(adjustedRows).ToArray();
        File.WriteAllText(currentMajorLogPath, HeaderLine + Environment.NewLine);
        if (majorRows.Length > 0)
            File.AppendAllLines(currentMajorLogPath, majorRows.Select(ToCsv));
    }

    private static IEnumerable<DebugComparisonRow> BuildBlockAdjustedRows(IReadOnlyList<DebugComparisonRow> rows)
    {
        foreach (var group in rows.GroupBy(row => row.LiveScore))
        {
            var groupRows = group.ToArray();
            var isSameScoreBlock = group.Key.HasValue && groupRows.Length > 1;
            var liveCounts = CountResults(groupRows.Select(row => row.LiveResult));
            var predictedCounts = CountResults(groupRows.Select(row => row.PredictedResult ?? "unknown"));
            var blockMatches = isSameScoreBlock && CountsEqual(liveCounts, predictedCounts);

            foreach (var row in groupRows)
                yield return blockMatches ? row with { Mismatch = false } : row;
        }
    }

    private static IEnumerable<DebugComparisonRow> FilterMajorRows(IReadOnlyList<DebugComparisonRow> rows)
    {
        var swapIndexes = new HashSet<int>();
        var mismatches = rows.Where(row => row.Mismatch).OrderBy(row => row.Index).ToArray();

        for (var i = 0; i < mismatches.Length; i++)
        {
            for (var j = i + 1; j < mismatches.Length; j++)
            {
                var distance = mismatches[j].Index - mismatches[i].Index;
                if (distance > 4)
                    break;

                if (mismatches[i].LiveResult == mismatches[j].PredictedResult &&
                    mismatches[i].PredictedResult == mismatches[j].LiveResult)
                {
                    swapIndexes.Add(mismatches[i].Index);
                    swapIndexes.Add(mismatches[j].Index);
                }
            }
        }

        return mismatches.Where(row => !swapIndexes.Contains(row.Index));
    }

    private static Dictionary<string, int> CountResults(IEnumerable<string> results)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var result in results)
            counts[result] = counts.GetValueOrDefault(result) + 1;
        return counts;
    }

    private static bool CountsEqual(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right)
    {
        return left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static void WriteHeader(string path)
    {
        File.WriteAllText(path, HeaderLine + Environment.NewLine);
    }

    private const string HeaderLine = "loggedAt,mode,index,liveTime,predictedTime,liveResult,predictedResult,mismatch,liveScore,predictedScore,liveCombo,predictedCombo,predictedColumn,predictedKind,predictedObjectTime,predictedOffset,replayPath,beatmapPath";

    private static string CreateLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LazerReplayCompare",
            "debug");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "judgement-mismatch-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
    }

    private static string CreateMajorLogPath(string logPath)
    {
        return Path.Combine(
            Path.GetDirectoryName(logPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(logPath) + "-major.csv");
    }

    private static string ToCsv(DebugComparisonRow row)
    {
        return string.Join(",",
            Escape(row.LoggedAt),
            Escape(row.Mode),
            row.Index.ToString(CultureInfo.InvariantCulture),
            row.LiveTime.ToString("0.###", CultureInfo.InvariantCulture),
            row.PredictedTime.ToString("0.###", CultureInfo.InvariantCulture),
            Escape(row.LiveResult),
            Escape(row.PredictedResult ?? "unknown"),
            row.Mismatch ? "1" : "0",
            row.LiveScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            row.PredictedScore.ToString(CultureInfo.InvariantCulture),
            row.LiveCombo?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            row.PredictedCombo.ToString(CultureInfo.InvariantCulture),
            row.PredictedColumn?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Escape(row.PredictedKind ?? string.Empty),
            row.PredictedObjectTime?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            row.PredictedOffset?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            Escape(row.ReplayPath),
            Escape(row.BeatmapPath));
    }

    private static string Escape(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

internal sealed record DebugComparisonRow(
    string LoggedAt,
    string Mode,
    int Index,
    double LiveTime,
    double PredictedTime,
    string LiveResult,
    string? PredictedResult,
    bool Mismatch,
    long? LiveScore,
    long PredictedScore,
    int? LiveCombo,
    int PredictedCombo,
    int? PredictedColumn,
    string? PredictedKind,
    double? PredictedObjectTime,
    double? PredictedOffset,
    string ReplayPath,
    string BeatmapPath)
{
    public static DebugComparisonRow From(DebugHitRequest request, ReplayTimelineResponse timeline, ReplayTimelineFrame frame, string? predicted, bool mismatch)
    {
        return new DebugComparisonRow(
            LoggedAt: DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            Mode: request.CorrectionMode.ToString(),
            Index: request.Index,
            LiveTime: request.Time,
            PredictedTime: frame.Time,
            LiveResult: request.Result,
            PredictedResult: predicted,
            Mismatch: mismatch,
            LiveScore: request.Score,
            PredictedScore: frame.Score,
            LiveCombo: request.Combo,
            PredictedCombo: frame.Combo,
            PredictedColumn: frame.DebugInfo?.Column,
            PredictedKind: frame.DebugInfo?.Kind,
            PredictedObjectTime: frame.DebugInfo?.ObjectTime,
            PredictedOffset: frame.DebugInfo?.Offset,
            ReplayPath: timeline.ReplayPath,
            BeatmapPath: timeline.BeatmapPath);
    }
}

public sealed record DebugHitResult(
    bool Accepted,
    string Message,
    string? Predicted,
    string Actual,
    bool Mismatch,
    string? LogPath,
    int MismatchCount);

public sealed record DebugRunResult(
    bool Accepted,
    string Message,
    string? LogPath,
    int MismatchCount);

public sealed record DebugRunRequest(
    string ReplayPath,
    string BeatmapPath,
    double Rate,
    CorrectionMode CorrectionMode)
{
    public static DebugRunRequest FromQuery(IReadOnlyDictionary<string, string> query, CorrectionMode correctionMode)
    {
        var replayPath = Get(query, "osr") ?? Get(query, "replay") ?? Get(query, "filePath") ?? string.Empty;
        var beatmapPath = Get(query, "osu") ?? Get(query, "beatmap") ?? string.Empty;
        var rate = ParseDouble(Get(query, "rate"), 0);
        return new DebugRunRequest(replayPath, beatmapPath, rate, correctionMode);
    }

    private static string? Get(IReadOnlyDictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static double ParseDouble(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}

public sealed record DebugHitRequest(
    string ReplayPath,
    string BeatmapPath,
    double Rate,
    CorrectionMode CorrectionMode,
    int Index,
    double Time,
    string Result,
    long? Score,
    int? Combo)
{
    public static DebugHitRequest FromQuery(IReadOnlyDictionary<string, string> query, CorrectionMode correctionMode)
    {
        var replayPath = Get(query, "osr") ?? Get(query, "replay") ?? Get(query, "filePath") ?? string.Empty;
        var beatmapPath = Get(query, "osu") ?? Get(query, "beatmap") ?? string.Empty;
        var result = NormalizeResult(Get(query, "result") ?? Get(query, "judgement") ?? string.Empty);
        var rate = ParseDouble(Get(query, "rate"), 0);
        var index = ParseInt(Get(query, "index") ?? Get(query, "hitIndex"), 0);
        var time = ParseDouble(Get(query, "time"), 0);
        var score = ParseLongNullable(Get(query, "score"));
        var combo = ParseIntNullable(Get(query, "combo"));

        return new DebugHitRequest(replayPath, beatmapPath, rate, correctionMode, index, time, result, score, combo);
    }

    private static string? Get(IReadOnlyDictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string NormalizeResult(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "perfect" or "geki" or "320" => "Perfect",
            "great" or "300" => "Great",
            "good" or "katu" or "200" => "Good",
            "ok" or "100" => "Ok",
            "meh" or "50" => "Meh",
            "miss" or "0" => "Miss",
            _ => value,
        };
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static int? ParseIntNullable(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseLongNullable(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double ParseDouble(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
