using System.Text.Json;

namespace LazerReplayCompare;

/// <summary>
/// Something an overlay can show. Built-in items are worked out by the overlays themselves; custom items (from
/// metrics.json) are weighted sums of judgement counts, sent to the overlays with their definition.
/// </summary>
/// <param name="Format">"integer", "percent", "decimal" (two decimals, no unit) or "rank" (#32).</param>
/// <param name="Weights">Value of one judgement (Perfect, Great, Good, Ok, Meh, Miss), custom items only.</param>
/// <param name="Average">Divide by the number of judgements (an accuracy-like value).</param>
/// <param name="Scale">Multiplier applied last.</param>
/// <param name="Per">Judgement values of a sum to divide by (instead of <paramref name="Average"/>), custom items only.</param>
/// <param name="Add">Added to the sum and to what it is divided by, so nothing starts at zero (0 ÷ 0, log 0).</param>
/// <param name="Log">Base of a logarithm taken of the value (before <paramref name="Scale"/>); null for none.</param>
/// <param name="Pow">Power the value is raised to after <paramref name="Log"/>, keeping its sign; null for none.</param>
public sealed record OverlayItem(
    string Key,
    string Name,
    string Format,
    bool BuiltIn,
    IReadOnlyDictionary<string, double>? Weights = null,
    bool Average = false,
    double Scale = 1,
    bool Round = false,
    IReadOnlyDictionary<string, double>? Per = null,
    double Add = 0,
    double? Log = null,
    double? Pow = null);

/// <summary>How an overlay shows its item: the difference to the compared replay, the live value, or the replay's value.</summary>
public static class OverlayShow
{
    public const string Diff = "diff";
    public const string Value = "value";
    public const string Target = "target";

    public static readonly string[] All = [Diff, Value, Target];

    public static string Normalize(string? value, string fallback)
        => All.FirstOrDefault(show => string.Equals(show, value, StringComparison.OrdinalIgnoreCase)) ?? fallback;
}

/// <summary>
/// The items the overlays can show: the built-in ones, plus custom ones from a metrics.json the user can edit (read
/// again whenever it changes, so new items need no rebuild).
/// </summary>
public static class OverlayItems
{
    public const string Off = "off";

    public static readonly IReadOnlyList<OverlayItem> BuiltIn =
    [
        new("score", "Score", "integer", true),
        new("score_acc", "Score acc", "percent", true),
        // 0 at a base picked on the Compare page (999,000, or a place on the leaderboard), +10 each time the score lost is
        // halved; all MAX counts as e^-gamma of a 300 (see metrics.js)
        new("score_acc_log", "Score log", "decimal", true),
        // where the score would land on the leaderboard at this pace; hidden below 50th
        new("live_rank", "Live rank", "rank", true),
        new("acc", "Acc", "percent", true),
        // 0 at a picked accuracy, +10 each time the accuracy lost is halved; the formula is picked too (metrics.js)
        new("acc_log", "Acc log", "decimal", true),
        new("stable_acc", "Stable acc", "percent", true),
        new("lazer_acc", "Lazer acc", "percent", true),
        // MAX ÷ 300, with no 300 yet as one (so it reads the MAX count until the first 300)
        new("ma_ratio", "MAX : 300", "decimal", true),
    ];

    // older metric names (app settings before 1.2)
    private static readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Accuracy"] = "acc",
        ["V1Acc"] = "stable_acc",
        ["LazerAcc"] = "lazer_acc",
        ["Bms"] = "bms",
        ["PpAcc"] = "pp_acc",
        ["PpScore"] = "pp_score",
        ["MaRatio"] = "ma_ratio",
        ["MA"] = "ma_ratio",
        ["rank_log"] = "score_acc_log",
        ["score_k"] = "score_acc_log",
        ["None"] = Off,
    };

    private static readonly JsonSerializerOptions json_options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly object sync = new();
    private static string customPath = string.Empty;
    private static DateTime customWritten;
    private static IReadOnlyList<OverlayItem> custom = [];

    /// <summary>The custom items' file. Created with examples when missing.</summary>
    public static string CustomPath
    {
        get => customPath;
        set
        {
            lock (sync)
            {
                customPath = value;
                customWritten = default;
                custom = [];
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(value) && !File.Exists(value))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(value)!);
                    File.WriteAllText(value, example);
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
            }
        }
    }

    /// <summary>Every item: built-in first, then custom ones (a custom item never replaces a built-in one).</summary>
    public static IReadOnlyList<OverlayItem> All => BuiltIn.Concat(Custom().Where(item => BuiltIn.All(b => !same(b.Key, item.Key)))).ToList();

    /// <summary>The custom items, re-read when the file changed; the last good ones when it is missing or broken.</summary>
    public static IReadOnlyList<OverlayItem> Custom()
    {
        lock (sync)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(customPath) || !File.Exists(customPath))
                    return custom = [];

                var written = File.GetLastWriteTimeUtc(customPath);
                if (written == customWritten)
                    return custom;

                customWritten = written;
                custom = parse(File.ReadAllText(customPath));
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
            }

            return custom;
        }
    }

    /// <summary>The item's canonical key; <see cref="Off"/> when allowed and asked for; otherwise <paramref name="fallback"/>.</summary>
    public static string Normalize(string? name, string fallback, bool allowOff = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        if (aliases.TryGetValue(name, out var alias))
            name = alias;
        if (same(name, Off))
            return allowOff ? Off : fallback;

        return All.FirstOrDefault(item => same(item.Key, name))?.Key ?? fallback;
    }

    /// <summary>The groups of the item lists, in order.</summary>
    public static readonly IReadOnlyList<string> Groups = ["Score", "Accuracy", "Judgements", "Ranking", "Custom"];

    private static readonly Dictionary<string, string> item_groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["score"] = "Score",
        ["score_acc"] = "Score",
        ["score_acc_log"] = "Score",
        ["pp_score"] = "Score",
        ["acc"] = "Accuracy",
        ["acc_log"] = "Accuracy",
        ["stable_acc"] = "Accuracy",
        ["lazer_acc"] = "Accuracy",
        ["pp_acc"] = "Accuracy",
        ["ma_ratio"] = "Judgements",
        ["bms"] = "Judgements",
        ["miss_count"] = "Judgements",
        ["live_rank"] = "Ranking",
    };

    /// <summary>The group an item is listed under; a custom item the app doesn't know goes under Custom.</summary>
    public static string GroupOf(string item) => item_groups.GetValueOrDefault(item, "Custom");

    /// <summary>The <see cref="MetricRegistry"/> metric a scaled timeline is stretched by for this item.</summary>
    public static string ScaleMetric(string item) => item.ToLowerInvariant() switch
    {
        "acc" => "Acc",
        "stable_acc" => "V1Acc",
        "lazer_acc" => "LazerAcc",
        _ => "Score",
    };

    public static string NameOf(string item) => same(item, Off) ? "Off" : All.FirstOrDefault(i => same(i.Key, item))?.Name ?? item;

    /// <summary>Items in the shape the overlays consume (camelCase JSON).</summary>
    public static IReadOnlyList<object> ToJson() => All.Select(item => (object)new
    {
        key = item.Key,
        name = item.Name,
        format = item.Format,
        builtIn = item.BuiltIn,
        weights = item.Weights,
        average = item.Average,
        scale = item.Scale,
        round = item.Round,
        per = item.Per,
        add = item.Add,
        log = item.Log,
        pow = item.Pow,
    }).ToList();

    private static bool same(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class CustomFile
    {
        public Dictionary<string, CustomItem>? Metrics { get; set; }
    }

    private sealed class CustomItem
    {
        public string? Name { get; set; }
        public Dictionary<string, double>? Weights { get; set; }
        public bool Average { get; set; }
        public string? Format { get; set; }
        public double? Scale { get; set; }
        public bool Round { get; set; }
        public Dictionary<string, double>? Per { get; set; }
        public double Add { get; set; }
        public double? Log { get; set; }
        public double? Pow { get; set; }
    }

    private static IReadOnlyList<OverlayItem> parse(string text)
    {
        var file = JsonSerializer.Deserialize<CustomFile>(text, json_options);
        return (file?.Metrics ?? new Dictionary<string, CustomItem>())
               .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value?.Weights is { Count: > 0 })
               .Select(entry => new OverlayItem(
                   entry.Key.Trim(),
                   string.IsNullOrWhiteSpace(entry.Value.Name) ? entry.Key.Trim() : entry.Value.Name.Trim(),
                   customFormat(entry.Value.Format),
                   false,
                   entry.Value.Weights,
                   entry.Value.Average,
                   entry.Value.Scale is > 0 or < 0 ? entry.Value.Scale.Value : 1,
                   entry.Value.Round,
                   entry.Value.Per is { Count: > 0 } ? entry.Value.Per : null,
                   entry.Value.Add,
                   entry.Value.Log is > 0 and not 1 ? entry.Value.Log : null,
                   entry.Value.Pow is > 0 and not 1 ? entry.Value.Pow : null))
               .ToList();
    }

    private static string customFormat(string? format)
    {
        if (string.Equals(format, "percent", StringComparison.OrdinalIgnoreCase))
            return "percent";

        return string.Equals(format, "decimal", StringComparison.OrdinalIgnoreCase) ? "decimal" : "integer";
    }

    private const string example = """
        {
            // Extra items for the overlays, made of judgement counts. They show up in the app's overlay pickers.
            // Saved changes apply right away.
            //
            //   name     shown name
            //   weights  value of one Perfect (MAX), Great (300), Good (200), Ok (100), Meh (50), Miss
            //   average  divide by the number of judgements (an accuracy-like value)
            //   per      divide by this sum of judgement values instead (optional)
            //   add      added to the sum and to what it is divided by, so nothing starts at zero (optional)
            //   log      take the logarithm in this base (optional): 2, 10, or 2.718281828 for ln
            //   pow      raise the value to this power, after log, keeping its sign (optional)
            //   format   "integer", "percent" (two decimals and %) or "decimal" (two decimals)
            //   scale    multiplier applied last (optional)
            //   round    round to a whole number (optional)
            "metrics": {
                "bms": {
                    "name": "BMS",
                    "weights": { "Perfect": 2, "Great": 1 },
                    "format": "integer"
                },
                "pp_acc": {
                    "name": "PPacc",
                    "weights": { "Perfect": 100, "Great": 93.75, "Good": 62.5, "Ok": 31.25, "Meh": 15.625 },
                    "average": true,
                    "format": "percent"
                },
                "pp_score": {
                    "name": "PPscore",
                    "weights": { "Perfect": 100, "Great": 93.75, "Good": 62.5, "Ok": 31.25, "Meh": 15.625 },
                    "format": "integer",
                    "round": true
                }
            }
        }

        """;
}
