namespace LazerReplayCompare;

/// <summary>
/// Gives the memory of a finished burst of work back once the app has gone quiet.
/// <para>
/// A replay decode, a timeline build or an analysis allocates hundreds of megabytes that stay alive while the work
/// runs, so they are promoted past the young generations. Left to itself the runtime's GC collects that older
/// generation only when its (large, machine-sized) budget is spent or physical memory runs short: the process was
/// seen growing past 9 GB of dead objects over an evening of plays. A full compacting collection a few seconds after
/// the last burst keeps the process near its live size (a few hundred megabytes) and takes well under a second.
/// </para>
/// </summary>
public static class IdleCollector
{
    private static readonly TimeSpan quiet = TimeSpan.FromSeconds(3);
    private static readonly Timer timer = new(_ => collect());

    /// <summary>A burst of work has ended: collect once nothing else has ended for a few seconds.</summary>
    public static void AfterHeavyWork()
    {
        try
        {
            timer.Change(quiet, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // closing
        }
    }

    private static void collect()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            InternalLogger.Log(ex);
        }
    }
}
