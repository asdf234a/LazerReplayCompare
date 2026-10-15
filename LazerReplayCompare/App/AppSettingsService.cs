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

    public void SetOsuStablePath(string path)
    {
        Current.OsuStablePath = path;
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

    public void SetShowBackground(bool show)
    {
        Current.ShowBackground = show;
        Save();
    }

    public void SetOpenDetailsOnResults(bool open)
    {
        Current.OpenDetailsOnResults = open;
        Save();
    }

    public void SetZoom(double zoom)
    {
        Current.Zoom = zoom;
        Save();
    }

    public void SetOverlayDisplay(string mainItem, string mainShow, string subItem, string subShow)
    {
        Current.MainItem = mainItem;
        Current.MainShow = mainShow;
        Current.SubItem = subItem;
        Current.SubShow = subShow;
        Save();
    }

    /// <summary>What the overlays show, normalised (unknown items fall back to score / acc).</summary>
    public OverlayDisplay OverlayDisplay => new(
        OverlayItems.Normalize(Current.MainItem, "score", allowOff: true), OverlayShow.Normalize(Current.MainShow, OverlayShow.Diff),
        OverlayItems.Normalize(Current.SubItem, "acc", allowOff: true), OverlayShow.Normalize(Current.SubShow, OverlayShow.Value));
}

/// <summary>What the Main and Sub overlays show (the live panel shows both items).</summary>
public sealed record OverlayDisplay(string MainItem, string MainShow, string SubItem, string SubShow);
