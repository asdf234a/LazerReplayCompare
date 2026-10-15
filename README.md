# LazerReplayCompare

LazerReplayCompare races your osu!mania play, in osu!lazer or osu!stable, against your own saved replays. While you play, a tosu overlay shows, note by note, whether you are ahead of or behind the replay you are comparing against.

## What's new in 1.2.0

- **Replay details.** Click a replay to open its details: rank, score, accuracy, MAX : 300 and max combo, the judgement ring, a hit error distribution, early/late, unstable rate and an offset hint, every press over the song, and long-note release charts. Each replay is judged by the engine of the game that recorded it; hover a chart to see its numbers.
- **Opens on the results screen.** Right after a play, or for a score opened from the leaderboard, the app opens that replay's details by itself (Settings → Replay details). A details page you opened yourself stays open, with its map's background, when you change maps.
- **Main and Sub overlays.** The single overlay is split into `LazerReplayCompare Main` and `LazerReplayCompare Sub`, and what each shows is picked in the app (an item, and Diff / Live / Replay) instead of a config file. New items: Score acc, Stable acc, Lazer acc and MAX : 300; more can be added in `metrics.json` without a rebuild. **Updating:** copy the new overlay folders into tosu's `static` folder and add them in tosu again; the old `LazerSameModScoreDiff by Codex` folder and its `config.json` are no longer used.
- **Zoom.** Ctrl + / Ctrl − / Ctrl 0, Ctrl + mouse wheel or Settings → Appearance, from 80% to 200%. Small windows and large zooms stack the pages' side-by-side parts.
- **Accuracy as the game shows it.** osu!lazer cuts accuracy off at two decimals instead of rounding (98.769% is 98.76%); the app and the overlays now do the same while you play osu!lazer.
- **Look.** The whole beatmap background shows, blurring gradually towards the bottom, with blurred sides in wide windows; the connection state and Compact mode sit in the top bar; checked settings use the accent colour in the dark theme.

## What's new in 1.1.0

- **osu!stable support.** Playing in osu!stable, the app reads osu!stable's own local scores (`scores.db`) and replays (`Data/r`), and the overlay compares you against them the way osu!stable scores: ScoreV1, osu!stable's accuracy and combo, one judgement per hold note.
- **osu!stable judgement engine.** osu!stable replays are judged by a new osu!stable engine (built on the YAVSRG and mania-judge rules and checked against osu!stable with recorded replays and live logs), then corrected so the timeline ends on the replay's recorded score, judgements and max combo.
- **Scores without replays.** osu!stable scores whose replay file is missing are listed with a NO REPLAY badge; they are never a comparison target.
- **osu!stable replays in osu!lazer.** Against osu!lazer, replays set in osu!stable (imported, Classic) are scaled onto their recorded result instead of showing lazer's raw playback, and judgement-count metrics account for lazer counting hold notes twice.
- **Old Hard Rock / Easy replays.** osu!lazer replays set before 2025.710 with Hard Rock or Easy are judged with that era's hit windows.

## What's new in 1.0.0

- **Redesigned app.** The beatmap's background sits behind a slim comparison card, and the window has its own title bar.
- **Judgement ring.** The comparison card shows the target replay's judgements as a ring chart, next to its score and accuracy.
- **osu!lazer mod icons.** Mods are shown with the same icons, colours, rate tabs (`1.20x`, `OD8`) and ordering osu!lazer uses.
- **Compact mode.** A small always-on-top window with the beatmap background, for while you play.
- **Matching auto target.** The automatic target is your best replay at the same speed and Classic setting as the current play, the same rule the overlay uses.
- **Timeline accuracy.** The app shows how closely the replay's timeline matches its recorded score: exact, approximate, raw or scaled.
- **New app icon.** The icon is a pink line overtaking a ghost line.

## Download

Download from the latest release: https://github.com/asdf234a/LazerReplayCompare/releases/latest

- `LazerReplayCompare.exe`: the app.
- The overlay folders, from `static` in this repository or the release.

You need the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

## How to use

1. Run `LazerReplayCompare.exe`.
2. Start osu!lazer (or osu!stable) and tosu.
3. Copy the overlay folders from `static` into tosu's `static` folder, then open an overlay from tosu.
4. Select a beatmap in the game. The app lists your saved replays for it.
5. Play. The overlay compares you against the target replay as you go.

If the app can't find your osu!lazer folder (or, while you play osu!stable, your osu!stable folder), click **Choose folder** on the banner, or set it in Settings.

Keep the app running while you use the overlay. Its overlay API listens on port `24052`; tosu is expected on `24050`.

## Choosing the target replay

- **Auto (default).** The target is your highest score at the same playback rate and Classic setting as the current play. If no saved replay matches, there is nothing to compare against until you pin one.
- **Playing in osu!stable.** The replay list comes from osu!stable itself: the local scores in its `scores.db`, with their replays from its `Data/r` folder. The target is your best one at the same rate, scored the way osu!stable does (ScoreV1, one judgement per hold note). The osu!stable folder is found through tosu; otherwise choose it in Settings → osu!stable folder.
- **Pinned.** Pin a replay in the list to compare against it instead, whatever its mods or speed. Unpin it to go back to auto.

## Replay details

Click a replay in the list to open its details. Each replay is judged by the engine of the game that recorded it: osu!stable replays by the osu!stable engine (one judgement per hold note, from its head and release together), osu!lazer replays by osu!lazer's (head and release judged apart, with release windows 1.5× wider).

- **Summary.** Rank, score (ScoreV1 for osu!stable), accuracy, MAX : 300 ratio ("Perfect" when there is no 300), max combo, the judgement ring, whether the judgements match the replay, and the difference to the target. **Show file** opens the replay file; **Pin as target** pins it.
- **Hit error distribution.** Every press and release in 3 ms bins, coloured by judgement, across the whole hit window with 0 in the middle. A long note judged differently from a note at the same error sits on top in its own colour. Next to it: early vs late, mean, median, unstable rate, on-time presses and an offset hint.
- **Over the song.** Every press (notes and long note heads) as a dot over time (above the line late, below early), with the judgement windows as lines and notes never pressed on the top edge. On maps up to 10 keys, one column can be highlighted.
- **Long notes.** The release mean, how often you let go early, the release errors coloured by judgement and every release over the song. osu!lazer judges a head like a note, so for osu!lazer replays the judgement share compares presses (notes + long note heads) with releases; for osu!stable replays it compares notes with long notes, and the head mean is shown too.

Hover any chart, the ring or a bar to see its numbers. A score without a replay file shows its summary only. Go back to the list with **Replays**, the mouse's back button, Backspace, Esc or Alt+Left; **Jump to** scrolls to a section.

On a results screen (right after a play, or a score opened from the leaderboard), the app opens that replay's details by itself as soon as the replay is in the list (Settings → Replay details to turn it off), and goes back to the list when the results screen closes. A details page you opened yourself stays open, with its map's background, when you select another map.

The pages can be zoomed with Ctrl + / Ctrl − / Ctrl 0, Ctrl + mouse wheel, or Settings → Appearance (80% to 200%).

## Overlays

Copy all four folders from `static` (the three overlays and `lazerReplayCompareShared`) into tosu's `static` folder.

- `LazerReplayCompare Main` shows one large item: by default the score difference to the replay you are compared against.
- `LazerReplayCompare Sub` shows one smaller item on its own, so it can be placed anywhere: by default your live accuracy.
- `LazerReplayCompareLive by Codex` shows a detailed live comparison: the Main and Sub items, combo, judgements, the target replay, and whether it was pinned or chosen automatically.

## What the overlays show

Pick it on the Compare page, next to "now playing": an item and how to show it, for **Main** and for **Sub**. The overlays follow within a second; the live panel shows both items.

- **Item**:

  | Item | Meaning |
  |---|---|
  | Score | Score, as the game counts it (osu!stable: ScoreV1, osu!lazer: lazer's score). |
  | Score acc | Score as a share of the best score possible so far: every judgement up to this note MAX. For example 270,000 when 300,000 was possible is 90%. |
  | Acc | Accuracy, as the game shows it. |
  | Stable acc | osu!stable's accuracy (MAX and 300 both count fully), from the judgements. Handy while playing osu!lazer. |
  | Lazer acc | osu!lazer's accuracy (MAX worth 305), from the judgements. Handy while playing osu!stable. |
  | MAX : 300 | MAX ÷ 300. With no 300 yet it divides by one, so it shows the MAX count until the first 300. |
  | Off | Hide the overlay. |

- **Show**: **Diff** (you minus the compared replay at the same note; positive means you are ahead), **Live** (your value) or **Replay** (the replay's value at the same note).

Percentages are shown the way the game shows them: while you play osu!lazer they are cut off at two decimals (98.769% shows 98.76%, as in osu!lazer), in osu!stable they are rounded. Differences are always rounded.

More items made of judgement counts can be added in `metrics.json` (Settings → tosu overlays → **Edit metrics.json**, stored in `%LocalAppData%\LazerReplayCompare`): a name, a value per judgement, and whether to average it per judgement (an accuracy-like value). It comes with BMS (MAX = 2, 300 = 1), PPacc and PPscore (MAX = 100, 300 = 93.75, 200 = 62.5, 100 = 31.25, 50 = 15.625). Saved changes show up in the pickers and the overlays right away, without a rebuild.

## Timeline modes

Settings → Replay timeline chooses how the replay's note-by-note score is built:

- **Corrected (default).** Rebuilds the timeline so it reproduces the replay's recorded score and judgements. The comparison card says whether the match is exact or approximate, and by how much.
- **Raw.** Exactly what osu!lazer's replay playback shows, even when that differs from the recorded score.
- **Scaled.** The raw timeline, stretched so its final value equals the replay's (the value of the overlay's item).

The first timeline for a replay can take a moment to build. Until it is ready, the overlay uses the raw replay data.

## Notes

- The app is for osu!lazer **mania** replays. Mirror is handled by flipping the columns.
- Settings are stored in `%LocalAppData%\LazerReplayCompare\settings.json`, custom overlay items in `metrics.json` next to it.

## Building from source

The app references osu!lazer's own assemblies, so it needs an osu!lazer installation:

```
dotnet publish LazerReplayCompare -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true
```

By default it looks for osu!lazer in `%LocalAppData%\osulazer\current`. Pass `-p:OsuLazerCurrentPath=<folder>` to use another one.

## Credits

- Mod icon textures come from osu!lazer's resources ([ppy/osu-resources](https://github.com/ppy/osu-resources)). See `LazerReplayCompare/Assets/Mods/NOTICE.txt`.
- The UI font is a subset of [Pretendard](https://github.com/orioncactus/pretendard), included as "LRC Sans" (SIL Open Font License). See `LazerReplayCompare/Assets/Fonts/LRCSans-NOTICE.txt`.
- Mod tab text uses [Baloo 2](https://github.com/EkType/Baloo2) (SIL Open Font License).
- This is a fan-made tool and isn't affiliated with ppy or osu!.
