using System.Globalization;
using System.Text;
using RawScoreBatchAnalyzer.Analysis;

namespace RawScoreBatchAnalyzer.Reports;

public sealed class CsvReportWriter : IDisposable
{
    private readonly object sync = new();
    private readonly StreamWriter writer;

    public CsvReportWriter(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(',', Header));
    }

    private static readonly string[] Header =
    {
        "ReplayFile",
        "BeatmapHash",
        "BeatmapPath",
        "Player",
        "Mods",
        "Rate",
        "Ruleset",
        "OsrScore",
        "RawScore",
        "ScoreDelta",
        "ScoreDeltaRatio",
        "CommitRawScore",
        "CommitScoreDelta",
        "CommitScoreDeltaRatio",
        "OsrAccuracy",
        "RawAccuracy",
        "AccuracyDelta",
        "CommitRawAccuracy",
        "CommitAccuracyDelta",
        "OsrPerfect",
        "RawPerfect",
        "OsrGreat",
        "RawGreat",
        "OsrGood",
        "RawGood",
        "OsrOk",
        "RawOk",
        "OsrMeh",
        "RawMeh",
        "OsrMiss",
        "RawMiss",
        "MissDelta",
        "OsrMaxCombo",
        "RawMaxCombo",
        "MaxComboDelta",
        "CommitRawMaxCombo",
        "CommitMaxComboDelta",
        "NoteCount",
        "Status",
        "Error",
    };

    public void Write(ReplayAnalysisResult result)
    {
        var values = new object?[]
        {
            result.ReplayFile,
            result.BeatmapHash,
            result.BeatmapPath,
            result.Player,
            result.Mods,
            result.Rate,
            result.Ruleset,
            result.OsrScore,
            result.RawScore,
            result.ScoreDelta,
            result.ScoreDeltaRatio,
            result.CommitRawScore,
            result.CommitScoreDelta,
            result.CommitScoreDeltaRatio,
            result.OsrAccuracy,
            result.RawAccuracy,
            result.AccuracyDelta,
            result.CommitRawAccuracy,
            result.CommitAccuracyDelta,
            result.OsrPerfect,
            result.RawPerfect,
            result.OsrGreat,
            result.RawGreat,
            result.OsrGood,
            result.RawGood,
            result.OsrOk,
            result.RawOk,
            result.OsrMeh,
            result.RawMeh,
            result.OsrMiss,
            result.RawMiss,
            result.MissDelta,
            result.OsrMaxCombo,
            result.RawMaxCombo,
            result.MaxComboDelta,
            result.CommitRawMaxCombo,
            result.CommitMaxComboDelta,
            result.NoteCount,
            result.Status,
            result.Error,
        };

        lock (sync)
            writer.WriteLine(string.Join(',', values.Select(Format)));
    }

    private static string Format(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            double d => d.ToString("G17", CultureInfo.InvariantCulture),
            float f => f.ToString("G9", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r'))
            return text;

        return '"' + text.Replace("\"", "\"\"") + '"';
    }

    public void Dispose()
    {
        lock (sync)
            writer.Dispose();
    }
}
