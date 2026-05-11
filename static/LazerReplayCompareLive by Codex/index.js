const TOSU_HOST = window.TOSU_HOST || location.host || '127.0.0.1:24050';
const LAZER_COMPARE_HOST = window.LAZER_COMPARE_HOST || '127.0.0.1:24052';
const RATE_EPSILON = 0.0001;
const IGNORED_GAMEPLAY_MODS = new Set(['MR', 'MIRROR']);

const $ = (id) => document.getElementById(id);

const cache = {
    state: '',
    client: 'lazer',
    beatmapChecksum: '',
    songsFolder: '',
    beatmapFolder: '',
    beatmapFile: '',
    osuPath: '',
    score: 0,
    accuracy: 0,
    combo: 0,
    hits: {},
    hitIndex: 0,
    modsKey: 'NM|1.0000',
    replayFrames: [],
    replayMods: '',
    replayPlayer: '',
    targetMode: '',
    targetFilePath: '',
    timelineSource: '',
    timelineTotalNotes: 0,
    loadBaseKey: '',
    loadKey: '',
    loading: false,
    error: '',
};

// --- Mod key helpers ---

function normalizeAcronym(acronym) {
    return String(acronym || '').trim().toUpperCase();
}

function normalizeRate(rate) {
    const value = Number(rate);
    return Number.isFinite(value) && value > 0 ? value : 1;
}

function parseRateValue(value) {
    if (value == null) return null;

    if (typeof value === 'number') {
        return Number.isFinite(value) && value > 0 ? value : null;
    }

    if (typeof value === 'string') {
        const match = value.match(/[\d.]+/);
        const rate = match ? Number(match[0]) : Number(value);
        return Number.isFinite(rate) && rate > 0 ? rate : null;
    }

    if (typeof value === 'object') {
        if ('value' in value) return parseRateValue(value.value);
        if ('Value' in value) return parseRateValue(value.Value);
    }

    return null;
}

function defaultRateForMods(acronyms) {
    if (acronyms.includes('DT') || acronyms.includes('NC')) return 1.5;
    if (acronyms.includes('HT') || acronyms.includes('DC')) return 0.75;
    return 1;
}

function buildModsKey(acronyms, rate) {
    const mods = [...new Set(acronyms.map(normalizeAcronym).filter((mod) => mod && !IGNORED_GAMEPLAY_MODS.has(mod)))].sort().join('+') || 'NM';
    return `${mods}|${normalizeRate(rate).toFixed(4)}`;
}

function getCurrentModsKey(mods) {
    const modArray = Array.isArray(mods?.array) ? mods.array : [];
    const acronyms = modArray.map((mod) => normalizeAcronym(mod.acronym));
    const settingsRate = modArray.map((mod) => getRateFromSettings(mod.settings)).find((v) => v != null);
    const rate = settingsRate ?? normalizeRate(mods?.rate);
    return buildModsKey(acronyms, rate !== 1 ? rate : defaultRateForMods(acronyms));
}

function getRateFromSettings(settings) {
    if (!settings || typeof settings !== 'object') return null;
    for (const [key, value] of Object.entries(settings)) {
        const name = String(key).toLowerCase();
        if (name.includes('speed') || name.includes('rate')) {
            const rate = parseRateValue(value);
            if (rate != null) return rate;
        }
    }
    return null;
}

function getReplayModsKey(replay) {
    const mods = Array.isArray(replay?.mods) ? replay.mods : [];
    const acronyms = mods.map((mod) => normalizeAcronym(mod.acronym));
    const rate = mods.map((mod) => getRateFromSettings(mod.settings)).find((v) => v != null)
        ?? defaultRateForMods(acronyms);
    return buildModsKey(acronyms, rate);
}

function isSameModAndRate(replay) {
    const [replayMods, replayRate] = getReplayModsKey(replay).split('|');
    const [currentMods, currentRate] = cache.modsKey.split('|');
    return replayMods === currentMods && Math.abs(Number(replayRate) - Number(currentRate)) <= RATE_EPSILON;
}

function chooseReplayTarget(replaysData) {
    const replays = Array.isArray(replaysData.replays) ? replaysData.replays : [];
    const selected = replaysData.selectedReplay;

    if (selected) {
        return { replay: selected, mode: 'SELECTED', error: '' };
    }

    const replay = replays
        .filter(isSameModAndRate)
        .sort((a, b) => Number(b.score || 0) - Number(a.score || 0))[0] ?? null;

    return { replay, mode: 'AUTO BEST', error: replay ? '' : 'no same-mod replay' };
}

// --- Hit index ---

function getHitIndex(hits) {
    if (!hits) return 0;
    const legacyCount =
        Number(hits[0] ?? hits['0'] ?? 0) +
        Number(hits[50] ?? hits['50'] ?? 0) +
        Number(hits[100] ?? hits['100'] ?? 0) +
        Number(hits[300] ?? hits['300'] ?? 0) +
        Number(hits.geki ?? hits.Geki ?? 0) +
        Number(hits.katu ?? hits.Katu ?? 0);
    if (legacyCount > 0) return legacyCount;
    return (
        Number(hits.miss ?? hits.Miss ?? 0) +
        Number(hits.meh ?? hits.Meh ?? 0) +
        Number(hits.ok ?? hits.Ok ?? 0) +
        Number(hits.good ?? hits.Good ?? 0) +
        Number(hits.great ?? hits.Great ?? 0) +
        Number(hits.perfect ?? hits.Perfect ?? 0) +
        Number(hits.geki ?? hits.Geki ?? 0) +
        Number(hits.katu ?? hits.Katu ?? 0)
    );
}

// --- osu path ---

function updateOsuPath() {
    if (!cache.songsFolder || !cache.beatmapFile) { cache.osuPath = ''; return; }
    cache.osuPath = cache.client === 'lazer'
        ? `${cache.songsFolder}\\${cache.beatmapFile}`
        : `${cache.songsFolder}\\${cache.beatmapFolder || ''}\\${cache.beatmapFile}`;
}

// --- Frame lookup ---

function getReplayFrameAtIndex(hitIndex) {
    let lo = 0, hi = cache.replayFrames.length - 1, found = null;
    while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        const frame = cache.replayFrames[mid];
        if (frame.index <= hitIndex) { found = frame; lo = mid + 1; }
        else hi = mid - 1;
    }
    return found;
}

// --- Format helpers ---

function fmtScore(v) {
    return Math.round(Number(v) || 0).toLocaleString('en-US');
}

function fmtAcc(v) {
    return ((Number(v) || 0) * 100).toFixed(2) + '%';
}

function fmtDiff(v) {
    const n = Math.round(Number(v) || 0);
    return (n > 0 ? '+' : '') + n.toLocaleString('en-US');
}

function fmtAccDiff(v) {
    const n = (Number(v) || 0) * 100;
    return (n > 0 ? '+' : '') + n.toFixed(2) + '%';
}

// --- Hit counts ---

const HIT_ROWS = [
    ['Perfect', 'P'],
    ['Great', 'Gr'],
    ['Good', 'Gd'],
    ['Ok', 'Ok'],
    ['Meh', 'Me'],
    ['Miss', 'Mi'],
];

// tosu stable/legacy keys that map to each PascalCase judgement name
const LEGACY_HIT_KEYS = {
    Perfect: ['perfect', 'geki', 'Geki'],
    Great:   ['great',   '300'],
    Good:    ['good',    'katu', 'Katu'],
    Ok:      ['ok',      '100'],
    Meh:     ['meh',     '50'],
    Miss:    ['miss',    '0'],
};

function hitVal(hits, key) {
    if (!hits) return 0;
    if (hits[key] != null) return Number(hits[key]);
    const aliases = LEGACY_HIT_KEYS[key] ?? [key.toLowerCase()];
    for (const alias of aliases) {
        if (hits[alias] != null) return Number(hits[alias]);
    }
    return 0;
}

function renderHits(liveHits, replayHits) {
    const grid = $('hitsGrid');
    const cells = grid.children;

    for (let i = 0; i < HIT_ROWS.length; i++) {
        const [key, label] = HIT_ROWS[i];
        const liveVal = hitVal(liveHits, key);
        const replayVal = hitVal(replayHits, key);
        const diff = liveVal - replayVal;

        let cell = cells[i];
        if (!cell) {
            cell = document.createElement('div');
            cell.className = `hit-cell ${key === 'Miss' ? 'miss' : ''}`;
            cell.innerHTML = `<span class="hit-label"></span><span class="hit-live"></span><span class="hit-diff-val"></span><span class="hit-replay"></span>`;
            grid.appendChild(cell);
        }

        cell.children[0].textContent = label;
        cell.children[1].textContent = liveVal;
        cell.children[2].textContent = diff !== 0 ? (diff > 0 ? '+' : '') + diff : '';
        cell.children[2].className = `hit-diff-val ${diff > 0 ? 'positive' : diff < 0 ? 'negative' : ''}`;
        cell.children[3].textContent = replayVal;
    }
}

// --- Render ---

function isPlaying() {
    return cache.state === 'play' || cache.state === 'playing';
}

function render() {
    const panel = $('panel');
    const playing = isPlaying();

    panel.className = `panel${playing ? ' visible' : ''}`;
    if (!playing) return;

    $('liveScore').textContent = fmtScore(cache.score);
    $('liveAcc').textContent = fmtAcc(cache.accuracy);
    $('liveCombo').textContent = `x${cache.combo}`;

    const frame = getReplayFrameAtIndex(cache.hitIndex);
    const hitsGrid = $('hitsGrid');

    if (frame) {
        const scoreDiff = cache.score - frame.score;
        const accDiff = cache.accuracy - frame.accuracy;

        $('replayScore').textContent = fmtScore(frame.score);
        $('replayAcc').textContent = fmtAcc(frame.accuracy);
        $('replayCombo').textContent = `x${frame.combo}`;

        const sdEl = $('scoreDiff');
        sdEl.textContent = fmtDiff(scoreDiff);
        sdEl.className = `score-diff ${scoreDiff > 0 ? 'positive' : scoreDiff < 0 ? 'negative' : 'neutral'}`;

        const adEl = $('accDiff');
        adEl.textContent = fmtAccDiff(accDiff);
        adEl.className = `acc-diff ${accDiff > 0 ? 'positive' : accDiff < 0 ? 'negative' : 'neutral'}`;

        renderHits(cache.hits, frame.hits);
        hitsGrid.style.display = '';
    } else {
        $('replayScore').textContent = '-';
        $('replayAcc').textContent = '-';
        $('replayCombo').textContent = '-';

        const sdEl = $('scoreDiff');
        sdEl.textContent = cache.loading ? '...' : cache.error ? '!' : '-';
        sdEl.className = 'score-diff neutral';

        $('accDiff').textContent = '-';
        $('accDiff').className = 'acc-diff';

        hitsGrid.style.display = 'none';
    }

    $('replayPlayer').textContent = cache.replayPlayer || 'REPLAY';
    $('replayLabel').textContent = cache.replayMods || '-';
    $('targetMode').textContent = cache.targetMode || 'AUTO BEST';

    $('timelineInfo').textContent = cache.loading
        ? 'loading...'
        : cache.error
            ? `error: ${cache.error}`
            : cache.timelineSource
                ? `${cache.timelineSource} - ${cache.timelineTotalNotes} notes`
                : '-';
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
            cache.replayMods = '';
            cache.replayPlayer = '';
            cache.targetFilePath = '';
            cache.timelineSource = '';
            cache.timelineTotalNotes = 0;
            cache.error = target.error;
            cache.loadBaseKey = baseKey;
            cache.loadKey = '';
            return;
        }

        const replayRate = parseFloat(getReplayModsKey(target.replay).split('|')[1]) || 1;
        const timelineRate = target.mode === 'SELECTED'
            ? replayRate
            : parseFloat(cache.modsKey.split('|')[1]) || 1;
        const key = `${baseKey}|${target.replay.filePath}|${timelineRate.toFixed(4)}`;
        if (key === cache.loadKey && cache.replayFrames.length > 0) {
            cache.loadBaseKey = baseKey;
            return;
        }

        cache.replayMods = target.replay.modsText || getReplayModsKey(target.replay);
        cache.replayPlayer = target.replay.player || 'unknown';
        cache.targetFilePath = target.replay.filePath || '';
        cache.timelineSource = '';
        cache.timelineTotalNotes = 0;
        render();

        const rawData = await fetchTimeline(target.replay.filePath, timelineRate, 'raw');
        applyTimeline(rawData);
        render();

        const correctedData = await fetchTimeline(target.replay.filePath, timelineRate, 'corrected');
        applyTimeline(correctedData);
        cache.loadBaseKey = baseKey;
        cache.loadKey = key;
    } catch (err) {
        cache.error = err.message;
        cache.loadKey = '';
        cache.loadBaseKey = '';
    } finally {
        cache.loading = false;
        render();
    }
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
    cache.timelineSource = data.source || '';
    cache.timelineTotalNotes = data.totalNotes || 0;

    const frames = (Array.isArray(data.frames) ? data.frames : [])
        .map((f) => ({
            index: Number(f.index),
            score: Number(f.score),
            accuracy: Number(f.accuracy ?? 0),
            combo: Number(f.combo ?? 0),
            maxCombo: Number(f.maxCombo ?? 0),
            hits: f.hits ?? {},
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
                { field: 'play', keys: ['score', 'accuracy', 'hits', 'combo', 'mods'] },
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
                if (data.play?.accuracy != null) {
                    const raw = Number(data.play.accuracy) || 0;
                    // tosu sends accuracy in [0, 100]; normalize to [0, 1] for fmtAcc
                    cache.accuracy = raw > 1 ? raw / 100 : raw;
                }
                if (data.play?.combo != null) {
                    cache.combo = Number(data.play.combo.current ?? data.play.combo) || 0;
                }

                if (data.play?.hits != null) {
                    cache.hits = data.play.hits;
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

                updateOsuPath();
                loadTimeline();
                render();
            } catch (err) {
                console.error('[LazerReplayCompareLive]', err);
            }
        });

        socket.addEventListener('close', () => {
            reconnectTimer = setTimeout(connect, 1000);
        });

        socket.addEventListener('error', (err) => {
            console.error('[LazerReplayCompareLive] ws error', err);
        });
    };

    connect();
}

createSocket();
setInterval(refreshReplayTarget, 2000);

render();
