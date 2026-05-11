const TOSU_HOST = window.TOSU_HOST || location.host || '127.0.0.1:24050';
const LAZER_COMPARE_HOST = window.LAZER_COMPARE_HOST || '127.0.0.1:24052';
const RATE_EPSILON = 0.0001;
const IGNORED_GAMEPLAY_MODS = new Set(['MR', 'MIRROR']);

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
        return { replay: selected, mode: 'selected', error: '' };
    }

    const replay = replays
        .filter(isSameModAndRate)
        .sort((a, b) => Number(b.score || 0) - Number(a.score || 0))[0] ?? null;

    return { replay, mode: 'auto', error: replay ? '' : 'no same-mod replay' };
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
        return;
    }
    diffElement.className = `scoreDiff visible ${diff > 0 ? 'positive' : diff < 0 ? 'negative' : 'neutral'}`;
}

function updateDisplay() {
    if (!isPlaying()) {
        setDisplay(0, false);
        return;
    }

    if (cache.replayFrames.length === 0 || cache.hitIndex <= 0) {
        setDisplay(0, true);
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
            return;
        }

        const replayRate = parseFloat(getReplayModsKey(target.replay).split('|')[1]) || 1;
        const timelineRate = target.mode === 'selected'
            ? replayRate
            : parseFloat(cache.modsKey.split('|')[1]) || 1;
        const key = `${baseKey}|${target.replay.filePath}|${timelineRate.toFixed(4)}`;
        if (key === cache.loadKey && cache.replayFrames.length > 0) {
            cache.loadBaseKey = baseKey;
            return;
        }

        cache.replayPath = '';
        updateDisplay();

        const rawData = await fetchTimeline(target.replay.filePath, timelineRate, 'raw');
        applyTimeline(rawData);
        updateDisplay();

        const correctedData = await fetchTimeline(target.replay.filePath, timelineRate, 'corrected');
        applyTimeline(correctedData);
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

                updateOsuPath();
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
