import {
    chooseReplayTarget as chooseSharedReplayTarget,
    getCurrentModsKey,
    getHitIndex,
    getReplayModsKey,
    updateOsuPath,
} from '../lazerReplayCompareShared/overlayState.js';

const TOSU_HOST = window.TOSU_HOST || location.host || '127.0.0.1:24050';
const LAZER_COMPARE_HOST = window.LAZER_COMPARE_HOST || '127.0.0.1:24052';

const diffElement = document.querySelector('#scoreDiff');
const diffValueElement = document.querySelector('#diffValue');

const cache = {
    state: '',
    client: 'lazer',
    beatmapChecksum: '',
    songsFolder: '',
    beatmapFolder: '',
    beatmapFile: '',
    osuPath: '',
    score: 0,
    hitIndex: 0,
    modsKey: 'NM|1.0000',
    replayFrames: [],
    replayPath: '',
    targetMode: '',
    correctionMode: 'corrected',
    loadBaseKey: '',
    loadKey: '',
    loading: false,
    error: '',
};

function chooseReplayTarget(replaysData) {
    return chooseSharedReplayTarget(replaysData, cache.modsKey, 'selected', 'auto');
}

// --- Frame lookup ---

function getReplayScoreAtIndex(hitIndex) {
    let lo = 0, hi = cache.replayFrames.length - 1, found = null;
    while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        const frame = cache.replayFrames[mid];
        if (frame.index <= hitIndex) { found = frame; lo = mid + 1; }
        else hi = mid - 1;
    }
    return found?.score ?? null;
}

// --- Display ---

function formatSigned(value) {
    const rounded = Math.round(Number(value) || 0);
    if (rounded > 0) return `+${rounded.toLocaleString('en-US')}`;
    if (rounded < 0) return `-${Math.abs(rounded).toLocaleString('en-US')}`;
    return '0';
}

function isPlaying() {
    return cache.state === 'play' || cache.state === 'playing';
}

function setDisplay(diff, visible) {
    diffValueElement.textContent = formatSigned(diff);
    if (!visible) {
        diffElement.className = 'scoreDiff';
        diffElement.style.color = '';
        return;
    }
    diffElement.className = `scoreDiff visible ${diff > 0 ? 'positive' : diff < 0 ? 'negative' : 'neutral'}`;
    diffElement.style.color = getDiffColor(diff);
}

function getDiffColor(diff) {
    const value = Number(diff) || 0;
    if (value === 0) return 'rgb(255, 255, 255)';

    const spectrum = 3 + Math.min(1, Math.abs(value) / 10000) * 7;
    const t = spectrum / 10;
    const target = value > 0
        ? { r: 119, g: 255, b: 154 }
        : { r: 255, g: 111, b: 127 };

    const r = Math.round(255 + (target.r - 255) * t);
    const g = Math.round(255 + (target.g - 255) * t);
    const b = Math.round(255 + (target.b - 255) * t);
    return `rgb(${r}, ${g}, ${b})`;
}

function updateDisplay() {
    if (!isPlaying()) {
        setDisplay(0, false);
        return;
    }

    if (cache.replayFrames.length === 0 || cache.hitIndex <= 0) {
        setDisplay(0, false);
        return;
    }

    const replayScore = getReplayScoreAtIndex(cache.hitIndex);
    if (replayScore == null) {
        setDisplay(0, false);
        return;
    }

    setDisplay(cache.score - replayScore, true);
}

// --- Load timeline ---

async function loadTimeline() {
    return loadTimelineInternal(false);
}

async function refreshReplayTarget() {
    return loadTimelineInternal(true);
}

async function loadTimelineInternal(forceTargetCheck) {
    if (!isPlaying() || cache.client !== 'lazer' || !cache.beatmapChecksum || !cache.osuPath || cache.loading) return;

    const baseKey = `${cache.beatmapChecksum}|${cache.osuPath}|${cache.modsKey}`;
    if (!forceTargetCheck && baseKey === cache.loadBaseKey) return;

    cache.loading = true;
    cache.error = '';
    updateDisplay();

    try {
        const replaysRes = await fetch(`http://${LAZER_COMPARE_HOST}/replays`);
        if (!replaysRes.ok) throw new Error(`/replays ${replaysRes.status}`);
        const replaysData = await replaysRes.json();

        // Server includes which beatmap its replay list covers. If it doesn't match
        // the current beatmap yet (race: C# hasn't finished loading), reset the key
        // and schedule a retry instead of caching a wrong timeline.
        if (replaysData.beatmapMd5 !== cache.beatmapChecksum) {
            cache.loadKey = '';
            cache.loadBaseKey = '';
            cache.error = '';
            setTimeout(() => { if (!cache.loading) loadTimeline(); }, 1500);
            return;
        }

        const target = chooseReplayTarget(replaysData);
        cache.targetMode = target.mode;

        if (!target.replay) {
            cache.replayFrames = [];
            cache.replayPath = '';
            cache.error = target.error;
            cache.loadBaseKey = baseKey;
            cache.loadKey = '';
            updateDisplay();
            return;
        }

        const replayRate = parseFloat(getReplayModsKey(target.replay).split('|')[1]) || 1;
        const timelineRate = target.mode === 'selected'
            ? replayRate
            : parseFloat(cache.modsKey.split('|')[1]) || 1;
        const correctionMode = await getTimelineMode();
        const key = `${baseKey}|${target.replay.filePath}|${timelineRate.toFixed(4)}|${correctionMode}`;
        if (key === cache.loadKey && cache.replayFrames.length > 0) {
            cache.loadBaseKey = baseKey;
            return;
        }

        cache.replayPath = '';
        updateDisplay();

        const timelineData = await fetchTimeline(target.replay.filePath, timelineRate, correctionMode);
        applyTimeline(timelineData);
        cache.replayPath = target.replay.filePath;
        cache.loadBaseKey = baseKey;
        cache.loadKey = key;
    } catch (err) {
        cache.error = err.message;
        cache.loadKey = '';
        cache.loadBaseKey = '';
    } finally {
        cache.loading = false;
        updateDisplay();
    }
}

async function getTimelineMode() {
    try {
        const res = await fetch(`http://${LAZER_COMPARE_HOST}/debug-state`);
        if (!res.ok) return cache.correctionMode;
        const data = await res.json();
        cache.correctionMode = data.correctionMode || cache.correctionMode;
    } catch {
        // Keep the previous mode if LazerReplayCompare is still starting.
    }
    return cache.correctionMode;
}

async function fetchTimeline(filePath, rate, correction) {
    const params = new URLSearchParams({
        osr: filePath,
        osu: cache.osuPath,
        rate: rate.toFixed(4),
        correction,
    });
    const timelineRes = await fetch(`http://${LAZER_COMPARE_HOST}/timeline?${params}`);
    if (!timelineRes.ok) throw new Error(`/timeline ${timelineRes.status}`);
    return timelineRes.json();
}

function applyTimeline(data) {
    const frames = (Array.isArray(data.frames) ? data.frames : [])
        .map((f) => ({
            index: Number(f.index),
            score: Number(f.score),
        }))
        .filter((f) => Number.isFinite(f.index) && Number.isFinite(f.score))
        .sort((a, b) => a.index - b.index);

    if (frames.length === 0)
        throw new Error('timeline empty');

    cache.replayFrames = frames;
}

// --- WebSocket ---

function createSocket() {
    let reconnectTimer = null;

    const connect = () => {
        const socket = new WebSocket(`ws://${TOSU_HOST}/websocket/v2`);

        socket.addEventListener('open', () => {
            if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
            socket.send(`applyFilters:${JSON.stringify([
                'client',
                { field: 'beatmap', keys: ['checksum'] },
                { field: 'files', keys: ['beatmap'] },
                { field: 'folders', keys: ['songs', 'beatmap'] },
                { field: 'play', keys: ['score', 'hits', 'mods'] },
                { field: 'state', keys: ['name'] },
            ])}`);
        });

        socket.addEventListener('message', (event) => {
            try {
                const data = JSON.parse(event.data);
                if (data.error) return;

                if (data.client != null) cache.client = data.client;
                if (data.state?.name != null) cache.state = data.state.name;

                if (data.beatmap?.checksum && data.beatmap.checksum !== cache.beatmapChecksum) {
                    cache.beatmapChecksum = data.beatmap.checksum;
                    cache.hitIndex = 0;
                    cache.loadKey = '';
                    cache.loadBaseKey = '';
                }

                if (data.folders?.songs) cache.songsFolder = data.folders.songs;
                if (data.folders?.beatmap != null) cache.beatmapFolder = data.folders.beatmap;
                if (data.files?.beatmap) cache.beatmapFile = data.files.beatmap;

                if (data.play?.score != null) cache.score = Number(data.play.score) || 0;

                if (data.play?.hits != null) {
                    cache.hitIndex = getHitIndex(data.play.hits);
                }

                if (data.play?.mods != null) {
                    const nextKey = getCurrentModsKey(data.play.mods);
                    if (nextKey !== cache.modsKey) {
                        cache.modsKey = nextKey;
                        cache.loadKey = '';
                        cache.loadBaseKey = '';
                    }
                }

                updateOsuPath(cache);
                loadTimeline();
                updateDisplay();
            } catch (err) {
                console.error('[LazerSameModScoreDiff]', err);
            }
        });

        socket.addEventListener('close', () => {
            reconnectTimer = setTimeout(connect, 1000);
        });

        socket.addEventListener('error', (err) => {
            console.error('[LazerSameModScoreDiff] ws error', err);
        });
    };

    connect();
}

createSocket();
setInterval(refreshReplayTarget, 2000);
updateDisplay();
