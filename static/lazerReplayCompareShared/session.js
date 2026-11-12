// The live play (from tosu) and the replay it is compared against (from LazerReplayCompare), shared by the overlays.
//
// Follows tosu's websocket, asks the app which replay to compare against and loads its timeline (a raw one first,
// then the corrected or scaled one), plus the perfect scores score_acc divides by, and the rivals (the app's /rivals) with
// their timelines when an overlay asks for them. Overlays read `state`, look up the replay at the live note with
// `frameAt`, and redraw from `onUpdate`.

import {
    chooseReplayTarget,
    getCurrentModsKey,
    getHitIndex,
    getReplayModsKey,
    isSupportedClient,
    normalizeAcronym,
    timelineScaleMetric,
    updateOsuPath,
} from './overlayState.js';
import { setClient } from './metrics.js';
import { toRivals } from './rivals.js';

const TOSU_HOST = window.TOSU_HOST || location.host || '127.0.0.1:24050';
const LAZER_COMPARE_HOST = window.LAZER_COMPARE_HOST || '127.0.0.1:24052';

/**
 * @param {object} options
 * @param {string} options.name                    Prefix for console messages.
 * @param {() => void} options.onUpdate            Called whenever something shown may have changed.
 * @param {() => string} [options.scaleMetric]     App metric to stretch scaled timelines by (from the shown item).
 * @param {() => boolean} [options.needsPerfect]   Whether perfect scores are needed (score_acc is shown).
 * @param {() => boolean} [options.needsRank]      Whether the leaderboard's scores are needed (Score log from a place, or Live rank, is shown).
 * @param {() => boolean} [options.needsRivals]    Whether the rivals are needed (a Rivals item, or the Pacemaker, is shown).
 * @param {() => string} [options.rivalMetric]     App metric the rivals' scaled timelines are stretched by.
 * @param {(data: object) => void} [options.onState]  Called with every /state answer (what to show, custom items).
 */
export function createSession({
    name,
    onUpdate,
    scaleMetric = () => 'Score',
    needsPerfect = () => false,
    needsRank = () => false,
    needsRivals = () => false,
    rivalMetric = () => 'Score',
    onState = () => {},
}) {
    const state = {
        client: 'lazer',
        gameState: '',
        beatmapChecksum: '',
        beatmapId: 0,
        songsFolder: '',
        beatmapFolder: '',
        beatmapFile: '',
        osuPath: '',
        modsKey: 'NM|1.0000',
        modAcronyms: [],
        player: '',
        score: 0,
        accuracy: 0,
        combo: 0,
        hits: {},
        hitIndex: 0,

        // compared replay
        target: null,           // the /replays entry
        targetMode: '',         // 'auto' | 'selected'
        frames: [],
        framesKey: '',          // baseKey the frames belong to
        timelineSource: '',
        totalNotes: 0,
        scaleMetric: '',
        scaleFactor: 1,
        correctionMode: 'corrected',
        loading: false,
        noReplay: false,
        error: '',

        // score if every judgement so far were MAX: live play's mods, and the replay's
        perfectLive: null,
        perfectReplay: null,

        // the beatmap's leaderboard, best first, without the play going on; the same without the player's own scores
        rankScores: [],
        rankOthers: [],

        // who the play is raced against (rivals.js), best final score first, and the name the app gives the player
        rivals: [],
        rivalsPlayer: '',

        // tosu's performance numbers for the play going on (see the PP items in metrics.js): the star rating of the part
        // played so far, tosu's own pp for it, and the whole beatmap's pp at 100% with the play's mods
        ppStarsLive: 0,
        ppCurrent: 0,
        ppFull: 0,
    };

    let loadedStateKey = '';
    let loadKey = '';
    let checking = false;
    // a state whose load failed is tried again only after a pause (a timeline that can't be built is rebuilt each try)
    let failedStateKey = '';
    let retryAfter = 0;
    const retry_delay = 5000;
    const perfectCache = new Map();
    let rankCheckAfter = 0;
    let rankLoaded = false;     // for this play
    const rank_check_delay = 5000;
    // rivals: asked for once per play and when the app's replay list or target changes; their timelines are kept for
    // the beatmap, so only new ones are built
    let plays = 0;
    let rivalsKey = '';
    let rivalsLoading = false;
    let rivalsCheckAfter = 0;
    const rivals_check_delay = 1000;
    const rival_timelines_at_once = 2;
    const rivalTimelines = new Map();   // timeline key → frames
    const rivalQueue = [];              // timelines waiting to be asked for
    const rivalQueued = new Set();      // keys waiting or being built
    let rivalBuilds = 0;

    const baseKey = () => `${state.beatmapChecksum}|${state.osuPath}|${state.modsKey}`;

    const isPlaying = () => {
        const value = String(state.gameState || '').toLowerCase();
        return value === 'play' || value === 'playing';
    };

    const isResults = () => {
        const value = String(state.gameState || '').toLowerCase();
        return value === 'result' || value === 'results' || value === 'resultscreen';
    };

    function update() {
        try {
            onUpdate();
        } catch (err) {
            console.error(`[${name}]`, err);
        }
    }

    function resetTarget() {
        loadedStateKey = '';
        loadKey = '';
        state.target = null;
        state.frames = [];
        state.framesKey = '';
        state.noReplay = false;
        state.error = '';
        state.perfectReplay = null;
    }

    /** The replay's frame at the live note (the last one with index ≤ hitIndex), or null. */
    function frameAt(hitIndex) {
        if (state.framesKey !== baseKey()) return null;

        let lo = 0, hi = state.frames.length - 1, found = null;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            if (state.frames[mid].index <= hitIndex) { found = state.frames[mid]; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /** Perfect score after `index` judgements from a list (the last one past its end), or null. */
    function perfectAt(scores, index) {
        if (!Array.isArray(scores) || scores.length === 0) return null;
        return scores[Math.max(0, Math.min(scores.length - 1, index))];
    }

    async function fetchJson(path) {
        const res = await fetch(`http://${LAZER_COMPARE_HOST}${path}`);
        if (!res.ok) throw new Error(`${path.split('?')[0]} ${res.status}`);
        return res.json();
    }

    async function fetchTimeline(replay, rate, correction, metric = '') {
        const params = new URLSearchParams({
            osr: replay.filePath,
            osu: state.osuPath,
            rate: rate.toFixed(4),
            correction,
            // osu!stable replays are scored the osu!stable way when the live play is in osu!stable
            client: state.client,
        });
        if (metric) params.set('metric', metric);
        return fetchJson(`/timeline?${params}`);
    }

    function applyTimeline(data, key, suffix = '') {
        const frames = (Array.isArray(data.frames) ? data.frames : [])
            .map((frame) => ({
                index: Number(frame.index),
                score: Number(frame.score),
                accuracy: Number(frame.accuracy || 0),
                combo: Number(frame.combo || 0),
                hits: frame.hits || {},
            }))
            .filter((frame) => Number.isFinite(frame.index) && Number.isFinite(frame.score))
            .sort((a, b) => a.index - b.index);

        if (frames.length === 0) throw new Error('timeline empty');

        state.frames = frames;
        state.framesKey = key;
        state.scaleMetric = data.scaleMetric || '';
        state.scaleFactor = Number(data.scaleFactor) || 1;
        state.timelineSource = `${data.source || ''}${suffix}`;
        state.totalNotes = Number(data.totalNotes || 0);
    }

    async function perfectScores(acronyms, rate) {
        const params = new URLSearchParams({ osu: state.osuPath, client: state.client, mods: acronyms.join(','), rate: rate.toFixed(4) });
        const key = params.toString();
        if (!perfectCache.has(key)) {
            const data = await fetchJson(`/perfect?${params}`);
            if (perfectCache.size > 6) perfectCache.delete(perfectCache.keys().next().value);
            perfectCache.set(key, Array.isArray(data.scores) ? data.scores.map(Number) : null);
        }
        return perfectCache.get(key);
    }

    async function loadPerfect(replay) {
        if (!needsPerfect() || !state.osuPath) return;
        const key = baseKey();
        try {
            const liveRate = parseFloat(state.modsKey.split('|')[1]) || 1;
            const live = await perfectScores(state.modAcronyms, liveRate);
            const replayAcronyms = (Array.isArray(replay?.mods) ? replay.mods : []).map((mod) => normalizeAcronym(mod.acronym));
            const replayRate = parseFloat(getReplayModsKey(replay).split('|')[1]) || 1;
            const recorded = replay ? await perfectScores(replayAcronyms, replayRate) : null;
            if (key !== baseKey()) return;
            state.perfectLive = live;
            state.perfectReplay = recorded;
        } catch (err) {
            console.warn(`[${name}] /perfect:`, err.message || err);
        }
    }

    function resetRivals() {
        state.rivals = [];
        rivalsKey = '';
        rivalsCheckAfter = 0;
        rivalTimelines.clear();
        rivalQueue.length = 0;
        rivalQueued.clear();
    }

    function rivalFrames(data) {
        return (Array.isArray(data?.frames) ? data.frames : [])
            .map((frame) => ({ index: Number(frame.index), score: Number(frame.score), accuracy: Number(frame.accuracy || 0) }))
            .filter((frame) => Number.isFinite(frame.index) && Number.isFinite(frame.score))
            .sort((a, b) => a.index - b.index);
    }

    /** Builds the waiting rivals' timelines, a few at a time. A rival is paced evenly until its own is there. */
    function buildRivalTimelines() {
        while (rivalBuilds < rival_timelines_at_once && rivalQueue.length > 0) {
            const job = rivalQueue.shift();
            rivalBuilds++;
            fetchTimeline(job.rival, job.rate, job.correction, job.metric)
                .then((data) => {
                    const frames = rivalFrames(data);
                    if (job.base !== baseKey() || frames.length === 0) return;
                    rivalTimelines.set(job.key, frames);
                    for (const rival of state.rivals) {
                        if (rival.timelineKey === job.key) rival.frames = frames;
                    }
                    update();
                })
                // not kept, so the next play asks again
                .catch((err) => console.warn(`[${name}] rival timeline:`, err.message || err))
                .finally(() => {
                    rivalBuilds--;
                    rivalQueued.delete(job.key);
                    buildRivalTimelines();
                });
        }
    }

    /**
     * Asks the app for the rivals when a play started or its replay list, target or rivals changed (`data` is /state's answer),
     * at most once a second, and queues the timelines of those with a replay file that aren't kept yet.
     */
    async function checkRivals(data) {
        if (!needsRivals() || rivalsLoading || !isPlaying()) return;

        const base = baseKey();
        const correction = state.correctionMode;
        const metric = rivalMetric();
        // (rivalsVersion: who the rivals are, and the online ranking, which can arrive after the play began)
        const key = `${base}|${state.client}|${plays}|${data.replayListVersion || ''}|${data.rivalsVersion || ''}|${data.selectedReplayKey || ''}|${correction}|${metric}`;
        if (key === rivalsKey || Date.now() < rivalsCheckAfter) return;

        rivalsCheckAfter = Date.now() + rivals_check_delay;
        rivalsLoading = true;
        try {
            const answer = await fetchJson('/rivals');
            if (base !== baseKey()) return;
            // the app's list is for the beatmap its game has selected; asked again until it is this one
            if (answer.beatmapMd5 !== state.beatmapChecksum) {
                state.rivals = [];
                return;
            }

            const rate = parseFloat(state.modsKey.split('|')[1]) || 1;
            const rivals = toRivals(answer.rivals);
            for (const rival of rivals) {
                if (rival.source !== 'replay') continue;
                rival.timelineKey = `${base}|${state.client}|${rival.filePath}|${correction}|${metric}`;
                rival.frames = rivalTimelines.get(rival.timelineKey) ?? null;
                // the compared replay's timeline is loaded already (see rivalList)
                if (rival.frames || rivalQueued.has(rival.timelineKey) || rival.filePath === state.target?.filePath) continue;
                rivalQueued.add(rival.timelineKey);
                rivalQueue.push({ key: rival.timelineKey, base, rival, rate, correction, metric });
            }

            state.rivals = rivals;
            state.rivalsPlayer = String(answer.player || '');
            rivalsKey = key;
            buildRivalTimelines();
        } catch (err) {
            // the app is not running, or is one without /rivals
            rivalsCheckAfter = Date.now() + retry_delay;
            console.warn(`[${name}] /rivals:`, err.message || err);
        } finally {
            rivalsLoading = false;
            update();
        }
    }

    /** The rivals as the items and the Pacemaker read them: the compared replay with the timeline loaded for it. */
    function rivalList() {
        if (state.rivals.length === 0) return state.rivals;
        const targetPath = state.framesKey === baseKey() && state.frames.length > 0 ? state.target?.filePath : '';
        return targetPath ? state.rivals.map((rival) => (rival.filePath === targetPath ? { ...rival, frames: state.frames } : rival)) : state.rivals;
    }

    /** The best possible score of the whole beatmap from a /perfect list, or null. */
    function perfectTotal(scores) {
        return Array.isArray(scores) && scores.length > 0 ? scores[scores.length - 1] : null;
    }

    async function load(stateKey, correction) {
        if (state.loading) return;

        const key = baseKey();
        state.loading = true;
        state.error = '';
        update();

        try {
            const replays = await fetchJson('/replays');
            // the app may still be reading the new beatmap's replays
            if (replays.beatmapMd5 !== state.beatmapChecksum) {
                loadedStateKey = '';
                return;
            }

            const target = chooseReplayTarget(replays, state.modsKey, 'selected', 'auto', state.client);
            state.targetMode = target.mode;
            if (!target.replay) {
                state.target = null;
                state.frames = [];
                state.framesKey = '';
                state.noReplay = true;
                state.error = target.error;
                loadedStateKey = stateKey;
                state.perfectLive = null;
                await loadPerfect(null);
                return;
            }

            state.noReplay = false;
            state.target = target.replay;
            const replayRate = parseFloat(getReplayModsKey(target.replay).split('|')[1]) || 1;
            const rate = target.mode === 'selected' ? replayRate : parseFloat(state.modsKey.split('|')[1]) || 1;
            const metric = timelineScaleMetric(correction, target.replay, state.client, scaleMetric());
            const nextLoadKey = `${key}|${state.client}|${target.replay.filePath}|${rate.toFixed(4)}|${correction}|${metric}`;
            await loadPerfect(target.replay);

            if (nextLoadKey === loadKey && state.framesKey === key) {
                loadedStateKey = stateKey;
                return;
            }

            const raw = await fetchTimeline(target.replay, rate, 'raw');
            if (key !== baseKey()) return;
            applyTimeline(raw, key, correction !== 'raw' ? ' (temporary)' : '');
            loadKey = nextLoadKey;
            loadedStateKey = stateKey;
            update();

            if (correction !== 'raw') {
                const corrected = await fetchTimeline(target.replay, rate, correction, metric);
                if (key !== baseKey()) return;
                applyTimeline(corrected, key);
            }
        } catch (err) {
            if (key === baseKey()) {
                state.error = err.message || String(err);
                loadKey = '';
                loadedStateKey = '';
                failedStateKey = stateKey;
                retryAfter = Date.now() + retry_delay;
            }
        } finally {
            state.loading = false;
            update();
        }
    }

    /**
     * Asks the app what to show and what to compare against; reloads when the target, the replay list, the timeline
     * mode or the shown item changed. Outside play it only picks up what to show.
     */
    async function check() {
        if (checking) return;
        if (!isPlaying() || !isSupportedClient(state.client) || !state.beatmapChecksum || !state.osuPath) {
            checking = true;
            try {
                const data = await fetchJson('/state');
                onState(data);
                update();
            } catch {
                // the app is not running (yet)
            } finally {
                checking = false;
            }
            return;
        }

        checking = true;
        try {
            const data = await fetchJson('/state');
            state.correctionMode = data.correctionMode || state.correctionMode;
            onState(data);
            update();
            if (data.beatmapMd5 !== state.beatmapChecksum) {
                resetTarget();
                update();
                return;
            }

            // not waited for: the rivals don't hold back the compared replay
            checkRivals(data);

            const stateKey = `${baseKey()}|${data.timelineKey || data.selectedReplayKey || ''}|${data.replayListVersion || ''}|${scaleMetric()}|${needsPerfect()}`;
            if (stateKey !== loadedStateKey && (stateKey !== failedStateKey || Date.now() >= retryAfter))
                await load(stateKey, state.correctionMode);
        } catch (err) {
            state.error = err.message || String(err);
            update();
        } finally {
            checking = false;
        }
    }

    /**
     * The leaderboard, once per play, as soon as it is there: osu!stable's from tosu, osu!lazer's from the app (tosu can't
     * read it; the app asks the osu! API, each beatmap once). Not part of the websocket's filters: it is the bulk of a
     * message (tosu can't pick fields of a list), and it doesn't change during a play.
     */
    async function loadRanks() {
        if (!isPlaying()) {
            rankLoaded = false;
            return;
        }

        if (!needsRank() || rankLoaded || Date.now() < rankCheckAfter) return;
        rankCheckAfter = Date.now() + rank_check_delay;
        try {
            const beatmap = state.beatmapChecksum;
            let rows;
            if (state.client === 'stable') {
                const response = await fetch(`http://${TOSU_HOST}/json/v2`);
                const data = await response.json();
                if (data.beatmap?.checksum !== beatmap || !Array.isArray(data.leaderboard)) return;
                const liveScore = Number(data.play?.score);
                rows = data.leaderboard.filter((row) => Number(row.id) !== 0 && !(row.name === state.player && Number(row.score) === liveScore));
            } else {
                if (!state.beatmapId) return;
                const data = await fetchJson(`/leaderboard?beatmap=${state.beatmapId}`);
                if (state.beatmapChecksum !== beatmap || !Array.isArray(data.scores)) return;
                rows = data.scores;
            }

            rows = [...rows].sort((a, b) => Number(a.position) - Number(b.position));
            if (rows.length === 0) return;

            state.rankScores = rows.map((row) => Number(row.score));
            state.rankOthers = rows.filter((row) => row.name !== state.player).map((row) => Number(row.score));
            rankLoaded = true;
            update();
        } catch (err) {
            console.warn(`[${name}] leaderboard:`, err.message || err);
        }
    }

    function onTosu(data) {
        if (data.client != null) {
            state.client = data.client;
            setClient(state.client);
        }
        if (data.state?.name != null) {
            const wasPlaying = isPlaying();
            state.gameState = data.state.name;
            // a new play: the rivals are asked for again
            if (!wasPlaying && isPlaying()) plays++;
        }
        if (data.profile != null) {
            state.player = data.profile.username || data.profile.name || data.profile.user?.username || data.profile.user?.name || state.player;
        }

        if (data.beatmap?.checksum && data.beatmap.checksum !== state.beatmapChecksum) {
            state.beatmapChecksum = data.beatmap.checksum;
            state.beatmapId = 0;
            state.hitIndex = 0;
            state.perfectLive = null;
            state.rankScores = [];
            state.rankOthers = [];
            rankCheckAfter = 0;
            rankLoaded = false;
            resetRivals();
            resetTarget();
        }

        if (data.beatmap?.id != null) state.beatmapId = Number(data.beatmap.id) || 0;
        if (data.folders?.songs) state.songsFolder = data.folders.songs;
        if (data.folders?.beatmap != null) state.beatmapFolder = data.folders.beatmap;
        if (data.files?.beatmap) state.beatmapFile = data.files.beatmap;

        if (data.play?.score != null) state.score = Number(data.play.score) || 0;
        if (data.play?.accuracy != null) {
            const value = Number(data.play.accuracy) || 0;
            state.accuracy = value > 1 ? value / 100 : value;
        }
        if (data.play?.combo != null) state.combo = Number(data.play.combo.current ?? data.play.combo) || 0;
        if (data.play?.hits != null) {
            state.hits = data.play.hits;
            state.hitIndex = getHitIndex(data.play.hits);
        }
        if (data.beatmap?.stats?.stars?.live != null) state.ppStarsLive = Number(data.beatmap.stats.stars.live) || 0;
        if (data.play?.pp?.current != null) state.ppCurrent = Number(data.play.pp.current) || 0;
        if (data.performance?.accuracy?.[100] != null) state.ppFull = Number(data.performance.accuracy[100]) || 0;
        if (data.play?.mods != null) {
            const next = getCurrentModsKey(data.play.mods);
            state.modAcronyms = (Array.isArray(data.play.mods.array) ? data.play.mods.array : []).map((mod) => normalizeAcronym(mod.acronym));
            if (next !== state.modsKey) {
                state.modsKey = next;
                state.perfectLive = null;
                resetTarget();
            }
        }

        updateOsuPath(state);
        // outside play (or after a failed load) the 1 s check is enough; in play a new target is picked up on the next message
        if (isPlaying() && state.framesKey !== baseKey() && !state.noReplay && Date.now() >= retryAfter) check();
        update();
    }

    function connect() {
        const socket = new WebSocket(`ws://${TOSU_HOST}/websocket/v2`);

        socket.addEventListener('open', () => {
            socket.send(`applyFilters:${JSON.stringify([
                'client',
                // (stats.stars, play.pp and performance.accuracy are for the PP items; the performance graph is left out)
                { field: 'beatmap', keys: ['checksum', 'id', { field: 'stats', keys: ['stars'] }] },
                { field: 'files', keys: ['beatmap'] },
                { field: 'folders', keys: ['songs', 'beatmap'] },
                { field: 'play', keys: ['score', 'accuracy', 'hits', 'combo', 'mods', { field: 'pp', keys: ['current'] }] },
                { field: 'performance', keys: ['accuracy'] },
                { field: 'profile', keys: ['username', 'name', 'user'] },
                { field: 'state', keys: ['name'] },
            ])}`);
        });

        socket.addEventListener('message', (event) => {
            try {
                const data = JSON.parse(event.data);
                if (!data.error) onTosu(data);
            } catch (err) {
                console.error(`[${name}]`, err);
            }
        });

        socket.addEventListener('close', () => setTimeout(connect, 1000));
        socket.addEventListener('error', (err) => console.error(`[${name}] ws error`, err));
    }

    connect();
    setInterval(check, 1000);
    setInterval(loadRanks, 1000);

    /** What the PP items need besides a source's own judgements (the same for the play and for a replay frame). */
    function ppContext() {
        const mods = state.modAcronyms || [];
        return {
            starsLive: state.ppStarsLive,
            current: state.ppCurrent,
            full: state.ppFull,
            liveHits: state.hits,
            // osu!mania's pp multipliers
            multiplier: (mods.includes('NF') ? 0.75 : 1) * (mods.includes('EZ') ? 0.5 : 1),
        };
    }

    return {
        state,
        frameAt,
        isPlaying,
        isResults,
        baseKey,
        /** The live play as an item source. */
        liveSource: () => ({
            score: state.score,
            accuracy: state.accuracy,
            hits: state.hits,
            perfect: perfectAt(state.perfectLive, state.hitIndex),
            rankScores: state.rankScores,
            rankOthers: state.rankOthers,
            index: state.hitIndex,
            perfectTotal: perfectTotal(state.perfectLive),
            rivals: rivalList(),
            pp: ppContext(),
        }),
        /** A replay frame as an item source. */
        frameSource: (frame) => (frame ? {
            score: frame.score,
            accuracy: frame.accuracy,
            hits: frame.hits,
            perfect: perfectAt(state.perfectReplay, frame.index),
            rankScores: state.rankScores,
            rankOthers: state.rankOthers,
            index: frame.index,
            perfectTotal: perfectTotal(state.perfectReplay),
            rivals: rivalList(),
            self: state.target?.filePath || '',
            pp: ppContext(),
        } : null),
        /** The rivals with their timelines (see rivals.js); empty when there are none or none are needed. */
        rivals: rivalList,
        /** Re-checks now (after a config change that affects what is loaded). */
        refresh: () => check(),
    };
}
