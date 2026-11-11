# analyser

The difficulty estimators of [osumania_map_analyser](https://github.com/LeoBlackMT/osumania_map_analyser)
by Leo_Black (MIT, see `LICENSE`), copied unchanged from commit `bc5e8c6` (2026-10-26):
`config.js` and the shared, browser-free modules under `js/` (estimator, rework, parser, ett,
patterns, interlude, pipeline). The analyser's own UI (`js/app`) is left out, as is the Node build
of onnxruntime.

`lr2hp/` is this app's part: `engine.js` strings the analyser's functions together the way its
`js/app/analysis.js` does for the dan label on its card (pipeline, SunnyWindow's LN part,
Companella, the 18.5 cap, VIBRO); `index.html` and `worker.js` run it in a headless browser that the
app starts, one Web Worker per core.

To update: copy the same files from a newer analyser, check `lr2hp/engine.js` against the newer
`js/app/analysis.js`, and raise `DanEngine.EngineVersion` in the app so cached results are redone.
