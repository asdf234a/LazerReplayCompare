# LazerReplayCompare

LazerReplayCompare is a companion app for osu!mania, in osu!lazer and osu!stable. It does two things:

- **It races you against replays while you play.** A [tosu](https://github.com/tosuapp/tosu) overlay shows, note by note, whether you are ahead of or behind a saved replay: your own, or one downloaded from the map's online ranking.
- **It is a library of your mania maps.** Every map is listed with its Sunny star rating, its dan and your best rank, so you can find what to play next by difficulty.

It is a fan-made tool and isn't affiliated with ppy or osu!.

## Install

1. Download `LazerReplayCompare-v…-win-x64.zip` from the [latest release](https://github.com/asdf234a/LazerReplayCompare/releases/latest) and unzip it. Keep the exe, `analyser` and `static` together in one folder.
2. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it.
3. Copy the four folders in `static` into tosu's `static` folder, and add the overlays you want in tosu.

You need Windows, osu!lazer installed (the app reads replays and beatmaps with the game's own code, also for osu!stable), and Edge or Chrome for the dan estimates.

**Updating:** unzip over the old folder (your `data` folder stays) and copy the `static` folders into tosu again.

## Quick start

1. Run `LazerReplayCompare.exe`, then start the game and tosu.
2. Select a beatmap in the game. The app shows the map and lists your saved replays of it.
3. Play. The overlay compares you against the target replay as you go.

If the app can't find your osu!lazer folder (or, while you play osu!stable, your osu!stable folder), click **Choose folder** on the banner or set it in Settings. Keep the app running while you use the overlays: they talk to it on port `24052`, and tosu is expected on `24050`.

The first start rates every map in the background. The Sunny ratings take minutes; the dan estimates of a large library take a few hours. Both pause while you play, the top bar shows how far they are, and the app can be used meanwhile.

## The map page

The Maps tab shows the map the game is on (or one you picked in the library).

- **Map card.** The map's number in a box coloured by difficulty, its dan (and LN dan when 20% of it or more is hold notes), status, title, artist, length, BPM and your plays; then osu!'s stars, the key count and difficulty name, LN ratio, OD and HP. **Open in osu!** opens the beatmap in the game.
- **Rate and OD mods.** With a rate mod, HR, EZ or Difficulty Adjust selected in the game, the card shows the difficulty they make.
- **Library card.** On the left: folded, every group with how its maps are graded; opened, the maps of one group with their own search, grouping and key count. Back (Backspace, Alt+Left, the mouse's back button) folds it and then goes to the library; Forward opens it.
- **Target.** The replay you are compared against, with its score, accuracy and judgements, and how closely its timeline matches its recorded score.
- **Replays.** Two tabs: your local replays, and the map's online ranking.

## The library

Open it from the folded library card, **Full library**, or Ctrl F.

- **Every mania map** of osu!lazer and osu!stable: number, dan, title, keys, stars, OD, BPM, length, your best rank and plays, with the map's background across its row.
- **Group by** dan, LN dan, Sunny, osu! stars, collection, best rank or when you last played.
- **Search** by words (title, artist, difficulty name, mapper, tags, source) and filters: `sunny`, `stars`, `dan`, `keys`, `od`, `hp`, `bpm`, `ln`, `length`, `notes`, `plays`, `acc`, `rank` with `=`, `!=`, `>`, `>=`, `<`, `<=`. For example `sunny>5 rank<s length<2:30`.
- **Quick filters** under the search box: played or not, played this month, best rank, rice or LN.
- **Sort** by clicking a column: once for its natural direction, again for the other way, a third time for no sorting.
- **Now playing** takes the list to the map the game is on.

Settings → Library picks the number that leads each map: **Sunny**, the **dan level** (Reform 3 runs from 3.00 to 3.99) or osu!'s **stars**. A map without a dan level yet shows its Sunny rating with a `*`.

Dans are given for 4K, 6K and 7K. They come from the analyser's estimators where there is one, and otherwise from the Sunny rating.

## Choosing the target replay

- **Auto (default).** Your highest score at the same playback rate and Classic setting as the current play. If no saved replay matches, there is nothing to compare against until you pin one.
- **Pinned.** Pin a replay in the list to compare against it instead, whatever its mods or speed. Unpin it to go back to auto.
- **Playing in osu!stable.** The list comes from osu!stable itself (`scores.db`, with replays from `Data/r`), scored the way osu!stable does: ScoreV1, one judgement per hold note. Scores whose replay file is missing are listed with a NO REPLAY badge and are never a target.

## Online ranking

The second tab lists the map's online top 50. Click a score to download its replay and compare against it.

This uses the osu! API with your own OAuth client: make one under osu! account settings → OAuth and enter it in Settings → osu! API. The app keeps well inside the API's terms: each map's ranking is asked for once, a replay is downloaded only on your click, one at a time, and never twice (it is kept in `data\replays`).

## Replay details

Click a replay to open its details. Each replay is judged by the engine of the game that recorded it.

- **Summary.** Rank, score, accuracy, MAX : 300, max combo, the judgement ring, whether the judgements match the replay, and the difference to the target.
- **Hit error distribution.** Every press and release in 3 ms bins, coloured by judgement, with early vs late, mean, median, unstable rate and an offset hint.
- **Over the song.** Every press as a dot over time, with the judgement windows as lines.
- **Lanes.** Each lane's judgements, accuracy, misses, mean error and unstable rate, and each hand's accuracy. With an odd key count the middle lane goes to the hand you pick.
- **Long notes.** The release mean, how often you let go early, and every release over the song.

Hover a chart to see its numbers. On a results screen the app opens that replay's details by itself (Settings → Replay details to turn it off).

## Overlays

| Overlay | What it shows |
|---|---|
| `LazerReplayCompare Main` | One large item: by default the score difference to the target replay. |
| `LazerReplayCompare Sub` | One smaller item, to place anywhere: by default your live accuracy. |
| `LazerReplayCompare Pacemaker` | A small live leaderboard: you and your rivals in order at this point of the song. |

What Main and Sub show, and who the rivals are, is picked in the replay list's tab row (**Main**, **Sub**, **Rivals**). The overlays follow within a second.

**Rivals** are your local replays at this rate, the online ranking, or both. A rival with a replay is where that replay was at this note; one without (an online score not downloaded) moves at an even pace towards its final score and is marked `est.`. The Pacemaker shows 8 rows at most: you, the two rivals directly above you, and the top of the list from first place down (a dashed line marks skipped places). Add `?rows=6&by=accuracy` to its address to change the number of rows or to compare by accuracy.

**Show** is **Diff** (you minus the target at the same note; positive means you are ahead), **Live** (your value) or **Replay** (the target's value at the same note).

| Item | Meaning |
|---|---|
| Score | Score, as the game counts it (osu!stable: ScoreV1). |
| Score acc | Score as a share of the best score possible so far. |
| Score log | How far you are from a base score (999,000 by default, or a place on the leaderboard): +10 each time the score lost is halved. |
| Acc | Accuracy, as the game shows it. |
| Acc log | The same scale for accuracy: 0 at a base accuracy you pick (95% by default), +10 for half the accuracy lost. |
| Stable acc | osu!stable's accuracy, from the judgements. Handy while playing osu!lazer. |
| Lazer acc | osu!lazer's accuracy, from the judgements. Handy while playing osu!stable. |
| MAX : 300 | MAX ÷ 300 (the MAX count until the first 300). |
| Rival rank | Your place among the rivals at this note, by score, shown as `#4`. |
| Next rival | Your score minus the score of the rival directly above you; in first place, your lead over the second. |
| Off | Hide the overlay. |

More items made of judgement counts can be added in `metrics.json` (Settings → tosu overlays → **Edit metrics.json**): a name and a value per judgement, optionally averaged, divided by a second sum (`per`), offset (`add`), on a logarithm (`log`) or a power (`pow`). It comes with BMS, PPacc and PPscore. Settings → Overlay items picks which items the lists offer.

## Timeline modes

Settings → Replay timeline chooses how a replay's note-by-note score is built:

- **Corrected (default).** Rebuilt so it reproduces the replay's recorded score and judgements. The target card says whether the match is exact or approximate.
- **Raw.** Exactly what the game's replay playback gives, even when that differs from the recorded score.
- **Scaled.** The raw timeline, stretched so its final value equals the replay's.

## Good to know

- **Data.** Everything the app keeps (settings, the library, ratings, records, downloaded replays) is in the `data` folder next to the exe, so the app's folder can be moved or backed up as a whole. Settings from before 2.0 are copied from `%LocalAppData%\LazerReplayCompare` on the first start.
- **The game's files are never changed.** osu!lazer's database is read from a temporary copy; new replays are noticed by watching its `files` folder. **Rescan replays** reads it again any time.
- **Mania only.** Mirror is handled by flipping the columns.
- **Zoom.** Ctrl + / Ctrl − / Ctrl 0, Ctrl + mouse wheel or Settings → Appearance, from 80% to 200%.
- **Scrolling.** The wheel glides; holding the wheel button and dragging scrolls fast.
- **Compact mode.** A small always-on-top window for while you play.
- **Themes.** Dark, light or the system's.

What changed in each version is in the [releases](https://github.com/asdf234a/LazerReplayCompare/releases).

## Building from source

The app references osu!lazer's own assemblies, so it needs an osu!lazer installation and the .NET 10 SDK:

```
dotnet publish LazerReplayCompare -c Release -r win-x64 --self-contained false -o publish
```

By default it looks for osu!lazer in `%LocalAppData%\osulazer\current`. Pass `-p:OsuLazerCurrentPath=<folder>` to use another one. The publish folder holds the exe, `analyser` and `static`.

The app's typeface is LRC Sans. Torus, osu!'s typeface, is not part of this repository or of the releases. If you have its files, put `Torus-Regular.otf` and `Torus-Bold.otf` into `LazerReplayCompare/LocalFonts` before building and the build uses it (`-p:IncludeLocalFonts=false` leaves it out); for the Pacemaker overlay, put the same two files into tosu's `static/lazerReplayCompareShared`.

## Licence

LazerReplayCompare is released under the [MIT License](LICENSE). That covers this project's own code and overlays. The work of others that it includes stays under its own licence, listed below; note that the mod icons are CC BY-NC (non-commercial).

## Third-party notices

LazerReplayCompare includes or is built from the work listed here. Each part stays under its own licence.

### Difficulty analysis

| What | Where it is used | Licence |
|---|---|---|
| [osumania_map_analyser](https://github.com/LeoBlackMT/osumania_map_analyser) by Leo_Black | `analyser/` (its estimators, copied unchanged, run in a hidden browser for the dan estimates); the Sunny rating (`LazerReplayCompare.Core/Difficulty/Sunny.cs`, `SunnyParser.cs`) and the dan tables (`DanTables.cs`) are C# ports of its code and data | MIT, Copyright (c) 2026 Leo_Black. See `analyser/LICENSE`. |
| [Star-Rating-Rebirth](https://github.com/sunnyxxy/Star-Rating-Rebirth) by sunnyxxy | The Sunny star rating algorithm itself, which the analyser's `js/rework` implements and this app's `Sunny.cs` follows | The repository has no licence file. It is used here through the analyser's MIT-licensed implementation, with credit to its author. |
| [Etterna](https://github.com/etternagame/etterna) MinaCalc | `analyser/js/ett/versions/*.wasm` and their loaders (builds of MinaCalc the estimators call) | MIT, Copyright (c) 2016-2023 Etterna |
| [Companella](https://github.com/Leinadix/companella) by Leinadix | `analyser/js/estimator/companella/dan_model.onnx` and `companellaEstimator.js` (the 4K dan model) | MIT, Copyright (c) 2025 Leinadix |
| [Daniel](https://github.com/TheBagelOfMan/Daniel) by TheBagelOfMan | `analyser/js/rework/danielAlgorithm.js`, `analyser/js/estimator/danielEstimator.js` | MIT, Copyright (c) 2026 TheBagelOfMan |
| [YAVSRG](https://github.com/YAVSRG/YAVSRG) (Interlude's chart analysis, from its Prelude library) by Percyqaz | `analyser/js/interlude/`, `analyser/js/patterns/` (a JavaScript port in the analyser) | MIT for the library folders of that repository (the game client and server there are GPLv3 and are not used) |
| [ONNX Runtime Web](https://github.com/microsoft/onnxruntime) | `analyser/js/estimator/companella/ort/` | MIT, Copyright (c) Microsoft Corporation |
| [Fraction.js](https://github.com/infusion/Fraction.js) by Robert Eisele | `analyser/js/parser/vendor/simfile-parser/fraction.js` | MIT, Copyright (c) Robert Eisele |
| nonebot_plugin_osumania_toolkit's `mc_to_osu.py` (after Jakads/malody2osu) | `analyser/js/parser/mcToOsuConverter.js` (a port in the analyser; this app doesn't call it) | MIT, as its header states |

### The program

These are compiled into `LazerReplayCompare.exe`.

| What | Licence |
|---|---|
| [osu!](https://github.com/ppy/osu) and [osu!framework](https://github.com/ppy/osu-framework) (the game's assemblies, to read its database, replays and beatmaps and to score them as the game does) | MIT, Copyright (c) ppy Pty Ltd |
| [Realm](https://github.com/realm/realm-dotnet) | Apache License 2.0 |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (with SkiaSharp and HarfBuzzSharp) | MIT, Copyright (c) AvaloniaUI OÜ |
| [Json.NET](https://github.com/JamesNK/Newtonsoft.Json) | MIT, Copyright (c) 2007 James Newton-King |
| .NET runtime libraries | MIT, Copyright (c) .NET Foundation and Contributors |

### Fonts and images

| What | Licence |
|---|---|
| [Pretendard](https://github.com/orioncactus/pretendard), a subset of it included as "LRC Sans" (the app's typeface, and the overlays') | SIL Open Font License 1.1, Copyright (c) 2021 Kil Hyung-jin. See `LazerReplayCompare/Assets/Fonts/LRCSans-NOTICE.txt`. |
| [Baloo 2](https://github.com/EkType/Baloo2) | SIL Open Font License 1.1. See `LazerReplayCompare/Assets/Fonts/Baloo2-OFL.txt`. |
| Mod icons from [osu-resources](https://github.com/ppy/osu-resources) | CC BY-NC 4.0, ppy Pty Ltd. See `LazerReplayCompare/Assets/Mods/NOTICE.txt`. |

### The MIT License

The MIT-licensed parts above are each under these terms, with the copyright line given for them:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The Apache License 2.0 is at https://www.apache.org/licenses/LICENSE-2.0, the SIL Open Font License 1.1 at https://openfontlicense.org, and CC BY-NC 4.0 at https://creativecommons.org/licenses/by-nc/4.0/.
