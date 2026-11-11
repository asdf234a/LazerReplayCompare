using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;

namespace LazerReplayCompare;

/// <summary>
/// The window's short movements: a page arriving, a number rolling to its new value, a bar filling.
///
/// What only moves or fades something already drawn (a page, a row arriving) is handed to the compositor: it runs on
/// the render thread, on the GPU, and stays smooth while the UI thread is busy laying out the page it shows. What has
/// to change the page itself (a number's text, a bar's value, a card's width) is stepped once per drawn frame.
/// </summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<object, Dictionary<string, int>> versions = new();

    /// <summary>Calls <paramref name="apply"/> with 0 now and then, once per frame, with eased progress up to 1.</summary>
    /// <param name="delay">Milliseconds before it starts moving (it stays at 0 meanwhile).</param>
    public static void Run(object owner, string channel, int milliseconds, Action<double> apply, Action? done = null, int delay = 0)
    {
        var table = versions.GetOrCreateValue(owner);
        int version = table[channel] = table.GetValueOrDefault(channel) + 1;

        var watch = Stopwatch.StartNew();
        apply(0);

        EachFrame(owner as Visual, () =>
        {
            if (table[channel] != version)
                return false;   // a newer one took over

            double t = Math.Clamp((watch.ElapsedMilliseconds - delay) / (double)Math.Max(milliseconds, 1), 0, 1);
            try
            {
                apply(1 - Math.Pow(1 - t, 3));
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
                return false;
            }

            if (t < 1)
                return true;

            done?.Invoke();
            return false;
        });
    }

    /// <summary>
    /// Calls <paramref name="step"/> once per frame the window draws, until it returns false. (In step with the screen:
    /// a timer would run now twice in a frame, now not at all, which shows as judder.)
    /// </summary>
    public static void EachFrame(Visual? anchor, Func<bool> step)
    {
        var top = anchor == null ? null : TopLevel.GetTopLevel(anchor);
        if (top == null)
        {
            // not on screen (yet): a timer does
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
            timer.Tick += (_, _) =>
            {
                if (!step())
                    timer.Stop();
            };
            timer.Start();
            return;
        }

        void frame(TimeSpan _)
        {
            if (step())
                top.RequestAnimationFrame(frame);
        }

        top.RequestAnimationFrame(frame);
    }

    /// <summary>Something arrives: it fades in while it settles into place from a little away and a little smaller (or larger).</summary>
    /// <param name="offsetY">Where it starts, in pixels below (positive) or above its place.</param>
    /// <param name="offsetX">And right (positive) or left of it.</param>
    public static void Enter(Control control, double offsetY = 20, double scale = 1, int milliseconds = 240, double fromOpacity = 0, int delay = 0, double offsetX = 0)
    {
        // (whatever an earlier way of moving it left behind)
        control.RenderTransform = null;
        control.Opacity = 1;

        if (ElementComposition.GetElementVisual(control) is not { } visual)
            return;   // not drawn yet: it simply is there

        var compositor = visual.Compositor;
        var easing = new CubicEaseOut();
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var wait = TimeSpan.FromMilliseconds(delay);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, (float)fromOpacity);
        fade.InsertKeyFrame(1f, 1f, easing);
        start(fade, "Opacity");

        var slide = compositor.CreateVector3DKeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3D(offsetX, offsetY, 0));
        slide.InsertKeyFrame(1f, new Vector3D(0, 0, 0), easing);
        start(slide, "Translation");

        if (Math.Abs(scale - 1) > 0.0001)
        {
            visual.CenterPoint = new Vector3D(control.Bounds.Width / 2, 0, 0);
            var grow = compositor.CreateVector3DKeyFrameAnimation();
            grow.InsertKeyFrame(0f, new Vector3D(scale, scale, 1));
            grow.InsertKeyFrame(1f, new Vector3D(1, 1, 1), easing);
            start(grow, "Scale");
        }

        void start(KeyFrameAnimation animation, string property)
        {
            animation.Duration = duration;
            if (delay > 0)
            {
                animation.DelayTime = wait;
                animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            }

            visual.StartAnimation(property, animation);
        }
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
