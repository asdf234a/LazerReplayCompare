# LazerReplayCompare

LazerReplayCompare races your osu!mania play, in osu!lazer or osu!stable, against your own saved replays. While you play, a tosu overlay shows, note by note, whether you are ahead of or behind the replay you are comparing against.

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

## Overlays

- `LazerReplayCompareLive by Codex` shows a detailed live comparison: score, accuracy, combo, judgements, the target replay, and whether it was pinned or chosen automatically.
- `LazerSameModScoreDiff by Codex` shows only the difference, in a compact format.

## Overlay metrics

The **Main** and **Sub** settings choose what the overlay compares. **Main** is the large value; **Sub** is the smaller one below it and can be **Off**.

| Metric | Meaning |
|---|---|
| `Score` | Score difference. Positive means you are ahead of the replay. |
| `Acc` | Accuracy difference, as osu!lazer/tosu show it. |
| `BMS` | BMS-style judgement score: 320 = 2 points, 300 = 1, everything else 0. |
| `PPacc` | pp-style accuracy: 320 = 100, 300 = 93.75, 200 = 62.5, 100 = 31.25, 50 = 15.625, Miss = 0, averaged as a percentage. |
| `PPscore` | The same values as `PPacc`, summed and rounded (ten 320s = 1000). |
| `V1acc` | Classic accuracy, where 320 and 300 both count as 100%. |

For example, `Main = Score, Sub = Acc` is the classic display, and `Main = BMS, Sub = Off` shows only the BMS comparison.

## Timeline modes

Settings → Replay timeline chooses how the replay's note-by-note score is built:

- **Corrected (default).** Rebuilds the timeline so it reproduces the replay's recorded score and judgements. The comparison card says whether the match is exact or approximate, and by how much.
- **Raw.** Exactly what osu!lazer's replay playback shows, even when that differs from the recorded score.
- **Scaled.** The raw timeline, stretched so its final value equals the replay's.

The first timeline for a replay can take a moment to build. Until it is ready, the overlay uses the raw replay data.

## Notes

- The app is for osu!lazer **mania** replays. Mirror is handled by flipping the columns.
- Settings are stored in `%LocalAppData%\LazerReplayCompare\settings.json`.

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
