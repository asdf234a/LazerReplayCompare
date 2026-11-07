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

    public void SetHiddenItems(IEnumerable<string> items)
    {
        var list = items.Order(StringComparer.Ordinal).ToList();
        if (list.SequenceEqual(Current.HiddenItems))
            return;

        Current.HiddenItems = list;
        Save();
    }

    public void SetOsuClient(string clientId, string secret)
    {
        Current.OsuClientId = clientId;
        Current.OsuClientSecret = secret;
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

    public void SetOverlayDisplay(OverlayDisplay display)
    {
        if (Current.MainItem == display.MainItem && Current.MainShow == display.MainShow && Current.SubItem == display.SubItem && Current.SubShow == display.SubShow
            && Current.BaseMode == display.BaseMode && Current.BaseScore == display.BaseScore && Current.RankTarget == display.RankTarget
            && Current.AccLogKind == display.AccLogKind && Current.AccLogBase == display.AccLogBase)
            return;

        Current.MainItem = display.MainItem;
        Current.MainShow = display.MainShow;
        Current.SubItem = display.SubItem;
        Current.SubShow = display.SubShow;
        Current.BaseMode = display.BaseMode;
        Current.BaseScore = display.BaseScore;
        Current.RankTarget = display.RankTarget;
        Current.AccLogKind = display.AccLogKind;
        Current.AccLogBase = display.AccLogBase;
        Save();
    }

    /// <summary>What the overlays show, normalised (unknown items fall back to score / acc).</summary>
    public OverlayDisplay OverlayDisplay => new(
        OverlayItems.Normalize(Current.MainItem, "score", allowOff: true), OverlayShow.Normalize(Current.MainShow, OverlayShow.Diff),
        OverlayItems.Normalize(Current.SubItem, "acc", allowOff: true), OverlayShow.Normalize(Current.SubShow, OverlayShow.Value),
        Math.Clamp(Current.RankTarget, 1, 50), Current.BaseMode == "rank" ? "rank" : "score", Math.Clamp(Current.BaseScore, 1, 999999),
        Current.AccLogKind is "stable" or "pp" ? Current.AccLogKind : "lazer", Math.Clamp(Current.AccLogBase, 0.01, 99.99));
}

/// <summary>What the Main and Sub overlays show (the live panel shows both items).</summary>
public sealed record OverlayDisplay(string MainItem, string MainShow, string SubItem, string SubShow, int RankTarget = 50, string BaseMode = "score", int BaseScore = 999000,
    string AccLogKind = "lazer", double AccLogBase = 95);
