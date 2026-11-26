using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LazerReplayCompare;

// The replay list's own column: the chosen plugin's value for every listed replay, worked out in the background.
public sealed partial class MainWindow
{
    private readonly ReplayPlugins plugins = new();
    private readonly PluginScores pluginScores = new();
    private CancellationTokenSource? pluginWork;

    // how long a map's replay list must stay before its replays are analysed for the column
    private static readonly TimeSpan plugin_settle = TimeSpan.FromMilliseconds(700);

    /// <summary>Reads the plugins folder and follows it: a saved script shows its new values at once.</summary>
    private void startPlugins()
    {
        plugins.Start();
        plugins.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (closing)
                return;

            fillListColumns();
            updateListColumn();
        });
        fillListColumns();
        applyListColumn();
    }

    private void fillListColumns()
        => viewModel.SetListColumns(plugins.All.Select(plugin => new OverlayOption(plugin.Key, plugin.Name)), settingsService.Current.ListColumn);

    /// <summary>
    /// Fills the column for the listed replays: kept values at once, the others as their replays are analysed (one at
    /// a time, and not while a play is going on).
    /// </summary>
    private void updateListColumn()
    {
        pluginWork?.Cancel();
        pluginWork = null;
        applyListColumn();

        var plugin = plugins.Find(viewModel.ListColumnKey);
        viewModel.ListColumnError = plugin?.Error ?? string.Empty;
        if (plugin == null)
        {
            foreach (var row in viewModel.ReplayRows)
                row.SetCustom(null, string.Empty);
            return;
        }

        var beatmapPath = BuildBeatmapPath(currentSnapshot);
        bool canAnalyse = plugin.Usable && !string.IsNullOrWhiteSpace(beatmapPath) && File.Exists(beatmapPath);
        var missing = new List<ReplayRow>();
        foreach (var row in viewModel.ReplayRows)
        {
            if (!row.HasReplay || !canAnalyse)
            {
                row.SetCustom(null, "—");
                continue;
            }

            var kept = pluginScores.Get(plugin, PluginScores.ReplayKey(row.Entry.FilePath, beatmapPath), out bool known);
            if (known)
                row.SetCustom(kept?.Value, kept?.Text ?? "—");
            else
            {
                row.SetCustom(null, "…");
                missing.Add(row);
            }
        }

        if (missing.Count == 0)
        {
            resortByListColumn();
            return;
        }

        var cancellation = pluginWork = new CancellationTokenSource();
        var token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // browsing song select changes the list on every map: its replays are analysed once the map stays
                await Task.Delay(plugin_settle, token);
                foreach (var row in missing)
                {
                    while (isPlaying(Volatile.Read(ref liveSnapshot)))
                        await Task.Delay(1000, token);
                    token.ThrowIfCancellationRequested();

                    PluginValue? value = null;
                    try
                    {
                        value = pluginScores.Compute(plugin, row.Entry, beatmapPath);
                    }
                    catch (Exception ex)
                    {
                        // a replay that can't be analysed has no value (asked again the next time it is listed)
                        InternalLogger.Log(ex);
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                            row.SetCustom(value?.Value, value?.Text ?? "—");
                    });
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    viewModel.ListColumnError = plugin.Error ?? string.Empty;
                    resortByListColumn();
                });
            }
            catch (OperationCanceledException)
            {
                // another list, column or script meanwhile
            }
        });
    }

    /// <summary>The list sorted by the column follows its values as they come in.</summary>
    private void resortByListColumn()
    {
        if (viewModel.SortKey != ReplaySortKey.Custom)
            return;

        var sorted = Sort(viewModel.ReplayRows).ToList();
        if (sorted.SequenceEqual(viewModel.ReplayRows))
            return;

        fillingReplayList = true;
        viewModel.ReplayRows.Clear();
        foreach (var row in sorted)
            viewModel.ReplayRows.Add(row);
        fillingReplayList = false;
    }

    /// <summary>Gives the column its room in the header and the rows (none when no plugin is chosen).</summary>
    private void applyListColumn()
    {
        applyReplayColumns(replayHeader);
        foreach (var container in replayList.GetRealizedContainers())
            applyReplayColumns(container);
    }

    private void OpenPluginsFolder(object? sender, RoutedEventArgs e) => OpenInExplorer($"\"{ReplayPlugins.Folder}\"");
}
