using System.Globalization;

namespace LazerReplayCompare;

/// <summary>Accuracies as the game the replays are listed from shows them, with two decimals.</summary>
public static class ShownAccuracy
{
    /// <summary>
    /// In percent: osu!lazer cuts it off at two decimals (FormatUtils.FormatAccuracy never shows a rounded-up value), so
    /// 98.769% is 98.76%; osu!stable rounds, so the value is left for the format to round.
    /// </summary>
    public static double Percent(double accuracy, bool stable) => stable ? accuracy * 100 : Math.Floor(accuracy * 10000) / 100;

    public static string Text(double accuracy, bool stable) => Percent(accuracy, stable).ToString("0.00", CultureInfo.InvariantCulture) + "%";
}

public sealed class ReplayRow : ObservableObject
{
    private bool isPinned;
    private bool isTarget;
    private string vsTargetText = "—";
    private long vsTargetDifference;

    /// <param name="stable">Listed from osu!stable (accuracies shown the osu!stable way).</param>
    public ReplayRow(ReplayEntry entry, bool stable = false)
    {
        Entry = entry;
        this.stable = stable;
    }

    private readonly bool stable;

    public ReplayEntry Entry { get; }
    public string Player => Entry.Player;
    public string DateText => FormatDateOnly(Entry.Date);
    public string ModsText => Entry.ModsText;
    public IReadOnlyList<ReplayMod> Mods => Entry.Mods;
    public bool HasMods => Entry.Mods.Count > 0;
    public string ScoreText => Entry.Score.ToString("N0", CultureInfo.InvariantCulture);
    public string AccuracyText => ShownAccuracy.Text(Entry.Accuracy, stable);
    public int Miss => Entry.Statistics.GetValueOrDefault("Miss");
    public string MissText => Miss.ToString(CultureInfo.InvariantCulture);
    public bool IsFullCombo => Miss == 0;
    public string ComboText => Entry.MaxCombo.ToString("N0", CultureInfo.InvariantCulture);
    public string Rank => Entry.Rank;

    /// <summary>False for an osu!stable score whose replay file is missing: listed, but it can't be compared against.</summary>
    public bool HasReplay => Entry.HasReplay;

    public bool IsPinned
    {
        get => isPinned;
        set
        {
            if (Set(ref isPinned, value))
                OnPropertyChanged(nameof(PinTooltip));
        }
    }

    public string PinTooltip => !HasReplay ? "No replay file in osu!stable's Data/r: this score can't be compared against"
        : IsPinned ? "Unpin (compare against the best score again)" : "Pin: compare against this replay";

    public bool IsTarget
    {
        get => isTarget;
        private set => Set(ref isTarget, value);
    }

    public string VsTargetText
    {
        get => vsTargetText;
        private set => Set(ref vsTargetText, value);
    }

    public bool IsAheadOfTarget => vsTargetDifference > 0;
    public bool IsBehindTarget => vsTargetDifference < 0;

    private void setDifference(long difference)
    {
        if (vsTargetDifference == difference)
            return;

        vsTargetDifference = difference;
        OnPropertyChanged(nameof(IsAheadOfTarget));
        OnPropertyChanged(nameof(IsBehindTarget));
    }

    public void UpdateTarget(ReplayEntry? target)
    {
        IsTarget = target != null && string.Equals(target.FilePath, Entry.FilePath, StringComparison.OrdinalIgnoreCase);

        if (target == null || IsTarget)
        {
            VsTargetText = "—";
            setDifference(0);
            return;
        }

        var difference = Entry.Score - target.Score;
        VsTargetText = difference.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);
        setDifference(difference);
    }

    public static string FormatDateOnly(string value)
    {
        return DateTimeOffset.TryParse(value, out var date)
            ? date.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;
    }
}
