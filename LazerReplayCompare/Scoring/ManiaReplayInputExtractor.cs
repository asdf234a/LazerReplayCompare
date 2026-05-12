using System.Collections;
using System.Globalization;
using osu.Game.Scoring;

namespace LazerReplayCompare;

public static class ManiaReplayInputExtractor
{
    // Returns per-column ordered list of (pressRealTime, releaseRealTime) pairs.
    // releaseRealTime is +Infinity if still held at end of replay.
    public static (double press, double release)[][] ExtractKeyPairs(Score score, int columns, bool mirrorColumns)
    {
        var events = new List<(double press, double release)>[columns];
        for (var c = 0; c < columns; c++)
            events[c] = new List<(double, double)>();

        var pressStart = new double?[columns];
        var previous = new bool[columns];

        foreach (var frame in score.Replay.Frames.OrderBy(f => f.Time))
        {
            var pressed = GetPressedColumns(frame, columns, mirrorColumns);
            for (var c = 0; c < columns; c++)
            {
                if (pressed[c] && !previous[c])
                    pressStart[c] = frame.Time;
                else if (!pressed[c] && previous[c] && pressStart[c].HasValue)
                {
                    events[c].Add((pressStart[c]!.Value, frame.Time));
                    pressStart[c] = null;
                }
            }

            previous = pressed;
        }

        for (var c = 0; c < columns; c++)
        {
            if (pressStart[c].HasValue)
                events[c].Add((pressStart[c]!.Value, double.PositiveInfinity));
        }

        return events.Select(e => e.ToArray()).ToArray();
    }

    private static bool[] GetPressedColumns(object frame, int columns, bool mirrorColumns)
    {
        var pressed = new bool[columns];
        var actions = frame.GetType().GetField("Actions")?.GetValue(frame) as IEnumerable;
        if (actions == null)
            return pressed;

        foreach (var action in actions)
        {
            var name = action?.ToString() ?? string.Empty;
            if (!name.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!int.TryParse(name[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var oneBasedColumn))
                continue;

            var col = oneBasedColumn - 1;
            if (mirrorColumns)
                col = columns - 1 - col;
            if (col >= 0 && col < columns)
                pressed[col] = true;
        }

        return pressed;
    }
}
