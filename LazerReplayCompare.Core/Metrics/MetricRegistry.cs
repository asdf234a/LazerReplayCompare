namespace LazerReplayCompare;

/// <summary>Where a metric's value comes from.</summary>
public enum MetricSource
{
    /// <summary>The game's total score.</summary>
    Score,

    /// <summary>The game's accuracy (0..1).</summary>
    Accuracy,

    /// <summary>A weighted sum of judgement counts.</summary>
    Hits,
}

/// <summary>How a metric is displayed.</summary>
public enum MetricFormat
{
    /// <summary>Rounded whole number with thousands separators.</summary>
    Integer,

    /// <summary>Two decimals followed by "%".</summary>
    Percent,
}

/// <summary>
/// One comparison metric (score, accuracy, judgement-weighted scores...). The app, the timeline scaler and the overlays
/// (through <c>/state</c> and <c>/metrics</c>) all evaluate metrics from these definitions, so a new metric only needs a
/// new entry in <see cref="MetricRegistry.All"/>.
/// </summary>
/// <param name="Key">Stable identifier stored in settings and sent to the overlays.</param>
/// <param name="DisplayName">Label shown in the app.</param>
/// <param name="Source">Where the value comes from.</param>
/// <param name="Format">How the value is displayed.</param>
/// <param name="HitWeights">Weight per judgement name (Perfect, Great, Good, Ok, Meh, Miss) for <see cref="MetricSource.Hits"/>.</param>
/// <param name="Average">Divide the weighted sum by the number of judgements (Perfect..Miss).</param>
/// <param name="Round">Round the value to a whole number.</param>
/// <param name="DisplayScale">Multiplier from the value to its displayed number (accuracy 0..1 is shown as 0..100%).</param>
/// <param name="Maximum">Upper bound applied when the value is rescaled (accuracy cannot exceed 100%).</param>
/// <param name="Aliases">Older or alternative names accepted from settings, queries and overlays.</param>
public sealed record MetricDefinition(
    string Key,
    string DisplayName,
    MetricSource Source,
    MetricFormat Format,
    IReadOnlyDictionary<string, double>? HitWeights = null,
    bool Average = false,
    bool Round = false,
    double DisplayScale = 1,
    double? Maximum = null,
    IReadOnlyList<string>? Aliases = null)
{
    public static readonly string[] JudgementNames = ["Perfect", "Great", "Good", "Ok", "Meh", "Miss"];

    /// <summary>The metric's value (in its own units, e.g. accuracy as 0..1).</summary>
    public double Evaluate(double score, double accuracy, Func<string, double> hits)
    {
        var value = Source switch
        {
            MetricSource.Score => score,
            MetricSource.Accuracy => accuracy,
            _ => evaluateHits(hits),
        };

        return Round ? Math.Round(value) : value;
    }

    private double evaluateHits(Func<string, double> hits)
    {
        if (HitWeights == null)
            return 0;

        var sum = HitWeights.Sum(weight => weight.Value * hits(weight.Key));
        if (!Average)
            return sum;

        var total = JudgementNames.Sum(hits);
        return total > 0 ? sum / total : 0;
    }

    /// <summary>
    /// Whether a scaled timeline can carry this metric inside its frames (score/accuracy fields). Hit-based metrics are
    /// derived from judgement counts, so consumers multiply them by the timeline's scale factor instead.
    /// </summary>
    public bool ScalesInFrames => Source != MetricSource.Hits;
}

public static class MetricRegistry
{
    /// <summary>Selectable for the sub metric only: hides it.</summary>
    public const string Off = "Off";

    private static readonly string[] off_aliases = ["None"];

    private static readonly Dictionary<string, double> pp_weights = new()
    {
        ["Perfect"] = 100, ["Great"] = 93.75, ["Good"] = 62.5, ["Ok"] = 31.25, ["Meh"] = 15.625,
    };

    /// <summary>Every metric, in the order the app lists them. The first one is the default.</summary>
    public static readonly IReadOnlyList<MetricDefinition> All =
    [
        new("Score", "Score", MetricSource.Score, MetricFormat.Integer, Aliases: ["ScoreDiff", "score_acc"]),
        new("Acc", "Acc", MetricSource.Accuracy, MetricFormat.Percent, DisplayScale: 100, Maximum: 1, Aliases: ["Accuracy", "AccuracyDiff"]),
        new("Bms", "BMS", MetricSource.Hits, MetricFormat.Integer,
            HitWeights: new Dictionary<string, double> { ["Perfect"] = 2, ["Great"] = 1 }, Aliases: ["BmsScore"]),
        new("PpAcc", "PPacc", MetricSource.Hits, MetricFormat.Percent, HitWeights: pp_weights, Average: true, Aliases: ["JudgeDiff", "Judge"]),
        new("PpScore", "PPscore", MetricSource.Hits, MetricFormat.Integer, HitWeights: pp_weights, Round: true),
        new("V1Acc", "V1acc", MetricSource.Hits, MetricFormat.Percent,
            HitWeights: new Dictionary<string, double> { ["Perfect"] = 100, ["Great"] = 100, ["Good"] = 200 / 3.0, ["Ok"] = 100 / 3.0, ["Meh"] = 50 / 3.0 },
            Average: true, Aliases: ["stable_acc"]),
        // osu!lazer's osu!mania accuracy (MAX 305, 300, 200, 100, 50), from any judgement counts
        new("LazerAcc", "Lazer acc", MetricSource.Hits, MetricFormat.Percent,
            HitWeights: new Dictionary<string, double> { ["Perfect"] = 100, ["Great"] = 30000 / 305.0, ["Good"] = 20000 / 305.0, ["Ok"] = 10000 / 305.0, ["Meh"] = 5000 / 305.0 },
            Average: true, Aliases: ["lazer_acc"]),
    ];

    public static MetricDefinition Default => All[0];

    /// <summary>The metric whose key or alias matches <paramref name="name"/> (case-insensitive), or null.</summary>
    public static MetricDefinition? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return All.FirstOrDefault(metric => matches(metric.Key, name) || (metric.Aliases?.Any(alias => matches(alias, name)) ?? false));
    }

    /// <summary>
    /// The canonical key for <paramref name="name"/>; <see cref="Off"/> when allowed and requested; otherwise
    /// <paramref name="fallback"/>.
    /// </summary>
    public static string Normalize(string? name, string fallback, bool allowOff = false)
    {
        if (allowOff && (matches(Off, name) || off_aliases.Any(alias => matches(alias, name))))
            return Off;

        return Find(name)?.Key ?? fallback;
    }

    /// <summary>Metric definitions in the shape the overlays consume (camelCase JSON).</summary>
    public static IReadOnlyList<object> ToJson() => All.Select(metric => (object)new
    {
        key = metric.Key,
        name = metric.DisplayName,
        source = metric.Source.ToString().ToLowerInvariant(),
        format = metric.Format.ToString().ToLowerInvariant(),
        weights = metric.HitWeights,
        average = metric.Average,
        round = metric.Round,
        displayScale = metric.DisplayScale,
        maximum = metric.Maximum,
        aliases = metric.Aliases ?? [],
    }).ToList();

    private static bool matches(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
