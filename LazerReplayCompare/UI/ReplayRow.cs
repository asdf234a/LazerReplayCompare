using System.Globalization;

namespace LazerReplayCompare;

public sealed class ReplayRow : ObservableObject
{
    private bool isPinned;
    private bool isTarget;
    private string vsTargetText = "—";
    private long vsTargetDifference;

    public ReplayRow(ReplayEntry entry)
    {
        Entry = entry;
    }

    public ReplayEntry Entry { get; }
    public string Player => Entry.Player;
    public string DateText => FormatDateOnly(Entry.Date);
    public string ModsText => Entry.ModsText;
    public string ScoreText => Entry.Score.ToString("N0", CultureInfo.InvariantCulture);
    public string AccuracyText => (Entry.Accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    public int Miss => Entry.Statistics.GetValueOrDefault("Miss");
    public string MissText => Miss.ToString(CultureInfo.InvariantCulture);
    public bool IsFullCombo => Miss == 0;
    public string ComboText => Entry.MaxCombo.ToString("N0", CultureInfo.InvariantCulture);
    public string Rank => Entry.Rank;

    public bool IsPinned
    {
        get => isPinned;
        set
        {
            if (Set(ref isPinned, value))
                OnPropertyChanged(nameof(PinTooltip));
        }
    }

    public string PinTooltip => IsPinned ? "Unpin (compare against the best score again)" : "Pin: compare against this replay";

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
