using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerReplayCompare;

public sealed class ReplayTimelineBuilder
{
    private readonly ManiaTimelineCalculator maniaTimelineCalculator = new();

    public ReplayTimelineResponse Build(string replayPath, string? beatmapPath, double rate = 0, bool applyCorrections = true)
    {
        if (string.IsNullOrWhiteSpace(replayPath))
            throw new ArgumentException("Missing osr path.", nameof(replayPath));

        if (!File.Exists(replayPath))
            throw new FileNotFoundException("Replay file was not found.", replayPath);

        using var stream = File.OpenRead(replayPath);
        var score = new LazerReplayScoreDecoder(beatmapPath).Parse(stream);

        var totalFrames = score.Replay.Frames.Count;
        var framesWithHeader = score.Replay.Frames.Count(f => f.Header != null);
        var frames = new List<ReplayTimelineFrame>();
        var lastHitCount = 0;

        foreach (var replayFrame in score.Replay.Frames.Where(frame => frame.Header != null).OrderBy(frame => frame.Time))
        {
            var header = replayFrame.Header!;
            var hits = ConvertStatistics(header.Statistics);
            var hitCount = GetHitCount(hits);

            if (hitCount <= lastHitCount)
                continue;

            frames.Add(new ReplayTimelineFrame(
                Time: replayFrame.Time,
                Index: hitCount,
                Score: header.TotalScore,
                Combo: header.Combo,
                MaxCombo: header.MaxCombo,
                Accuracy: header.Accuracy,
                Hits: hits));

            lastHitCount = hitCount;
        }

        var source = $"frame-header(total={totalFrames},withHeader={framesWithHeader})";
        if (frames.Count == 0)
        {
            var detectedRate = GetPlaybackRate(score);
            var finalRate = rate > 0 ? rate : detectedRate;
            var scoreMultiplier = GetScoreMultiplier(score);
            frames = maniaTimelineCalculator.Build(score, beatmapPath ?? string.Empty, finalRate, scoreMultiplier, applyCorrections);
            var correctionMode = applyCorrections
                ? "density-limited-judgement-score-corrected"
                : "raw-judgement";
            source = $"mania-simulated(rate={finalRate:F4},scoreMultiplier={scoreMultiplier:F4},matcher=lazer-event,{correctionMode},totalFrames={totalFrames},withHeader={framesWithHeader})";
        }

        if (frames.Count == 0)
            throw new InvalidOperationException("Could not build a replay timeline from this replay.");

        return new ReplayTimelineResponse(
            ReplayPath: replayPath,
            BeatmapPath: beatmapPath ?? string.Empty,
            Source: source,
            Estimated: false,
            TotalNotes: frames.Last().Index,
            FinalScore: score.ScoreInfo.TotalScore,
            FinalAccuracy: score.ScoreInfo.Accuracy,
            FinalMaxCombo: score.ScoreInfo.MaxCombo,
            Timeline: frames.Select(frame => new object[] { frame.Time, frame.Score, frame.Combo }).ToList(),
            Frames: frames);
    }

    private static Dictionary<string, int> ConvertStatistics(IReadOnlyDictionary<HitResult, int> statistics)
    {
        return statistics.ToDictionary(
            pair => pair.Key.ToString(),
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    private static int GetHitCount(Dictionary<string, int> hits)
    {
        return hits
            .Where(pair => IsJudgementResult(pair.Key))
            .Sum(pair => pair.Value);
    }

    private static bool IsJudgementResult(string key)
    {
        return key is
            "Miss" or
            "Meh" or
            "Ok" or
            "Good" or
            "Great" or
            "Perfect";
    }

    private static double GetPlaybackRate(Score score)
    {
        try
        {
            var mods = score.ScoreInfo.GetType().GetProperty("Mods")?.GetValue(score.ScoreInfo);
            if (mods is not IEnumerable modsEnumerable)
                return 1.0;

            foreach (var mod in modsEnumerable.Cast<object>())
            {
                var acronym = mod.GetType().GetProperty("Acronym")?.GetValue(mod)?.ToString()?.ToUpperInvariant();
                if (acronym is null)
                    continue;

                var isSpeedUp = acronym is "DT" or "NC";
                var isSlowDown = acronym is "HT" or "DC";
                if (!isSpeedUp && !isSlowDown)
                    continue;

                var rate = TryGetSpeedChangeSetting(mod);
                if (rate.HasValue)
                    return rate.Value;

                return isSpeedUp ? 1.5 : 0.75;
            }
        }
        catch
        {
        }

        return 1.0;
    }

    private static double GetScoreMultiplier(Score score)
    {
        var multiplier = 1.0;

        try
        {
            var mods = score.ScoreInfo.GetType().GetProperty("Mods")?.GetValue(score.ScoreInfo);
            if (mods is not IEnumerable modsEnumerable)
                return multiplier;

            foreach (var mod in modsEnumerable.Cast<object>())
            {
                var value = mod.GetType().GetProperty("ScoreMultiplier")?.GetValue(mod);
                if (value is IConvertible convertible)
                    multiplier *= convertible.ToDouble(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            return 1.0;
        }

        return multiplier > 0 ? multiplier : 1.0;
    }

    private static double? TryGetSpeedChangeSetting(object mod)
    {
        try
        {
            var settingsObj = mod.GetType().GetProperty("Settings")?.GetValue(mod);

            if (settingsObj is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    var key = entry.Key?.ToString()?.ToLowerInvariant();
                    if (key?.Contains("speed") == true || key?.Contains("rate") == true)
                    {
                        if (TryParseRate(GetSettingValue(entry.Value), out var r))
                            return r;
                    }
                }
            }
            else if (settingsObj is IEnumerable enumerable)
            {
                foreach (var item in enumerable.Cast<object>())
                {
                    var key = item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()?.ToLowerInvariant();
                    if (key?.Contains("speed") == true || key?.Contains("rate") == true)
                    {
                        var raw = item.GetType().GetProperty("Value")?.GetValue(item);
                        if (TryParseRate(GetSettingValue(raw), out var r))
                            return r;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryParseRate(string? text, out double rate)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) && rate > 0)
            return true;

        var match = Regex.Match(text ?? string.Empty, @"\d+(\.\d+)?");
        return match.Success &&
            double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) &&
            rate > 0;
    }

    private static string? GetSettingValue(object? value)
    {
        if (value == null)
            return null;

        var nestedValue = value.GetType().GetProperty("Value")?.GetValue(value);
        if (nestedValue != null && !ReferenceEquals(nestedValue, value))
            value = nestedValue;

        return value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}

public sealed record ReplayTimelineResponse(
    string ReplayPath,
    string BeatmapPath,
    string Source,
    bool Estimated,
    int TotalNotes,
    long FinalScore,
    double FinalAccuracy,
    int FinalMaxCombo,
    List<object[]> Timeline,
    List<ReplayTimelineFrame> Frames);

public sealed record ReplayTimelineFrame(
    double Time,
    int Index,
    long Score,
    int Combo,
    int MaxCombo,
    double Accuracy,
    Dictionary<string, int> Hits);
