using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazerReplayCompare;

public sealed class AppSettings
{
    public string OsuLazerPath { get; set; } = string.Empty;
    public string OsuStablePath { get; set; } = string.Empty;
    public string Theme { get; set; } = "System";
    public string CorrectionMode { get; set; } = "Corrected";

    // What the overlays show: an item (OverlayItems) and how (OverlayShow), for Main and Sub.
    public string MainItem { get; set; } = string.Empty;
    public string MainShow { get; set; } = OverlayShow.Diff;
    public string SubItem { get; set; } = string.Empty;
    public string SubShow { get; set; } = OverlayShow.Value;

    /// <summary>What Score log measures from: "score" (<see cref="BaseScore"/>) or "rank" (<see cref="RankTarget"/>).</summary>
    public string BaseMode { get; set; } = "score";
    public int BaseScore { get; set; } = 999000;

    /// <summary>The leaderboard place (1..50) Score log measures from.</summary>
    public int RankTarget { get; set; } = 50;

    /// <summary>What Acc log measures: "stable", "lazer" or "pp".</summary>
    public string AccLogKind { get; set; } = "lazer";

    /// <summary>The accuracy, in percent, where Acc log is 0.</summary>
    public double AccLogBase { get; set; } = 95;

    /// <summary>Items left out of the Main and Sub lists (Settings).</summary>
    public List<string> HiddenItems { get; set; } = new();

    /// <summary>The user's osu! OAuth client, for osu!lazer's leaderboards (tosu can't read them).</summary>
    public string OsuClientId { get; set; } = string.Empty;
    public string OsuClientSecret { get; set; } = string.Empty;

    // Before 1.2 the overlays' metrics: read once into MainItem / SubItem, then dropped.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MainMetric { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubMetric { get; set; }
    public bool ShowBackground { get; set; } = true;

    /// <summary>Open the details of the replay a results screen shows.</summary>
    public bool OpenDetailsOnResults { get; set; } = true;

    /// <summary>Zoom of the pages (1 = 100%).</summary>
    public double Zoom { get; set; } = 1;

    private static string SettingsPath => AppPaths.Settings;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            if (string.IsNullOrWhiteSpace(settings.MainItem) && !string.IsNullOrWhiteSpace(settings.MainMetric))
                settings.MainItem = settings.MainMetric;
            if (string.IsNullOrWhiteSpace(settings.SubItem) && !string.IsNullOrWhiteSpace(settings.SubMetric))
                settings.SubItem = settings.SubMetric;
            settings.MainMetric = settings.SubMetric = null;
            // Rank N log became Score log measured from a place
            if (settings.MainItem == "rank_log" || settings.SubItem == "rank_log")
                settings.BaseMode = "rank";
            return settings;
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return new AppSettings();
        }
    }

    private static readonly JsonSerializerOptions save_options = new() { WriteIndented = true };

    /// <summary>
    /// Writes the settings (to a temporary file first, so a crash mid-write can't leave half a file). A file that can't be
    /// written (locked by a sync tool or antivirus) is logged, not thrown: the settings still apply for this run.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, save_options));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InternalLogger.Log(ex);
        }
    }
}
