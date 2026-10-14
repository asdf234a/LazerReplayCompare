import {
    chooseReplayTarget,
    getCurrentModsKey,
    getHitIndex,
    getReplayModsKey,
    isSupportedClient,
    timelineScaleMetric,
    updateOsuPath,
} from '../lazerReplayCompareShared/overlayState.js';
import {
    OFF,
    formatMetric,
    hitValue,
    metricDiff,
    metricValue as evaluateMetric,
    resolveMetric,
    setMetricDefinitions,
    timelineFactor,
} from '../lazerReplayCompareShared/metrics.js';

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
    livePlayer: 'PLAYER',
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
    scaleMetric: '',
    scaleFactor: 1,
    displayMetrics: {
        main: 'Score',
        sub: 'Acc',
    },
    loadingKey: '',
    loadedKey: '',
    loadedStateKey: '',
    checkingState: false,
    targetKey: '',
    noReplayKey: '',
    error: '',
};

const HIT_ROWS = [
    ['Perfect', '320'],
    ['Great', '300'],
    ['Good', '200'],
    ['Ok', '100'],
    ['Meh', '50'],
    ['Miss', 'Miss'],
];

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

function updateDisplayMetrics(data) {
    setMetricDefinitions(data?.metrics);
    if (!data?.displayMetrics) return;
    state.displayMetrics.main = resolveMetric(data.displayMetrics.main, state.displayMetrics.main);
    state.displayMetrics.sub = resolveMetric(data.displayMetrics.sub, state.displayMetrics.sub);
}

function frameFactor(key) {
    return timelineFactor(key, state.scaleMetric, state.scaleFactor);
}

function metricValue(metric, frame) {
    const key = resolveMetric(metric);
    if (!frame) return { text: '-', className: 'neutral', hidden: key === OFF };
    if (key === OFF) return { text: '', className: 'neutral', hidden: true };

    const value = metricDiff(key, liveMetricSource(), frame, frameFactor(key));
    return {
        text: formatMetric(key, value, true).text,
        className: value > 0 ? 'positive' : value < 0 ? 'negative' : 'neutral',
        hidden: false,
    };
}

function metricAbsoluteValue(metric, source, factor = 1) {
    const key = resolveMetric(metric);
    if (!source || key === OFF) return { text: '-', hidden: key === OFF };
    return { text: formatMetric(key, evaluateMetric(key, source, factor)).text, hidden: false };
}

function liveMetricSource() {
    return {
        score: state.score,
        accuracy: state.accuracy,
        hits: state.hits,
    };
}

function renderSideMetrics(frame) {
    const liveSource = liveMetricSource();
    const liveMain = metricAbsoluteValue(state.displayMetrics.main, liveSource);
    const liveSub = metricAbsoluteValue(state.displayMetrics.sub, liveSource);
    const replayMain = metricAbsoluteValue(state.displayMetrics.main, frame, frameFactor(resolveMetric(state.displayMetrics.main)));
    const replaySub = metricAbsoluteValue(state.displayMetrics.sub, frame, frameFactor(resolveMetric(state.displayMetrics.sub)));

    $('liveScore').textContent = liveMain.text;
    $('liveAcc').textContent = liveSub.hidden ? '' : liveSub.text;
    $('liveAcc').style.display = liveSub.hidden ? 'none' : '';
    $('replayScore').textContent = replayMain.text;
    $('replayAcc').textContent = replaySub.hidden ? '' : replaySub.text;
    $('replayAcc').style.display = replaySub.hidden ? 'none' : '';
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

function renderHits(liveHits, replayHits, showReplay = true) {
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
        if (showReplay && diff !== 0) {
            row.children[1].innerHTML = `${live} <span class="hit-inline-diff ${diff > 0 ? 'positive' : 'negative'}">(${diff > 0 ? '+' : ''}${diff})</span>`;
        } else {
            row.children[1].textContent = live;
        }
        row.children[2].textContent = '';
        row.children[2].className = `hit-diff-val ${showReplay ? diff > 0 ? 'positive' : diff < 0 ? 'negative' : '' : ''}`;
        row.children[3].textContent = showReplay ? replay : '';
    }
}

function render() {
    const visible = isVisible();
    $('panel').className = `panel${visible ? ' visible' : ''}`;
    if (!visible) return;

    $('liveCombo').textContent = `x${state.combo}`;
    $('replayPlayer').textContent = state.replayPlayer || 'REPLAY';
    $('replayLabel').textContent = state.replayMods || '-';
    $('targetMode').textContent = state.targetMode || 'AUTO BEST';
    $('livePlayer').textContent = state.livePlayer || 'PLAYER';
    $('liveLabel').textContent = state.modsKey.split('|')[0] || 'NM';

    const frame = findReplayFrame(state.hitIndex);
    renderSideMetrics(frame);
    if (!frame) {
        $('replayCombo').textContent = '-';
        $('scoreDiff').textContent = '-';
        $('scoreDiff').className = 'score-diff neutral';
        $('accDiff').textContent = '-';
        $('accDiff').className = 'acc-diff';
        $('accDiff').style.display = state.displayMetrics.sub === 'Off' ? 'none' : '';
        renderHits(state.hits, null, false);
        $('hitsGrid').style.display = '';
    } else {
        const mainMetric = metricValue(state.displayMetrics.main, frame);
        const subMetric = metricValue(state.displayMetrics.sub, frame);

        $('replayCombo').textContent = `x${frame.combo}`;
        $('scoreDiff').textContent = mainMetric.text;
        $('scoreDiff').className = `score-diff ${mainMetric.className}`;
        $('accDiff').textContent = subMetric.text;
        $('accDiff').className = `acc-diff ${subMetric.className}`;
        $('accDiff').style.display = subMetric.hidden ? 'none' : '';
        renderHits(state.hits, frame.hits);
        $('hitsGrid').style.display = '';
    }

    $('timelineInfo').textContent = state.timelineSource && state.replayBaseKey === baseKey()
        ? `${state.timelineSource} - ${state.timelineTotalNotes} notes`
        : state.loadingKey
            ? 'loading...'
            : state.noReplayKey === baseKey()
                ? 'no replay'
                : state.error
                    ? `error: ${state.error}`
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
        updateDisplayMetrics(data);
    } catch {
        // Keep the previous mode while the app is starting.
    }
    return state.correctionMode;
}

async function checkTimelineState(force = false) {
    if (!isPlaying() || !isSupportedClient(state.client) || !state.beatmapChecksum || !state.osuPath || state.checkingState) return;

    state.checkingState = true;
    try {
        const data = await fetchJson('/state');
        state.correctionMode = data.correctionMode || state.correctionMode;
        updateDisplayMetrics(data);

        if (data.beatmapMd5 !== state.beatmapChecksum) {
            state.loadedStateKey = '';
            state.loadedKey = '';
            state.targetKey = '';
            state.replayFrames = [];
            state.replayBaseKey = '';
            render();
            return;
        }

        const timelineStateKey = `${baseKey()}|${data.timelineKey || data.selectedReplayKey || ''}|${data.replayListVersion || ''}`;
        if (!force && timelineStateKey === state.loadedStateKey && (state.replayBaseKey === baseKey() || state.noReplayKey === baseKey())) {
            render();
            return;
        }

        await loadTimeline(true, timelineStateKey, data.correctionMode || state.correctionMode);
    } catch (err) {
        state.error = err.message || String(err);
        render();
    } finally {
        state.checkingState = false;
    }
}

async function fetchTimeline(replayPath, osuPath, rate, correction, scaleMetric = '') {
    const query = new URLSearchParams({
        osr: replayPath,
        osu: osuPath,
        rate: rate.toFixed(4),
        correction,
        // osu!stable replays are scored the osu!stable way when the live play is in osu!stable
        client: state.client,
    });
    if (scaleMetric) query.set('metric', scaleMetric);
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
    state.scaleMetric = data.scaleMetric || '';
    state.scaleFactor = Number(data.scaleFactor) || 1;
    state.timelineSource = `${data.source || ''}${sourceSuffix}`;
    state.timelineTotalNotes = Number(data.totalNotes || 0);
}

async function loadTimeline(force = false, timelineStateKey = '', correctionOverride = '') {
    if (!isPlaying() || !isSupportedClient(state.client) || !state.beatmapChecksum || !state.osuPath) return;

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
            state.noReplayKey = '';
            render();
            return;
        }

        const target = chooseReplayTarget(replays, state.modsKey, 'SELECTED', 'AUTO BEST', state.client);
        state.targetMode = target.mode;
        if (!target.replay) {
            state.error = '';
            state.loadedKey = currentBaseKey;
            state.loadedStateKey = timelineStateKey || `${currentBaseKey}|no-replay`;
            state.targetKey = '';
            state.noReplayKey = currentBaseKey;
            state.replayFrames = [];
            state.replayBaseKey = '';
            state.timelineSource = '';
            state.timelineTotalNotes = 0;
            state.replayPlayer = 'NO REPLAY';
            state.replayMods = state.modsKey.split('|')[0] || 'NM';
            return;
        }

        state.noReplayKey = '';
        state.replayPlayer = target.replay.player || 'unknown';
        state.replayMods = target.replay.modsText || getReplayModsKey(target.replay);

        const replayRate = parseFloat(getReplayModsKey(target.replay).split('|')[1]) || 1;
        const rate = target.mode === 'SELECTED'
            ? replayRate
            : parseFloat(state.modsKey.split('|')[1]) || 1;
        const correction = correctionOverride || await getCorrectionMode();
        const scaleMetric = timelineScaleMetric(correction, target.replay, state.client, state.displayMetrics.main);
        const targetKey = `${currentBaseKey}|${state.client}|${target.replay.filePath}|${rate.toFixed(4)}|${correction}|${scaleMetric}`;

        if (!force && state.targetKey === targetKey && state.replayBaseKey === currentBaseKey) {
            state.loadedKey = currentBaseKey;
            return;
        }

        const raw = await fetchTimeline(target.replay.filePath, state.osuPath, rate, 'raw');
        if (currentBaseKey !== baseKey()) return;
        applyTimeline(raw, currentBaseKey, correction !== 'raw' ? ' (temporary)' : '');
        state.loadedKey = currentBaseKey;
        state.loadedStateKey = timelineStateKey || targetKey;
        state.targetKey = targetKey;
        render();

        if (correction !== 'raw') {
            const corrected = await fetchTimeline(target.replay.filePath, state.osuPath, rate, correction, scaleMetric);
            if (currentBaseKey !== baseKey()) return;
            applyTimeline(corrected, currentBaseKey);
            state.targetKey = targetKey;
            state.loadedStateKey = timelineStateKey || targetKey;
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
    if (data.profile != null) {
        state.livePlayer = data.profile.username ||
            data.profile.name ||
            data.profile.user?.username ||
            data.profile.user?.name ||
            state.livePlayer;
    }

    if (data.beatmap?.checksum && data.beatmap.checksum !== state.beatmapChecksum) {
        state.beatmapChecksum = data.beatmap.checksum;
        state.hitIndex = 0;
        state.loadedKey = '';
        state.loadedStateKey = '';
        state.targetKey = '';
        state.noReplayKey = '';
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
            state.loadedStateKey = '';
            state.targetKey = '';
            state.noReplayKey = '';
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
            { field: 'profile', keys: ['username', 'name', 'user'] },
            { field: 'state', keys: ['name'] },
        ])}`);
    });

    socket.addEventListener('message', (event) => {
        try {
            const data = JSON.parse(event.data);
            if (data.error) return;
            updateFromTosu(data);
            if (state.replayBaseKey !== baseKey() && state.noReplayKey !== baseKey()) checkTimelineState();
            render();
        } catch (err) {
            console.error('[LazerReplayCompareLive]', err);
        }
    });

    socket.addEventListener('close', () => setTimeout(connectTosu, 1000));
    socket.addEventListener('error', (err) => console.error('[LazerReplayCompareLive] ws error', err));
}

connectTosu();
setInterval(checkTimelineState, 2000);
render();
