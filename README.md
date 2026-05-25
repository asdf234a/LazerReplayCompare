# LazerReplayCompare

LazerReplayCompare is a helper app for osu!lazer players who want to compare their current play with their saved lazer replays in real time.

It works with tosu overlays. While you play, it can show the score difference against your best matching replay, or against a replay you manually select in the app.

## What It Does

- Finds replay scores saved by osu!lazer.
- Shows replays for the beatmap currently selected in osu!lazer.
- Lets you pin one replay as the comparison target.
- Compares against the highest score with the same playback speed by default.
- Handles Mirror by flipping mania columns during replay timeline calculation.
- Provides overlays for live score difference and detailed replay comparison.

## Download

Download `LazerReplayCompare.exe` from the latest release:

https://github.com/asdf234a/LazerReplayCompare/releases/latest

You may need the .NET 8 Desktop Runtime installed on Windows.

## How To Use

1. Download and run `LazerReplayCompare.exe`.
2. Start osu!lazer.
3. Start tosu.
4. In osu!lazer, select the beatmap you want to play.
5. If the app does not find your osu!lazer folder automatically, click `...` and select your osu!lazer folder.
6. Copy the overlay folders from `static` into your tosu `static` folder.
7. Open the overlay from tosu.
8. Play the map.

## Overlays

`LazerReplayCompareLive by Codex`

Shows a detailed live comparison: score, accuracy, combo, judgements, replay target, and whether the target was selected manually or chosen automatically.

`LazerSameModScoreDiff by Codex`

Shows only the score difference in a compact format.

## Choosing A Replay

If no replay is checked in LazerReplayCompare, the overlay automatically compares against the highest score with the same playback speed.

If you check a replay in the app, that replay becomes the comparison target even if its mod or speed is different.

## Overlay Metrics

The app has `Main` and `Sub` overlay metric settings.

`Main` controls the large value shown between the replay and live score. `Sub` controls the smaller value below it. `Sub` can also be set to `Off`.

- `Score`: current score difference. Positive means your live score is ahead of the replay.
- `Acc`: the normal accuracy percentage difference shown by osu!lazer/tosu.
- `BMS`: BMS-style judgement score difference. 320 = 2 points, 300 = 1 point, all other judgements = 0.
- `PPacc`: pp-style accuracy difference. 320 = 100, 300 = 93.75, 200 = 62.5, 100 = 31.25, 50 = 15.625, Miss = 0, then averaged as a percentage.
- `PPscore`: absolute pp-style judgement score difference. It uses the same values as `PPacc`, but sums them and rounds to an integer. For example, ten 320s = 1000 points.
- `V1acc`: classic accuracy difference where 320 and 300 both count as 100%.

Example:

- `Main = Score`, `Sub = Acc`: classic score-difference display.
- `Main = PPacc`, `Sub = Acc`: shows pp-style judgement quality as the main comparison, with normal accuracy difference below it.
- `Main = BMS`, `Sub = Off`: shows only the BMS-style comparison.

## Notes

- Keep `LazerReplayCompare.exe` running while using the overlay.
- The app is intended for osu!lazer mania replay comparison.
- The first timeline calculation can take a moment. If you start playing before it finishes, the overlay may temporarily use raw replay data and update once calculation is ready.
