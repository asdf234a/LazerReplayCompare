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
}
