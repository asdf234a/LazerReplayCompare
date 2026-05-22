using osu.Game.Rulesets.Scoring;

namespace LazerReplayCompare;

internal sealed record ManiaNote(int Column, double StartTime, double? EndTime);

internal sealed class ManiaBeatmap
{
    public int Mode { get; init; }
    public int Columns { get; init; }
    public double OverallDifficulty { get; init; }
    public List<ManiaNote> Notes { get; } = new();
}

internal sealed record ManiaWindows(double Perfect, double Great, double Good, double Ok, double Meh, double Miss)
{
    public static ManiaWindows ForDifficulty(double overallDifficulty, double speedMultiplier)
    {
        var multiplier = speedMultiplier > 0 ? speedMultiplier : 1.0;
        return new ManiaWindows(
            Perfect: Window(overallDifficulty, 22.4, 19.4, 13.9, multiplier),
            Great: Window(overallDifficulty, 64, 49, 34, multiplier),
            Good: Window(overallDifficulty, 97, 82, 67, multiplier),
            Ok: Window(overallDifficulty, 127, 112, 97, multiplier),
            Meh: Window(overallDifficulty, 151, 136, 121, multiplier),
            Miss: Window(overallDifficulty, 188, 173, 158, multiplier));
    }

    public HitResult ResultFor(double offset)
    {
        var absolute = Math.Abs(offset);
        if (absolute <= Perfect) return HitResult.Perfect;
        if (absolute <= Great) return HitResult.Great;
        if (absolute <= Good) return HitResult.Good;
        if (absolute <= Ok) return HitResult.Ok;
        if (absolute <= Meh) return HitResult.Meh;
        return HitResult.Miss;
    }

    // Tail windows are 1.5x more lenient than head windows.
    public HitResult ResultForTail(double offset)
    {
        var absolute = Math.Abs(offset);
        if (absolute <= Perfect * 1.5) return HitResult.Perfect;
        if (absolute <= Great * 1.5) return HitResult.Great;
        if (absolute <= Good * 1.5) return HitResult.Good;
        if (absolute <= Ok * 1.5) return HitResult.Ok;
        if (absolute <= Meh * 1.5) return HitResult.Meh;
        return HitResult.Miss;
    }

    private static double Window(double difficulty, double easy, double normal, double hard, double multiplier)
    {
        var value = difficulty > 5
            ? normal + (hard - normal) * (difficulty - 5) / 5
            : easy + (normal - easy) * difficulty / 5;

        return Math.Floor(value * multiplier) + 0.5;
    }
}

internal sealed class RawInputEvent(int id, double time, int column, bool isPress, int pairId)
{
    public int Id { get; } = id;
    public double Time { get; } = time;
    public int Column { get; } = column;
    public bool IsPress { get; } = isPress;
    public int PairId { get; } = pairId;
    public bool Consumed { get; set; }
    public int? ConsumedByObjectId { get; set; }
}

internal sealed class NoteState(int id, ManiaNote note)
{
    public int Id { get; } = id;
    public ManiaNote Note { get; } = note;
    public bool HeadJudged { get; set; }
    public bool HeadHit { get; set; }
    public bool TailJudged { get; set; }
    public bool TailConsumed { get; set; }
    public bool IsHolding { get; set; }
    public bool BodyBroken { get; set; }
    public double? BodyBreakTime { get; set; }
    public double? PressTime { get; set; }
}
