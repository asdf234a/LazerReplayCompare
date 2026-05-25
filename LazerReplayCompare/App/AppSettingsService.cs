namespace LazerReplayCompare;

public sealed class AppSettingsService
{
    public AppSettings Current { get; } = AppSettings.Load();

    public void Save()
    {
        Current.Save();
    }

    public void SetOsuLazerPath(string path)
    {
        Current.OsuLazerPath = path;
        Save();
    }

    public void SetTheme(string theme)
    {
        Current.Theme = string.IsNullOrWhiteSpace(theme) ? "System" : theme;
        Save();
    }

    public void SetCorrectionMode(CorrectionMode mode)
    {
        Current.CorrectionMode = mode.ToString();
        Save();
    }

    public void SetMainMetric(string metric)
    {
        Current.MainMetric = string.IsNullOrWhiteSpace(metric) ? "Score" : metric;
        Save();
    }

    public void SetSubMetric(string metric)
    {
        Current.SubMetric = string.IsNullOrWhiteSpace(metric) ? "Off" : metric;
        Save();
    }
}
