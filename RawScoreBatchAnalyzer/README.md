# Raw Score Batch Analyzer

Batch analyzer for testing the currently checked-out `LazerReplayCompare` Raw osu!mania score calculator against many `.osr` files.

## How It Chooses The Calculator

This project references `..\LazerReplayCompare\LazerReplayCompare.csproj`.

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

The CSV contains one row per replay, including:

- replay and beatmap path
- final OSR score and Raw score
- score delta and normalized score delta
- final accuracy delta
- judgement count deltas
- max combo delta
- status and error message

Only osu!mania replays are analyzed. Other modes are skipped.
