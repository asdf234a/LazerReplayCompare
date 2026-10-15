// The live play (from tosu) and the replay it is compared against (from LazerReplayCompare), shared by the overlays.
//
// Follows tosu's websocket, asks the app which replay to compare against and loads its timeline (a raw one first,
// then the corrected or scaled one), plus the perfect scores score_acc divides by. Overlays read `state`, look up the
// replay at the live note with `frameAt`, and redraw from `onUpdate`.

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

const TOSU_HOST = window.TOSU_HOST || location.host || '127.0.0.1:24050';
const LAZER_COMPARE_HOST = window.LAZER_COMPARE_HOST || '127.0.0.1:24052';

/**
 * @param {object} options
 * @param {string} options.name                    Prefix for console messages.
 * @param {() => void} options.onUpdate            Called whenever something shown may have changed.
 * @param {() => string} [options.scaleMetric]     App metric to stretch scaled timelines by (from the shown item).
 * @param {() => boolean} [options.needsPerfect]   Whether perfect scores are needed (score_acc is shown).
 */
export function createSession({ name, onUpdate, scaleMetric = () => 'Score', needsPerfect = () => false }) {
    const state = {
        client: 'lazer',
        gameState: '',
        beatmapChecksum: '',
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
    };

    let loadedStateKey = '';
    let loadKey = '';
    let checking = false;
    const perfectCache = new Map();

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
            }
        } finally {
            state.loading = false;
            update();
        }
    }

    /** Asks the app what to compare against; reloads when the target, the replay list or the timeline mode changed. */
    async function check() {
        if (!isPlaying() || !isSupportedClient(state.client) || !state.beatmapChecksum || !state.osuPath || checking) return;

        checking = true;
        try {
            const data = await fetchJson('/state');
            state.correctionMode = data.correctionMode || state.correctionMode;
            if (data.beatmapMd5 !== state.beatmapChecksum) {
                resetTarget();
                update();
                return;
            }

            const stateKey = `${baseKey()}|${data.timelineKey || data.selectedReplayKey || ''}|${data.replayListVersion || ''}|${scaleMetric()}|${needsPerfect()}`;
            if (stateKey !== loadedStateKey) await load(stateKey, state.correctionMode);
        } catch (err) {
            state.error = err.message || String(err);
            update();
        } finally {
            checking = false;
        }
    }

    function onTosu(data) {
        if (data.client != null) state.client = data.client;
        if (data.state?.name != null) state.gameState = data.state.name;
        if (data.profile != null) {
            state.player = data.profile.username || data.profile.name || data.profile.user?.username || data.profile.user?.name || state.player;
        }

        if (data.beatmap?.checksum && data.beatmap.checksum !== state.beatmapChecksum) {
            state.beatmapChecksum = data.beatmap.checksum;
            state.hitIndex = 0;
            state.perfectLive = null;
            resetTarget();
        }

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
        if (state.framesKey !== baseKey() && !state.noReplay) check();
        update();
    }

    function connect() {
        const socket = new WebSocket(`ws://${TOSU_HOST}/websocket/v2`);

        socket.addEventListener('open', () => {
            socket.send(`applyFilters:${JSON.stringify([
                'client',
                { field: 'beatmap', keys: ['checksum'] },
                { field: 'files', keys: ['beatmap'] },
                { field: 'folders', keys: ['songs', 'beatmap'] },
                { field: 'play', keys: ['score', 'accuracy', 'hits', 'combo', 'mods'] },
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
    setInterval(check, 2000);

    return {
        state,
        frameAt,
        isPlaying,
        isResults,
        baseKey,
        /** The live play as an item source. */
        liveSource: () => ({ score: state.score, accuracy: state.accuracy, hits: state.hits, perfect: perfectAt(state.perfectLive, state.hitIndex) }),
        /** A replay frame as an item source. */
        frameSource: (frame) => (frame ? { score: frame.score, accuracy: frame.accuracy, hits: frame.hits, perfect: perfectAt(state.perfectReplay, frame.index) } : null),
        /** Re-checks now (after a config change that affects what is loaded). */
        refresh: () => check(),
    };
}
