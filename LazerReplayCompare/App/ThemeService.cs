using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace LazerReplayCompare;

public static class ThemeService
{
    public static void Apply(Window target, string theme)
    {
        var normalized = string.IsNullOrWhiteSpace(theme) ? "System" : theme;
        target.RequestedThemeVariant = normalized.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Dark
            : normalized.Equals("Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Default;

        var isLight = normalized.Equals("Light", StringComparison.OrdinalIgnoreCase);
        if (normalized.Equals("System", StringComparison.OrdinalIgnoreCase))
            isLight = target.ActualThemeVariant == ThemeVariant.Light;

        SetPalette(target, isLight);
    }

    private static void SetPalette(Window target, bool light)
    {
        var resources = target.Resources;
        resources["PageBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#F8FAFC") : Color.Parse("#050C19"));
        resources["CardBackgroundBrush"] = new SolidColorBrush(light ? Colors.White : Color.Parse("#0D192C"));
        resources["CardAltBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#F1F5F9") : Color.Parse("#111F34"));
        resources["CardBorderBrush"] = new SolidColorBrush(light ? Color.Parse("#CBD5E1") : Color.Parse("#29436F"));
        resources["InputBackgroundBrush"] = new SolidColorBrush(light ? Colors.White : Color.Parse("#050D1B"));
        resources["ButtonBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#E2E8F0") : Color.Parse("#16243B"));
        resources["BadgeBackgroundBrush"] = new SolidColorBrush(light ? Color.Parse("#EFF6FF") : Color.Parse("#12233A"));
        resources["RowHoverBrush"] = new SolidColorBrush(light ? Color.Parse("#EFF6FF") : Color.Parse("#12223A"));
        resources["RowSelectedBrush"] = new SolidColorBrush(light ? Color.Parse("#DBEAFE") : Color.Parse("#1E3A70"));
        resources["HeadingTextBrush"] = new SolidColorBrush(light ? Color.Parse("#0F172A") : Colors.White);
        resources["BodyTextBrush"] = new SolidColorBrush(light ? Color.Parse("#1E293B") : Color.Parse("#E8EEF8"));
        resources["MutedTextBrush"] = new SolidColorBrush(light ? Color.Parse("#64748B") : Color.Parse("#97A4BC"));
    }
}
