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

    private void ShowTiming(object? sender, RoutedEventArgs e) => scrollTo("TimingSection");

    private void ShowSong(object? sender, RoutedEventArgs e) => scrollTo("SongSection");

    private void ShowLongNotes(object? sender, RoutedEventArgs e) => scrollTo("LongNotesSection");

    /// <summary>Scrolls so the section starts at the top of the page.</summary>
    private void scrollTo(string name)
    {
        if (this.FindControl<Control>(name) is { } section && scroll.Content is Visual content && section.TranslatePoint(default, content) is { } at)
            scroll.Offset = new Vector(0, Math.Max(0, at.Y - 12));
    }
}
