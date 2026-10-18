namespace LazerReplayCompare;

public sealed class AppSettingsService
{
    public AppSettings Current { get; } = AppSettings.Load();

    public void Save()
    {
        Current.Save();
    }

    // Each setter saves only when the value changes (the window passes every setting after any one of them changed).

    public void SetOsuLazerPath(string path)
    {
        if (Current.OsuLazerPath == path)
            return;

        Current.OsuLazerPath = path;
        Save();
    }

    public void SetOsuStablePath(string path)
    {
        if (Current.OsuStablePath == path)
            return;

        Current.OsuStablePath = path;
        Save();
    }

    public void SetTheme(string theme)
    {
        theme = string.IsNullOrWhiteSpace(theme) ? "System" : theme;
        if (Current.Theme == theme)
            return;

        Current.Theme = theme;
        Save();
    }

    public void SetCorrectionMode(CorrectionMode mode)
    {
        if (Current.CorrectionMode == mode.ToString())
            return;

        Current.CorrectionMode = mode.ToString();
        Save();
    }

    public void SetShowBackground(bool show)
    {
        if (Current.ShowBackground == show)
            return;

        Current.ShowBackground = show;
        Save();
    }

    public void SetOpenDetailsOnResults(bool open)
    {
        if (Current.OpenDetailsOnResults == open)
            return;

        Current.OpenDetailsOnResults = open;
        Save();
    }

    public void SetZoom(double zoom)
    {
        if (Current.Zoom == zoom)
            return;

        Current.Zoom = zoom;
        Save();
    }

    public void SetOverlayDisplay(string mainItem, string mainShow, string subItem, string subShow)
    {
        if (Current.MainItem == mainItem && Current.MainShow == mainShow && Current.SubItem == subItem && Current.SubShow == subShow)
            return;

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
