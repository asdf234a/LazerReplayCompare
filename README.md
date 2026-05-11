# LazerReplayCompare

LazerReplayCompare is a helper app for osu!lazer players who want to compare their current play with their saved lazer replays in real time.

It works with tosu overlays. While you play, it can show the score difference against your best matching replay, or against a replay you manually select in the app.

## What It Does

- Finds replay scores saved by osu!lazer.
- Shows replays for the beatmap currently selected in osu!lazer.
- Lets you pin one replay as the comparison target.
- Compares only the same mod and speed by default.
- Treats Mirror as the same mod for comparison.
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

If no replay is checked in LazerReplayCompare, the overlay automatically compares against the highest score with the same mod and speed.

If you check a replay in the app, that replay becomes the comparison target even if its mod or speed is different.

## Notes

- Keep `LazerReplayCompare.exe` running while using the overlay.
- The app is intended for osu!lazer mania replay comparison.
- The first timeline calculation can take a moment. If you start playing before it finishes, the overlay may temporarily use raw replay data and update once calculation is ready.
