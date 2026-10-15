# Lazer Replay Compare

Lazer Replay Compare is a Windows GUI helper app for osu!lazer and tosu.

It reads osu!lazer's `client.realm` and `files` storage, finds replay files for the beatmap currently selected in tosu, and exposes a small localhost API for overlay plugins.

## Features

- Shows a Windows UI when launched, with two pages:
  - **Compare**: the beatmap selected in osu!lazer or osu!stable (over its background image, which can be turned off in Settings), what the Main and Sub overlays show (an item and Diff / Live / Replay each), the replay being compared against (in auto mode the best score at the current play's rate and Classic mod — the same rule as the overlays — or the one you pinned) with how exactly its timeline matches the recorded score and its judgement breakdown, and a sortable list of the map's replays (score difference to the target, pin buttons).
  - **Replay details** (click a replay): its recorded result and judgement ring, and an analysis of every press and release judged by the engine of the game that recorded it — hit error distribution (long notes judged differently stacked on top), early/late and unstable rate, an offset hint, every press over the song (column highlight up to 10 keys) and long-note release charts. Charts show their numbers on hover.
  - **Settings**: replay timeline mode (Corrected / Raw / Scaled), osu!lazer folder, osu!stable folder, overlay folder, theme (System / Dark / Light) and whether to show the beatmap background.
- Shows problems (tosu not running, osu!lazer or — while playing osu!stable — osu!stable folder not found, overlay API port in use) as banners at the top, and the connection state in the top bar.
- Compact mode: a small always-on-top window with the target and the match status, for while you play.
- Custom overlay items (weighted judgement counts) in `%LocalAppData%\LazerReplayCompare\metrics.json`, read again when it changes.
- Uses one osu!lazer root folder and automatically resolves:
  - `client.realm`
  - `files`
- Reads the currently selected beatmap checksum from tosu.
- Finds osu!lazer replay files for the current beatmap.
- Includes mod settings in API data, so plugins can compare by playback speed or implement their own mod filters.
- Provides a replay timeline API. It first uses lazer replay frame headers when present, then falls back to a drawable-free port of osu!lazer's own mania replay playback (judged with lazer's hit windows and scored with lazer's score processor).

## Requirements

- Windows
- .NET 10 SDK (to build) / .NET 10 Desktop Runtime (to run)
- osu!lazer
- tosu

Default values:

```txt
osu-lazer: auto-detected from LocalAppData if possible
tosu:      127.0.0.1:24050
API:       127.0.0.1:24052
```

## Run From Source

From the `tosu` folder:

```powershell
dotnet run --project .\LazerReplayCompare
```

## Project Layout

- `LazerReplayCompare.Core` - class library with the replay scoring code: `.osr` decoding, the drawable-free osu!lazer mania replay simulator, boundary/approximate correction and score timelines (`ReplayTimelineBuilder`, `ManiaReplaySimulation`). It has no UI dependencies. By default it compiles against the installed osu!lazer (`OsuLazerCurrentPath`); build with `-p:OsuReference=NuGet` to use the `ppy.osu.Game` NuGet packages instead.
- `LazerReplayCompare` - the Avalonia app, overlay API server and tosu integration; references the library.
- `RawScoreBatchAnalyzer` - batch analysis tool (one CSV row per replay); references the library, not the app.

## Build Release

From the `tosu` folder:

```powershell
dotnet publish .\LazerReplayCompare -c Release -r win-x64 --self-contained false -o .\LazerReplayCompare\publish
```

The executable will be created here:

```txt
LazerReplayCompare\publish\LazerReplayCompare.exe
```

## Usage

1. Start osu!lazer.
2. Start tosu.
3. Select a beatmap in osu!lazer.
4. Start `LazerReplayCompare.exe`.
5. If the osu!lazer folder is wrong, choose the correct folder from the banner or in Settings.
6. Check that replay rows appear in the UI.
7. Start the tosu overlay plugin that uses this API.

Clicking a replay row opens its details page; **Show file** there opens the replay file location in Explorer.

## API

All endpoints run on localhost.

```txt
GET http://127.0.0.1:24052/health
GET http://127.0.0.1:24052/replays
GET http://127.0.0.1:24052/best-replay
GET http://127.0.0.1:24052/timeline?osr=<replay-file>&osu=<beatmap-file>
GET http://127.0.0.1:24052/perfect?osu=<beatmap-file>&client=lazer|stable&mods=HD,DT&rate=1.5
GET http://127.0.0.1:24052/state
GET http://127.0.0.1:24052/metrics
```

### Overlay items

The overlays work out their items themselves (`static/lazerReplayCompareShared/metrics.js`): `score`, `score_acc`, `acc`, `stable_acc`, `lazer_acc`, `ma_ratio` (MAX ÷ 300, dividing by one while there is no 300), plus custom items. `/state` tells them what to show — `display.main` / `display.sub` as `{ item, show }` (`show`: `diff`, `value` or `target`), picked in the app — and lists every item in `items`, custom ones (from `metrics.json`, `OverlayItems` in Core) with their weights.

`/perfect` returns `scores`: the score after 0, 1, 2… judgements if every judgement were MAX, as the client scores the beatmap with those mods (osu!lazer: standardised score, hold note heads and tails judged apart; osu!stable: ScoreV1, one judgement per hold note). `score_acc` is the live (or replay) score divided by it at the same judgement count.

### Metrics

`MetricRegistry` (`LazerReplayCompare.Core/Metrics/MetricRegistry.cs`) defines the metrics a scaled timeline can be stretched by: `Score` (also used for `score_acc`), `Acc`, `V1Acc` (`stable_acc`), `LazerAcc` (`lazer_acc`), and `Bms`, `PpAcc`, `PpScore`. `/metrics` sends them as JSON.

Use `correction=raw` to get the simulator timeline exactly as osu!lazer plays the replay back. Omit it, or use `correction=corrected`, to additionally reconcile it with the replay's recorded statistics and score: `.osr` frame times are rounded to whole milliseconds, so the only judgements that can differ from the real play are those whose result changes within ±0.5ms (in practice hold-note tails, whose 1.5x windows end on x.25/x.75ms). The corrector picks the smallest set of such flips that reproduces the recorded counts and score exactly, preferring the solution closest to the mean of all exact solutions. If hit-to-hit flips cannot close the gap, up to three Meh↔Miss flips are tried as well: each candidate set is forced into a fresh simulation (so hold-note cascades follow lazer's rules) and accepted only if the highest combo stays equal to the recorded max combo and the miss/hold statistics match. When the simulation has too many misses, early presses stored exactly on the miss window's edge are tried as ignored presses too (the real press may have come just before the window opened, leaving a later press free to hit the note); one such change can close more than one miss, so smaller sets are tried first. Presses stored exactly on the next object's start time (OrderedHitPolicy compares `time < next.StartTime`, and object times are whole milliseconds) are also tried 0.4ms earlier/later, together with any chord frames stored at the same millisecond. If no exact solution exists with the ±0.5ms rounding candidates, the whole chain is retried assuming ±1.0, ±1.5 and ±2.0ms of timing error (the label then carries `widened=±…ms`). If even that fails, a last-resort search assumes ±2, 4, 8 and 16ms of timing error, tries the raw simulation together with Meh↔Miss / edge-press re-simulations, and keeps the result with the smallest statistics gap, then the smallest score difference (nearest judgements to a boundary are flipped first). When that lands exactly on the recorded statistics and score, the label reads `boundary-corrected(…,widened=±…ms)`; otherwise it is `approx-corrected(flips=…,hitΔ=…,dS=…,±…ms)` and the final values are close to, but not exactly, the recorded ones. Large gaps are solved by computing the net flip flow along Perfect↔Great↔Good↔Ok↔Meh and choosing the combo-affecting flips by local search on the closed-form score.

Replays set before osu!lazer 2025.710.0 (by client version, or play date when the version is missing) are simulated with that era's hit windows: the plain difficulty range scaled by the rate mod, without the flooring and half-millisecond adjustment introduced in 2025.710.0. Replays recorded before 2025.530.0 only contain frames where the input changed: the frame that 2025.530.0 started writing for every judgement (including auto-misses) is missing. Played back as is, a late press would land on a note that live gameplay had already missed, and would be used up there instead of hitting the next note in the column. For these replays the simulator adds one update just before every input change (0.1ms, override with `LRC_LIVE_FRAME`, `off` to disable), the way live gameplay kept updating between inputs.

Replays set in osu!stable (imported into osu!lazer, which marks them Classic) depend on the game the live play runs in, passed as `client=lazer|stable` (tosu's `client`; osu!lazer when missing):

- **Against osu!lazer:** osu!stable counts one judgement per hold note and osu!lazer two, so the recorded statistics cannot be reconciled with lazer's judgements. `correction=corrected` (and `scaled`) return lazer's playback scaled onto the recorded result by `metric` (the overlay's item), like `scaled`; for hit-based metrics the recorded counts are first brought to lazer's judgement total (`holdNotes=x…` in the label).
- **Against osu!stable:** the replay is scored the way osu!stable does, by the stable engine in `LazerReplayCompare.Core/Scoring/Stable`: one judgement per hold note, ScoreV1, osu!stable's accuracy and combo (hold notes give combo for every press and every 100 ms held). The judgement rules are ported from YAVSRG's osu!mania ruleset (MIT) and zzzzv/mania-judge (MIT), and the remaining rules were measured against osu!stable with recorded replays and live logs. `correction=corrected` matches the recorded judgement counts by flipping the fewest judgements that could have gone the other way: errors within 1 ms of a hit window edge, then rule decisions taken within 1 ms of their threshold (re-simulated with that key event moved by ±1 ms). The ScoreV1 left over (osu!stable keeps state the `.osr` does not record) is spread over the judgements in proportion to how many have been made, so the timeline ends on the recorded score, and the final max combo is the recorded one. Statuses: `Exact` (counts and score reproduced), `Corrected` (counts reproduced, score aligned), `Approximate` (some counts differ). ScoreV2 replays are left to the osu!lazer path.

Use `correction=scaled` to get the raw simulator timeline linearly scaled so its final value matches the replay. The scaling metric is `metric=<key>` (any key or alias from `/metrics`; the overlays send the one for the item they show), `Score` when missing. Score- and accuracy-based metrics are scaled in the returned frames; for hit-based metrics the frames keep raw judgements and the response's `scaleFactor` (with `scaleMetric`) must be applied to the frame's metric value.

### `/replays`

Returns the replay list currently loaded in the UI. If a replay is checked in the app, the response also includes `selectedReplay`.

Each replay includes:

```json
{
  "filePath": "C:\\path\\to\\osu-lazer\\files\\...",
  "player": "player name",
  "score": 1234567,
  "modsText": "DT(speedChange: 1.5), HD",
  "modsKey": "DT(speedChange=1.5)+HD",
  "accuracy": 0.9876,
  "maxCombo": 1234,
  "rank": "S",
  "statistics": {
    "Great": 1000,
    "Ok": 10,
    "Miss": 1
  },
  "isLegacy": false,
  "hasReplay": true
}
```

`isLegacy` is true for replays set in osu!stable. Playing in osu!stable, the overlays only compare against these.

While tosu reports osu!stable, the list is read from osu!stable's own folder (tosu's `folders.game`, or Settings → osu!stable folder) instead of osu!lazer's database: the beatmap's osu!mania scores in `scores.db`, each with its replay from `Data/r` (full `.osr` files). Replays are matched to scores by replay hash (or timestamp), looking at files named `{beatmap md5}-*.osr` first and at every replay header otherwise (headers are cached by size and write time); replays in `Data/r` for the beatmap that `scores.db` does not list yet are included too. Scores whose replay is missing from `Data/r` are listed with `hasReplay: false` (and a key instead of a file in `filePath`): they show in the list, but are never a comparison target and can't be pinned. Score, accuracy and rank are osu!stable's (ScoreV1, MAX and 300 count fully).

Use `modsKey` or the structured `mods` array when a plugin needs mod or playback-rate filtering.

### `/best-replay`

Returns the highest score replay from the currently loaded replay list.

### `/timeline`

Builds a replay timeline from the `.osr` file.

Timeline source priority:

1. Existing lazer replay frame headers, if the replay already contains them.
2. The lazer replay port (see `correction` above).

The timeline includes:

- total score
- accuracy
- combo
- max combo
- cumulative hit statistics

Example:

```json
{
  "replayPath": "C:\\path\\to\\osu-lazer\\files\\...",
  "beatmapPath": "C:\\path\\to\\osu-lazer\\files\\...",
  "totalNotes": 1234,
  "finalScore": 987654,
  "finalAccuracy": 0.9876,
  "finalMaxCombo": 1234,
  "timeline": [
    [1704.0, 260, 1]
  ],
  "frames": [
    {
      "time": 1704.0,
      "index": 1,
      "score": 260,
      "combo": 1,
      "maxCombo": 1,
      "accuracy": 1.0,
      "hits": {
        "Great": 1
      }
    }
  ]
}
```

For score-difference plugins, prefer `frames[].index` and `frames[].score`.

Example:

```txt
currentHitIndex = current live hit count from tosu
replayFrame = latest frame where frame.index <= currentHitIndex
scoreDiff = currentLiveScore - replayFrame.score
```

This is more stable than comparing by timestamp because it compares the current play and replay at the same judged-note index.

## Notes

- If `/timeline` returns a `source` starting with `mania-simulated(engine=lazer-port`, the timeline came from the simulator. `boundary-corrected(flips=N,solutions=M)` means N sub-millisecond judgement flips were applied and M flip sets reproduced the recorded score exactly.
- Build warnings about `System.Text.Json`, `System.Diagnostics.DiagnosticSource`, or similar assemblies can appear because osu!lazer ships newer DLLs than the installed .NET 10 reference assemblies. These warnings are expected if the build still finishes with `0 errors`.
