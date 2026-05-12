namespace LazerReplayCompare;

public sealed class ReplaySelectionService
{
    public string SelectedReplayPath { get; private set; } = string.Empty;

    public bool IsSelected(ReplayEntry replay)
    {
        return string.Equals(replay.FilePath, SelectedReplayPath, StringComparison.OrdinalIgnoreCase);
    }

    public void Select(ReplayEntry replay)
    {
        SelectedReplayPath = replay.FilePath;
    }

    public void Clear()
    {
        SelectedReplayPath = string.Empty;
    }

    public void ClearIfMissing(IEnumerable<ReplayEntry> replays)
    {
        if (!replays.Any(IsSelected))
            Clear();
    }

    public ReplayEntry? GetSelected(IEnumerable<ReplayEntry> replays)
    {
        return string.IsNullOrWhiteSpace(SelectedReplayPath)
            ? null
            : replays.FirstOrDefault(IsSelected);
    }
}
