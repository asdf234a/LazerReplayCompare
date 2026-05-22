namespace LazerReplayCompare;

internal static class ManiaInputEventBuilder
{
    public static List<RawInputEvent> CreateRawInputEvents((double press, double release)[][] keyPairs)
    {
        var events = new List<RawInputEvent>();
        var eventId = 0;
        var pairId = 0;

        for (var c = 0; c < keyPairs.Length; c++)
        {
            foreach (var pair in keyPairs[c])
            {
                pairId++;
                events.Add(new RawInputEvent(++eventId, pair.press, c, true, pairId));
                if (!double.IsPositiveInfinity(pair.release))
                    events.Add(new RawInputEvent(++eventId, pair.release, c, false, pairId));
            }
        }

        return events
            .OrderBy(e => e.Time)
            .ThenBy(e => e.IsPress ? 1 : 0)
            .ThenBy(e => e.Column)
            .ToList();
    }
}
