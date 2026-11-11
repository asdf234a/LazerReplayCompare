using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LazerReplayCompare;

/// <summary>
/// The colours of difficulty numbers and grades. A number is coloured by its own scale: Sunny star ratings and osu!'s
/// star ratings run along osu!'s difficulty spectrum; the estimators' dan numbers run along the same spectrum, which
/// they cover about 1.8 times as fast.
/// </summary>
public static class DifficultyColors
{
    // osu!'s star rating spectrum (OsuColour.ForStarDifficulty)
    private static readonly (double At, Color Color)[] spectrum =
    [
        (0.1, Color.Parse("#4290FB")), (1.25, Color.Parse("#4FC0FF")), (2.0, Color.Parse("#4FFFD5")), (2.5, Color.Parse("#7CFF4F")),
        (3.3, Color.Parse("#F6F05C")), (4.2, Color.Parse("#FF8068")), (4.9, Color.Parse("#FF4E6F")), (5.8, Color.Parse("#C645B8")),
        (6.7, Color.Parse("#6563DE")), (7.7, Color.Parse("#18158E")), (9.0, Color.Parse("#000000")),
    ];

    private static readonly Dictionary<int, IBrush> cache = new();

    /// <summary>From here on osu! writes a star rating in yellow (on a badge that goes on to black).</summary>
    public const double HighStars = 6.5;

    private static readonly IBrush highText = new SolidColorBrush(Color.Parse("#FFD966")).ToImmutable();
    private static readonly IBrush darkText = new SolidColorBrush(Color.Parse("#14171D")).ToImmutable();
    private static readonly Dictionary<int, IBrush> badges = new();

    /// <summary>A star rating as text: along the spectrum, and yellow from 6.5 stars as in the game.</summary>
    public static IBrush ForStars(double stars) => double.IsFinite(stars) && stars >= HighStars ? highText : brush(stars);

    /// <summary>Where a Sunny rating is read on osu!'s star spectrum.</summary>
    public const double SunnyScale = 0.85;

    // The Sunny rating maps of a dan level have: the median of each level (x.00 to x.99, at x.5) over a library of
    // 29,000 4K and 14,000 7K maps the estimators gave a number. A dan level is coloured as that Sunny rating, so a map
    // has much the same colour by either number. (4K's first point is everything under Reform 3.)
    private static readonly (double Level, double Sunny)[] dan4K =
    [
        (2.5, 2.61), (3.5, 3.79), (4.5, 4.31), (5.5, 4.74), (6.5, 5.14), (7.5, 5.42), (8.5, 5.65), (9.5, 5.86), (10.5, 6.18),
        (11.5, 6.51), (12.5, 6.87), (13.5, 7.30), (14.5, 7.89), (15.5, 8.62), (16.5, 9.29), (17.5, 9.88),
    ];

    private static readonly (double Level, double Sunny)[] dan7K =
    [
        (-0.5, 3.64), (0.5, 4.06), (1.5, 4.62), (2.5, 5.10), (3.5, 5.50), (4.5, 5.84), (5.5, 6.18), (6.5, 6.62), (7.5, 7.00),
        (8.5, 7.39), (9.5, 7.83), (10.5, 8.27), (11.5, 8.75), (12.5, 9.22), (13.5, 9.80), (14.5, 10.35),
    ];

    /// <summary>The Sunny rating a dan level stands for (between the surveyed points, and on past them at their last slope).</summary>
    public static double SunnyOfDanLevel(int keys, double level)
    {
        var points = keys == 4 ? dan4K : dan7K;
        int i = 1;
        while (i < points.Length - 1 && level > points[i].Level)
            i++;

        var (a, b) = (points[i - 1], points[i]);
        return Math.Max(0, a.Sunny + (level - a.Level) / (b.Level - a.Level) * (b.Sunny - a.Sunny));
    }

    private static readonly IBrush noBadge = new SolidColorBrush(Color.Parse("#22FFFFFF")).ToImmutable();

    // osu!'s colours of a beatmap's status
    private static readonly Dictionary<string, Color> statuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ranked"] = Color.Parse("#B3FF66"),
        ["approved"] = Color.Parse("#B3FF66"),
        ["qualified"] = Color.Parse("#66CCFF"),
        ["loved"] = Color.Parse("#FF66AB"),
        ["pending"] = Color.Parse("#FFD966"),
        ["wip"] = Color.Parse("#FF9966"),
        ["graveyard"] = Color.Parse("#9AA3B2"),
    };

    /// <summary>A status's pill: its colour seen through, and the colour itself for the text.</summary>
    public static (IBrush Background, IBrush Text) ForStatus(string? status)
    {
        var color = statuses.GetValueOrDefault(status ?? string.Empty, Color.Parse("#9AA3B2"));
        return (new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)).ToImmutable(), new SolidColorBrush(color).ToImmutable());
    }

    /// <summary>The box behind a number read at <paramref name="stars"/> on the spectrum (NaN: no number).</summary>
    public static IBrush Badge(double stars) => double.IsFinite(stars) ? StarsBadge(stars) : noBadge;

    /// <summary>The text in that box: dark, yellow from 6.5 stars as in the game.</summary>
    public static IBrush BadgeText(double stars) => double.IsFinite(stars) ? StarsBadgeText(stars) : Neutral;

    /// <summary>The badge behind a star rating: the spectrum as it is, down to black.</summary>
    public static IBrush StarsBadge(double stars)
    {
        if (!double.IsFinite(stars))
            return Neutral;

        int key = (int)Math.Round(Math.Clamp(stars, 0, 12) * 20);
        lock (badges)
        {
            if (!badges.TryGetValue(key, out var found))
                badges[key] = found = new SolidColorBrush(at(key / 20.0)).ToImmutable();
            return found;
        }
    }

    /// <summary>The text on that badge: dark, yellow from 6.5 stars.</summary>
    public static IBrush StarsBadgeText(double stars) => double.IsFinite(stars) && stars >= HighStars ? highText : darkText;

    /// <summary>Sunny ratings sit a little above osu!'s for the same map, so they are read a little lower on the spectrum.</summary>
    public static IBrush ForSunny(double sunny) => brush(sunny * 0.85);

    public static IBrush ForDanLevel(double level) => brush(level * 0.55);

    public static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#9AA3B2")).ToImmutable();

    private static IBrush brush(double value)
    {
        if (!double.IsFinite(value))
            return Neutral;

        int key = (int)Math.Round(Math.Clamp(value, 0, 12) * 20);
        lock (cache)
        {
            if (!cache.TryGetValue(key, out var found))
                cache[key] = found = new SolidColorBrush(readable(at(key / 20.0))).ToImmutable();
            return found;
        }
    }

    private static Color at(double value)
    {
        if (value <= spectrum[0].At)
            return spectrum[0].Color;

        for (int i = 1; i < spectrum.Length; i++)
        {
            if (value > spectrum[i].At)
                continue;

            var (a, b) = (spectrum[i - 1], spectrum[i]);
            double t = (value - a.At) / (b.At - a.At);
            return Color.FromRgb(mix(a.Color.R, b.Color.R, t), mix(a.Color.G, b.Color.G, t), mix(a.Color.B, b.Color.B, t));
        }

        return spectrum[^1].Color;
    }

    private static byte mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    /// <summary>The spectrum's dark end is unreadable as text on the app's dark surfaces: it is lifted towards a light violet.</summary>
    private static Color readable(Color color)
    {
        double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
        if (luminance >= 0.42)
            return color;

        double lift = Math.Clamp((0.42 - luminance) / 0.42, 0, 1) * 0.75 + 0.15;
        var target = Color.Parse("#C9BBFF");
        return Color.FromRgb(mix(color.R, target.R, lift), mix(color.G, target.G, lift), mix(color.B, target.B, lift));
    }

    // --- Grades ---

    private static readonly Dictionary<string, IBrush> ranks = new()
    {
        ["SS"] = new SolidColorBrush(Color.Parse("#FF9CC8")).ToImmutable(),
        ["S"] = new SolidColorBrush(Color.Parse("#F5C451")).ToImmutable(),
        ["A"] = new SolidColorBrush(Color.Parse("#6EE7A0")).ToImmutable(),
        ["B"] = new SolidColorBrush(Color.Parse("#60A5FA")).ToImmutable(),
        ["C"] = new SolidColorBrush(Color.Parse("#C4A1FF")).ToImmutable(),
        ["D"] = new SolidColorBrush(Color.Parse("#FF8A8A")).ToImmutable(),
        ["F"] = new SolidColorBrush(Color.Parse("#8B93A3")).ToImmutable(),
        [""] = new SolidColorBrush(Color.Parse("#4A5261")).ToImmutable(),
    };

    /// <summary>The colour of a grade, by its letter ("SS" … "D"; osu!lazer's "X", "XH", "SH" are understood too).</summary>
    public static IBrush ForRank(string? rank) => ranks.GetValueOrDefault(Ranks.Letter(rank)) ?? Neutral;
}

/// <summary>How many of a group's maps have each grade, as one thin bar (SS first, never played last).</summary>
public sealed class RankBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<int>?> CountsProperty = AvaloniaProperty.Register<RankBar, IReadOnlyList<int>?>(nameof(Counts));

    /// <summary>Counts in <see cref="Ranks.Letters"/> order.</summary>
    public IReadOnlyList<int>? Counts
    {
        get => GetValue(CountsProperty);
        set => SetValue(CountsProperty, value);
    }

    static RankBar()
    {
        AffectsRender<RankBar>(CountsProperty);
    }

    public override void Render(DrawingContext context)
    {
        var counts = Counts;
        double total = counts?.Sum() ?? 0;
        if (counts == null || total <= 0)
            return;

        double x = 0;
        using var clip = context.PushClip(new RoundedRect(new Rect(Bounds.Size), Bounds.Height / 2));
        for (int i = 0; i < counts.Count && i < Ranks.Letters.Length; i++)
        {
            if (counts[i] <= 0)
                continue;

            double width = Bounds.Width * counts[i] / total;
            context.FillRectangle(DifficultyColors.ForRank(Ranks.Letters[i]), new Rect(x, 0, Math.Max(0, width - 1), Bounds.Height));
            x += width;
        }
    }
}
