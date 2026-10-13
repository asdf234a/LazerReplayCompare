using LazerReplayCompare;
using LazerReplayVerifier;
using osu.Framework;
using osu.Framework.Platform;
using osu.Game.Rulesets.Scoring;

if (args.Length < 2)
{
    Console.WriteLine("Usage: LazerReplayVerifier <replay.osr> <beatmap.osu> [--no-headless]");
    return 2;
}

string replayPath = args[0], beatmapPath = args[1];
var kinds = new[] { HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss };
string stats(IReadOnlyDictionary<HitResult, int> s) => string.Join(" ", kinds.Select(k => $"{k}={s.GetValueOrDefault(k)}"));

osu.Game.Scoring.Score Decode()
{
    using var stream = File.OpenRead(replayPath);
    return new LazerReplayScoreDecoder(beatmapPath).Parse(stream);
}

var recorded = Decode().ScoreInfo;
Console.WriteLine($"recorded   score={recorded.TotalScore} acc={recorded.Accuracy:P4} maxCombo={recorded.MaxCombo}{(recorded.LegacyTotalScore != null ? " (stable replay, converted)" : "")}");
Console.WriteLine($"           {stats(recorded.Statistics)}");

foreach (var mode in new[] { CorrectionMode.Raw, CorrectionMode.Corrected })
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var timeline = new ReplayTimelineBuilder().Build(replayPath, beatmapPath, 0, mode);
    var last = timeline.Frames[^1];
    var hits = last.Hits.ToDictionary(p => Enum.Parse<HitResult>(p.Key), p => p.Value);
    Console.WriteLine($"port/{mode,-9} score={last.Score} acc={last.Accuracy:P4} maxCombo={timeline.Frames.Max(f => f.MaxCombo)}  [{sw.ElapsedMilliseconds}ms]");
    Console.WriteLine($"           {stats(hits)}");
}

if (args.Contains("--no-headless"))
    return 0;

var headlessSw = System.Diagnostics.Stopwatch.StartNew();
var game = new HeadlessReplayGame(Decode(), beatmapPath);
var hostName = "lrc-verifier-" + Guid.NewGuid().ToString("N");
using (var host = new HeadlessGameHost(hostName, new HostOptions(), realtime: false))
    host.Run(game);

// The headless host keeps its (throwaway) game storage under ./headless/<name>.
foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
{
    var storage = Path.Combine(root, "headless", hostName);
    try
    {
        if (Directory.Exists(storage))
            Directory.Delete(storage, recursive: true);
        var parent = Path.Combine(root, "headless");
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
    }
    catch (IOException)
    {
    }
}

if (game.Result is not { } headless)
{
    Console.WriteLine($"headless   failed: {game.Error?.Message ?? "no result"}");
    return 1;
}

Console.WriteLine($"headless   score={headless.TotalScore} acc={headless.Accuracy:P4} maxCombo={headless.MaxCombo}  [{headlessSw.Elapsed.TotalSeconds:F1}s]");
Console.WriteLine($"           {stats(headless.Statistics)}");
return 0;
