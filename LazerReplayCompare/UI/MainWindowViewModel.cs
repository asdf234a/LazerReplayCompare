using System.Collections.ObjectModel;

namespace LazerReplayCompare;

public sealed class MainWindowViewModel
{
    public ObservableCollection<ReplayRow> ReplayRows { get; } = new();
}
