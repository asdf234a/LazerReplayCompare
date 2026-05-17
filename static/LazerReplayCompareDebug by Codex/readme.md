# Lazer Replay Compare Debug

Use this overlay only while checking replay simulation accuracy.

1. Start tosu.
2. Start Lazer Replay Compare.
3. Enable `Debug` in the Lazer Replay Compare window.
4. Select the replay you want to compare.
5. Play the same replay in osu!lazer.

The overlay sends live judgement count changes from tosu to Lazer Replay Compare.
Mismatches are saved as CSV files under:

`%LOCALAPPDATA%\LazerReplayCompare\debug`

Note: tosu currently exposes live cumulative judgement counts, not osu!lazer's internal per-hit offset. The log compares the live judgement result against the calculated timeline judgement at the same hit index.
