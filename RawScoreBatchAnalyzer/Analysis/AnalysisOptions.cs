namespace RawScoreBatchAnalyzer.Analysis;

using LazerReplayCompare;

public sealed record AnalysisOptions(
    string RealmPath,
    string FilesRoot,
    string ReplayFolder,
    string OutputCsvPath,
    int MaxDegreeOfParallelism,
    bool Recursive);

public sealed record AnalysisProgress(
    int Total,
    int Processed,
    int Success,
    int Skipped,
    int Failed,
    double FilesPerSecond,
    string CurrentFile,
    string Message);

public sealed record AnalysisSummary(
    int Total,
    int Success,
    int Skipped,
    int Failed,
    TimeSpan Elapsed,
    string OutputCsvPath);
