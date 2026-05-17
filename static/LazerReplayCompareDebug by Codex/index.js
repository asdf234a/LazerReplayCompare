const TOSU_HOST = window.TOSU_HOST || location.host || '127.0.0.1:24050';
const LAZER_COMPARE_HOST = window.LAZER_COMPARE_HOST || '127.0.0.1:24052';

const $ = (id) => document.getElementById(id);

const cache = {
    client: 'lazer',
    state: '',
    beatmapChecksum: '',
    songsFolder: '',
    beatmapFolder: '',
    beatmapFile: '',
    osuPath: '',
    score: 0,
    combo: 0,
    hits: {},
    previousHits: {},
    previousScore: 0,
    recording: false,
    flushed: false,
    runStarted: false,
    debugEnabled: false,
    processingHits: false,
    flushing: false,
    liveEvents: [],
    sent: 0,
    mismatch: 0,
    mode: 'corrected',
    replayPath: '',
    replayRate: 1,
    status: 'waiting...',
    last: '-',
};

const HIT_KEYS = ['Perfect', 'Great', 'Good', 'Ok', 'Meh', 'Miss'];
const HIT_ALIASES = {
    Perfect: ['Perfect', 'perfect', 'geki', 'Geki', '320'],
    Great: ['Great', 'great', '300'],
    Good: ['Good', 'good', 'katu', 'Katu', '200'],
    Ok: ['Ok', 'ok', '100'],
    Meh: ['Meh', 'meh', '50'],
    Miss: ['Miss', 'miss', '0'],
};

function hitVal(hits, key) {
    for (const alias of HIT_ALIASES[key] ?? [key]) {
        if (hits?.[alias] != null) return Number(hits[alias]) || 0;
    }
    return 0;
}

function totalHits(hits) {
    return HIT_KEYS.reduce((sum, key) => sum + hitVal(hits, key), 0);
}

function normalizePath(path) {
    return String(path || '').replaceAll('/', '\\');
}

function updateOsuPath() {
    if (!cache.songsFolder || !cache.beatmapFile) {
        cache.osuPath = '';
        return;
    }

    cache.osuPath = cache.client === 'lazer'
        ? `${normalizePath(cache.songsFolder)}\\${normalizePath(cache.beatmapFile)}`
        : `${normalizePath(cache.songsFolder)}\\${normalizePath(cache.beatmapFolder || '')}\\${normalizePath(cache.beatmapFile)}`;
}

function getBeatmapPath() {
    return cache.osuPath;
}

async function refreshDebugState() {
    try {
        const res = await fetch(`http://${LAZER_COMPARE_HOST}/debug-state`);
        if (!res.ok) throw new Error(`/debug-state ${res.status}`);
        const data = await res.json();
        cache.mode = data.correctionMode || 'corrected';
        cache.mismatch = Number(data.mismatchCount) || 0;
        cache.replayPath = data.selectedReplay?.filePath || data.selectedReplay?.FilePath || cache.replayPath;
        cache.debugEnabled = Boolean(data.enabled);
        cache.status = data.enabled ? 'debug enabled' : 'debug disabled in app';
        return cache.debugEnabled;
    } catch (err) {
        cache.debugEnabled = false;
        cache.status = err.message;
        return false;
    } finally {
        render();
    }
}

async function sendHit(index, result, time = Date.now(), score = cache.score, combo = cache.combo) {
    if (!cache.replayPath || !cache.osuPath) return;

    const params = new URLSearchParams({
        osr: cache.replayPath,
        osu: getBeatmapPath(),
        rate: String(cache.replayRate || 1),
        correctionMode: cache.mode,
        index: String(index),
        time: String(time),
        result,
        score: String(score || 0),
        combo: String(combo || 0),
    });

    try {
        const res = await fetch(`http://${LAZER_COMPARE_HOST}/debug-hit?${params}`);
        const data = await res.json();
        if (data.accepted) {
            cache.sent++;
            cache.mismatch = Number(data.mismatchCount) || cache.mismatch;
            cache.last = data.mismatch
                ? `${result} != ${data.predicted || '?'}`
                : result;
        } else {
            cache.last = data.message || 'not accepted';
        }
    } catch (err) {
        cache.last = err.message;
    }

    render();
}

function resetRun() {
    cache.previousHits = {};
    cache.previousScore = 0;
    cache.recording = false;
    cache.flushed = false;
    cache.runStarted = false;
    cache.liveEvents = [];
    cache.sent = 0;
    cache.mismatch = 0;
    cache.last = '-';
}

async function startDebugRun() {
    if (cache.runStarted || !cache.replayPath || !cache.osuPath)
        return;

    const params = new URLSearchParams({
        osr: cache.replayPath,
        osu: getBeatmapPath(),
        rate: String(cache.replayRate || 1),
        correctionMode: cache.mode,
    });

    try {
        const res = await fetch(`http://${LAZER_COMPARE_HOST}/debug-start?${params}`);
        const data = await res.json();
        cache.runStarted = Boolean(data.accepted);
        cache.last = cache.runStarted ? 'log started' : (data.reason || data.message || 'start rejected');
    } catch (err) {
        cache.last = err.message;
    }
}

function captureHitDiff(nextHits) {
    if (!cache.recording)
        return;

    const previousTotal = totalHits(cache.previousHits);
    let index = previousTotal;

    for (const result of HIT_KEYS) {
        const delta = hitVal(nextHits, result) - hitVal(cache.previousHits, result);
        for (let i = 0; i < delta; i++) {
            index++;
            cache.liveEvents.push({
                index,
                result,
                time: Date.now(),
                score: cache.score,
                combo: cache.combo,
            });
            cache.last = `queued ${index} ${result}`;
        }
    }
}

async function flushRun() {
    if (cache.flushed || cache.flushing || !cache.recording || cache.liveEvents.length === 0)
        return;

    cache.flushing = true;
    cache.flushed = true;
    cache.status = `comparing ${cache.liveEvents.length} notes...`;
    render();

    try {
        await refreshDebugState();
        if (!cache.debugEnabled)
            return;

        for (const event of cache.liveEvents)
            await sendHit(event.index, event.result, event.time, event.score, event.combo);

        cache.status = `comparison finished: ${cache.liveEvents.length} notes`;
    } finally {
        cache.flushing = false;
        render();
    }
}

async function processHitDiff(nextHits) {
    if (cache.processingHits)
        return;

    cache.processingHits = true;
    try {
    if (!cache.debugEnabled) {
        cache.status = 'waiting debug-state...';
        return;
    }

    if (!cache.recording && cache.previousScore <= 0 && cache.score > 0) {
        const firstSnapshotHits = totalHits(nextHits);
        if (firstSnapshotHits > 16) {
            cache.previousHits = { ...nextHits };
            cache.previousScore = cache.score;
            cache.status = `ignored late snapshot: ${firstSnapshotHits} hits`;
            return;
        }

        cache.recording = true;
        cache.flushed = false;
        cache.runStarted = false;
        cache.liveEvents = [];
        cache.previousHits = {};
        cache.status = 'recording from first score gain';
        await startDebugRun();
    }

    captureHitDiff(nextHits);
    cache.previousHits = { ...nextHits };
    cache.previousScore = cache.score;
    } finally {
        cache.processingHits = false;
    }
}

function isVisible() {
    const state = String(cache.state || '').toLowerCase();
    return state === 'play' || state === 'playing' || state === 'result' || state === 'results' || state === 'resultscreen';
}

function isResultState() {
    const state = String(cache.state || '').toLowerCase();
    return state === 'result' || state === 'results' || state === 'resultscreen';
}

function render() {
    $('panel').className = `panel${isVisible() ? ' visible' : ''}`;
    $('status').textContent = cache.status;
    $('mode').textContent = cache.mode;
    $('sent').textContent = cache.sent;
    $('mismatch').textContent = cache.mismatch;
    $('last').textContent = cache.last;
}

function createSocket() {
    let reconnectTimer = null;

    const connect = () => {
        const socket = new WebSocket(`ws://${TOSU_HOST}/websocket/v2`);

        socket.addEventListener('open', () => {
            if (reconnectTimer) {
                clearTimeout(reconnectTimer);
                reconnectTimer = null;
            }

            refreshDebugState();

            socket.send(`applyFilters:${JSON.stringify([
                'client',
                { field: 'beatmap', keys: ['checksum'] },
                { field: 'files', keys: ['beatmap'] },
                { field: 'folders', keys: ['songs', 'beatmap'] },
                { field: 'play', keys: ['score', 'hits', 'combo', 'mods'] },
                { field: 'state', keys: ['name'] },
            ])}`);
        });

        socket.addEventListener('message', (event) => {
            try {
                const data = JSON.parse(event.data);
                if (data.error) return;

                if (data.client != null) cache.client = data.client;
                const oldState = cache.state;
                if (data.state?.name != null) cache.state = data.state.name;
                if (oldState && oldState !== cache.state && !isVisible()) {
                    resetRun();
                }
                if (data.beatmap?.checksum && data.beatmap.checksum !== cache.beatmapChecksum) {
                    cache.beatmapChecksum = data.beatmap.checksum;
                    resetRun();
                }

                if (data.folders?.songs) cache.songsFolder = data.folders.songs;
                if (data.folders?.beatmap != null) cache.beatmapFolder = data.folders.beatmap;
                if (data.files?.beatmap) cache.beatmapFile = data.files.beatmap;
                if (data.play?.score != null) cache.score = Number(data.play.score) || 0;
                if (data.play?.combo != null) cache.combo = Number(data.play.combo.current ?? data.play.combo) || 0;
                if (data.play?.mods != null) cache.replayRate = extractRate(data.play.mods);

                updateOsuPath();

                if (data.play?.hits != null)
                    processHitDiff(data.play.hits);

                if (isResultState())
                    flushRun();

                render();
            } catch (err) {
                cache.status = err.message;
                render();
            }
        });

        socket.addEventListener('close', () => {
            reconnectTimer = setTimeout(connect, 1000);
        });
    };

    connect();
}

function extractRate(mods) {
    const text = JSON.stringify(mods || {}).toLowerCase();
    const speed = text.match(/speed[_\s-]*change["']?\s*[:=]\s*([0-9.]+)/);
    if (speed) return Number(speed[1]) || 1;
    if (text.includes('"acronym":"dt"') || text.includes('"dt"')) return 1.5;
    if (text.includes('"acronym":"ht"') || text.includes('"ht"')) return 0.75;
    return 1;
}

setInterval(refreshDebugState, 1500);
createSocket();
render();
