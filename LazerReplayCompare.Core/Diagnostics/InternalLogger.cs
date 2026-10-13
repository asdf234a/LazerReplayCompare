using System.Diagnostics;

namespace LazerReplayCompare;

public static class InternalLogger
{
    public static void Log(Exception ex)
    {
        Debug.WriteLine(ex);
    }
}
