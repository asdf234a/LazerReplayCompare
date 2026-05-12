using System.Diagnostics;

namespace LazerReplayCompare;

internal static class InternalLogger
{
    public static void Log(Exception ex)
    {
        Debug.WriteLine(ex);
    }
}
