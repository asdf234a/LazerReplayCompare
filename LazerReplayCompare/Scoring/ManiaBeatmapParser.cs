using System.Globalization;

namespace LazerReplayCompare;

internal static class ManiaBeatmapParser
{
    public static ManiaBeatmap Parse(string beatmapPath)
    {
        var mode = 0;
        var columns = 4;
        var overallDifficulty = 5d;
        var notes = new List<ManiaNote>();
        var section = string.Empty;

        foreach (var rawLine in File.ReadLines(beatmapPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                section = line;
                continue;
            }

            if (section == "[General]")
            {
                if (TryReadKeyValue(line, "Mode", out var value))
                    mode = ParseInt(value, mode);
            }
            else if (section == "[Difficulty]")
            {
                if (TryReadKeyValue(line, "CircleSize", out var value))
                    columns = Math.Max(1, ParseInt(value, columns));
                else if (TryReadKeyValue(line, "OverallDifficulty", out value))
                    overallDifficulty = ParseDouble(value, overallDifficulty);
            }
            else if (section == "[HitObjects]")
            {
                var parts = line.Split(',');
                if (parts.Length < 5)
                    continue;

                var x = ParseInt(parts[0], 0);
                var time = ParseDouble(parts[2], 0);
                var type = ParseInt(parts[3], 0);
                var isNote = (type & 1) != 0;
                var isHold = (type & 128) != 0;
                if (!isNote && !isHold)
                    continue;

                double? endTime = null;
                if (isHold && parts.Length > 5)
                    endTime = ParseDouble(parts[5].Split(':')[0], time);

                var column = Math.Clamp((int)Math.Floor(x * columns / 512d), 0, columns - 1);
                notes.Add(new ManiaNote(column, time, endTime));
            }
        }

        var beatmap = new ManiaBeatmap
        {
            Mode = mode,
            Columns = columns,
            OverallDifficulty = overallDifficulty,
        };

        beatmap.Notes.AddRange(notes.OrderBy(n => n.StartTime).ThenBy(n => n.Column));
        return beatmap;
    }

    private static bool TryReadKeyValue(string line, string key, out string value)
    {
        value = string.Empty;
        var prefix = key + ":";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        value = line[prefix.Length..].Trim();
        return true;
    }

    private static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    private static double ParseDouble(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }
}
