namespace LazerReplayCompare;

public record ReplayMod(
    string Acronym,
    Dictionary<string, string> Settings,
    string Display,
    string MatchKey);

public record ReplayEntry(
    string FilePath,
    string Player,
    long Score,
    List<ReplayMod> Mods,
    string ModsText,
    string ModsKey,
    double Accuracy,
    int MaxCombo,
    string Rank,
    Dictionary<string, int> Statistics,
    long Timestamp,
    string Date);
