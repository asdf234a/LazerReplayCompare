using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace LazerReplayCompare;

/// <summary>One lane of the details page's Lanes section: a column with its accuracy bar, judgement strip and timing.</summary>
public sealed class LaneColumn : ObservableObject
{
    /// <summary>Height of the bar's track, in page units.</summary>
    public const double TrackHeight = 120;

    private static readonly CultureInfo invariant = CultureInfo.InvariantCulture;

    private bool isSelected;

    /// <param name="floor">The accuracy (percent) at the bottom of the bars; 100% is the top.</param>
    /// <param name="stable">Accuracies shown the osu!stable way (rounded; osu!lazer cuts them off).</param>
    public LaneColumn(LaneStat lane, double floor, bool stable)
    {
        Column = lane.Lane;
        NumberText = (lane.Lane + 1).ToString(invariant);
        HandText = lane.LeftHand ? "L" : "R";
        AccuracyText = lane.Accuracy is double accuracy ? ShownAccuracy.Text(accuracy / 100, stable) : "—";
        BarHeight = lane.Accuracy is double a ? Math.Max(2, Math.Clamp((a - floor) / Math.Max(1e-9, 100 - floor), 0, 1) * TrackHeight) : 0;
        HasMisses = lane.Misses > 0;
        MissText = lane.Misses.ToString("N0", invariant);

        double total = Math.Max(1, lane.Objects);
        Segments = ReplayAnalysis.Judgements
            .Select(r => new BarSegment(lane.Counts.GetValueOrDefault(r), JudgementText.BrushKey(r), JudgementText.Name(r),
                FormattableString.Invariant($"{lane.Counts.GetValueOrDefault(r):N0} ({(lane.Counts.GetValueOrDefault(r) / total * 100).ToString("0.0", invariant)}%)")))
            .ToList();
        Title = FormattableString.Invariant($"Lane {NumberText} · {lane.Objects:N0} judgements");

        // (the sign alone: the caption says ms, and a lane is narrow)
        MeanText = lane.Mean is double mean ? (mean > 0 ? "+" : mean < 0 ? "−" : "±") + Math.Abs(mean).ToString("0.0", invariant) : "—";
        IsEarly = lane.Mean <= -near_zero;
        IsLate = lane.Mean >= near_zero;
        UnstableRateText = lane.UnstableRate is double ur ? ur.ToString("0", invariant) : "—";

        string hand = lane.LeftHand ? "left hand" : "right hand";
        Tip = FormattableString.Invariant($"Lane {NumberText} ({hand}) · {AccuracyText}\n")
              + string.Join(" · ", ReplayAnalysis.Judgements.Select(r => FormattableString.Invariant($"{JudgementText.Name(r)} {lane.Counts.GetValueOrDefault(r):N0}")))
              + (lane.Mean is double tipMean ? FormattableString.Invariant($"\nMean {JudgementText.Ms(tipMean, "0.0")} · UR {UnstableRateText} · {lane.Presses:N0} presses") : string.Empty)
              + "\nClick to highlight the lane in Over the song";
    }

    /// <summary>Below this many ms off 0 a lane's mean is neither early nor late (as "on time" on the page).</summary>
    private const double near_zero = 0.5;

    public int Column { get; }
    public string NumberText { get; }
    public string HandText { get; }
    public string AccuracyText { get; }
    public double BarHeight { get; }
    public bool HasMisses { get; }
    public string MissText { get; }
    public IReadOnlyList<BarSegment> Segments { get; }
    public string Title { get; }
    public string MeanText { get; }
    public bool IsEarly { get; }
    public bool IsLate { get; }
    public string UnstableRateText { get; }
    public string Tip { get; }

    /// <summary>The lane highlighted in Over the song.</summary>
    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }
}

/// <summary>
/// Lays lanes out side by side in equal columns, left to right, no wider than <see cref="MaxItemWidth"/> each. When
/// they would get narrower than <see cref="MinItemWidth"/> (many keys in a narrow page) they go on as many rows as
/// it takes, split evenly.
/// </summary>
public sealed class LanePanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<LanePanel, double>(nameof(MinItemWidth), 62);

    public static readonly StyledProperty<double> MaxItemWidthProperty =
        AvaloniaProperty.Register<LanePanel, double>(nameof(MaxItemWidth), 108);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<LanePanel, double>(nameof(Spacing), 6);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<LanePanel, double>(nameof(RowSpacing), 14);

    static LanePanel()
    {
        AffectsMeasure<LanePanel>(MinItemWidthProperty, MaxItemWidthProperty, SpacingProperty, RowSpacingProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double MaxItemWidth
    {
        get => GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>How many lanes go on a row of <paramref name="available"/> width, and how wide each is.</summary>
    private (int PerRow, double Width) layout(double available)
    {
        int count = Children.Count;
        if (count == 0)
            return (1, 0);
        if (double.IsInfinity(available))
            return (count, MaxItemWidth);

        for (int rows = 1; ; rows++)
        {
            int perRow = (count + rows - 1) / rows;
            double width = (available - Spacing * (perRow - 1)) / perRow;
            if (width >= MinItemWidth || perRow == 1)
                return (perRow, Math.Clamp(width, 0, MaxItemWidth));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (perRow, width) = layout(availableSize.Width);
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        int rows = (Children.Count + perRow - 1) / perRow;
        return Children.Count == 0
            ? default
            : new Size(Math.Min(perRow, Children.Count) * width + Spacing * (Math.Min(perRow, Children.Count) - 1), rows * height + RowSpacing * (rows - 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (perRow, width) = layout(finalSize.Width);
        double height = Children.Count == 0 ? 0 : Children.Max(c => c.DesiredSize.Height);
        for (int i = 0; i < Children.Count; i++)
            Children[i].Arrange(new Rect(i % perRow * (width + Spacing), i / perRow * (height + RowSpacing), width, height));

        return finalSize;
    }
}
