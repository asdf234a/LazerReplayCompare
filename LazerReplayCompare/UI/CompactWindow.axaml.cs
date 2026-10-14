using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace LazerReplayCompare;

/// <summary>Small always-on-top view of the comparison, for while you play.</summary>
public sealed partial class CompactWindow : Window
{
    private readonly Action? openFullWindow;
    private readonly Action? closeApp;

    public CompactWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CompactWindow(MainWindowViewModel viewModel, string theme, Action openFullWindow, Action closeApp)
        : this()
    {
        DataContext = viewModel;
        this.openFullWindow = openFullWindow;
        this.closeApp = closeApp;
        ThemeService.Apply(this, theme);
    }

    private void OpenFullWindow(object? sender, RoutedEventArgs e)
    {
        openFullWindow?.Invoke();
        Close();
    }

    private void CloseApp(object? sender, RoutedEventArgs e) => closeApp?.Invoke();

    /// <summary>No title bar here, so dragging any empty part of the window moves it.</summary>
    private void DragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
