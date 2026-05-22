using LazerReplayCompare;
using osu.Game.Rulesets.Scoring;

internal static class TimelineDump
{
    public static int Run(string[] args)
    {
        var replayPath = GetArg(args, "--replay");
        var beatmapPath = GetArg(args, "--beatmap");
        var outputCsvPath = GetArg(args, "--out-csv");
        var rateText = GetArg(args, "--rate");
        var rate = double.TryParse(rateText, out var parsedRate) ? parsedRate : 0;

        if (string.IsNullOrWhiteSpace(replayPath) || string.IsNullOrWhiteSpace(beatmapPath))
        {
            Console.WriteLine("Usage: --dump-timeline --replay <path.osr> --beatmap <beatmap path> [--rate 1.0]");
            return 2;
        }

        using var stream = File.OpenRead(replayPath);
        var score = new LazerReplayScoreDecoder(beatmapPath).Parse(stream);
        var finalRate = rate > 0 ? rate : ModUtility.GetPlaybackRate(score);
        var od = ReadOverallDifficulty(beatmapPath);
        var windows = Windows.ForDifficulty(od, finalRate);

        var timeline = new ReplayTimelineBuilder().Build(
            replayPath,
            beatmapPath,
            finalRate,
            CorrectionMode.Raw);

        Console.WriteLine($"Replay: {Path.GetFileName(replayPath)}");
        Console.WriteLine($"Beatmap: {beatmapPath}");
        Console.WriteLine($"OD={od:F2}, Rate={finalRate:F4}, Frames={timeline.Frames.Count}, Source={timeline.Source}");
        Console.WriteLine($"OSR score={score.ScoreInfo.TotalScore}, acc={score.ScoreInfo.Accuracy:P4}, maxCombo={score.ScoreInfo.MaxCombo}");
        PrintStats("OSR", score.ScoreInfo.Statistics.ToDictionary(pair => pair.Key, pair => pair.Value));

        var rawStats = timeline.Frames.Last().Hits.ToDictionary(
            pair => Enum.Parse<HitResult>(pair.Key),
            pair => pair.Value);
        PrintStats("RAW", rawStats);

        var byKind = timeline.Frames
            .Where(frame => frame.DebugInfo != null)
            .GroupBy(frame => frame.DebugInfo!.Kind)
            .OrderBy(group => group.Key);

        Console.WriteLine();
        Console.WriteLine("Raw result by kind:");
        foreach (var group in byKind)
            PrintStringStats(group.Key, group.GroupBy(frame => frame.DebugInfo!.Result).ToDictionary(g => g.Key, g => g.Count()));

        var tailFrames = timeline.Frames
            .Where(frame => frame.DebugInfo?.Kind == "HoldTail")
            .Select(frame =>
            {
                var debug = frame.DebugInfo!;
                var natural = windows.ResultForTail(debug.Offset);
                return new TailRecord(frame, natural);
            })
            .ToArray();

        Console.WriteLine();
        Console.WriteLine("HoldTail details:");
        Console.WriteLine($"  total={tailFrames.Length}");
        Console.WriteLine($"  raw Meh={tailFrames.Count(t => t.Frame.DebugInfo!.Result == nameof(HitResult.Meh))}");
        Console.WriteLine($"  raw Miss={tailFrames.Count(t => t.Frame.DebugInfo!.Result == nameof(HitResult.Miss))}");
        Console.WriteLine($"  capped-to-Meh candidates={tailFrames.Count(t => t.Frame.DebugInfo!.Result == nameof(HitResult.Meh) && t.NaturalResult > HitResult.Meh)}");
        Console.WriteLine($"  natural-Meh tails={tailFrames.Count(t => t.Frame.DebugInfo!.Result == nameof(HitResult.Meh) && t.NaturalResult == HitResult.Meh)}");

        Console.WriteLine();
        Console.WriteLine("First capped HoldTail -> Meh candidates:");
        foreach (var record in tailFrames
            .Where(t => t.Frame.DebugInfo!.Result == nameof(HitResult.Meh) && t.NaturalResult > HitResult.Meh)
            .Take(30))
        {
            var frame = record.Frame;
            var debug = frame.DebugInfo!;
            Console.WriteLine(
                $"  idx={frame.Index}, t={frame.Time:F1}, col={debug.Column}, tail={debug.ObjectTime:F1}, offset={debug.Offset:F1}, natural={record.NaturalResult}, raw={debug.Result}, score={frame.Score}, combo={frame.Combo}");
        }

        Console.WriteLine();
        Console.WriteLine("First raw Miss events:");
        foreach (var frame in timeline.Frames
            .Where(frame => frame.DebugInfo?.Result == nameof(HitResult.Miss))
            .Take(30))
        {
            var debug = frame.DebugInfo!;
            Console.WriteLine(
                $"  idx={frame.Index}, kind={debug.Kind}, t={frame.Time:F1}, col={debug.Column}, obj={debug.ObjectTime:F1}, offset={debug.Offset:F1}, score={frame.Score}, combo={frame.Combo}");
        }

        Console.WriteLine();
        Console.WriteLine("Raw Miss offset summary:");
        foreach (var group in timeline.Frames
            .Where(frame => frame.DebugInfo?.Result == nameof(HitResult.Miss))
            .GroupBy(frame => frame.DebugInfo!.Kind)
            .OrderBy(group => group.Key))
        {
            var offsets = group.Select(frame => frame.DebugInfo!.Offset).ToArray();
            var early = offsets.Count(offset => offset < 0);
            var late = offsets.Length - early;
            var boundary = offsets.Count(offset => Math.Abs(Math.Abs(offset) - windows.Miss) <= 8 ||
                (group.Key == "HoldTail" && Math.Abs(Math.Abs(offset) - windows.Miss * 1.5) <= 8));

            Console.WriteLine(
                $"  {group.Key}: total={offsets.Length}, early={early}, late={late}, boundary-ish={boundary}, avgOffset={offsets.Average():F1}, medianAbs={MedianAbs(offsets):F1}, p90Abs={PercentileAbs(offsets, 0.90):F1}");
        }

        var keyPairs = ManiaReplayInputExtractor.ExtractKeyPairs(score, 10, ModUtility.HasMirrorMod(score));

        if (!string.IsNullOrWhiteSpace(outputCsvPath))
        {
            WriteTimelineCsv(outputCsvPath, replayPath, beatmapPath, timeline.Frames, keyPairs, score.ScoreInfo.Statistics.ToDictionary(pair => pair.Key, pair => pair.Value));
            Console.WriteLine();
            Console.WriteLine($"Timeline CSV: {outputCsvPath}");
        }

        Console.WriteLine();
        Console.WriteLine("Input pairs around first raw Miss events:");
        foreach (var frame in timeline.Frames
            .Where(frame => frame.DebugInfo?.Result == nameof(HitResult.Miss))
            .Take(12))
        {
            var debug = frame.DebugInfo!;
            Console.WriteLine($"  miss idx={frame.Index}, kind={debug.Kind}, col={debug.Column}, obj={debug.ObjectTime:F1}, judge={frame.Time:F1}, offset={debug.Offset:F1}");
            if (debug.Column >= keyPairs.Length)
                continue;

            foreach (var pair in keyPairs[debug.Column]
                .Where(pair => pair.press >= debug.ObjectTime - 350 && pair.press <= debug.ObjectTime + 350 ||
                    pair.release >= debug.ObjectTime - 350 && pair.release <= debug.ObjectTime + 350)
                .Take(8))
            {
                Console.WriteLine($"    pair press={pair.press:F1}, release={(double.IsPositiveInfinity(pair.release) ? "inf" : pair.release.ToString("F1"))}");
            }
        }

        return 0;
    }

    private static void WriteTimelineCsv(
        string outputCsvPath,
        string replayPath,
        string beatmapPath,
        IReadOnlyList<ReplayTimelineFrame> frames,
        (double press, double release)[][] keyPairs,
        IReadOnlyDictionary<HitResult, int> osrStats)
    {
        var directory = Path.GetDirectoryName(outputCsvPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var writer = new StreamWriter(outputCsvPath);
        writer.WriteLine(string.Join(',', new[]
        {
            "ReplayFile",
            "BeatmapPath",
            "Index",
            "Kind",
            "Result",
            "Column",
            "ObjectTime",
            "JudgeTime",
            "Offset",
            "Score",
            "Combo",
            "MaxCombo",
            "Accuracy",
            "RawPerfect",
            "RawGreat",
            "RawGood",
            "RawOk",
            "RawMeh",
            "RawMiss",
            "OsrPerfect",
            "OsrGreat",
            "OsrGood",
            "OsrOk",
            "OsrMeh",
            "OsrMiss",
            "PrevPress",
            "PrevPressOffset",
            "NextPress",
            "NextPressOffset",
            "PrevRelease",
            "PrevReleaseOffset",
            "NextRelease",
            "NextReleaseOffset",
            "NearbyPairs"
        }));

        foreach (var frame in frames)
        {
            var debug = frame.DebugInfo;
            if (debug == null)
                continue;

            var pairs = debug.Column >= 0 && debug.Column < keyPairs.Length
                ? keyPairs[debug.Column]
                : Array.Empty<(double press, double release)>();

            var prevPress = pairs
                .Where(pair => pair.press <= debug.ObjectTime)
                .Select(pair => (double?)pair.press)
                .LastOrDefault();
            var nextPress = pairs
                .Where(pair => pair.press > debug.ObjectTime)
                .Select(pair => (double?)pair.press)
                .FirstOrDefault();
            var releases = pairs
                .Where(pair => !double.IsPositiveInfinity(pair.release))
                .Select(pair => pair.release)
                .Order()
                .ToArray();
            var prevRelease = releases
                .Where(release => release <= debug.ObjectTime)
                .Select(release => (double?)release)
                .LastOrDefault();
            var nextRelease = releases
                .Where(release => release > debug.ObjectTime)
                .Select(release => (double?)release)
                .FirstOrDefault();

            var nearbyPairs = string.Join(" | ", pairs
                .Where(pair =>
                    Math.Abs(pair.press - debug.ObjectTime) <= 350 ||
                    (!double.IsPositiveInfinity(pair.release) && Math.Abs(pair.release - debug.ObjectTime) <= 350))
                .Take(8)
                .Select(pair => $"{pair.press:F1}/{(double.IsPositiveInfinity(pair.release) ? "inf" : pair.release.ToString("F1"))}"));

            writer.WriteLine(string.Join(',', new[]
            {
                Csv(Path.GetFileName(replayPath)),
                Csv(beatmapPath),
                frame.Index.ToString(),
                Csv(debug.Kind),
                Csv(debug.Result),
                debug.Column.ToString(),
                Number(debug.ObjectTime),
                Number(frame.Time),
                Number(debug.Offset),
                frame.Score.ToString(),
                frame.Combo.ToString(),
                frame.MaxCombo.ToString(),
                Number(frame.Accuracy),
                Hit(frame, "Perfect").ToString(),
                Hit(frame, "Great").ToString(),
                Hit(frame, "Good").ToString(),
                Hit(frame, "Ok").ToString(),
                Hit(frame, "Meh").ToString(),
                Hit(frame, "Miss").ToString(),
                Get(osrStats, HitResult.Perfect).ToString(),
                Get(osrStats, HitResult.Great).ToString(),
                Get(osrStats, HitResult.Good).ToString(),
                Get(osrStats, HitResult.Ok).ToString(),
                Get(osrStats, HitResult.Meh).ToString(),
                Get(osrStats, HitResult.Miss).ToString(),
                Number(prevPress),
                Number(prevPress - debug.ObjectTime),
                Number(nextPress),
                Number(nextPress - debug.ObjectTime),
                Number(prevRelease),
                Number(prevRelease - debug.ObjectTime),
                Number(nextRelease),
                Number(nextRelease - debug.ObjectTime),
                Csv(nearbyPairs)
            }));
        }
    }

    private static void PrintStats(string label, IReadOnlyDictionary<HitResult, int> stats)
    {
        Console.WriteLine(
            $"{label}: P={Get(stats, HitResult.Perfect)}, Gr={Get(stats, HitResult.Great)}, Gd={Get(stats, HitResult.Good)}, Ok={Get(stats, HitResult.Ok)}, Meh={Get(stats, HitResult.Meh)}, Miss={Get(stats, HitResult.Miss)}");
    }

    private static void PrintStringStats(string label, IReadOnlyDictionary<string, int> stats)
    {
        Console.WriteLine(
            $"  {label}: P={Get(stats, "Perfect")}, Gr={Get(stats, "Great")}, Gd={Get(stats, "Good")}, Ok={Get(stats, "Ok")}, Meh={Get(stats, "Meh")}, Miss={Get(stats, "Miss")}");
    }

    private static int Get(IReadOnlyDictionary<HitResult, int> stats, HitResult result)
    {
        return stats.TryGetValue(result, out var value) ? value : 0;
    }

    private static int Get(IReadOnlyDictionary<string, int> stats, string result)
    {
        return stats.TryGetValue(result, out var value) ? value : 0;
    }

    private static int Hit(ReplayTimelineFrame frame, string result)
    {
        return frame.Hits.TryGetValue(result, out var value) ? value : 0;
    }

    private static string Number(double? value)
    {
        return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? value.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string Number(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value)
            ? value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string Csv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static double ReadOverallDifficulty(string beatmapPath)
    {
        foreach (var line in File.ReadLines(beatmapPath))
        {
            if (line.StartsWith("OverallDifficulty:", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(line.Split(':', 2)[1], out var od))
                return od;
        }

        return 5;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static double MedianAbs(double[] values) => PercentileAbs(values, 0.5);

    private static double PercentileAbs(double[] values, double percentile)
    {
        if (values.Length == 0)
            return 0;

        var ordered = values.Select(Math.Abs).Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private sealed record TailRecord(ReplayTimelineFrame Frame, HitResult NaturalResult);

    private sealed record Windows(double Perfect, double Great, double Good, double Ok, double Meh, double Miss)
    {
        public static Windows ForDifficulty(double overallDifficulty, double speedMultiplier)
        {
            var multiplier = speedMultiplier > 0 ? speedMultiplier : 1.0;
            return new Windows(
                Perfect: Window(overallDifficulty, 22.4, 19.4, 13.9, multiplier),
                Great: Window(overallDifficulty, 64, 49, 34, multiplier),
                Good: Window(overallDifficulty, 97, 82, 67, multiplier),
                Ok: Window(overallDifficulty, 127, 112, 97, multiplier),
                Meh: Window(overallDifficulty, 151, 136, 121, multiplier),
                Miss: Window(overallDifficulty, 188, 173, 158, multiplier));
        }

        public HitResult ResultForTail(double offset)
        {
            var absolute = Math.Abs(offset);

            if (absolute <= Perfect * 1.5) return HitResult.Perfect;
            if (absolute <= Great * 1.5) return HitResult.Great;
            if (absolute <= Good * 1.5) return HitResult.Good;
            if (absolute <= Ok * 1.5) return HitResult.Ok;
            if (absolute <= Meh * 1.5) return HitResult.Meh;
            return HitResult.Miss;
        }

        private static double Window(double difficulty, double easy, double normal, double hard, double multiplier)
        {
            var value = difficulty > 5
                ? normal + (hard - normal) * (difficulty - 5) / 5
                : normal + (normal - easy) * (5 - difficulty) / 5;

            return value / multiplier;
        }
    }
}
