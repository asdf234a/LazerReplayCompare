# LazerReplayCompare

LazerReplayCompare is a Windows helper app and tosu overlay set for comparing a live osu!lazer play against stored lazer replay scores.

It reads osu!lazer `client.realm` and `files`, finds replay files for the currently selected beatmap, and exposes a small localhost API that tosu overlays can use.

## Included

- `LazerReplayCompare`: Windows GUI helper app and localhost API server.
- `LazerReplayCompareLive by Codex`: tosu overlay with score, accuracy, combo, judgement, and replay target display.
- `LazerSameModScoreDiff by Codex`: compact score-difference overlay.
- GitHub Releases: ready-to-run `LazerReplayCompare.exe`.

## Requirements

- Windows
- osu!lazer
- tosu
- .NET 8 Desktop Runtime, unless you build a self-contained release yourself

## Quick Start

1. Download `LazerReplayCompare.exe` from the latest GitHub Release.
2. Run `LazerReplayCompare.exe`.
3. Start osu!lazer and tosu.
4. Select a beatmap in osu!lazer.
5. If needed, choose your osu!lazer folder in the app with the `...` button.
6. Copy the overlay folders from `static` into your tosu `static` folder.
7. Open the overlay through tosu.

## Build From Source

The project references DLLs from your local osu!lazer installation. By default it looks in:

```txt
%LocalAppData%\osulazer\current
```

Build:

```powershell
dotnet build .\LazerReplayCompare\LazerReplayCompare.csproj
```

Publish:

```powershell
dotnet publish .\LazerReplayCompare\LazerReplayCompare.csproj -c Release -r win-x64 --self-contained false -o .\LazerReplayCompare\publish
```

If your osu!lazer DLLs are in a different location, pass:

```powershell
dotnet build .\LazerReplayCompare\LazerReplayCompare.csproj -p:OsuLazerCurrentPath="C:\path\to\osulazer\current"
```

## API

Default API host:

```txt
http://127.0.0.1:24052
```

Endpoints:

```txt
GET /health
GET /replays
GET /best-replay
GET /timeline?osr=<replay-file>&osu=<beatmap-file>
```

Use `correction=raw` for a fast uncorrected timeline while the corrected timeline is still loading.

More details are in [LazerReplayCompare/README.md](LazerReplayCompare/README.md).
