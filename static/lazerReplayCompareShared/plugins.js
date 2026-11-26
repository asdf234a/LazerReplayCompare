// Replay plugins during a play: the app's plugins (data\plugins\*.js, see the app's Column picker) as overlay items.
//
// A plugin scores a replay from how many milliseconds off every press and release was. For the play going on those
// come from tosu (play.hitErrorArray): bare numbers, one per judgement that was not a miss, in the order they were
// judged. Nothing says which object a number belongs to, and where a hold note ends as another note starts it can't
// be worked out either (the two errors fit both ways round; giving them to objects by their timing was tried and
// went wrong too often to compare with).
//
// So during a play a plugin is told less than it is of a finished replay: every judged press and release as a note
// with its error (no lane, no head or release), and every miss the game counted as a note never pressed. The compared
// replay is told the very same way (its errors, from /plugin-replay, in the order they were judged, up to the same
// moment of the song), so the two numbers are like for like and their difference holds.
//
// What that means for the numbers: without hold notes they are what the app's Column shows for the finished replay.
// With hold notes they differ from it, the more the plugin treats releases its own way (a release counts as a press
// here), and a press the game judged a miss counts as never pressed (tosu doesn't report its timing).

const HIT_RESULTS = ['Perfect', 'Great', 'Good', 'Ok', 'Meh'];

// tosu's errors are taken to be in song time, like the game's own hit events (so divided by the rate for real time).
// Checked at 1x only.
const LIVE_ERRORS_IN_SONG_TIME = true;

/** The plugin's score(replay) function from its script, or null. */
export function compilePlugin(source) {
    try {
        const made = new Function(`${source}\n;return typeof score === 'function' ? score : null;`)();
        return typeof made === 'function' ? made : null;
    } catch (err) {
        console.warn('[plugins] script:', err.message || err);
        return null;
    }
}

/** A plugin's number for a replay-shaped object, or null (a script's error is one too). */
export function runPlugin(score, replay) {
    if (!score || !replay) return null;
    try {
        const made = score(replay);
        const value = made != null && typeof made === 'object' ? Number(made.value) : Number(made);
        return made == null || !Number.isFinite(value) ? null : value;
    } catch (err) {
        return null;
    }
}

/**
 * A play as it is told by its hit errors alone (the chart, /chart's answer, gives the beatmap's numbers and the rate).
 * `update(errors, songTime)` with every message; `replay(extra, misses)` is the play so far as a plugin sees a replay.
 */
export function createLiveTracker(chart) {
    const rate = Number(chart.rate) || 1;
    const windows = chart.windows || {};

    let hits = [];
    let counts = null;
    let now = 0;

    function reset() {
        hits = [];
        counts = { Perfect: 0, Great: 0, Good: 0, Ok: 0, Meh: 0, Miss: 0 };
    }

    reset();

    /** The judgement a note press this far off gets. */
    function resultOf(ms) {
        const off = Math.abs(ms);
        for (const result of HIT_RESULTS) {
            if (windows[result] != null && off <= windows[result]) return result;
        }
        return 'Meh';
    }

    /** `errors`: every error so far (tosu's array); `songTime`: the song's time when they were told, in ms. */
    function update(errors, songTime) {
        if (!Array.isArray(errors)) return;
        now = Number(songTime) / rate;
        if (errors.length < hits.length) reset();     // a retry

        for (let i = hits.length; i < errors.length; i++) {
            const ms = Number(errors[i]) / (LIVE_ERRORS_IN_SONG_TIME ? rate : 1);
            const result = resultOf(ms);
            counts[result]++;
            // (when it was judged is only known to the message: the note's time is about that, less the error)
            hits.push({ ms, kind: 'note', result, time: now - ms, column: -1 });
        }
    }

    /**
     * @param {object} extra   What else is known of the play (player, score, mods …), as a replay's fields.
     * @param {number} misses  How many misses the game has counted so far.
     */
    function replay(extra = {}, misses = 0) {
        const missed = Number.isFinite(misses) && misses > 0 ? Math.round(misses) : 0;
        return {
            player: '', score: 0, accuracy: 0, maxCombo: 0, rank: '', mods: [],
            ...extra,
            counts: { ...counts, Miss: missed },
            rate,
            od: chart.overallDifficulty,
            columns: chart.columns,
            length: chart.length,
            stable: false,
            notes: chart.notes,
            holds: chart.holds,
            misses: missed,
            hits: hits.slice(),   // (a plugin may sort it)
            unpressed: Array.from({ length: missed }, () => ({ time: now, column: -1, result: 'Miss' })),
            heldPastEnd: [],
        };
    }

    return { update, replay, reset, rate, get now() { return now; }, get count() { return hits.length; } };
}

/**
 * A replay's data (/plugin-replay's answer) as tosu would have told it during its play: the errors of its judged
 * presses and releases in the order they were judged, with the song time each was judged at, and when each of its
 * misses was counted.
 */
export function prepareReplay(data) {
    const rate = Number(data?.rate) || 1;
    const all = Array.isArray(data?.hits) ? data.hits : [];
    const told = all.filter((hit) => hit.result !== 'Miss')
        // (the data is in real time: back to song time, as tosu's are taken to be)
        .map((hit) => ({ error: hit.ms * (LIVE_ERRORS_IN_SONG_TIME ? rate : 1), judged: (hit.time + hit.ms) * rate }))
        .sort((a, b) => a.judged - b.judged);
    // when each miss was counted (from an app without missTimes: a press judged a miss when it was made, a note never
    // pressed about when its chance ended)
    const missed = (Array.isArray(data?.missTimes)
        ? data.missTimes.map((time) => time * rate)
        : [
            ...all.filter((hit) => hit.result === 'Miss').map((hit) => (hit.time + hit.ms) * rate),
            ...(Array.isArray(data?.unpressed) ? data.unpressed : []).map((item) => (item.time + 130) * rate),
        ]).sort((a, b) => a - b);

    return {
        errors: told.map((item) => item.error),
        judged: told.map((item) => item.judged),
        missed,
        about: { player: data?.player ?? '', score: data?.score ?? 0, accuracy: data?.accuracy ?? 0, maxCombo: data?.maxCombo ?? 0, rank: data?.rank ?? '', mods: data?.mods ?? [] },
    };
}

function countUpTo(sorted, value) {
    let lo = 0, hi = sorted.length;
    while (lo < hi) {
        const mid = (lo + hi) >> 1;
        if (sorted[mid] <= value) lo = mid + 1;
        else hi = mid;
    }
    return lo;
}

/** How many of the replay's errors had been told by `songTime`. */
export function toldBy(prepared, songTime) {
    return countUpTo(prepared.judged, songTime);
}

/** How many misses the replay had by `songTime`. */
export function missedBy(prepared, songTime) {
    return countUpTo(prepared.missed, songTime);
}

/**
 * Keeps one plugin's values for a session: asks the app for the script, the charts and the compared replay's data as
 * they are needed, follows the play, and works out the plugin's number for the play and for the replay at the same moment.
 *
 * @param {object} options
 * @param {(path: string) => Promise<object>} options.fetchJson  Asks the app.
 * @param {() => void} options.onChange                          Called when a value may have changed.
 */
export function createPluginRunner({ fetchJson, onChange }) {
    let plugin = null;          // { key, version }
    let score = null;
    let scoreKey = '';
    let chartKey = '';
    let tracker = null;         // the play
    let replayKey = '';
    let replay = null;          // { prepared, tracker }: the compared replay, told the same way
    const loading = new Set();
    let extra = {};
    let missCount = 0;
    let songTime = 0;
    let live = null;
    let target = null;
    // what was last asked for (an answer for anything else comes too late and is dropped)
    let wantedChart = '';
    let wantedReplay = '';

    async function once(key, work) {
        if (loading.has(key)) return;
        loading.add(key);
        try {
            await work();
        } catch (err) {
            console.warn('[plugins]', err.message || err);
        } finally {
            loading.delete(key);
        }
    }

    const chartOf = (osuPath, mods, rate) => fetchJson(`/chart?${new URLSearchParams({ osu: osuPath, mods: (mods || []).join(','), rate: Number(rate || 1).toFixed(4) })}`);

    function compute() {
        const before = `${live}|${target}`;
        if (!plugin || !score || !tracker) {
            live = null;
            target = null;
        } else {
            live = runPlugin(score, tracker.replay(extra, missCount));
            if (replay) {
                // the replay up to the same moment of the song (its errors so far; fewer after a retry)
                const count = toldBy(replay.prepared, songTime);
                replay.tracker.update(count === replay.prepared.errors.length ? replay.prepared.errors : replay.prepared.errors.slice(0, count), songTime);
                target = runPlugin(score, replay.tracker.replay(replay.prepared.about, missedBy(replay.prepared, songTime)));
            } else {
                target = null;
            }
        }
        if (before !== `${live}|${target}`) onChange();
    }

    /**
     * What is shown and played now: loads what is missing.
     * @param {object} want  { plugin: { key, version } | null, osuPath, mods (acronyms), rate, base (beatmap + mods key),
     *                         target: { path, mods (acronyms), rate } | null }
     */
    function sync(want) {
        plugin = want.plugin || null;
        if (!plugin) {
            tracker = null;
            chartKey = '';
            replay = null;
            replayKey = '';
            wantedChart = '';
            wantedReplay = '';
            compute();
            return;
        }

        const wantedScore = `${plugin.key}|${plugin.version}`;
        if (wantedScore !== scoreKey) {
            once(`script|${wantedScore}`, async () => {
                const answer = await fetchJson(`/plugin?${new URLSearchParams({ key: plugin.key })}`);
                score = compilePlugin(String(answer.source || ''));
                scoreKey = wantedScore;
                compute();
            });
        }

        wantedChart = want.osuPath ? `${want.base}` : '';
        if (wantedChart !== chartKey) {
            // another beatmap or other mods: the play so far is not this chart's
            tracker = null;
            chartKey = '';
            const asked = wantedChart;
            if (asked) {
                once(`chart|${asked}`, async () => {
                    const chart = await chartOf(want.osuPath, want.mods, want.rate);
                    if (asked !== wantedChart) return;
                    tracker = createLiveTracker(chart);
                    chartKey = asked;
                    compute();
                });
            }
        }

        const wanted = want.target;
        wantedReplay = wanted?.path && want.osuPath ? `${wanted.path}|${want.osuPath}|${(wanted.mods || []).join(',')}|${Number(wanted.rate || 1).toFixed(4)}` : '';
        if (wantedReplay !== replayKey) {
            replay = null;
            replayKey = '';
            const asked = wantedReplay;
            if (asked) {
                once(`replay|${asked}`, async () => {
                    // the replay's own chart: its mods may make another one than the play's
                    const [data, chart] = await Promise.all([
                        fetchJson(`/plugin-replay?${new URLSearchParams({ osr: wanted.path, osu: want.osuPath })}`),
                        chartOf(want.osuPath, wanted.mods, wanted.rate),
                    ]);
                    if (asked !== wantedReplay) return;
                    replay = { prepared: prepareReplay(data), tracker: createLiveTracker(chart) };
                    replayKey = asked;
                    compute();
                });
            }
        }
    }

    return {
        sync,
        /**
         * A message from tosu during a play: its hit errors, the song's time, what the play is so far (for the plugin's
         * replay fields) and how many misses the game counts.
         */
        onLive(errors, time, playSoFar = {}, misses = 0) {
            if (!tracker) return;
            extra = playSoFar;
            missCount = Number(misses) || 0;
            songTime = Number(time) || 0;
            tracker.update(errors, songTime);
            compute();
        },
        /** { [plugin key]: value } for the play so far, or null. */
        liveValues: () => (plugin && live != null ? { [plugin.key]: live } : null),
        /** { [plugin key]: value } for the compared replay at the same moment, or null. */
        replayValues: () => (plugin && target != null ? { [plugin.key]: target } : null),
    };
}
