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

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LazerReplayCompare",
        "settings.json");

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
            return settings;
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return new AppSettings();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }
}
