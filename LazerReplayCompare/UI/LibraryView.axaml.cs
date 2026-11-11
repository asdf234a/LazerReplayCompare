using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LazerReplayCompare;

/// <summary>The whole library: groups on the left, the selected group's maps on the right.</summary>
public sealed partial class LibraryView : UserControl
{
    // The map list's columns: number and dan, map, keys, stars, OD, BPM, length, a gap, best, plays, chevron.
    // A narrow list drops BPM and length; a very narrow window hides the groups (the "Group by" list stays reachable there).
    private const string map_columns = "132,*,48,62,48,58,64,28,126,56,30";
    private const string narrow_map_columns = "120,*,44,58,44,0,0,16,118,0,26";
    private static readonly int[] narrow_hidden_columns = [5, 6, 9];
    private const double narrow_list_width = 1000;
    private const double no_groups_width = 760;

    private readonly ListBox mapList;
    private readonly Grid mapHeader;
    private readonly Grid panes;
    private readonly Control groupsPane;
    private readonly TextBox searchBox;
    private bool narrow;
    private LibraryViewModel? watched;
    private string cascadeKey = string.Empty;
    private long cascadeUntil;

    // rows on screen, whose backgrounds are loaded (two at a time, so fast scrolling doesn't decode a hundred images)
    private readonly HashSet<MapRow> realized = new();
    private static readonly SemaphoreSlim decoding = new(2, 2);

    /// <summary>A map was chosen (clicked, or Enter on it).</summary>
    public event Action<MapRow>? MapChosen;

    /// <summary>The "Now playing" button: the map the game is on is wanted.</summary>
    public event Action? NowPlayingChosen;

    private void GoToNowPlaying(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => NowPlayingChosen?.Invoke();

    public LibraryView()
    {
        AvaloniaXamlLoader.Load(this);
        mapList = this.FindControl<ListBox>("MapList")!;
        mapHeader = this.FindControl<Grid>("MapHeader")!;
        panes = this.FindControl<Grid>("Panes")!;
        groupsPane = this.FindControl<Control>("GroupsPane")!;
        searchBox = this.FindControl<TextBox>("SearchBox")!;

        mapList.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not Control container)
                return;

            // another group or another search: its first rows arrive one after the other
            int index = mapList.IndexFromContainer(container);
            if (Environment.TickCount64 < cascadeUntil && index is >= 0 and < 18)
                Motion.Enter(container, 12, 1, 220, delay: index * 18);
            else if (container.Opacity < 1 && Environment.TickCount64 >= cascadeUntil)
            {
                container.Opacity = 1;
                container.RenderTransform = null;
            }

            Dispatcher.UIThread.Post(() =>
            {
                applyColumns(container);
                if (container.DataContext is MapRow row)
                    show(row);
            }, DispatcherPriority.Loaded);
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not LibraryViewModel model || model == watched)
                return;

            watched = model;
            model.Located += row => Dispatcher.UIThread.Post(() => mapList.ScrollIntoView(row), DispatcherPriority.Loaded);
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(LibraryViewModel.Rows))
                    return;

                // (the same list read again in the background is not another list)
                string key = $"{model.GroupTitle}|{model.Rows.Count}|{(model.Rows.Count > 0 ? model.Rows[0].Md5 : string.Empty)}";
                if (key != cascadeKey && IsEffectivelyVisible)
                    cascadeUntil = Environment.TickCount64 + 350;
                cascadeKey = key;
            };
        };
        mapList.ContainerClearing += (_, e) =>
        {
            if (e.Container is Control { DataContext: MapRow row })
                hide(row);
        };
        mapList.SizeChanged += (_, e) => setNarrow(e.NewSize.Width < narrow_list_width);
        SizeChanged += (_, e) =>
        {
            bool groups = e.NewSize.Width >= no_groups_width;
            groupsPane.IsVisible = groups;
            panes.ColumnDefinitions = new ColumnDefinitions(groups ? "264,*" : "0,*");
            panes.ColumnSpacing = groups ? 12 : 0;
        };
    }

    public void FocusSearch()
    {
        searchBox.Focus();
        searchBox.SelectAll();
    }

    /// <summary>Scrolls the list to a map (when it is in the shown group).</summary>
    public void ScrollTo(MapRow row) => mapList.ScrollIntoView(row);

    private void SortClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<LibrarySortKey>(tag, out var key) && DataContext is LibraryViewModel model)
            model.SortBy(key);
    }

    private void MapTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is StyledElement { DataContext: MapRow row })
            MapChosen?.Invoke(row);
    }

    private void MapListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && mapList.SelectedItem is MapRow row)
            MapChosen?.Invoke(row);
    }

    private void setNarrow(bool value)
    {
        if (value == narrow)
            return;

        narrow = value;
        applyColumns(mapHeader);
        foreach (var container in mapList.GetRealizedContainers())
            applyColumns(container);
    }

    private void applyColumns(Control control)
    {
        var grid = control as Grid ?? control.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Classes.Contains("mapCells"));
        if (grid == null)
            return;

        grid.ColumnDefinitions = new ColumnDefinitions(narrow ? narrow_map_columns : map_columns);
        foreach (var cell in grid.Children)
            cell.IsVisible = !narrow || !narrow_hidden_columns.Contains(Grid.GetColumn(cell));
    }

    // --- Row backgrounds ---

    private void show(MapRow row)
    {
        if (!realized.Add(row) || row.Background != null)
            return;

        var path = row.Map.BackgroundPath;
        if (string.IsNullOrWhiteSpace(path) || DataContext is LibraryViewModel { ShowRowBackgrounds: false })
            return;

        _ = Task.Run(async () =>
        {
            await decoding.WaitAsync();
            Bitmap? bitmap = null;
            try
            {
                // scrolled past meanwhile
                bool wanted = await Dispatcher.UIThread.InvokeAsync(() => realized.Contains(row) && row.Background == null);
                if (!wanted || !File.Exists(path))
                    return;

                await using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, 720, BitmapInterpolationMode.MediumQuality);
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
            }
            finally
            {
                decoding.Release();
            }

            if (bitmap == null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (realized.Contains(row) && row.Background == null)
                    row.Background = bitmap;
                else
                    bitmap.Dispose();
            });
        });
    }

    private void hide(MapRow row)
    {
        realized.Remove(row);
        if (row.Background is { } bitmap)
        {
            row.Background = null;
            bitmap.Dispose();
        }
    }

    private void ClearQuickFilters(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => (DataContext as LibraryViewModel)?.ClearQuickFilters();
}
