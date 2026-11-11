using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LazerReplayCompare;

/// <summary>
/// Scrolling for a whole window: the wheel glides to where it is going instead of jumping a step, and holding the wheel
/// (middle) button and dragging scrolls on its own, faster the further the pointer is from where it was pressed.
/// </summary>
public static class SmoothScroll
{
    private const double wheel_step = 110;      // pixels a notch of the wheel moves
    private const double glide_time = 0.075;    // seconds in which about two thirds of what is left is covered
    private const double dead_zone = 10;        // pixels around the press where a drag doesn't scroll yet

    private sealed class Glide
    {
        public double Target;
        public double LastSet = double.NaN;
        public bool Running;
        public Stopwatch Watch = new();
    }

    private static readonly ConditionalWeakTable<ScrollViewer, Glide> glides = new();

    public static void Attach(Window window)
    {
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => onWheel(e), RoutingStrategies.Tunnel);

        // the wheel button held and dragged
        ScrollViewer? dragged = null;
        double pressedAt = 0, pointerAt = 0;
        bool dragLoop = false;
        var dragWatch = new Stopwatch();
        Cursor? cursorBefore = null;

        void stopDrag()
        {
            if (dragged == null)
                return;

            dragged = null;
            window.Cursor = cursorBefore;
        }

        window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsMiddleButtonPressed || scrollerAt(e.Source) is not { } scroller)
                return;

            dragged = scroller;
            pressedAt = pointerAt = e.GetPosition(window).Y;
            stop(scroller);
            cursorBefore = window.Cursor;
            window.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            e.Pointer.Capture(window);
            e.Handled = true;

            dragWatch.Restart();
            if (!dragLoop)
            {
                dragLoop = true;
                Motion.EachFrame(window, () => dragLoop = tick());
            }
        }, RoutingStrategies.Tunnel);

        // one frame of the drag; false once the button is up
        bool tick()
        {
            if (dragged == null)
                return false;

            double seconds = Math.Min(dragWatch.Elapsed.TotalSeconds, 0.05);
            dragWatch.Restart();

            double away = pointerAt - pressedAt, past = Math.Abs(away) - dead_zone;
            if (past <= 0)
                return true;

            // a little away is a slow crawl, far away runs through thousands of rows
            double speed = Math.Sign(away) * (past * 9 + past * past * 0.09);
            setOffset(dragged, dragged.Offset.Y + speed * seconds);
            return true;
        }

        window.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (dragged != null)
                pointerAt = e.GetPosition(window).Y;
        }, RoutingStrategies.Tunnel);

        window.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (dragged == null || e.InitialPressMouseButton != MouseButton.Middle)
                return;

            e.Pointer.Capture(null);
            stopDrag();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        window.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => stopDrag(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        window.Deactivated += (_, _) => stopDrag();
    }

    private static void onWheel(PointerWheelEventArgs e)
    {
        // Ctrl + wheel zooms the window; Shift + wheel and sideways wheels are left to the lists
        if (e.Handled || e.Delta.Y == 0 || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        if (scrollerAt(e.Source) is not { } scroller)
            return;

        var glide = glides.GetOrCreateValue(scroller);
        double now = scroller.Offset.Y;

        // not moving, or moved by something else meanwhile (its bar, a key): the glide starts from where it is
        if (!glide.Running || double.IsNaN(glide.LastSet) || Math.Abs(now - glide.LastSet) > 1)
            glide.Target = now;

        glide.Target = Math.Clamp(glide.Target - e.Delta.Y * wheel_step, 0, most(scroller));
        e.Handled = true;

        if (!glide.Running)
        {
            glide.Running = true;
            glide.LastSet = now;
            glide.Watch.Restart();
            Motion.EachFrame(scroller, () => glide.Running && step(scroller, glide));
        }
    }

    /// <summary>One frame of a glide. False when it is over.</summary>
    private static bool step(ScrollViewer scroller, Glide glide)
    {
        double seconds = Math.Min(glide.Watch.Elapsed.TotalSeconds, 0.05);
        glide.Watch.Restart();

        double now = scroller.Offset.Y;
        if (Math.Abs(now - glide.LastSet) > 1)
        {
            // taken elsewhere (the bar was dragged, a row was scrolled to)
            return glide.Running = false;
        }

        glide.Target = Math.Clamp(glide.Target, 0, most(scroller));
        double left = glide.Target - now;
        if (Math.Abs(left) < 0.5)
        {
            setOffset(scroller, glide.Target);
            return glide.Running = false;
        }

        setOffset(scroller, now + left * (1 - Math.Exp(-seconds / glide_time)));
        glide.LastSet = scroller.Offset.Y;
        return true;
    }

    private static void stop(ScrollViewer scroller)
    {
        if (glides.TryGetValue(scroller, out var glide))
            glide.Running = false;
    }

    private static double most(ScrollViewer scroller) => Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);

    private static void setOffset(ScrollViewer scroller, double y)
        => scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(y, 0, most(scroller)));

    /// <summary>The nearest list or page around what the pointer is on that has somewhere to scroll to.</summary>
    private static ScrollViewer? scrollerAt(object? source)
    {
        if (source is not Visual visual)
            return null;

        foreach (var scroller in visual.GetSelfAndVisualAncestors().OfType<ScrollViewer>())
        {
            if (scroller.Extent.Height > scroller.Viewport.Height + 1 && scroller.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                return scroller;
        }

        return null;
    }
}
