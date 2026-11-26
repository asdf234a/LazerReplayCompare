using System.Globalization;

namespace LazerReplayCompare;

/// <summary>
/// What an overlay item comes to for a finished replay, worked out the way the overlays work it out during a play
/// (static/lazerReplayCompareShared/metrics.js), from what the replay's score records. Items that need more than
/// that have no value here: Score acc and Score log (the best possible score), the PP items (tosu's star rating)
/// and the Rivals items.
/// </summary>
public static class ItemValues
{
    private static readonly string[] judgements = ["Perfect", "Great", "Good", "Ok", "Meh", "Miss"];

    private static readonly Dictionary<string, double> stable_values = new() { ["Perfect"] = 300, ["Great"] = 300, ["Good"] = 200, ["Ok"] = 100, ["Meh"] = 50 };
    private static readonly Dictionary<string, double> lazer_values = new() { ["Perfect"] = 305, ["Great"] = 300, ["Good"] = 200, ["Ok"] = 100, ["Meh"] = 50 };
    private static readonly Dictionary<string, double> pp_values = new() { ["Perfect"] = 320, ["Great"] = 300, ["Good"] = 200, ["Ok"] = 100, ["Meh"] = 50 };

    // e^-gamma: how many 300s a run of nothing but MAX stands for on a log scale (see metrics.js)
    private static readonly double greats_in_all_max = Math.Exp(-0.5772156649015329);

    /// <summary>The item's value in display units (percent items as 0..100), or null when it can't be told from the score.</summary>
    /// <param name="accLogKind">What Acc log measures: "stable", "lazer" or "pp".</param>
    /// <param name="accLogBase">The accuracy (in percent) where Acc log is 0.</param>
    public static double? Final(OverlayItem item, ReplayEntry replay, string accLogKind = "lazer", double accLogBase = 95)
    {
        var hits = replay.Statistics;
        double total = judgements.Sum(name => (double)hits.GetValueOrDefault(name));

        double? value = item.Key.ToLowerInvariant() switch
        {
            "score" => replay.Score,
            "acc" => replay.Accuracy * 100,
            "stable_acc" => total > 0 ? weighted(hits, stable_values) / (300 * total) * 100 : null,
            "lazer_acc" => total > 0 ? weighted(hits, lazer_values) / (305 * total) * 100 : null,
            "ma_ratio" => hits.GetValueOrDefault("Perfect") + hits.GetValueOrDefault("Great") > 0
                ? hits.GetValueOrDefault("Perfect") / (double)Math.Max(hits.GetValueOrDefault("Great"), 1)
                : null,
            "acc_log" => accLog(hits, total, accLogKind, accLogBase),
            _ => item.BuiltIn || item.Weights == null ? null : custom(item, hits, total),
        };

        if (value is not { } number || !double.IsFinite(number))
            return null;

        return item.Round ? Math.Round(number) : number;
    }

    /// <summary>The value as the overlays write it: whole numbers with separators, percentages and ratios with two decimals.</summary>
    /// <param name="cutPercent">osu!lazer never shows a rounded-up percentage (98.769 is 98.76); osu!stable rounds.</param>
    public static string Text(OverlayItem item, double value, bool cutPercent)
    {
        return item.Format switch
        {
            "percent" => (cutPercent ? Math.Floor(value * 100 + 1e-6) / 100 : value).ToString("0.00", CultureInfo.InvariantCulture) + "%",
            "decimal" => value.ToString("0.00", CultureInfo.InvariantCulture),
            _ => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A difference between two values, signed ("+1,234", "−0.35%", "±0").</summary>
    public static string DifferenceText(OverlayItem item, double difference)
    {
        return item.Format switch
        {
            "percent" => difference.ToString("+0.00;−0.00;±0.00", CultureInfo.InvariantCulture) + "%",
            "decimal" => difference.ToString("+0.00;−0.00;±0.00", CultureInfo.InvariantCulture),
            _ => Math.Round(difference).ToString("+#,0;−#,0;±0", CultureInfo.InvariantCulture),
        };
    }

    private static double weighted(IReadOnlyDictionary<string, int> hits, IReadOnlyDictionary<string, double> weights)
        => weights.Sum(weight => hits.GetValueOrDefault(weight.Key) * weight.Value);

    private static double? accLog(IReadOnlyDictionary<string, int> hits, double total, string kind, double basePercent)
    {
        if (total <= 0)
            return null;

        var (values, max, smallestLoss) = kind switch
        {
            "stable" => (stable_values, 300.0, 1 / 3.0),
            "pp" => (pp_values, 320.0, 20 / 320.0),
            _ => (lazer_values, 305.0, 5 / 305.0),
        };

        double lost = 1 - weighted(hits, values) / (max * total);
        if (lost <= 1e-12)
            lost = greats_in_all_max * smallestLoss / total;
        return 10 * Math.Log2((1 - basePercent / 100) / lost);
    }

    private static double? custom(OverlayItem item, IReadOnlyDictionary<string, int> hits, double total)
    {
        double value = weighted(hits, item.Weights!) + item.Add;
        if (item.Per != null)
        {
            double divisor = weighted(hits, item.Per) + item.Add;
            if (divisor <= 0)
                return null;
            value /= divisor;
        }
        else if (item.Average)
        {
            if (total <= 0)
                return null;
            value /= total;
        }

        if (item.Log is { } logBase && logBase > 0 && logBase != 1)
        {
            if (total <= 0 || value <= 0)
                return null;
            value = Math.Log(value) / Math.Log(logBase);
        }

        if (item.Pow is { } power && power > 0 && power != 1)
            value = Math.Sign(value) * Math.Pow(Math.Abs(value), power);

        return value * item.Scale;
    }
}
