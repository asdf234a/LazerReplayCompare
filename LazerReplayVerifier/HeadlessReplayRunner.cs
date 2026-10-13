using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Framework.Timing;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Skinning;

namespace LazerReplayVerifier;

public sealed record HeadlessResult(long TotalScore, double Accuracy, int MaxCombo, IReadOnlyDictionary<HitResult, int> Statistics);

/// <summary>
/// Runs a replay through osu!lazer's real <see cref="ReplayPlayer"/>. The gameplay clock is driven by a stepped clock
/// (one fixed step per host frame) instead of the audio track, so playback runs as fast as the host can update while
/// frame-stable replay handling keeps judgements identical to 1x playback.
/// </summary>
public partial class HeadlessReplayGame : OsuGameBase
{
    private readonly Score score;
    private readonly string beatmapPath;
    private readonly double step;
    private SteppedReplayPlayer? player;

    public HeadlessResult? Result { get; private set; }
    public Exception? Error { get; private set; }

    public HeadlessReplayGame(Score score, string beatmapPath, double step = 50)
    {
        this.score = score;
        this.beatmapPath = beatmapPath;
        this.step = step;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        try
        {
            var working = new HeadlessWorkingBeatmap(beatmapPath);
            working.LoadTrack();
            Ruleset.Value = RulesetStore.GetRuleset(score.ScoreInfo.Ruleset.OnlineID)!;
            Beatmap.Value = working;
            SelectedMods.Value = score.ScoreInfo.Mods;
            score.ScoreInfo.BeatmapInfo = working.BeatmapInfo;

            var stack = new OsuScreenStack();
            Add(stack);
            stack.Push(player = new SteppedReplayPlayer(score, new PlayerConfiguration { ShowResults = false, AllowRestart = false }));
        }
        catch (Exception e)
        {
            // OsuGameBase soft-handles exceptions; surface them instead of spinning forever.
            Error = e;
            Exit();
        }
    }

    protected override void Update()
    {
        base.Update();
        if (Result != null || player?.IsLoaded != true || player.GameplayState == null)
            return;

        player.Source.Advance(step);
        var processor = player.Processor;
        if (!processor.HasCompleted.Value)
            return;

        Result = new HeadlessResult(processor.TotalScore.Value, processor.Accuracy.Value, processor.HighestCombo.Value,
            processor.Statistics.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value));
        Exit();
    }

    private sealed class SteppedClock : IAdjustableClock
    {
        public double CurrentTime { get; private set; }
        public double Rate { get; set; } = 1;
        public bool IsRunning { get; private set; }
        public void Advance(double ms) { if (IsRunning) CurrentTime += ms; }
        public void Reset() { IsRunning = false; CurrentTime = 0; }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public bool Seek(double position) { CurrentTime = position; return true; }
        public void ResetSpeedAdjustments() => Rate = 1;
    }

    private partial class SteppedReplayPlayer : ReplayPlayer
    {
        public readonly SteppedClock Source = new SteppedClock();

        public SteppedReplayPlayer(Score score, PlayerConfiguration configuration)
            : base(score, configuration)
        {
        }

        public ScoreProcessor Processor => ScoreProcessor;

        protected override GameplayClockContainer CreateGameplayClockContainer(WorkingBeatmap beatmap, double gameplayStart)
        {
            Source.Seek(Math.Min(gameplayStart, 0) - 2000);
            return new GameplayClockContainer(Source, applyOffsets: false, requireDecoupling: false);
        }
    }

    /// <summary><see cref="FlatWorkingBeatmap"/> throws for track/skin/background; headless gameplay needs none of them.</summary>
    private class HeadlessWorkingBeatmap : FlatWorkingBeatmap
    {
        public HeadlessWorkingBeatmap(string file)
            : base(file)
        {
        }

        protected override Track GetBeatmapTrack() => new TrackVirtual((Beatmap.HitObjects.LastOrDefault()?.GetEndTime() ?? 0) + 10000);
        public override Texture GetBackground() => null!;
        protected override ISkin GetSkin() => null!;
        public override Stream GetStream(string storagePath) => null!;
    }
}
