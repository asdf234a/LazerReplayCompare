using System.Globalization;
using LazerReplayCompare;
using RawScoreBatchAnalyzer.Analysis;

var workspace = FindWorkspaceRoot();
if (args.Contains("--compare-models", StringComparer.OrdinalIgnoreCase))
{
    return await RiceModelComparison.RunAsync(args, workspace);
}

if (args.Contains("--dump-timeline", StringComparer.OrdinalIgnoreCase))
{
    return TimelineDump.Run(args);
}

var options = CliOptions.Parse(args, workspace);

Directory.CreateDirectory(Path.GetDirectoryName(options.OutputCsvPath)!);

Console.WriteLine($"Replay folder: {options.ReplayFolder}");
Console.WriteLine($"osu!lazer root: {options.OsuLazerRoot}");
Console.WriteLine($"Output CSV:    {options.OutputCsvPath}");
Console.WriteLine($"Parallelism:   {options.Parallelism}");

var analysisOptions = new AnalysisOptions(
    RealmPath: Path.Combine(options.OsuLazerRoot, "client.realm"),
    FilesRoot: Path.Combine(options.OsuLazerRoot, "files"),
    ReplayFolder: options.ReplayFolder,
    OutputCsvPath: options.OutputCsvPath,
    MaxDegreeOfParallelism: options.Parallelism,
    Recursive: options.Recursive);

var analyzer = new RawReplayAnalyzer();
var progress = new Progress<AnalysisProgress>(p =>
{
    if (p.Total == 0 || p.Processed == 0 || p.Processed == p.Total || p.Processed % 25 == 0)
        Console.WriteLine($"{p.Processed}/{p.Total} ok={p.Success} skipped={p.Skipped} failed={p.Failed} {p.FilesPerSecond:F1}/s");
});

var summary = await analyzer.AnalyzeAsync(analysisOptions, progress, CancellationToken.None);
Console.WriteLine($"Done. Success={summary.Success}, Skipped={summary.Skipped}, Failed={summary.Failed}, Elapsed={summary.Elapsed}");
Console.WriteLine($"CSV: {summary.OutputCsvPath}");

PrintReportSummary(summary.OutputCsvPath, "current");

if (!string.IsNullOrWhiteSpace(options.CompareCsvPath))
    PrintComparison(options.CompareCsvPath, summary.OutputCsvPath);

return summary.Failed == 0 ? 0 : 1;

static string FindWorkspaceRoot()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current != null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "LazerReplayCompare")) &&
            Directory.Exists(Path.Combine(current.FullName, "RawScoreBatchAnalyzer")))
            return current.FullName;

        current = current.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static void PrintReportSummary(string csvPath, string label)
{
    var rows = ReadRows(csvPath).Where(row => Get(row, "Status") == "ok").ToArray();
    if (rows.Length == 0)
    {
        Console.WriteLine($"[{label}] no ok rows");
        return;
    }

    var absScores = rows.Select(row => Math.Abs(Number(row, "ScoreDelta"))).ToArray();
    var absAccuracy = rows.Select(row => Math.Abs(Number(row, "AccuracyDelta"))).ToArray();
    var absMiss = rows.Select(row => Math.Abs(Number(row, "MissDelta"))).ToArray();
    var absCombo = rows.Select(row => Math.Abs(Number(row, "MaxComboDelta"))).ToArray();

    Console.WriteLine($"[{label}] rows={rows.Length}");
    Console.WriteLine($"[{label}] meanAbsScore={absScores.Average():F2}, medianAbsScore={Median(absScores):F2}, p90AbsScore={Percentile(absScores, 0.90):F2}, scoreBias={rows.Average(row => Number(row, "ScoreDelta")):F2}");
    Console.WriteLine($"[{label}] meanAbsAccuracy={absAccuracy.Average():F6}, accuracyBias={rows.Average(row => Number(row, "AccuracyDelta")):F6}");
    Console.WriteLine($"[{label}] meanAbsMiss={absMiss.Average():F3}, missBias={rows.Average(row => Number(row, "MissDelta")):F3}, totalAbsCombo={absCombo.Sum():F0}");

    if (rows[0].ContainsKey("CommitScoreDelta"))
    {
        var commitAbsScores = rows.Select(row => Math.Abs(Number(row, "CommitScoreDelta"))).ToArray();
        var commitAbsAccuracy = rows.Select(row => Math.Abs(Number(row, "CommitAccuracyDelta"))).ToArray();
        var commitAbsCombo = rows.Select(row => Math.Abs(Number(row, "CommitMaxComboDelta"))).ToArray();
        var scoreImproved = rows.Count(row => Math.Abs(Number(row, "CommitScoreDelta")) < Math.Abs(Number(row, "ScoreDelta")));
        var scoreWorsened = rows.Count(row => Math.Abs(Number(row, "CommitScoreDelta")) > Math.Abs(Number(row, "ScoreDelta")));
        var scoreSame = rows.Length - scoreImproved - scoreWorsened;

        Console.WriteLine($"[{label}/commit] meanAbsScore={commitAbsScores.Average():F2}, medianAbsScore={Median(commitAbsScores):F2}, p90AbsScore={Percentile(commitAbsScores, 0.90):F2}, scoreBias={rows.Average(row => Number(row, "CommitScoreDelta")):F2}");
        Console.WriteLine($"[{label}/commit] meanAbsAccuracy={commitAbsAccuracy.Average():F6}, accuracyBias={rows.Average(row => Number(row, "CommitAccuracyDelta")):F6}, totalAbsCombo={commitAbsCombo.Sum():F0}");
        Console.WriteLine($"[{label}/commit-vs-time] ScoreDelta improved={scoreImproved}, worsened={scoreWorsened}, same={scoreSame}, totalAbsTime={absScores.Sum():F2}, totalAbsCommit={commitAbsScores.Sum():F2}");
    }
}

static void PrintComparison(string beforeCsv, string afterCsv)
{
    if (!File.Exists(beforeCsv))
    {
        Console.WriteLine($"[compare] missing before CSV: {beforeCsv}");
        return;
    }

    var before = ReadRows(beforeCsv)
        .Where(row => Get(row, "Status") == "ok")
        .ToDictionary(row => Path.GetFileName(Get(row, "ReplayFile")).ToLowerInvariant());
    var after = ReadRows(afterCsv)
        .Where(row => Get(row, "Status") == "ok")
        .ToDictionary(row => Path.GetFileName(Get(row, "ReplayFile")).ToLowerInvariant());

    var common = before.Keys.Intersect(after.Keys).ToArray();
    Console.WriteLine($"[compare] common={common.Length}");
    foreach (var column in new[] { "ScoreDelta", "AccuracyDelta", "MissDelta", "MaxComboDelta" })
    {
        var improved = 0;
        var worsened = 0;
        var same = 0;
        var totalBefore = 0d;
        var totalAfter = 0d;

        foreach (var key in common)
        {
            var beforeAbs = Math.Abs(Number(before[key], column));
            var afterAbs = Math.Abs(Number(after[key], column));
            totalBefore += beforeAbs;
            totalAfter += afterAbs;

            if (afterAbs < beforeAbs) improved++;
            else if (afterAbs > beforeAbs) worsened++;
            else same++;
        }

        Console.WriteLine($"[compare] {column}: improved={improved}, worsened={worsened}, same={same}, totalAbsBefore={totalBefore:F6}, totalAbsAfter={totalAfter:F6}");
    }
}

static List<Dictionary<string, string>> ReadRows(string path)
{
    using var reader = new StreamReader(path);
    var headerLine = reader.ReadLine();
    if (headerLine == null)
        return new List<Dictionary<string, string>>();

    var headers = SplitCsvLine(headerLine).ToArray();
    var rows = new List<Dictionary<string, string>>();
    while (reader.ReadLine() is { } line)
    {
        var values = SplitCsvLine(line).ToArray();
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Length; i++)
            row[headers[i]] = i < values.Length ? values[i] : string.Empty;
        rows.Add(row);
    }

    return rows;
}

static IEnumerable<string> SplitCsvLine(string line)
{
    var current = new List<char>();
    var quoted = false;
    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (ch == '"')
        {
            if (quoted && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Add('"');
                i++;
            }
            else
            {
                quoted = !quoted;
            }
        }
        else if (ch == ',' && !quoted)
        {
            yield return new string(current.ToArray());
            current.Clear();
        }
        else
        {
            current.Add(ch);
        }
    }

    yield return new string(current.ToArray());
}

static string Get(Dictionary<string, string> row, string key)
{
    return row.TryGetValue(key, out var value) ? value : string.Empty;
}

static double Number(Dictionary<string, string> row, string key)
{
    return double.TryParse(Get(row, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        ? value
        : 0;
}

static double Median(double[] values)
{
    return Percentile(values, 0.5);
}

static double Percentile(double[] values, double percentile)
{
    if (values.Length == 0)
        return 0;

    var ordered = values.Order().ToArray();
    var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
    return ordered[index];
}

internal sealed record CliOptions(
    string ReplayFolder,
    string OutputCsvPath,
    string OsuLazerRoot,
    int Parallelism,
    bool Recursive,
    string? CompareCsvPath)
{
    public static CliOptions Parse(string[] args, string workspace)
    {
        var replayFolder = Path.Combine(workspace, "analysis-data", "samples", "raw-score-sample-300");
        var outputCsv = Path.Combine(workspace, "analysis-data", "reports", $"raw-score-sample-300-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var osuRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "osulazer");
        var parallelism = Math.Max(1, Environment.ProcessorCount / 2);
        var recursive = false;
        string? compareCsv = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            switch (arg)
            {
                case "--replays":
                    replayFolder = Path.GetFullPath(value);
                    i++;
                    break;
                case "--out":
                    outputCsv = Path.GetFullPath(value);
                    i++;
                    break;
                case "--osu":
                    osuRoot = Path.GetFullPath(value);
                    i++;
                    break;
                case "--parallel":
                    if (int.TryParse(value, out var parsed))
                        parallelism = Math.Max(1, parsed);
                    i++;
                    break;
                case "--recursive":
                    recursive = true;
                    break;
                case "--compare":
                    compareCsv = Path.GetFullPath(value);
                    i++;
                    break;
            }
        }

        return new CliOptions(replayFolder, outputCsv, osuRoot, parallelism, recursive, compareCsv);
    }
}
