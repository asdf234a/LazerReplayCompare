using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace LazerReplayCompare;

/// <summary>
/// Loads the backgrounds of a list's map rows while they are on screen, two at a time (so fast scrolling doesn't decode
/// a hundred images), and lets them go when their rows leave.
/// </summary>
public sealed class RowBackgrounds
{
    private static readonly SemaphoreSlim decoding = new(2, 2);

    private readonly HashSet<MapRow> shown = new();
    private readonly Func<MapRow, Bitmap?> get;
    private readonly Action<MapRow, Bitmap?> set;
    private readonly Func<bool> enabled;
    private readonly int width;

    /// <param name="width">The width the images are decoded to, in pixels.</param>
    public RowBackgrounds(ListBox list, Func<MapRow, Bitmap?> get, Action<MapRow, Bitmap?> set, int width, Func<bool> enabled)
    {
        this.get = get;
        this.set = set;
        this.width = width;
        this.enabled = enabled;

        list.ContainerPrepared += (_, e) =>
        {
            if (e.Container is Control container)
                Dispatcher.UIThread.Post(() =>
                {
                    if (container.DataContext is MapRow row)
                        show(row);
                }, DispatcherPriority.Loaded);
        };
        list.ContainerClearing += (_, e) =>
        {
            if (e.Container is Control { DataContext: MapRow row })
                hide(row);
        };
    }

    private void show(MapRow row)
    {
        if (!shown.Add(row) || get(row) != null)
            return;

        var path = row.Map.BackgroundPath;
        if (string.IsNullOrWhiteSpace(path) || !enabled())
            return;

        _ = Task.Run(async () =>
        {
            await decoding.WaitAsync();
            Bitmap? bitmap = null;
            try
            {
                // scrolled past meanwhile
                bool wanted = await Dispatcher.UIThread.InvokeAsync(() => shown.Contains(row) && get(row) == null);
                if (!wanted || !File.Exists(path))
                    return;

                await using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
            }
            catch (Exception ex)
            {
                InternalLogger.Log(ex);
            }
            finally
            {
                decoding.Release();
            }

            if (bitmap == null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (shown.Contains(row) && get(row) == null)
                    set(row, bitmap);
                else
                    bitmap.Dispose();
            });
        });
    }

    private void hide(MapRow row)
    {
        shown.Remove(row);
        if (get(row) is { } bitmap)
        {
            set(row, null);
            bitmap.Dispose();
        }
    }
}
