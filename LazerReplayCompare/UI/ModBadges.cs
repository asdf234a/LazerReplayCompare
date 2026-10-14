using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LazerReplayCompare;

/// <summary>
/// A replay's mods drawn the way osu!lazer draws them: a hexagon in the mod type's colour with the mod's icon, and
/// the extra information (rate, OD, …) on a tab behind it. Unknown mods fall back to their acronym.
/// </summary>
public sealed class ModBadges : Control
{
    public static readonly StyledProperty<IReadOnlyList<ReplayMod>?> ModsProperty =
        AvaloniaProperty.Register<ModBadges, IReadOnlyList<ReplayMod>?>(nameof(Mods));

    public static readonly StyledProperty<double> BadgeHeightProperty =
        AvaloniaProperty.Register<ModBadges, double>(nameof(BadgeHeight), 22);

    // Proportions of osu!lazer's mod-icon (135×100) and mod-icon-extender (194×100) textures.
    private const double hexagon_aspect = 1.35;
    private const double extender_aspect = 1.94;
    // Badges overlap like osu!lazer's mod display; later ones sit on top of the previous badge's edge.
    private const double overlap = 0.3;

    private static readonly Dictionary<string, Bitmap?> textures = new();
    // osu!lazer's UI font is Torus, which can't be bundled (it needs a commercial licence), so it is used when
    // installed and Quicksand (OFL, similar rounded geometric shapes) stands in otherwise.
    private static readonly Typeface typeface = new(new FontFamily("Torus, avares://LazerReplayCompare/Assets/Fonts#Quicksand"), FontStyle.Normal, FontWeight.Bold);

    static ModBadges()
    {
        AffectsMeasure<ModBadges>(ModsProperty, BadgeHeightProperty);
        AffectsRender<ModBadges>(ModsProperty, BadgeHeightProperty);
    }

    public IReadOnlyList<ReplayMod>? Mods
    {
        get => GetValue(ModsProperty);
        set => SetValue(ModsProperty, value);
    }

    public double BadgeHeight
    {
        get => GetValue(BadgeHeightProperty);
        set => SetValue(BadgeHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var mods = Mods ?? Array.Empty<ReplayMod>();
        var width = mods.Sum(mod => layout(mod).Width) - Math.Max(0, mods.Count - 1) * overlap * BadgeHeight;
        return new Size(width, mods.Count == 0 ? 0 : BadgeHeight);
    }

    public override void Render(DrawingContext context)
    {
        var height = BadgeHeight;
        var top = Math.Max(0, (Bounds.Height - height) / 2);
        var x = 0.0;

        foreach (var mod in Mods ?? Array.Empty<ReplayMod>())
        {
            var badge = layout(mod);
            var colour = typeColour(mod.Type);
            var hexagon = new Rect(x, top, height * hexagon_aspect, height);

            if (badge.Text is { } info)
            {
                // Extended information on a dark tab in the mod's colour, behind the hexagon (as in osu!lazer).
                var tab = new Rect(x + hexagon.Width / 2, top, badge.Width - hexagon.Width / 2, height);
                fill(context, "icon-extender", tab, darken(colour, 2.8));
                var textX = hexagon.Right + (tab.Right - height * 0.4 - hexagon.Right - info.Width) / 2;
                context.DrawText(withColour(info, colour), new Point(textX, top + (height - info.Height) / 2));
            }

            fill(context, "icon", hexagon, colour);

            // The icon is drawn almost black, tinted towards the background colour.
            var iconColour = Color.FromRgb((byte)(colour.R * 0.1), (byte)(colour.G * 0.1), (byte)(colour.B * 0.1));
            var icon = texture(iconName(mod));
            if (icon != null)
            {
                var iconHeight = height * 0.62;
                var iconWidth = iconHeight * icon.Size.Width / icon.Size.Height;
                fill(context, icon, new Rect(hexagon.Center.X - iconWidth / 2, hexagon.Center.Y - iconHeight / 2, iconWidth, iconHeight), iconColour);
            }
            else
            {
                var acronym = text(mod.Acronym, height * 0.42, iconColour);
                context.DrawText(acronym, new Point(hexagon.Center.X - acronym.Width / 2, hexagon.Center.Y - acronym.Height / 2));
            }

            x += badge.Width - overlap * height;
        }
    }

    private (double Width, FormattedText? Text) layout(ReplayMod mod)
    {
        var height = BadgeHeight;
        var hexagonWidth = height * hexagon_aspect;
        var info = extendedInfo(mod);
        if (string.IsNullOrEmpty(info))
            return (hexagonWidth, null);

        var formatted = text(info, height * 0.46, Colors.White);
        var width = Math.Max(hexagonWidth / 2 + height * extender_aspect, hexagonWidth + formatted.Width + height * 0.7);
        return (width, formatted);
    }

    /// <summary>osu!lazer's own text when known; otherwise the rate for speed mods (the most important case).</summary>
    private static string extendedInfo(ReplayMod mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.ExtendedInfo))
            return mod.ExtendedInfo;

        if (!ModUtility.IsSpeedChangeMod(mod.Acronym))
            return string.Empty;

        var rate = ModUtility.GetPlaybackRate(new[] { mod });
        return Math.Abs(rate - ModUtility.DefaultRateForMod(mod.Acronym)) < 0.0001
            ? string.Empty
            : rate.ToString("0.00", CultureInfo.InvariantCulture) + "x";
    }

    // "Double Time" -> "mod-double-time", the texture osu!lazer uses for the mod.
    private static string iconName(ReplayMod mod)
        => string.IsNullOrWhiteSpace(mod.Name) ? string.Empty : "mod-" + Regex.Replace(mod.Name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-");

    // osu!lazer's OsuColour.ForModType.
    private static Color typeColour(string type) => type switch
    {
        "DifficultyReduction" => Color.FromRgb(178, 255, 102),
        "DifficultyIncrease" => Color.FromRgb(255, 102, 102),
        "Conversion" => Color.FromRgb(140, 102, 255),
        "Automation" => Color.FromRgb(102, 204, 255),
        "Fun" => Color.FromRgb(255, 102, 171),
        "System" => Color.FromRgb(255, 204, 34),
        _ => Color.FromRgb(170, 176, 190),
    };

    // osu!framework's Colour4.Darken.
    private static Color darken(Color colour, double amount)
        => Color.FromRgb((byte)(colour.R / (1 + amount)), (byte)(colour.G / (1 + amount)), (byte)(colour.B / (1 + amount)));

    private static FormattedText text(string value, double size, Color colour)
        => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(colour));

    private static FormattedText withColour(FormattedText text, Color colour)
    {
        text.SetForegroundBrush(new SolidColorBrush(colour));
        return text;
    }

    private static void fill(DrawingContext context, string textureName, Rect rect, Color colour)
    {
        if (texture("mod-" + textureName) is { } shape)
            fill(context, shape, rect, colour);
    }

    // Draws the texture's shape (its alpha) in a solid colour.
    private static void fill(DrawingContext context, Bitmap shape, Rect rect, Color colour)
    {
        using (context.PushOpacityMask(new ImageBrush(shape) { Stretch = Stretch.Fill }, rect))
            context.FillRectangle(new SolidColorBrush(colour), rect);
    }

    private static Bitmap? texture(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (textures.TryGetValue(name, out var cached))
            return cached;

        Bitmap? bitmap = null;
        try
        {
            var uri = new Uri($"avares://LazerReplayCompare/Assets/Mods/{name}.png");
            if (AssetLoader.Exists(uri))
                bitmap = new Bitmap(AssetLoader.Open(uri));
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }

        textures[name] = bitmap;
        return bitmap;
    }
}
