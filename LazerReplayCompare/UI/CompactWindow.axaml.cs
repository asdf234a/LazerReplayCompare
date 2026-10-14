using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace LazerReplayCompare;

/// <summary>Small always-on-top view of the comparison, for while you play.</summary>
public sealed partial class CompactWindow : Window
{
    private readonly Action? openFullWindow;

    public CompactWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CompactWindow(MainWindowViewModel viewModel, string theme, Action openFullWindow)
        : this()
    {
        DataContext = viewModel;
        this.openFullWindow = openFullWindow;
        ThemeService.Apply(this, theme);
    }

    private void OpenFullWindow(object? sender, RoutedEventArgs e)
    {
        openFullWindow?.Invoke();
        Close();
    }
}
