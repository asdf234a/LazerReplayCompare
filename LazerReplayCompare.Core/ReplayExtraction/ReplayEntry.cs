namespace LazerReplayCompare;

/// <param name="Name">osu!lazer's name for the mod ("Double Time"), which also names its icon.</param>
/// <param name="Type">osu!lazer's ModType ("DifficultyIncrease", …), which picks the icon colour.</param>
/// <param name="ExtendedInfo">What osu!lazer shows next to the icon ("1.20x", "OD8"), if anything.</param>
public record ReplayMod(
    string Acronym,
    Dictionary<string, string> Settings,
    string Display,
    string MatchKey,
    string Name = "",
    string Type = "",
    string ExtendedInfo = "");

/// <param name="IsLegacy">Set in osu!stable (imported into osu!lazer): scored with ScoreV1, one judgement per hold note.</param>
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
    string Date,
    bool IsLegacy = false);
