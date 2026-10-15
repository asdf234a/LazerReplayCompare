using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

/// <param name="BrushKey">Theme brush of the dot in front of the label (none when null).</param>
public sealed record HoverRow(string Label, string Value, string? BrushKey = null, bool Strong = false);

public sealed record HoverInfo(string Title, IReadOnlyList<HoverRow> Rows)
{
    public bool SameAs(HoverInfo? other) => other != null && other.Title == Title && other.Rows.SequenceEqual(Rows);
}

/// <summary>
/// The card that shows what is under the pointer, immediately and next to it (instead of a tooltip's delay). One per
/// window, on top of everything; charts find it through <see cref="Show"/> and <see cref="Hide"/>.
/// </summary>
public sealed class HoverCardLayer : Canvas
{
    private static readonly ConditionalWeakTable<TopLevel, HoverCardLayer> layers = new();

    private readonly Border card;
    private readonly TextBlock title;
    private readonly StackPanel rows;
    private HoverInfo? shown;

    public HoverCardLayer()
    {
        IsHitTestVisible = false;
        title = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold };
        rows = new StackPanel { Spacing = 3 };
        card = new Border
        {
            Child = new StackPanel { Spacing = 6, Children = { title, rows } },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            BorderThickness = new Thickness(1),
            MinWidth = 120,
            BoxShadow = BoxShadows.Parse("0 6 18 0 #40000000"),
            IsVisible = false,
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("HoverCardBrush"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("HoverCardBorderBrush"));
        Children.Add(card);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
            layers.AddOrUpdate(top, this);
    }

    /// <summary>Shows <paramref name="info"/> next to <paramref name="point"/> (in <paramref name="source"/>'s coordinates).</summary>
    public static void Show(Visual source, Point point, HoverInfo info)
    {
        if (TopLevel.GetTopLevel(source) is { } top && layers.TryGetValue(top, out var layer))
            layer.show(source, point, info);
    }

    public static void Hide(Visual source)
    {
        if (TopLevel.GetTopLevel(source) is { } top && layers.TryGetValue(top, out var layer))
            layer.hide();
    }

    private void show(Visual source, Point point, HoverInfo info)
    {
        if (!info.SameAs(shown))
        {
            shown = info;
            title.Text = info.Title;
            rows.Children.Clear();
            foreach (var row in info.Rows)
                rows.Children.Add(rowFor(row));
        }

        card.IsVisible = true;
        card.Measure(Size.Infinity);
        var size = card.DesiredSize;
        var at = source.TranslatePoint(point, this) ?? point;
        double x = at.X + 14, y = at.Y + 16;
        if (x + size.Width > Bounds.Width - 6) x = at.X - 14 - size.Width;
        if (y + size.Height > Bounds.Height - 6) y = at.Y - 12 - size.Height;
        SetLeft(card, Math.Max(6, x));
        SetTop(card, Math.Max(6, y));
    }

    private void hide()
    {
        card.IsVisible = false;
        shown = null;
    }

    private Control rowFor(HoverRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
        if (row.BrushKey != null)
        {
            var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable(row.BrushKey));
            grid.Children.Add(dot);
        }

        var label = new TextBlock { Text = row.Label, FontSize = 12 };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("TextSecondaryBrush"));
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var value = new TextBlock
        {
            Text = row.Value,
            FontSize = 12,
            FontWeight = row.Strong ? FontWeight.Bold : FontWeight.SemiBold,
            FontFeatures = FontFeatureCollection.Parse("+tnum"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(14, 0, 0, 0),
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }
}

/// <summary>Judgement names as osu! shows them (MAX, 300, …), for every judgement in the app.</summary>
public static class JudgementText
{
    public static string Name(HitResult result) => result switch
    {
        HitResult.Perfect => "MAX",
        HitResult.Great => "300",
        HitResult.Good => "200",
        HitResult.Ok => "100",
        HitResult.Meh => "50",
        HitResult.Miss => "Miss",
        _ => result.ToString(),
    };

    public static string Name(string key) => Enum.TryParse<HitResult>(key, out var result) ? Name(result) : key;

    public static string BrushKey(HitResult result) => $"Judgement{result}Brush";

    public static string Ms(double value, string format = "0") => (value > 0 ? "+" : value < 0 ? "−" : "±") + Math.Abs(value).ToString(format, CultureInfo.InvariantCulture) + " ms";

    public static string Clock(double ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}

/// <summary>Custom-drawn chart: theme brushes, text, and the hover card.</summary>
public abstract class ChartControl : Control
{
    protected ChartControl()
    {
        ResourcesChanged += (_, _) => InvalidateVisual();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Gray;

    protected static IBrush Faded(IBrush brush, double opacity)
        => brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    protected FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, weight), size, brush);

    /// <summary>Hit-testable everywhere, not only where something is drawn.</summary>
    protected void FillHitArea(DrawingContext context) => context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (HoverAt(point) is { } info)
            HoverCardLayer.Show(this, point, info);
        else
            HoverCardLayer.Hide(this);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoverCardLayer.Hide(this);
        ClearHover();
    }

    protected abstract HoverInfo? HoverAt(Point point);

    protected virtual void ClearHover()
    {
    }
}

/// <summary>
/// Hit error histogram: bars of judgement-coloured samples, with hold-note samples judged unlike a note at the same
/// error stacked on top in their own colour. Optional zero line, dashed mean marker and tick labels.
/// </summary>
public sealed class HitHistogramChart : ChartControl
{
    public static readonly StyledProperty<IReadOnlyList<HistogramBin>?> BinsProperty =
        AvaloniaProperty.Register<HitHistogramChart, IReadOnlyList<HistogramBin>?>(nameof(Bins));

    public static readonly StyledProperty<double?> MeanProperty =
        AvaloniaProperty.Register<HitHistogramChart, double?>(nameof(Mean));

    public static readonly StyledProperty<string?> MeanLabelProperty =
        AvaloniaProperty.Register<HitHistogramChart, string?>(nameof(MeanLabel));

    public static readonly StyledProperty<IReadOnlyList<double>?> TicksProperty =
        AvaloniaProperty.Register<HitHistogramChart, IReadOnlyList<double>?>(nameof(Ticks));

    public static readonly StyledProperty<string> StackedLabelProperty =
        AvaloniaProperty.Register<HitHistogramChart, string>(nameof(StackedLabel), "Long note");

    private const double axis_height = 20;
    private int hovered = -1;

    static HitHistogramChart()
    {
        AffectsRender<HitHistogramChart>(BinsProperty, MeanProperty, MeanLabelProperty, TicksProperty);
    }

    public IReadOnlyList<HistogramBin>? Bins
    {
        get => GetValue(BinsProperty);
        set => SetValue(BinsProperty, value);
    }

    public double? Mean
    {
        get => GetValue(MeanProperty);
        set => SetValue(MeanProperty, value);
    }

    public string? MeanLabel
    {
        get => GetValue(MeanLabelProperty);
        set => SetValue(MeanLabelProperty, value);
    }

    public IReadOnlyList<double>? Ticks
    {
        get => GetValue(TicksProperty);
        set => SetValue(TicksProperty, value);
    }

    /// <summary>What the stacked parts are called in the hover card.</summary>
    public string StackedLabel
    {
        get => GetValue(StackedLabelProperty);
        set => SetValue(StackedLabelProperty, value);
    }

    private double top => Mean != null ? 22 : 4;

    private Rect plot => new(0, top, Bounds.Width, Math.Max(0, Bounds.Height - top - axis_height));

    public override void Render(DrawingContext context)
    {
        FillHitArea(context);
        var bins = Bins;
        if (bins == null || bins.Count == 0 || Bounds.Width <= 0)
            return;

        var area = plot;
        double slot = area.Width / bins.Count;
        double width = Math.Max(1, slot - (slot >= 3 ? 1 : 0));
        int max = Math.Max(1, bins.Max(b => b.Total));

        if (hovered >= 0 && hovered < bins.Count)
            context.FillRectangle(Brush("RowHoverBrush"), new Rect(hovered * slot, area.Top, slot, area.Height));

        for (int i = 0; i < bins.Count; i++)
        {
            var bin = bins[i];
            int total = bin.Total;
            if (total == 0)
                continue;

            var parts = bin.Base.Concat(bin.Stacked).ToList();
            double height = Math.Max(2, total / (double)max * area.Height);
            double available = Math.Max(0, height - (parts.Count - 1));
            double x = i * slot, y = area.Bottom;
            for (int p = 0; p < parts.Count; p++)
            {
                double h = Math.Max(2, parts[p].Count / (double)total * available);
                var rect = new Rect(x, y - h, width, h);
                var brush = Brush(JudgementText.BrushKey(parts[p].Result));
                if (p == parts.Count - 1 && width >= 4)
                    context.DrawRectangle(brush, null, new RoundedRect(rect, 2, 2, 0, 0));
                else
                    context.FillRectangle(brush, rect);
                y -= h + 1;
            }
        }

        double from = bins[0].From, span = bins[^1].To - from;
        double xOf(double value) => (value - from) / span * area.Width;

        if (from < 0 && from + span > 0)
            context.FillRectangle(Brush("ChartZeroBrush"), new Rect(Math.Round(xOf(0)), area.Top, 1, area.Height));

        if (Mean is double mean)
        {
            var meanBrush = Brush("TargetLabelBrush");
            double x = xOf(mean);
            context.DrawLine(new Pen(meanBrush, 2, new DashStyle(new double[] { 2, 2 }, 0)), new Point(x, 0), new Point(x, area.Bottom));
            if (!string.IsNullOrEmpty(MeanLabel))
            {
                var label = Text(MeanLabel, 12, meanBrush, FontWeight.SemiBold);
                double lx = x + 6 + label.Width > area.Width ? x - 6 - label.Width : x + 6;
                context.DrawText(label, new Point(lx, 2));
            }
        }

        context.FillRectangle(Brush("ChartGridBrush"), new Rect(0, area.Bottom, area.Width, 1));
        if (Ticks is { } ticks)
        {
            var muted = Brush("TextMutedBrush");
            foreach (var tick in ticks)
            {
                var text = Text(tick == 0 ? "0" : (tick > 0 ? "+" : "−") + Math.Abs(tick).ToString(CultureInfo.InvariantCulture), 11, muted);
                double x = Math.Clamp(xOf(tick) - text.Width / 2, 0, area.Width - text.Width);
                context.DrawText(text, new Point(x, area.Bottom + 4));
            }
        }
    }

    protected override HoverInfo? HoverAt(Point point)
    {
        var bins = Bins;
        if (bins == null || bins.Count == 0 || point.Y > plot.Bottom)
        {
            setHovered(-1);
            return null;
        }

        int index = Math.Clamp((int)(point.X / (plot.Width / bins.Count)), 0, bins.Count - 1);
        setHovered(index);
        var bin = bins[index];

        double width = bin.To - bin.From;
        string title = width <= 5
            ? JudgementText.Ms((bin.From + bin.To) / 2)
            : $"{signed(bin.From)} to {signed(bin.To)} ms";

        var rows = new List<HoverRow>();
        foreach (var (result, count) in bin.Base)
            rows.Add(new HoverRow(JudgementText.Name(result), count.ToString("N0", CultureInfo.InvariantCulture), JudgementText.BrushKey(result)));
        foreach (var (result, count) in bin.Stacked)
            rows.Add(new HoverRow($"{StackedLabel} · {JudgementText.Name(result)}", count.ToString("N0", CultureInfo.InvariantCulture), JudgementText.BrushKey(result)));
        if (rows.Count != 1)
            rows.Add(new HoverRow("Total", bin.Total.ToString("N0", CultureInfo.InvariantCulture), Strong: true));

        return new HoverInfo(title, rows);

        static string signed(double v) => v == 0 ? "0" : (v > 0 ? "+" : "−") + Math.Abs(v).ToString("0.#", CultureInfo.InvariantCulture);
    }

    protected override void ClearHover() => setHovered(-1);

    private void setHovered(int index)
    {
        if (hovered == index)
            return;

        hovered = index;
        InvalidateVisual();
    }
}

/// <param name="Kind">Shown in the hover card (Note, Long note head, Release).</param>
public sealed record ScatterPoint(double Time, double Error, int Column, HitResult Result, string Kind);

/// <summary>A tick on the chart's top edge: an object with no time to plot (never pressed, held past its end).</summary>
public sealed record ScatterMark(double Time, int Column, HitResult Result, string Label);

public sealed record WindowEdge(double Value, HitResult Result);

/// <summary>
/// Every press (or release) as a dot over the song: time across, error up (late) and down (early), coloured by its
/// judgement, with the judgement windows as lines. One column can be highlighted.
/// </summary>
public sealed class HitScatterChart : ChartControl
{
    public static readonly StyledProperty<IReadOnlyList<ScatterPoint>?> PointsProperty =
        AvaloniaProperty.Register<HitScatterChart, IReadOnlyList<ScatterPoint>?>(nameof(Points));

    public static readonly StyledProperty<IReadOnlyList<ScatterMark>?> MarksProperty =
        AvaloniaProperty.Register<HitScatterChart, IReadOnlyList<ScatterMark>?>(nameof(Marks));

    public static readonly StyledProperty<IReadOnlyList<WindowEdge>?> EdgesProperty =
        AvaloniaProperty.Register<HitScatterChart, IReadOnlyList<WindowEdge>?>(nameof(Edges));

    public static readonly StyledProperty<double> RangeProperty =
        AvaloniaProperty.Register<HitScatterChart, double>(nameof(Range), 180);

    public static readonly StyledProperty<double> LengthProperty =
        AvaloniaProperty.Register<HitScatterChart, double>(nameof(Length), 1);

    public static readonly StyledProperty<int> HighlightColumnProperty =
        AvaloniaProperty.Register<HitScatterChart, int>(nameof(HighlightColumn), -1);

    private const double axis_height = 22;
    private ScatterPoint? hoveredPoint;

    static HitScatterChart()
    {
        AffectsRender<HitScatterChart>(PointsProperty, MarksProperty, EdgesProperty, RangeProperty, LengthProperty, HighlightColumnProperty);
    }

    public IReadOnlyList<ScatterPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IReadOnlyList<ScatterMark>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    public IReadOnlyList<WindowEdge>? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    /// <summary>The error at the top and bottom edges (ms).</summary>
    public double Range
    {
        get => GetValue(RangeProperty);
        set => SetValue(RangeProperty, value);
    }

    /// <summary>Song length (ms): the right edge.</summary>
    public double Length
    {
        get => GetValue(LengthProperty);
        set => SetValue(LengthProperty, value);
    }

    /// <summary>Column drawn at full strength while the others fade (-1: all).</summary>
    public int HighlightColumn
    {
        get => GetValue(HighlightColumnProperty);
        set => SetValue(HighlightColumnProperty, value);
    }

    private Rect plot => new(0, 0, Bounds.Width, Math.Max(0, Bounds.Height - axis_height));

    private double xOf(double time) => Math.Clamp(time / Math.Max(1, Length), 0, 1) * plot.Width;

    private double yOf(double error)
    {
        double range = Math.Max(1, Range);
        return (range - Math.Clamp(error, -range + 2, range - 2)) / (2 * range) * plot.Height;
    }

    public override void Render(DrawingContext context)
    {
        FillHitArea(context);
        var area = plot;
        if (area.Width <= 0 || area.Height <= 0)
            return;

        var frame = new RoundedRect(area, 10);
        context.DrawRectangle(Brush("ChartBackgroundBrush"), new Pen(Brush("SurfaceBorderBrush")), frame);

        using (context.PushClip(frame))
        {
            foreach (var edge in Edges ?? Array.Empty<WindowEdge>())
                context.FillRectangle(Faded(Brush(JudgementText.BrushKey(edge.Result)), 0.45), new Rect(0, Math.Round(yOf(edge.Value)), area.Width, 1));

            context.DrawLine(new Pen(Brush("ChartZeroBrush"), 1, new DashStyle(new double[] { 3, 3 }, 0)), new Point(0, yOf(0)), new Point(area.Width, yOf(0)));

            int highlight = HighlightColumn;
            var brushes = new Dictionary<(HitResult, bool), IBrush>();
            IBrush brushFor(HitResult result, bool strong)
            {
                if (!brushes.TryGetValue((result, strong), out var brush))
                    brushes[(result, strong)] = brush = Faded(Brush(JudgementText.BrushKey(result)), strong ? 0.9 : 0.12);
                return brush;
            }

            foreach (var point in Points ?? Array.Empty<ScatterPoint>())
            {
                bool strong = highlight < 0 || point.Column == highlight;
                context.FillRectangle(brushFor(point.Result, strong), new Rect(xOf(point.Time) - 2, yOf(point.Error) - 2, 4, 4));
            }

            foreach (var mark in Marks ?? Array.Empty<ScatterMark>())
            {
                bool strong = highlight < 0 || mark.Column == highlight;
                context.FillRectangle(brushFor(mark.Result, strong), new Rect(xOf(mark.Time) - 1.5, 0, 3, 12));
            }

            if (hoveredPoint is { } hovered)
                context.DrawEllipse(null, new Pen(Brush("TextBrush"), 1.5), new Point(xOf(hovered.Time), yOf(hovered.Error)), 5, 5);

            string range = Math.Round(Range).ToString(CultureInfo.InvariantCulture);
            context.DrawText(Text($"Late (+{range} ms)", 12, Brush("LateBrush"), FontWeight.SemiBold), new Point(12, 14));
            var early = Text($"Early (−{range} ms)", 12, Brush("EarlyBrush"), FontWeight.SemiBold);
            context.DrawText(early, new Point(12, area.Height - early.Height - 8));
        }

        // time axis
        var muted = Brush("TextMutedBrush");
        for (int i = 0; i <= 6; i++)
        {
            var text = Text(JudgementText.Clock(Length * i / 6), 11, muted);
            double x = i == 0 ? 0 : i == 6 ? area.Width - text.Width : area.Width * i / 6 - text.Width / 2;
            context.DrawText(text, new Point(x, area.Bottom + 6));
        }
    }

    protected override HoverInfo? HoverAt(Point point)
    {
        var area = plot;
        int highlight = HighlightColumn;
        HoverInfo? info = null;
        ScatterPoint? nearest = null;

        if (point.Y <= area.Bottom)
        {
            if (point.Y <= 16 && Marks is { } marks)
            {
                var mark = marks.Where(m => highlight < 0 || m.Column == highlight)
                                .Select(m => (Mark: m, Distance: Math.Abs(xOf(m.Time) - point.X)))
                                .Where(m => m.Distance <= 4).OrderBy(m => m.Distance).Select(m => m.Mark).FirstOrDefault();
                if (mark != null)
                {
                    info = new HoverInfo($"{clock(mark.Time)} · column {mark.Column + 1}", new[]
                    {
                        new HoverRow(mark.Label, JudgementText.Name(mark.Result), JudgementText.BrushKey(mark.Result)),
                    });
                }
            }

            if (info == null && Points is { Count: > 0 } points)
            {
                // points are in time order: only look at the few around the pointer
                double timeAt(double x) => x / Math.Max(1, area.Width) * Length;
                int start = lowerBound(points, timeAt(point.X - 6));
                double best = 6 * 6;
                for (int i = start; i < points.Count && xOf(points[i].Time) <= point.X + 6; i++)
                {
                    var p = points[i];
                    if (highlight >= 0 && p.Column != highlight) continue;
                    double dx = xOf(p.Time) - point.X, dy = yOf(p.Error) - point.Y, distance = dx * dx + dy * dy;
                    if (distance <= best)
                    {
                        best = distance;
                        nearest = p;
                    }
                }

                if (nearest != null)
                {
                    info = new HoverInfo($"{clock(nearest.Time)} · column {nearest.Column + 1}", new[]
                    {
                        new HoverRow(nearest.Kind, JudgementText.Ms(nearest.Error, "0.#")),
                        new HoverRow("Judgement", JudgementText.Name(nearest.Result), JudgementText.BrushKey(nearest.Result)),
                    });
                }
            }
        }

        if (!ReferenceEquals(nearest, hoveredPoint))
        {
            hoveredPoint = nearest;
            InvalidateVisual();
        }

        return info;
    }

    protected override void ClearHover()
    {
        if (hoveredPoint == null)
            return;

        hoveredPoint = null;
        InvalidateVisual();
    }

    private static string clock(double ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }

    private static int lowerBound(IReadOnlyList<ScatterPoint> points, double time)
    {
        int lo = 0, hi = points.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}

public sealed record BarSegment(double Value, string BrushKey, string Label, string ValueText);

/// <summary>A thin bar split into parts (early / on time / late, judgement shares), each with its own hover card.</summary>
public sealed class SegmentBar : ChartControl
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentBar, IReadOnlyList<BarSegment>?>(nameof(Segments));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SegmentBar, string?>(nameof(Title));

    static SegmentBar()
    {
        AffectsRender<SegmentBar>(SegmentsProperty);
    }

    public IReadOnlyList<BarSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Hover card title (the segment's label when null).</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private IEnumerable<(BarSegment Segment, Rect Rect)> layout()
    {
        var segments = (Segments ?? Array.Empty<BarSegment>()).Where(s => s.Value > 0).ToList();
        double total = segments.Sum(s => s.Value);
        if (total <= 0)
            yield break;

        double available = Bounds.Width - 2 * (segments.Count - 1), x = 0;
        foreach (var segment in segments)
        {
            double width = Math.Max(2, segment.Value / total * available);
            yield return (segment, new Rect(x, 0, width, Bounds.Height));
            x += width + 2;
        }
    }

    public override void Render(DrawingContext context)
    {
        FillHitArea(context);
        using (context.PushClip(new RoundedRect(new Rect(Bounds.Size), Bounds.Height / 2)))
        {
            foreach (var (segment, rect) in layout())
                context.FillRectangle(Brush(segment.BrushKey), rect);
        }
    }

    protected override HoverInfo? HoverAt(Point point)
    {
        foreach (var (segment, rect) in layout())
        {
            if (point.X >= rect.Left && point.X <= rect.Right + 2)
                return new HoverInfo(Title ?? segment.Label, new[] { new HoverRow(segment.Label, segment.ValueText, segment.BrushKey) });
        }

        return null;
    }
}

/// <summary>Judgement counts next to a ring: dot, name and count for each, in two columns.</summary>
public sealed class JudgementLegendPanel : Avalonia.Controls.Primitives.UniformGrid
{
    public static readonly StyledProperty<IReadOnlyList<JudgementSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<JudgementLegendPanel, IReadOnlyList<JudgementSegment>?>(nameof(Segments));

    public JudgementLegendPanel()
    {
        Columns = 2;
    }

    public IReadOnlyList<JudgementSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SegmentsProperty)
            return;

        Children.Clear();
        foreach (var segment in Segments ?? Array.Empty<JudgementSegment>())
        {
            var item = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6, Margin = new Thickness(0, 1, 16, 1) };
            var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable($"Judgement{segment.Key}Brush"));
            item.Children.Add(dot);
            var name = new TextBlock { Text = segment.Label, FontSize = 11, Classes = { "secondary" } };
            Grid.SetColumn(name, 1);
            item.Children.Add(name);
            var count = new TextBlock { Text = segment.CountText, FontSize = 11, Classes = { "mono" }, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(count, 2);
            item.Children.Add(count);
            Children.Add(item);
        }
    }
}

/// <summary>Key of the judgement colours (MAX … Miss); anything placed inside follows it on the same line.</summary>
public sealed class JudgementKeyRow : WrapPanel
{
    public JudgementKeyRow()
    {
        Orientation = Orientation.Horizontal;
        ItemSpacing = 12;
        LineSpacing = 4;
        foreach (var result in ReplayAnalysis.Judgements)
        {
            var swatch = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            swatch.Bind(Border.BackgroundProperty, swatch.GetResourceObservable(JudgementText.BrushKey(result)));
            Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { swatch, new TextBlock { Text = JudgementText.Name(result), FontSize = 12, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center } },
            });
        }
    }
}
