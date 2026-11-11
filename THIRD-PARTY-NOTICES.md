# Third-party notices

LazerReplayCompare includes or is built from the work listed here. Each part stays under its own licence.

## Difficulty analysis

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

## The program

These are compiled into `LazerReplayCompare.exe`.

| What | Licence |
|---|---|
| [osu!](https://github.com/ppy/osu) and [osu!framework](https://github.com/ppy/osu-framework) (the game's assemblies, to read its database, replays and beatmaps and to score them as the game does) | MIT, Copyright (c) ppy Pty Ltd |
| [Realm](https://github.com/realm/realm-dotnet) | Apache License 2.0 |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (with SkiaSharp and HarfBuzzSharp) | MIT, Copyright (c) AvaloniaUI OÜ |
| [Json.NET](https://github.com/JamesNK/Newtonsoft.Json) | MIT, Copyright (c) 2007 James Newton-King |
| .NET runtime libraries | MIT, Copyright (c) .NET Foundation and Contributors |

## Fonts and images

| What | Licence |
|---|---|
| [Pretendard](https://github.com/orioncactus/pretendard), a subset of it included as "LRC Sans" | SIL Open Font License 1.1, Copyright (c) 2021 Kil Hyung-jin. See `LazerReplayCompare/Assets/Fonts/LRCSans-NOTICE.txt`. |
| [Baloo 2](https://github.com/EkType/Baloo2) | SIL Open Font License 1.1. See `LazerReplayCompare/Assets/Fonts/Baloo2-OFL.txt`. |
| Torus by Paulo Goode, osu!'s typeface | A commercial typeface. It is not open source and is not covered by this repository's terms; it is included as the typeface the game itself uses. |
| Mod icons from [osu-resources](https://github.com/ppy/osu-resources) | CC BY-NC 4.0, ppy Pty Ltd. See `LazerReplayCompare/Assets/Mods/NOTICE.txt`. |

## The MIT License

The MIT-licensed parts above are each under these terms, with the copyright line given for them:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The Apache License 2.0 is at https://www.apache.org/licenses/LICENSE-2.0, the SIL Open Font License 1.1 at https://openfontlicense.org, and CC BY-NC 4.0 at https://creativecommons.org/licenses/by-nc/4.0/.

LazerReplayCompare is a fan-made tool and isn't affiliated with ppy or osu!.
