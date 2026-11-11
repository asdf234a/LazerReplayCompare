namespace LazerReplayCompare;

/// <summary>
/// One opponent of the pacemaker overlay. A "replay" rival has a replay file, so the overlay can ask /timeline for its
/// score at every note; an "online" rival is a leaderboard score with no replay here, so only its final result is known
/// and the overlay moves it at an even pace.
/// </summary>
/// <param name="Source">"replay" or "online".</param>
/// <param name="FilePath">The replay file (empty for an online rival).</param>
/// <param name="Accuracy">0 to 1.</param>
/// <param name="IsTarget">The replay the Main and Sub overlays compare against.</param>
/// <param name="IsUser">One of the user's own plays.</param>
public sealed record Rival(string Id, string Name, string Source, string FilePath, long Score, double Accuracy, int MaxCombo, string Rank, string ModsText,
    string Date, bool IsTarget, bool IsUser);

/// <summary>What /rivals answers with: the opponents for the beatmap being played.</summary>
/// <param name="Mode">Which opponents the user picked: "local", "online" or "both".</param>
/// <param name="Player">The user's name (the live play's row is labelled with it).</param>
/// <param name="Rivals">Best score first.</param>
public sealed record RivalSet(string BeatmapMd5, string Mode, string Player, IReadOnlyList<Rival> Rivals);
