using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace LazerReplayCompare;

/// <summary>
/// The window's short movements: a page arriving, a number rolling to its new value, a bar filling. Each runs on a
/// timer and eases out; a newer one on the same thing (and channel) takes over from the one still running.
/// </summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<object, Dictionary<string, int>> versions = new();

    /// <summary>Calls <paramref name="apply"/> with 0 now and then with eased progress up to 1.</summary>
    /// <param name="delay">Milliseconds before it starts moving (it stays at 0 meanwhile).</param>
    public static void Run(object owner, string channel, int milliseconds, Action<double> apply, Action? done = null, int delay = 0)
    {
        var table = versions.GetOrCreateValue(owner);
        int version = table[channel] = table.GetValueOrDefault(channel) + 1;

        var watch = Stopwatch.StartNew();
        apply(0);

        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            if (table[channel] != version)
            {
                timer.Stop();
                return;
            }

            double t = Math.Clamp((watch.ElapsedMilliseconds - delay) / (double)Math.Max(milliseconds, 1), 0, 1);
            try
            {
                apply(1 - Math.Pow(1 - t, 3));
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
                timer.Stop();
                return;
            }

            if (t >= 1)
            {
                timer.Stop();
                done?.Invoke();
            }
        };
        timer.Start();
    }

    /// <summary>Something arrives: it fades in while it settles into place from a little away and a little smaller (or larger).</summary>
    /// <param name="offsetY">Where it starts, in pixels below (positive) or above its place.</param>
    public static void Enter(Control control, double offsetY = 20, double scale = 1, int milliseconds = 240, double fromOpacity = 0, int delay = 0)
    {
        control.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
        Run(control, "enter", milliseconds, t =>
        {
            control.Opacity = fromOpacity + (1 - fromOpacity) * t;
            double size = scale + (1 - scale) * t;
            control.RenderTransform = t >= 1 ? null : new MatrixTransform(Matrix.CreateScale(size, size) * Matrix.CreateTranslation(0, offsetY * (1 - t)));
        }, delay: delay);
    }

    public static Color Mix(Color a, Color b, double t)
    {
        static byte part(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(part(a.A, b.A, t), part(a.R, b.R, t), part(a.G, b.G, t), part(a.B, b.B, t));
    }

    /// <summary>
    /// Scrolls a list so the row at <paramref name="index"/> is in its middle. The rows are all one height, so the place
    /// is worked out from the index (a row far away has no container to ask yet); it is set once the list has laid out
    /// its new rows, and once more a moment later in case it was still settling.
    /// </summary>
    public static void CentreRow(ListBox list, int index, double fallbackRowHeight)
    {
        if (index < 0)
            return;

        void place()
        {
            if (list.Scroll is not ScrollViewer scroll || scroll.Viewport.Height <= 0)
                return;

            double height = list.GetRealizedContainers().FirstOrDefault()?.Bounds.Height is > 0 and var h ? h : fallbackRowHeight;
            double wanted = index * height - (scroll.Viewport.Height - height) / 2;
            double most = Math.Max(0, Math.Max(scroll.Extent.Height, list.ItemCount * height) - scroll.Viewport.Height);
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(wanted, 0, most));
        }

        Dispatcher.UIThread.Post(place, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(place, TimeSpan.FromMilliseconds(180));
    }
}
