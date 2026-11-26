using System.Collections.ObjectModel;

namespace LazerReplayCompare;

// The replay list's own column: a plugin's value for each replay (see ReplayPlugins).
public sealed partial class MainWindowViewModel
{
    private static readonly OverlayOption no_list_column = new(string.Empty, "Off");

    private OverlayOption? selectedListColumn;
    private bool fillingListColumns;
    private string listColumnError = string.Empty;

    /// <summary>Off, then every plugin in the plugins folder.</summary>
    public ObservableCollection<OverlayOption> ListColumnOptions { get; } = new() { no_list_column };

    public OverlayOption? SelectedListColumn
    {
        get => selectedListColumn ?? no_list_column;
        set
        {
            // (a picker briefly loses its selection while its list is rebuilt)
            if (value == null || fillingListColumns || !Set(ref selectedListColumn, value))
                return;

            listColumnChanged();
            SettingsChanged?.Invoke();
        }
    }

    /// <summary>The chosen plugin's key, or "" for no column.</summary>
    public string ListColumnKey => SelectedListColumn?.Key ?? string.Empty;

    public bool HasListColumn => ListColumnKey.Length > 0;

    /// <summary>The column's header: the plugin's name, with the sort arrow when the list is sorted by it.</summary>
    public string CustomHeader => HasListColumn ? header(SelectedListColumn!.Name, ReplaySortKey.Custom) : string.Empty;

    /// <summary>Why the chosen plugin gives no values (its script's error), or "".</summary>
    public string ListColumnError
    {
        get => listColumnError;
        set
        {
            if (Set(ref listColumnError, value))
                OnPropertyChanged(nameof(HasListColumnError));
        }
    }

    public bool HasListColumnError => ListColumnError.Length > 0;

    /// <summary>Fills the picker with the plugins and selects <paramref name="selectedKey"/> (Off when it is not among them).</summary>
    public void SetListColumns(IEnumerable<OverlayOption> plugins, string selectedKey)
    {
        fillingListColumns = true;
        ListColumnOptions.Clear();
        ListColumnOptions.Add(no_list_column);
        foreach (var plugin in plugins)
            ListColumnOptions.Add(plugin);

        selectedListColumn = ListColumnOptions.FirstOrDefault(option => option.Key == selectedKey) ?? no_list_column;
        fillingListColumns = false;

        OnPropertyChanged(nameof(SelectedListColumn));
        listColumnChanged();
    }

    private void listColumnChanged()
    {
        // a list sorted by a column that is gone goes back to the score
        if (!HasListColumn && SortKey == ReplaySortKey.Custom)
        {
            sortKey = ReplaySortKey.Score;
            sortDescending = true;
            OnPropertyChanged(nameof(SortKey));
            OnPropertyChanged(nameof(SortDescending));
            headersChanged();
        }

        OnPropertyChanged(nameof(ListColumnKey));
        OnPropertyChanged(nameof(HasListColumn));
        OnPropertyChanged(nameof(CustomHeader));
    }
}
