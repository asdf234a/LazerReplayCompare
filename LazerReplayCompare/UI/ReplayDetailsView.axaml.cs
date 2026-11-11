using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace LazerReplayCompare;

/// <summary>A replay's details page (<see cref="ReplayDetailsViewModel"/>); the window handles its buttons.</summary>
public sealed partial class ReplayDetailsView : UserControl
{
    public event Action? BackRequested;
    public event Action<ReplayEntry>? ShowFileRequested;
    public event Action<ReplayEntry>? PinRequested;

    private readonly ScrollViewer scroll;
    private bool? narrow;

    public ReplayDetailsView()
    {
        AvaloniaXamlLoader.Load(this);
        scroll = this.FindControl<ScrollViewer>("Scroll")!;

        // A narrow page (small window or large zoom): side-by-side parts stack.
        this.FindControl<StackPanel>("Page")!.SizeChanged += (_, e) => setNarrow(e.NewSize.Width < ResponsiveLayout.NarrowWidth);
    }

    private void setNarrow(bool value)
    {
        if (narrow == value)
            return;

        narrow = value;
        // back button, then the jump buttons under it
        ResponsiveLayout.Apply(this.FindControl<Grid>("NavRow")!, value, "*", (0, 0), (1, 0));
        ResponsiveLayout.Apply(this.FindControl<Grid>("SummaryRow")!, value);
        ResponsiveLayout.Apply(this.FindControl<Grid>("StatusRow")!, value);
        ResponsiveLayout.Apply(this.FindControl<Grid>("LanesHeader")!, value);
        ResponsiveLayout.Apply(this.FindControl<Grid>("TimingRow")!, value);
        ResponsiveLayout.Apply(this.FindControl<Grid>("SongHeader")!, value);
        ResponsiveLayout.Apply(this.FindControl<Grid>("LongNotesRow")!, value);
        ResponsiveLayout.Apply(this.FindControl<Grid>("ReleaseHeader")!, value);
    }

    private ReplayDetailsViewModel? details => DataContext as ReplayDetailsViewModel;

    /// <summary>Back at the top for the next replay.</summary>
    public void ScrollToTop() => scroll.Offset = default;

    private void Back(object? sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void ShowFile(object? sender, RoutedEventArgs e)
    {
        if (details is { } d)
            ShowFileRequested?.Invoke(d.Entry);
    }

    private void Pin(object? sender, RoutedEventArgs e)
    {
        if (details is { } d)
            PinRequested?.Invoke(d.Entry);
    }

    private void PickColumn(object? sender, RoutedEventArgs e)
    {
        if (details is { } d && sender is Button { DataContext: ColumnChip chip })
            d.HighlightColumn = chip.Column;
    }

    /// <summary>A lane of the Lanes section: highlights it in Over the song; again, shows every lane.</summary>
    private void PickLane(object? sender, RoutedEventArgs e)
    {
        if (details is { } d && sender is Button { DataContext: LaneColumn lane })
            d.HighlightColumn = d.HighlightColumn == lane.Column ? -1 : lane.Column;
    }

    private void MiddleLaneToLeft(object? sender, RoutedEventArgs e)
    {
        if (details is { } d)
            d.MiddleLaneLeft = true;
    }

    private void MiddleLaneToRight(object? sender, RoutedEventArgs e)
    {
        if (details is { } d)
            d.MiddleLaneLeft = false;
    }

    private void ShowLanes(object? sender, RoutedEventArgs e) => scrollTo("LanesSection");

    private void ShowTiming(object? sender, RoutedEventArgs e) => scrollTo("TimingSection");

    private void ShowSong(object? sender, RoutedEventArgs e) => scrollTo("SongSection");

    private void ShowLongNotes(object? sender, RoutedEventArgs e) => scrollTo("LongNotesSection");

    /// <summary>Scrolls so the section starts at the top of the page.</summary>
    private void scrollTo(string name)
    {
        if (this.FindControl<Control>(name) is { } section && scroll.Content is Visual content && section.TranslatePoint(default, content) is { } at)
            scroll.Offset = new Vector(0, Math.Max(0, at.Y - 12));
    }

    /// <summary>The page was opened: its sections arrive one after the other, each rising into place.</summary>
    public void PlayEntrance()
    {
        if (this.FindControl<StackPanel>("Page") is not { } page)
            return;

        // (once the replay's sections are laid out: which of them there are depends on the replay)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            int index = 0;
            foreach (var section in page.Children)
            {
                if (section.IsVisible)
                    Motion.Enter(section, 22, 1, 320, delay: 60 + 55 * Math.Min(index++, 6));
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
