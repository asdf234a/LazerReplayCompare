import {
    chooseReplayTarget,
    getCurrentModsKey,
    getHitIndex,
    getReplayModsKey,
    updateOsuPath,
} from '../lazerReplayCompareShared/overlayState.js';

const TOSU_HOST = window.TOSU_HOST || location.host || '127.0.0.1:24050';
const LAZER_COMPARE_HOST = window.LAZER_COMPARE_HOST || '127.0.0.1:24052';
const $ = (id) => document.getElementById(id);

const state = {
    client: 'lazer',
    gameState: '',
    beatmapChecksum: '',
    songsFolder: '',
    beatmapFolder: '',
    beatmapFile: '',
    osuPath: '',
    modsKey: 'NM|1.0000',
    score: 0,
    accuracy: 0,
    combo: 0,
    hits: {},
    hitIndex: 0,
    replayFrames: [],
    replayBaseKey: '',
    replayPlayer: '',
    replayMods: '',
    targetMode: 'AUTO BEST',
    timelineSource: '',
    timelineTotalNotes: 0,
    correctionMode: 'corrected',
    loadingKey: '',
    loadedKey: '',
    targetKey: '',
    error: '',
};

const HIT_ROWS = [
    ['Perfect', 'P'],
    ['Great', 'Gr'],
    ['Good', 'Gd'],
    ['Ok', 'Ok'],
    ['Meh', 'Me'],
    ['Miss', 'Mi'],
];

const HIT_ALIASES = {
    Perfect: ['perfect', 'geki', 'Geki'],
    Great: ['great', '300'],
    Good: ['good', 'katu', 'Katu'],
    Ok: ['ok', '100'],
    Meh: ['meh', '50'],
    Miss: ['miss', '0'],
};

function isPlaying() {
    const name = String(state.gameState || '').toLowerCase();
    return name === 'play' || name === 'playing';
}

function isVisible() {
    const name = String(state.gameState || '').toLowerCase();
    return isPlaying() || name === 'result' || name === 'results' || name === 'resultscreen';
}

function baseKey() {
    return `${state.beatmapChecksum}|${state.osuPath}|${state.modsKey}`;
}

function fmtScore(value) {
    return Math.round(Number(value) || 0).toLocaleString('en-US');
}

function fmtAcc(value) {
    return `${((Number(value) || 0) * 100).toFixed(2)}%`;
}

function fmtDiff(value) {
    const n = Math.round(Number(value) || 0);
    return `${n > 0 ? '+' : ''}${n.toLocaleString('en-US')}`;
}

function fmtAccDiff(value) {
    const n = (Number(value) || 0) * 100;
    return `${n > 0 ? '+' : ''}${n.toFixed(2)}%`;
}

function hitValue(hits, key) {
    if (!hits) return 0;
    if (hits[key] != null) return Number(hits[key]) || 0;
    for (const alias of HIT_ALIASES[key] || []) {
        if (hits[alias] != null) return Number(hits[alias]) || 0;
    }
    return 0;
}

function findReplayFrame(hitIndex) {
    if (state.replayBaseKey !== baseKey()) return null;

    let lo = 0;
    let hi = state.replayFrames.length - 1;
    let found = null;

    while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        const frame = state.replayFrames[mid];
        if (frame.index <= hitIndex) {
            found = frame;
            lo = mid + 1;
        } else {
            hi = mid - 1;
        }
    }

    return found;
}

function renderHits(liveHits, replayHits) {
    const grid = $('hitsGrid');

    for (let i = 0; i < HIT_ROWS.length; i++) {
        const [key, label] = HIT_ROWS[i];
        const live = hitValue(liveHits, key);
        const replay = hitValue(replayHits, key);
        const diff = live - replay;

        let row = grid.children[i];
        if (!row) {
            row = document.createElement('div');
            row.className = `hit-cell ${key === 'Miss' ? 'miss' : ''}`;
            row.innerHTML = '<span class="hit-label"></span><span class="hit-live"></span><span class="hit-diff-val"></span><span class="hit-replay"></span>';
            grid.appendChild(row);
        }

        row.children[0].textContent = label;
        row.children[1].textContent = live;
        row.children[2].textContent = diff === 0 ? '' : `${diff > 0 ? '+' : ''}${diff}`;
        row.children[2].className = `hit-diff-val ${diff > 0 ? 'positive' : diff < 0 ? 'negative' : ''}`;
        row.children[3].textContent = replay;
    }
}

function render() {
    const visible = isVisible();
    $('panel').className = `panel${visible ? ' visible' : ''}`;
    if (!visible) return;

    $('liveScore').textContent = fmtScore(state.score);
    $('liveAcc').textContent = fmtAcc(state.accuracy);
    $('liveCombo').textContent = `x${state.combo}`;
    $('replayPlayer').textContent = state.replayPlayer || 'REPLAY';
    $('replayLabel').textContent = state.replayMods || '-';
    $('targetMode').textContent = state.targetMode || 'AUTO BEST';

    const frame = findReplayFrame(state.hitIndex);
    if (!frame) {
        $('replayScore').textContent = '-';
        $('replayAcc').textContent = '-';
        $('replayCombo').textContent = '-';
        $('scoreDiff').textContent = state.loadingKey ? '...' : state.error ? '!' : '-';
        $('scoreDiff').className = 'score-diff neutral';
        $('accDiff').textContent = '-';
        $('accDiff').className = 'acc-diff';
        $('hitsGrid').style.display = 'none';
    } else {
        const scoreDiff = state.score - frame.score;
        const accDiff = state.accuracy - frame.accuracy;

        $('replayScore').textContent = fmtScore(frame.score);
        $('replayAcc').textContent = fmtAcc(frame.accuracy);
        $('replayCombo').textContent = `x${frame.combo}`;
        $('scoreDiff').textContent = fmtDiff(scoreDiff);
        $('scoreDiff').className = `score-diff ${scoreDiff > 0 ? 'positive' : scoreDiff < 0 ? 'negative' : 'neutral'}`;
        $('accDiff').textContent = fmtAccDiff(accDiff);
        $('accDiff').className = `acc-diff ${accDiff > 0 ? 'positive' : accDiff < 0 ? 'negative' : 'neutral'}`;
        renderHits(state.hits, frame.hits);
        $('hitsGrid').style.display = '';
    }

    $('timelineInfo').textContent = state.loadingKey
        ? 'loading...'
        : state.error
            ? `error: ${state.error}`
            : state.timelineSource && state.replayBaseKey === baseKey()
                ? `${state.timelineSource} - ${state.timelineTotalNotes} notes`
                : '-';
}

async function fetchJson(path) {
    const res = await fetch(`http://${LAZER_COMPARE_HOST}${path}`);
    if (!res.ok) throw new Error(`${path.split('?')[0]} ${res.status}`);
    return res.json();
}

async function getCorrectionMode() {
    try {
        const data = await fetchJson('/state');
        state.correctionMode = data.correctionMode || state.correctionMode;
    } catch {
        // Keep the previous mode while the app is starting.
    }
    return state.correctionMode;
}

async function fetchTimeline(replayPath, osuPath, rate, correction) {
    const query = new URLSearchParams({
        osr: replayPath,
        osu: osuPath,
        rate: rate.toFixed(4),
        correction,
    });
    return fetchJson(`/timeline?${query}`);
}

function applyTimeline(data, key, sourceSuffix = '') {
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

    state.replayFrames = frames;
    state.replayBaseKey = key;
    state.timelineSource = `${data.source || ''}${sourceSuffix}`;
    state.timelineTotalNotes = Number(data.totalNotes || 0);
}

async function loadTimeline(force = false) {
    if (!isPlaying() || state.client !== 'lazer' || !state.beatmapChecksum || !state.osuPath) return;

    const currentBaseKey = baseKey();
    if (!force && state.loadedKey === currentBaseKey) return;
    if (state.loadingKey === currentBaseKey) return;

    state.loadingKey = currentBaseKey;
    state.error = '';
    render();

    try {
        const replays = await fetchJson('/replays');
        if (replays.beatmapMd5 !== state.beatmapChecksum) {
            state.loadedKey = '';
            state.loadingKey = '';
            render();
            return;
        }

        const target = chooseReplayTarget(replays, state.modsKey, 'SELECTED', 'AUTO BEST');
        state.targetMode = target.mode;
        if (!target.replay) {
            state.error = target.error;
            state.loadedKey = currentBaseKey;
            state.targetKey = '';
            return;
        }

        state.replayPlayer = target.replay.player || 'unknown';
        state.replayMods = target.replay.modsText || getReplayModsKey(target.replay);

        const replayRate = parseFloat(getReplayModsKey(target.replay).split('|')[1]) || 1;
        const rate = target.mode === 'SELECTED'
            ? replayRate
            : parseFloat(state.modsKey.split('|')[1]) || 1;
        const correction = await getCorrectionMode();
        const targetKey = `${currentBaseKey}|${target.replay.filePath}|${rate.toFixed(4)}|${correction}`;

        if (!force && state.targetKey === targetKey && state.replayBaseKey === currentBaseKey) {
            state.loadedKey = currentBaseKey;
            return;
        }

        const raw = await fetchTimeline(target.replay.filePath, state.osuPath, rate, 'raw');
        if (currentBaseKey !== baseKey()) return;
        applyTimeline(raw, currentBaseKey, correction === 'corrected' ? ' (temporary)' : '');
        state.loadedKey = currentBaseKey;
        state.targetKey = targetKey;
        render();

        if (correction === 'corrected') {
            const corrected = await fetchTimeline(target.replay.filePath, state.osuPath, rate, 'corrected');
            if (currentBaseKey !== baseKey()) return;
            applyTimeline(corrected, currentBaseKey);
            state.targetKey = targetKey;
        }
    } catch (err) {
        if (currentBaseKey === baseKey()) {
            state.error = err.message || String(err);
            state.loadedKey = '';
        }
    } finally {
        if (state.loadingKey === currentBaseKey) state.loadingKey = '';
        render();
    }
}

function updateFromTosu(data) {
    if (data.client != null) state.client = data.client;
    if (data.state?.name != null) state.gameState = data.state.name;

    if (data.beatmap?.checksum && data.beatmap.checksum !== state.beatmapChecksum) {
        state.beatmapChecksum = data.beatmap.checksum;
        state.hitIndex = 0;
        state.loadedKey = '';
        state.targetKey = '';
        state.error = '';
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
        const nextModsKey = getCurrentModsKey(data.play.mods);
        if (nextModsKey !== state.modsKey) {
            state.modsKey = nextModsKey;
            state.loadedKey = '';
            state.targetKey = '';
            state.error = '';
        }
    }

    updateOsuPath(state);
}

function connectTosu() {
    const socket = new WebSocket(`ws://${TOSU_HOST}/websocket/v2`);

    socket.addEventListener('open', () => {
        socket.send(`applyFilters:${JSON.stringify([
            'client',
            { field: 'beatmap', keys: ['checksum'] },
            { field: 'files', keys: ['beatmap'] },
            { field: 'folders', keys: ['songs', 'beatmap'] },
            { field: 'play', keys: ['score', 'accuracy', 'hits', 'combo', 'mods'] },
            { field: 'state', keys: ['name'] },
        ])}`);
    });

    socket.addEventListener('message', (event) => {
        try {
            const data = JSON.parse(event.data);
            if (data.error) return;
            updateFromTosu(data);
            loadTimeline();
            render();
        } catch (err) {
            console.error('[LazerReplayCompareLive]', err);
        }
    });

    socket.addEventListener('close', () => setTimeout(connectTosu, 1000));
    socket.addEventListener('error', (err) => console.error('[LazerReplayCompareLive] ws error', err));
}

connectTosu();
render();
