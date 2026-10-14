using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace LazerReplayCompare;

public static class ThemeService
{
    /// <summary>Named colours used by the windows' styles: (dark, light).</summary>
    private static readonly Dictionary<string, (string Dark, string Light)> palette = new()
    {
        ["PageBrush"] = ("#0E1014", "#F4F5F7"),
        ["HeaderBrush"] = ("#12151B", "#FFFFFF"),
        ["HeaderBorderBrush"] = ("#222731", "#E1E4EA"),
        ["SurfaceBrush"] = ("#14171D", "#FFFFFF"),
        ["SurfaceBorderBrush"] = ("#222833", "#DEE2E8"),
        ["RowDividerBrush"] = ("#1F242E", "#ECEEF2"),
        ["RowHoverBrush"] = ("#1A1E26", "#F2F4F7"),
        ["InputBrush"] = ("#161A21", "#FFFFFF"),
        ["InputBorderBrush"] = ("#323948", "#C9CFD8"),
        ["TextBrush"] = ("#E8EAEE", "#14171D"),
        ["TextSecondaryBrush"] = ("#B8BFCC", "#4A5261"),
        ["TextMutedBrush"] = ("#9AA3B2", "#5F6776"),
        ["ButtonBrush"] = ("#1E2330", "#EEF0F4"),
        ["ButtonHoverBrush"] = ("#272D3B", "#E3E6EC"),
        ["ButtonBorderBrush"] = ("#2E3441", "#D3D8E0"),
        ["PrimaryButtonBrush"] = ("#E8EAEE", "#14171D"),
        ["PrimaryButtonTextBrush"] = ("#0E1014", "#FFFFFF"),
        ["ChipBrush"] = ("#262131", "#EEF0F4"),
        ["ChipTextBrush"] = ("#D6DBE4", "#3A4250"),
        ["OptionBrush"] = ("#1A1E26", "#F7F8FA"),
        ["OptionBorderBrush"] = ("#2A303C", "#DEE2E8"),
        ["OptionSelectedBrush"] = ("#1F232D", "#FFFFFF"),
        ["OptionSelectedBorderBrush"] = ("#C9D1DE", "#3A4250"),

        // The comparison target is the only place the accent colour appears.
        ["TargetCardBrush"] = ("#1A1621", "#FFF6FA"),
        ["TargetBorderBrush"] = ("#4A2A3E", "#F3C2D9"),
        ["TargetLabelBrush"] = ("#FF9CC8", "#B3326E"),
        ["TargetAccentBrush"] = ("#FF66AA", "#D63C82"),
        ["TargetRowBrush"] = ("#221A27", "#FDEEF5"),
        ["TargetBadgeBrush"] = ("#3A1C2C", "#FBD8E8"),
        ["TargetBadgeTextBrush"] = ("#FF9CC8", "#9A2A5D"),

        ["GoodBackgroundBrush"] = ("#14301F", "#E3F7EA"),
        ["GoodTextBrush"] = ("#8FEDB6", "#1B7A43"),
        ["WarningBackgroundBrush"] = ("#33270F", "#FFF3D6"),
        ["WarningTextBrush"] = ("#FBD87F", "#8A5A00"),
        ["InfoBackgroundBrush"] = ("#13233A", "#E3EEFF"),
        ["InfoTextBrush"] = ("#A9CCFF", "#1F5BB5"),
        ["ErrorBackgroundBrush"] = ("#3A1A1C", "#FDE7E7"),
        ["ErrorTextBrush"] = ("#FFC2C2", "#B42323"),

        ["ErrorBannerBrush"] = ("#2E1517", "#FDECEC"),
        ["ErrorBannerBorderBrush"] = ("#6B2A2F", "#F2B8B8"),
        ["ErrorBannerTextBrush"] = ("#FFC2C2", "#9B1C1C"),
        ["ErrorBannerSubtextBrush"] = ("#D9B3B3", "#7A3A3A"),
        ["WarningBannerBrush"] = ("#2B2312", "#FFF6E0"),
        ["WarningBannerBorderBrush"] = ("#6A5422", "#EBD08A"),
        ["WarningBannerTextBrush"] = ("#FBE2A8", "#7A5300"),
        ["WarningBannerSubtextBrush"] = ("#D8C79C", "#6B5A2E"),
        ["WarningButtonBrush"] = ("#F5C451", "#C98A00"),
        ["WarningButtonTextBrush"] = ("#1F1806", "#FFFFFF"),

        ["PositiveTextBrush"] = ("#6EE7A0", "#1B7A43"),
        ["NegativeTextBrush"] = ("#FF8A8A", "#C02626"),
        ["RankTextBrush"] = ("#F5C451", "#A86B00"),

        ["JudgementPerfectBrush"] = ("#E8EAEE", "#2B313C"),
        ["JudgementGreatBrush"] = ("#F5C451", "#D69A10"),
        ["JudgementGoodBrush"] = ("#6EE7A0", "#2FA866"),
        ["JudgementOkBrush"] = ("#60A5FA", "#2F7BE0"),
        ["JudgementMehBrush"] = ("#8B93A3", "#9AA3B2"),
        ["JudgementMissBrush"] = ("#FF8A8A", "#E04848"),
    };

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

        foreach (var (key, colors) in palette)
            target.Resources[key] = new SolidColorBrush(Color.Parse(isLight ? colors.Light : colors.Dark));

        // Beatmap background banner: fades towards the page colour and a page-coloured scrim behind the title, so the
        // text keeps its normal colour (and contrast) over any image in both themes.
        var page = Color.Parse(isLight ? palette["PageBrush"].Light : palette["PageBrush"].Dark);
        foreach (var alpha in new[] { 0, 10, 25, 35, 55, 62, 70, 80, 85, 92, 100 })
            target.Resources[$"BannerScrim{alpha}Color"] = Color.FromArgb((byte)Math.Round(alpha * 2.55), page.R, page.G, page.B);

        // Translucent versions of the surfaces for when the blurred background sits behind them.
        foreach (var (key, alpha) in new[] { ("TargetCardBrush", 0.62), ("SurfaceBrush", 0.6), ("TargetBorderBrush", 0.55), ("SurfaceBorderBrush", 0.5) })
        {
            var color = Color.Parse(isLight ? palette[key].Light : palette[key].Dark);
            target.Resources[key.Replace("Brush", "GlassBrush")] = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B));
        }

    }
}
