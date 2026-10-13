# Lazer Replay Compare

Lazer Replay Compare is a Windows GUI helper app for osu!lazer and tosu.

It reads osu!lazer's `client.realm` and `files` storage, finds replay files for the beatmap currently selected in tosu, and exposes a small localhost API for overlay plugins.

## Features

- Shows a Windows UI when launched.
- Uses one osu!lazer root folder and automatically resolves:
  - `client.realm`
  - `files`
- Lets you choose the osu!lazer folder with the `...` button.
- Reads the currently selected beatmap checksum from tosu.
- Finds osu!lazer replay files for the current beatmap.
- Shows replay rows in this order:
  - player
  - date
  - mods
  - score
  - accuracy
  - miss
  - max combo
  - rank
  - file
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
5. If the osu!lazer folder is wrong, choose the correct folder with the `...` button.
6. Check that replay rows appear in the UI.
7. Start the tosu overlay plugin that uses this API.

Double-clicking a replay row opens the replay file location in Explorer.

## API

All endpoints run on localhost.

```txt
GET http://127.0.0.1:24052/health
GET http://127.0.0.1:24052/replays
GET http://127.0.0.1:24052/best-replay
GET http://127.0.0.1:24052/timeline?osr=<replay-file>&osu=<beatmap-file>
```

Use `correction=raw` to get the simulator timeline exactly as osu!lazer plays the replay back. Omit it, or use `correction=corrected`, to additionally reconcile it with the replay's recorded statistics and score: `.osr` frame times are rounded to whole milliseconds, so the only judgements that can differ from the real play are those whose result changes within ±0.5ms (in practice hold-note tails, whose 1.5x windows end on x.25/x.75ms). The corrector picks the smallest set of such flips that reproduces the recorded counts and score exactly, preferring the solution closest to the mean of all exact solutions. If hit-to-hit flips cannot close the gap, up to three Meh↔Miss flips are tried as well: each candidate set is forced into a fresh simulation (so hold-note cascades follow lazer's rules) and accepted only if the highest combo stays equal to the recorded max combo and the miss/hold statistics match. When the simulation has too many misses, early presses stored exactly on the miss window's edge are tried as ignored presses too (the real press may have come just before the window opened, leaving a later press free to hit the note); one such change can close more than one miss, so smaller sets are tried first. Presses stored exactly on the next object's start time (OrderedHitPolicy compares `time < next.StartTime`, and object times are whole milliseconds) are also tried 0.4ms earlier/later, together with any chord frames stored at the same millisecond. If no exact solution exists with the ±0.5ms rounding candidates, the whole chain is retried assuming ±1.0, ±1.5 and ±2.0ms of timing error (the label then carries `widened=±…ms`). If even that fails, a last-resort search assumes ±2, 4, 8 and 16ms of timing error, tries the raw simulation together with Meh↔Miss / edge-press re-simulations, and keeps the result with the smallest statistics gap, then the smallest score difference (nearest judgements to a boundary are flipped first). When that lands exactly on the recorded statistics and score, the label reads `boundary-corrected(…,widened=±…ms)`; otherwise it is `approx-corrected(flips=…,hitΔ=…,dS=…,±…ms)` and the final values are close to, but not exactly, the recorded ones. Large gaps are solved by computing the net flip flow along Perfect↔Great↔Good↔Ok↔Meh and choosing the combo-affecting flips by local search on the closed-form score.

Replays set before osu!lazer 2025.710.0 (by client version, or play date when the version is missing) are simulated with that era's hit windows: the plain difficulty range scaled by the rate mod, without the flooring and half-millisecond adjustment introduced in 2025.710.0. Replays recorded before 2025.530.0 only contain frames where the input changed: the frame that 2025.530.0 started writing for every judgement (including auto-misses) is missing. Played back as is, a late press would land on a note that live gameplay had already missed, and would be used up there instead of hitting the next note in the column. For these replays the simulator adds one update just before every input change (0.1ms, override with `LRC_LIVE_FRAME`, `off` to disable), the way live gameplay kept updating between inputs. Stable replays (whose statistics use stable's one-judgement-per-hold-note counting) fall back to the raw timeline.

Use `correction=scaled` to get the raw simulator timeline linearly scaled so its final value matches the replay. The scaling metric is the overlay main metric (override with `metric=Score|Acc|Bms|PpAcc|PpScore|V1Acc`). `Score` and `Acc` are scaled in the returned frames; for hit-based metrics the frames keep raw judgements and the response's `scaleFactor` (with `scaleMetric`) must be applied to the frame's metric value.

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
  }
}
```

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
- `LazerReplayVerifier` (repository root) plays a replay through osu!lazer's real `ReplayPlayer` on a headless host and prints it next to the port's Raw/Corrected results: `dotnet run --project LazerReplayVerifier -c Release -- <replay.osr> <beatmap.osu>`. It uses osu!lazer from NuGet, so it runs without a local install.
- Build warnings about `System.Text.Json`, `System.Diagnostics.DiagnosticSource`, or similar assemblies can appear because osu!lazer ships newer DLLs than the installed .NET 10 reference assemblies. These warnings are expected if the build still finishes with `0 errors`.
