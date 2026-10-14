using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LazerReplayCompare;

/// <summary>
/// A replay's mods drawn the way osu!lazer's ModIcon draws them (osu.Game/Rulesets/UI/ModIcon.cs), laid out like
/// song select's leaderboard. Unknown mods fall back to their acronym.
/// </summary>
public sealed class ModBadges : Control
{
    public static readonly StyledProperty<IReadOnlyList<ReplayMod>?> ModsProperty =
        AvaloniaProperty.Register<ModBadges, IReadOnlyList<ReplayMod>?>(nameof(Mods));

    public static readonly StyledProperty<double> BadgeHeightProperty =
        AvaloniaProperty.Register<ModBadges, double>(nameof(BadgeHeight), 22);

    // ModIcon's geometry, in its own units: an 80×80 box holding the hexagon (mod-icon, 135×100, fitted), the icon
    // (131/135 × 92/135 of the box) and the settings marker (20 across, centred at 64,14); and from x = 58 a 116×80 box
    // with the extender (mod-icon-extender, 194×100, fitted) and its text (Torus Bold 34, 6 right of centre).
    private const double box = 80;
    private const double hexagon_height = box * 100 / 135;
    private const double extended_x = box - 22;
    private const double extended_width = 116;
    private const double extended_text_size = 34;

    // BeatmapLeaderboardScore: icons at 0.3 scale with -10 spacing, i.e. -10 / 0.3 in icon units; later ones on top.
    private const double spacing = -10 / 0.3;

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

    /// <summary>Height of the hexagon itself, in pixels.</summary>
    public double BadgeHeight
    {
        get => GetValue(BadgeHeightProperty);
        set => SetValue(BadgeHeightProperty, value);
    }

    private double unit => BadgeHeight / hexagon_height;

    protected override Size MeasureOverride(Size availableSize)
    {
        var mods = Mods ?? Array.Empty<ReplayMod>();
        if (mods.Count == 0)
            return default;

        return new Size((mods.Sum(width) + (mods.Count - 1) * spacing) * unit, BadgeHeight);
    }

    private static double width(ReplayMod mod) => string.IsNullOrEmpty(extendedInfo(mod)) ? box : extended_x + extended_width;

    public override void Render(DrawingContext context)
    {
        var u = unit;
        var centreY = Bounds.Height / 2;
        var x = 0.0;

        foreach (var mod in Mods ?? Array.Empty<ReplayMod>())
        {
            var colour = typeColour(mod.Type);
            // Interpolation.ValueAt(0.1, Black, colour): almost black, tinted towards the mod's colour.
            var iconColour = Color.FromRgb((byte)(colour.R * 0.1), (byte)(colour.G * 0.1), (byte)(colour.B * 0.1));
            var info = extendedInfo(mod);

            if (!string.IsNullOrEmpty(info))
            {
                const double extender_height = extended_width * 100 / 194;
                fill(context, texture("mod-icon-extender"),
                    new Rect((x + extended_x) * u, centreY - extender_height / 2 * u, extended_width * u, extender_height * u), darken(colour, 2.8));

                var text = new FormattedText(info, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, extended_text_size * u, null);
                var textCentreX = (x + extended_x + extended_width / 2 + 6) * u;
                drawHeavy(context, text, new Point(textCentreX - text.Width / 2, centreY - text.Height / 2), colour, u);
            }

            fill(context, texture("mod-icon"), new Rect(x * u, centreY - hexagon_height / 2 * u, box * u, hexagon_height * u), colour);

            if (texture(iconName(mod)) is { } icon)
            {
                const double icon_width = box * 131 / 135;
                const double icon_height = box * 92 / 135;
                fill(context, icon, new Rect((x + (box - icon_width) / 2) * u, centreY - icon_height / 2 * u, icon_width * u, icon_height * u), iconColour);
            }
            else
            {
                // OsuFont.Numeric Black 22 in osu!lazer.
                var acronym = new FormattedText(mod.Acronym, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 22 * u, new SolidColorBrush(iconColour));
                context.DrawText(acronym, new Point((x + box / 2) * u - acronym.Width / 2, centreY - acronym.Height / 2));
            }

            // Settings marker (shown when the mod's settings differ from the defaults, which is when a score stores any):
            // a cog in the mod's colour on a dark circle.
            if (mod.Settings.Count > 0)
            {
                var marker = new Point((x + 64) * u, centreY + (14 - box / 2) * u);
                context.DrawEllipse(new SolidColorBrush(iconColour), null, marker, 10 * u, 10 * u);
                context.DrawGeometry(new SolidColorBrush(colour), null, cog(marker, 6 * u));
            }

            x += width(mod) + spacing;
        }
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

    // OsuColour.ForModType.
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

    /// <summary>
    /// Torus Bold is heavier than the stand-in's bold, so the glyphs get a thin outline in their own colour.
    /// </summary>
    private static void drawHeavy(DrawingContext context, FormattedText text, Point origin, Color colour, double unit)
    {
        var brush = new SolidColorBrush(colour);
        if (text.BuildGeometry(origin) is { } geometry)
        {
            context.DrawGeometry(brush, new Pen(brush, 1.6 * unit, lineJoin: PenLineJoin.Round), geometry);
            return;
        }

        text.SetForegroundBrush(brush);
        context.DrawText(text, origin);
    }

    // Draws the texture's shape (its alpha) in a solid colour, as osu!framework does when colouring a sprite.
    private static void fill(DrawingContext context, Bitmap? shape, Rect rect, Color colour)
    {
        if (shape == null)
            return;

        using (context.PushOpacityMask(new ImageBrush(shape) { Stretch = Stretch.Fill }, rect))
            context.FillRectangle(new SolidColorBrush(colour), rect);
    }

    // An eight-toothed cog with a hole (FontAwesome's cog in osu!lazer), radius r.
    private static Geometry cog(Point centre, double r)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.SetFillRule(FillRule.EvenOdd);

            const int steps = 8 * 4;
            for (var i = 0; i < steps; i++)
            {
                var angle = Math.PI * 2 * i / steps;
                var radius = i % 4 < 2 ? r : r * 0.72;
                var point = new Point(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
                if (i == 0)
                    path.BeginFigure(point, true);
                else
                    path.LineTo(point);
            }

            path.EndFigure(true);

            var hole = r * 0.32;
            path.BeginFigure(new Point(centre.X + hole, centre.Y), true);
            path.ArcTo(new Point(centre.X - hole, centre.Y), new Size(hole, hole), 0, false, SweepDirection.Clockwise);
            path.ArcTo(new Point(centre.X + hole, centre.Y), new Size(hole, hole), 0, false, SweepDirection.Clockwise);
            path.EndFigure(true);
        }

        return geometry;
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
