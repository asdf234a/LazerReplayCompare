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
- Provides a replay timeline API. It first uses lazer replay frame headers when present, then falls back to the mania simulator with large excess-miss correction guided by final score and accuracy.

## Requirements

- Windows
- .NET 8 Runtime or SDK
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

Use `correction=raw` to get the fast uncorrected simulator timeline. Omit it, or use `correction=corrected`, to get the density-limited corrected timeline.

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
2. The mania simulator fallback with large excess-miss correction guided by final score and accuracy.

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

- If `/timeline` returns a `source` containing `density-limited-judgement-score-corrected`, the timeline came from the simulator. It adjusts simulated judgements only in likely error sections: broad high-density regions and short windows where judgements swing sharply toward lower results. Correction candidates are prioritized by volatility first and density second, while scores are still produced only through the osu!mania scoring formula.
- Build warnings about `System.Text.Json`, `System.Diagnostics.DiagnosticSource`, or similar assemblies can appear because osu!lazer ships newer DLLs than the installed .NET 8 reference assemblies. These warnings are expected if the build still finishes with `0 errors`.
