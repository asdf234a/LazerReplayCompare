# Raw Score Batch Analyzer

Batch analyzer for testing the currently checked-out `LazerReplayCompare` Raw osu!mania score calculator against many `.osr` files.

## How It Chooses The Calculator

This project references the scoring library `..\LazerReplayCompare.Core\LazerReplayCompare.Core.csproj` (not the app), so it can also be published on its own:

```powershell
dotnet publish .\RawScoreBatchAnalyzer -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

To test another calculator version, switch the Git branch first, then run this app:

```powershell
git switch main
# or
git switch experiment/raw-correction-tuning
```

## Inputs

- `client.realm`: osu!lazer realm file.
- `files folder`: osu!lazer `files` storage folder.
- `OSR folder`: folder containing many `.osr` replay files.
- `Output CSV`: report path.

## Output

Each replay is decoded once and simulated once with the lazer replay port (the same judgement code path osu!lazer uses
for playback, scored with lazer's own score processor). The CSV contains one row per replay, including:

- replay and beatmap path
- recorded score (`OsrScore`, the lazer score; for stable replays lazer's conversion of the stable score)
- `Raw*`: the port's result, exactly as osu!lazer plays the replay back
- `Corrected*`: the result after boundary correction (sub-millisecond judgement flips that reproduce the recorded
  statistics and score; equal to Raw for stable replays)
- score, accuracy, judgement-count and max-combo deltas for both, plus `RawHitDelta` / `CorrectedHitDelta`
  (sum of absolute per-judgement count differences)
- `IsLegacy` (stable replay: its statistics count one judgement per hold note, so count deltas are not comparable)
- `Correction` label, status and error message

Only osu!mania replays are analyzed. Other modes are skipped.
