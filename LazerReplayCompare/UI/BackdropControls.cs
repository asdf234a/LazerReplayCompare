using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace LazerReplayCompare;

/// <summary>
/// Draws a bitmap cropped to fill its bounds around a fixed focal point (like CSS object-position), so resizing the
/// window only reveals more or less around that point instead of sliding the picture. Fades in when the bitmap changes.
/// </summary>
public sealed class FocalImage : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<FocalImage, Bitmap?>(nameof(Source));

    /// <summary>Where the focal point sits, as a fraction of the image (and of the bounds).</summary>
    public static readonly StyledProperty<double> FocalYProperty =
        AvaloniaProperty.Register<FocalImage, double>(nameof(FocalY), 0.28);

    private static readonly Transitions fade = new() { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) } };

    static FocalImage()
    {
        AffectsRender<FocalImage>(SourceProperty, FocalYProperty);
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double FocalY
    {
        get => GetValue(FocalYProperty);
        set => SetValue(FocalYProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty || change.NewValue == null)
            return;

        Transitions = null;
        Opacity = 0;
        Dispatcher.UIThread.Post(() =>
        {
            Transitions = fade;
            Opacity = 1;
        }, DispatcherPriority.Background);
    }

    public override void Render(DrawingContext context)
    {
        var source = Source;
        var bounds = new Rect(Bounds.Size);
        if (source == null || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var size = source.Size;
        var scale = Math.Max(bounds.Width / size.Width, bounds.Height / size.Height);
        var width = size.Width * scale;
        var height = size.Height * scale;
        var x = (bounds.Width - width) / 2;
        var y = (bounds.Height - height) * Math.Clamp(FocalY, 0, 1);

        // Only the visible part of the bitmap, so nothing is drawn outside the bounds.
        var visible = new Rect(-x / scale, -y / scale, bounds.Width / scale, bounds.Height / scale).Intersect(new Rect(size));
        context.DrawImage(source, visible, bounds);
    }
}

/// <summary>Ring chart of judgement counts, drawn with the window's Judgement{Key}Brush resources.</summary>
public sealed class JudgementDonut : Control
{
    public static readonly StyledProperty<IReadOnlyList<JudgementSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<JudgementDonut, IReadOnlyList<JudgementSegment>?>(nameof(Segments));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<JudgementDonut, double>(nameof(Thickness), 11);

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<JudgementDonut, IBrush?>(nameof(Track));

    static JudgementDonut()
    {
        AffectsRender<JudgementDonut>(SegmentsProperty, ThicknessProperty, TrackProperty);
    }

    public IReadOnlyList<JudgementSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>Repaints with the current theme's brushes.</summary>
    public void Refresh() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        var thickness = Math.Min(Thickness, size / 2);
        var radius = (size - thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (Track != null)
            context.DrawEllipse(null, new Pen(Track, thickness), center, radius, radius);

        var segments = (Segments ?? Array.Empty<JudgementSegment>()).Where(segment => segment.Count > 0).ToList();
        var total = segments.Sum(segment => (double)segment.Count);
        if (total <= 0)
            return;

        // A small gap between segments, unless one judgement fills the whole ring.
        var gap = segments.Count > 1 ? 0.035 : 0;
        var start = -Math.PI / 2;
        foreach (var segment in segments)
        {
            var sweep = segment.Count / total * Math.PI * 2;
            var drawn = Math.Max(sweep - gap, 0.012);
            var brush = this.TryFindResource($"Judgement{segment.Key}Brush", ActualThemeVariant, out var resource) && resource is IBrush found
                ? found
                : Brushes.Gray;
            var pen = new Pen(brush, thickness);

            if (drawn >= Math.PI * 2 - 0.0001)
            {
                context.DrawEllipse(null, pen, center, radius, radius);
            }
            else
            {
                var from = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
                var end = start + drawn;
                var to = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(from, false);
                    path.ArcTo(to, new Size(radius, radius), 0, drawn > Math.PI, SweepDirection.Clockwise);
                    path.EndFigure(false);
                }

                context.DrawGeometry(null, pen, geometry);
            }

            start += sweep;
        }
    }
}

public static class BackdropImage
{
    /// <summary>
    /// A small, heavily blurred copy of <paramref name="source"/> for the window backdrop. Blurring a ~160 px copy and
    /// letting it scale up keeps this cheap, and nothing is re-blurred while the UI redraws.
    /// </summary>
    public static Bitmap? CreateBlurred(Bitmap source, int width = 160, int radius = 5, int passes = 3)
    {
        try
        {
            var height = Math.Max(1, (int)Math.Round(source.Size.Height * width / source.Size.Width));
            using var small = source.CreateScaledBitmap(new PixelSize(width, height), BitmapInterpolationMode.HighQuality);

            var stride = width * 4;
            var pixels = new byte[stride * height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                small.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            var scratch = new byte[pixels.Length];
            for (var pass = 0; pass < passes; pass++)
            {
                boxBlur(pixels, scratch, width, height, radius, horizontal: true);
                boxBlur(scratch, pixels, width, height, radius, horizontal: false);
            }

            var result = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var buffer = result.Lock())
            {
                for (var row = 0; row < height; row++)
                    Marshal.Copy(pixels, row * stride, buffer.Address + row * buffer.RowBytes, stride);
            }

            return result;
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
            return null;
        }
    }

    // Box blur along one axis with clamped edges (4 channels, so the pixel format does not matter).
    private static void boxBlur(byte[] from, byte[] to, int width, int height, int radius, bool horizontal)
    {
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var window = radius * 2 + 1;
        Span<int> sum = stackalloc int[4];

        for (var line = 0; line < lines; line++)
        {
            int index(int i) => horizontal ? (line * width + Math.Clamp(i, 0, length - 1)) * 4 : (Math.Clamp(i, 0, length - 1) * width + line) * 4;

            sum.Clear();
            for (var i = -radius; i <= radius; i++)
            {
                var p = index(i);
                for (var c = 0; c < 4; c++)
                    sum[c] += from[p + c];
            }

            for (var i = 0; i < length; i++)
            {
                var target = index(i);
                for (var c = 0; c < 4; c++)
                    to[target + c] = (byte)(sum[c] / window);

                var add = index(i + radius + 1);
                var remove = index(i - radius);
                for (var c = 0; c < 4; c++)
                    sum[c] += from[add + c] - from[remove + c];
            }
        }
    }
}
