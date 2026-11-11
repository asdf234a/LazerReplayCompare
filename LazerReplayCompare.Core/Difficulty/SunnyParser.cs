using System.Globalization;

namespace LazerReplayCompare;

/// <summary>
/// The .osu reading the Sunny star rating is defined on: a port of osumania_map_analyser's
/// js/parser/osuFileParser.js (MIT, Leo_Black), keeping its quirks (e.g. any line holding
/// "Mode:" sets the ruleset, CircleSize is truncated, x maps to a lane by truncation).
/// </summary>
public sealed class SunnyParser
{
    public double Od = -1;
    public int ColumnCount = -1;
    public readonly List<int> Columns = new();
    public readonly List<double> NoteStarts = new();
    public readonly List<double> NoteEnds = new();
    public readonly List<int> NoteTypes = new();
    /// <summary>"init", "OK", "NotMania" or "Fail"</summary>
    public string Status = "init";
    public readonly List<(double Time, double BeatLength)> TimingPoints = new();

    /// <summary>JavaScript's parseFloat: the longest leading number, NaN when there is none</summary>
    public static double ParseFloat(string s)
    {
        s = s.TrimStart();
        var end = 0;
        if (end < s.Length && s[end] is '+' or '-') end++;
        var digits = false;
        while (end < s.Length && char.IsAsciiDigit(s[end])) { end++; digits = true; }
        if (end < s.Length && s[end] == '.')
        {
            end++;
            while (end < s.Length && char.IsAsciiDigit(s[end])) { end++; digits = true; }
        }
        if (!digits)
        {
            var rest = s[(s.Length > 0 && s[0] is '+' or '-' ? 1 : 0)..];
            if (rest.StartsWith("Infinity", StringComparison.Ordinal)) return s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            return double.NaN;
        }
        if (end < s.Length && s[end] is 'e' or 'E')
        {
            var e = end + 1;
            if (e < s.Length && s[e] is '+' or '-') e++;
            var expDigits = e;
            while (e < s.Length && char.IsAsciiDigit(s[e])) e++;
            if (e > expDigits) end = e;
        }
        return double.Parse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>JavaScript's parseInt(s, 10): the leading integer, NaN when there is none</summary>
    public static double ParseInt(string s)
    {
        s = s.TrimStart();
        var end = 0;
        if (end < s.Length && s[end] is '+' or '-') end++;
        var start = end;
        while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
        if (end == start) return double.NaN;
        return double.Parse(s[..end], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    private static double StringToInt(string value) => Math.Truncate(ParseFloat(value));

    public static SunnyParser Parse(string text)
    {
        var p = new SunnyParser();
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToArray();
        var inTiming = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line == "[TimingPoints]")
            {
                inTiming = true;
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']')) inTiming = false;
            if (inTiming) p.TimingPoint(line);

            if (line.Contains("OverallDifficulty:"))
            {
                var parts = line.Split(':');
                if (parts.Length > 1)
                {
                    var od = ParseFloat(parts[1].Trim());
                    if (!double.IsNaN(od)) p.Od = od;
                }
            }
            if (line.Contains("CircleSize:"))
            {
                var parts = line.Split(':');
                if (parts.Length > 1)
                {
                    var cs = parts[1].Trim();
                    var v = cs == "0" ? 10 : StringToInt(cs);
                    p.ColumnCount = double.IsNaN(v) ? -1 : (int)v;
                }
            }
            if (line.Contains("Mode:"))
            {
                var parts = line.Split(':');
                if (parts.Length > 1 && parts[1].Trim() != "3") p.Status = "NotMania";
            }
            if (line == "[HitObjects]")
            {
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var obj = lines[j].Trim();
                    if (obj.Length > 0) p.HitObject(obj);
                }
                break;
            }
        }
        if (p.TimingPoints.Count == 0) p.TimingPoints.Add((0, 500.0));
        if (p.Status is not ("Fail" or "NotMania")) p.Status = "OK";
        return p;
    }

    private void TimingPoint(string line)
    {
        if (line.StartsWith("//", StringComparison.Ordinal)) return;
        var parts = line.Split(',').Select(x => x.Trim()).ToArray();
        if (parts.Length < 2) return;
        var t = Math.Truncate(ParseFloat(parts[0]));
        var beatLength = ParseFloat(parts[1]);
        var uninherited = parts.Length > 6 && parts[6].Length > 0 ? ParseInt(parts[6]) : 1;
        if (!double.IsNaN(t) && !double.IsNaN(beatLength) && uninherited == 1 && beatLength > 0) TimingPoints.Add((t, beatLength));
    }

    private void HitObject(string line)
    {
        var p = line.Split(',');
        if (p.Length < 5) return;
        var x = StringToInt(p[0]);
        var column = 0;
        if (ColumnCount > 0)
        {
            // trunc(x / 512 * keys), clamped to the lanes
            var col = Math.Truncate(x / 512.0 * ColumnCount);
            column = double.IsNaN(col) ? 0 : (int)Math.Clamp(col, 0, ColumnCount - 1);
        }
        Columns.Add(column);
        var start = ParseInt(p[2]);
        var type = ParseInt(p[3]);
        var typeBits = double.IsNaN(type) ? 0 : (int)type;
        NoteStarts.Add(start);
        NoteTypes.Add(typeBits);
        var end = start;
        if ((typeBits & 128) != 0 && p.Length >= 6) end = ParseInt(p[5].Split(':')[0]);
        NoteEnds.Add(end);
    }

    /// <summary>Hold notes over all notes</summary>
    public double LnRatio => NoteTypes.Count == 0 ? 0 : (double)NoteTypes.Count(t => (t & 128) != 0) / NoteTypes.Count;

    /// <summary>
    /// The chart with lazer's Invert ("IN") or Hold Off ("HO") applied, as the JS's modIN / modHO do it; any other
    /// flag gives this chart back. A copy: this one stays as read.
    /// </summary>
    public SunnyParser Converted(string? cvtFlag)
    {
        if (cvtFlag is not ("IN" or "HO")) return this;
        var c = new SunnyParser { Od = Od, ColumnCount = ColumnCount, Status = Status };
        c.TimingPoints.AddRange(TimingPoints);
        if (cvtFlag == "HO")
        {
            // Every hold note becomes a note
            c.Columns.AddRange(Columns);
            c.NoteStarts.AddRange(NoteStarts);
            for (var i = 0; i < NoteTypes.Count; i++)
            {
                var hold = (NoteTypes[i] & 128) != 0;
                c.NoteTypes.Add(hold ? 1 : NoteTypes[i]);
                c.NoteEnds.Add(hold ? 0 : NoteEnds[i]);
            }
            return c;
        }

        // Invert: each note holds until shortly before the next one in its lane (the lane's last note goes)
        var points = TimingPoints.OrderBy(t => t.Time).ToList();
        double BeatLengthAt(double time)
        {
            var t = Math.Truncate(time);
            var (lo, hi) = (0, points.Count);
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (points[mid].Time <= t) lo = mid + 1;
                else hi = mid;
            }
            return points.Count == 0 ? 500.0 : points[Math.Max(lo - 1, 0)].BeatLength;
        }
        var objects = new List<(double Start, int Column, double End)>();
        foreach (var lane in Enumerable.Range(0, Columns.Count).GroupBy(i => Columns[i]).OrderBy(g => g.Key))
        {
            var starts = lane.Select(i => NoteStarts[i]).Order().ToList();
            for (var i = 0; i < starts.Count - 1; i++)
            {
                var duration = starts[i + 1] - starts[i];
                duration = Math.Max(duration / 2, duration - BeatLengthAt(starts[i + 1]) / 4);
                // JavaScript's Math.round
                objects.Add((Math.Floor(starts[i] + 0.5), lane.Key, Math.Floor(starts[i] + duration + 0.5)));
            }
        }
        foreach (var o in objects.OrderBy(o => o.Start).ThenBy(o => o.Column))
        {
            c.Columns.Add(o.Column);
            c.NoteStarts.Add(o.Start);
            c.NoteTypes.Add(128);
            c.NoteEnds.Add(o.End);
        }
        return c;
    }
}
