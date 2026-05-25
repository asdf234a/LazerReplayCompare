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
const subValueElement = document.querySelector('#subValue');

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
    hits: {},
    hitIndex: 0,
    modsKey: 'NM|1.0000',
    replayFrames: [],
    replayPath: '',
    targetMode: '',
    correctionMode: 'corrected',
    displayMetrics: {
        main: 'Score',
        sub: 'Off',
    },
    timelineBaseKey: '',
    loadBaseKey: '',
    loadKey: '',
    loadedStateKey: '',
    checkingState: false,
    loading: false,
    error: '',
    displayedDiff: 0,
    displayedScaledValue: 0,
    hasDisplayedDiff: false,
    displayedSign: '',
    displayedValue: '0',
    displayAnimation: 0,
    displayedSubScaledValue: 0,
    hasDisplayedSub: false,
    subAnimation: 0,
};

function chooseReplayTarget(replaysData) {
    return chooseSharedReplayTarget(replaysData, cache.modsKey, 'selected', 'auto');
}

// --- Frame lookup ---

function getReplayScoreAtIndex(hitIndex) {
    if (cache.timelineBaseKey !== getBaseKey()) return null;

    let lo = 0, hi = cache.replayFrames.length - 1, found = null;
    while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        const frame = cache.replayFrames[mid];
        if (frame.index <= hitIndex) { found = frame; lo = mid + 1; }
        else hi = mid - 1;
    }
    return found;
}

// --- Display ---

function formatSigned(value) {
    const rounded = Math.round(Number(value) || 0);
    return {
        sign: rounded > 0 ? '+' : rounded < 0 ? '-' : '',
        value: Math.abs(rounded).toLocaleString('en-US'),
    };
}

function isPlaying() {
    return cache.state === 'play' || cache.state === 'playing';
}

function getBaseKey() {
    return `${cache.beatmapChecksum}|${cache.osuPath}|${cache.modsKey}`;
}

function setDisplay(metric, visible) {
    const scaledValue = scaleMetricValue(metric);
    renderAnimatedCounter(metric, scaledValue, visible && cache.hasDisplayedDiff);

    cache.displayedDiff = Math.round(Number(metric.value) || 0);
    cache.displayedScaledValue = scaledValue;
    cache.hasDisplayedDiff = visible;
    cache.displayedSign = metric.formatted.sign;
    cache.displayedValue = metric.formatted.value;

    if (!visible) {
        diffValueElement.style.display = 'none';
        updateContainerVisibility();
        return;
    }
    diffValueElement.style.display = '';
    diffElement.className = `scoreDiff visible ${metric.value > 0 ? 'positive' : metric.value < 0 ? 'negative' : 'neutral'}`;
}

function renderStaticMetric(metric) {
    diffValueElement.innerHTML =
        `<span class="metricPrefix">${metric.prefix || ''}</span>` +
        `<span class="diffSign">${metric.formatted.sign}</span>` +
        `<span class="diffNumber">${metric.formatted.value}${metric.suffix || ''}</span>`;
}

function scaleMetricValue(metric) {
    return metric.suffix === '%'
        ? Math.round((Number(metric.value) || 0) * 100)
        : Math.round(Number(metric.value) || 0);
}

function formatMetricFromScaled(metric, scaledValue) {
    if (metric.suffix === '%') {
        const value = scaledValue / 100;
        return {
            sign: value > 0 ? '+' : value < 0 ? '-' : '',
            value: Math.abs(value).toFixed(2),
        };
    }

    return formatSigned(scaledValue);
}

function renderSubMetric(metric) {
    if (!metric.visible) {
        subValueElement.textContent = '';
        subValueElement.style.display = 'none';
        cache.hasDisplayedSub = false;
        updateContainerVisibility();
        return;
    }

    subValueElement.style.display = '';
    subValueElement.className = 'subValue neutral';
    renderAnimatedSubCounter(metric, cache.hasDisplayedSub);
    cache.displayedSubScaledValue = scaleMetricValue(metric);
    cache.hasDisplayedSub = true;
    updateContainerVisibility();
}

function updateContainerVisibility() {
    const hasMain = diffValueElement.style.display !== 'none';
    const hasSub = subValueElement.style.display !== 'none' && subValueElement.textContent !== '';
    if (hasMain || hasSub) {
        diffElement.classList.add('visible');
    } else {
        diffElement.className = 'scoreDiff';
    }
}

function renderDiffValue(formatted, animate, direction) {
    const previous = cache.displayedValue || formatted.value;
    const current = formatted.value;
    const offset = previous.length - current.length;
    const parts = [`<span class="diffSign">${formatted.sign}</span><span class="diffNumber">`];

    for (let i = 0; i < current.length; i++) {
        const char = current[i];
        const previousChar = previous[i + offset];
        const shouldRoll = animate && /\d/.test(char) && /\d/.test(previousChar || '') && previousChar !== char;

        if (shouldRoll) {
            const sequence = digitSequence(previousChar, char, direction);
            const distance = sequence.length - 1;
            const duration = Math.min(420, 110 + distance * 36);
            parts.push(
                `<span class="digitRoll">` +
                `<span class="digitStack" style="--roll-distance:${distance};--roll-duration:${duration}ms;">` +
                sequence.map((digit) => `<span>${digit}</span>`).join('') +
                `</span>` +
                `</span>`
            );
        } else {
            parts.push(`<span class="${/\d/.test(char) ? 'digitStatic' : 'digitSeparator'}">${char}</span>`);
        }
    }

    parts.push('</span>');
    diffValueElement.innerHTML = parts.join('');
}

function digitSequence(fromChar, toChar, direction) {
    const from = Number(fromChar);
    const to = Number(toChar);
    const sequence = [from];
    let current = from;
    const step = direction === 'down' ? -1 : 1;

    while (current !== to) {
        current = (current + step + 10) % 10;
        sequence.push(current);
    }

    return sequence;
}

function renderAnimatedCounter(metric, targetValue, animate) {
    const startValue = cache.displayedScaledValue;
    const endValue = Math.round(Number(targetValue) || 0);
    const animationId = ++cache.displayAnimation;

    if (!animate || startValue === endValue) {
        renderDiffValue(formatMetricFromScaled(metric, endValue), false, 'up');
        return;
    }

    const distance = Math.abs(endValue - startValue);
    const steps = Math.min(10, Math.max(3, Math.ceil(distance / Math.max(1, distance / 8))));
    const duration = Math.min(360, 130 + steps * 22);
    const started = performance.now();

    const tick = (now) => {
        if (animationId !== cache.displayAnimation) return;

        const progress = Math.min(1, (now - started) / duration);
        const eased = 1 - Math.pow(1 - progress, 3);
        const value = Math.round(startValue + (endValue - startValue) * eased);
        renderDiffValue(formatMetricFromScaled(metric, value), false, endValue < startValue ? 'down' : 'up');

        if (progress < 1) {
            requestAnimationFrame(tick);
        } else {
            renderDiffValue(formatMetricFromScaled(metric, endValue), false, endValue < startValue ? 'down' : 'up');
        }
    };

    requestAnimationFrame(tick);
}

function renderAnimatedSubCounter(metric, animate) {
    const startValue = cache.displayedSubScaledValue;
    const endValue = scaleMetricValue(metric);
    const animationId = ++cache.subAnimation;

    const render = (scaledValue) => {
        const formatted = formatMetricFromScaled(metric, scaledValue);
        subValueElement.innerHTML =
            `<span class="metricPrefix">${metric.prefix || ''}</span>` +
            `<span class="diffNumber">${formatted.value}${metric.suffix || ''}</span>`;
    };

    if (!animate || startValue === endValue) {
        render(endValue);
        return;
    }

    const distance = Math.abs(endValue - startValue);
    const steps = Math.min(10, Math.max(3, Math.ceil(distance / Math.max(1, distance / 8))));
    const duration = Math.min(360, 130 + steps * 22);
    const started = performance.now();

    const tick = (now) => {
        if (animationId !== cache.subAnimation) return;

        const progress = Math.min(1, (now - started) / duration);
        const eased = 1 - Math.pow(1 - progress, 3);
        const value = Math.round(startValue + (endValue - startValue) * eased);
        render(value);

        if (progress < 1) {
            requestAnimationFrame(tick);
        } else {
            render(endValue);
        }
    };

    requestAnimationFrame(tick);
}

function updateDisplay() {
    if (!isPlaying()) {
        setDisplay(getMetric('Score', null), false);
        renderSubMetric(getMetric('Off', null));
        return;
    }

    if (cache.replayFrames.length === 0 || cache.hitIndex <= 0) {
        setDisplay(getMetric('Score', null), false);
        renderSubMetric(getLiveMetric(cache.displayMetrics.sub));
        return;
    }

    const frame = getReplayScoreAtIndex(cache.hitIndex);
    if (frame == null) {
        setDisplay(getMetric('Score', null), false);
        renderSubMetric(getLiveMetric(cache.displayMetrics.sub));
        return;
    }

    setDisplay(getMetric(cache.displayMetrics.main, frame), true);
    renderSubMetric(getLiveMetric(cache.displayMetrics.sub));
}

// --- Load timeline ---

async function loadTimeline() {
    return loadTimelineInternal(false);
}

function formatSignedDecimal(value, digits = 2) {
    const number = Number(value) || 0;
    return {
        sign: number > 0 ? '+' : number < 0 ? '-' : '',
        value: Math.abs(number).toFixed(digits),
    };
}

const HIT_ALIASES = {
    Perfect: ['perfect', 'geki', 'Geki', '320'],
    Great: ['great', '300'],
    Good: ['good', 'katu', 'Katu', '200'],
    Ok: ['ok', '100'],
    Meh: ['meh', '50'],
    Miss: ['miss', '0'],
};

function hitValue(hits, key) {
    if (!hits) return 0;
    if (hits[key] != null) return Number(hits[key]) || 0;
    for (const alias of HIT_ALIASES[key] || []) {
        if (hits[alias] != null) return Number(hits[alias]) || 0;
    }
    return 0;
}

function ppAccuracy(hits) {
    const total =
        hitValue(hits, 'Perfect') +
        hitValue(hits, 'Great') +
        hitValue(hits, 'Good') +
        hitValue(hits, 'Ok') +
        hitValue(hits, 'Meh') +
        hitValue(hits, 'Miss');

    if (total <= 0) return 0;

    const value =
        hitValue(hits, 'Perfect') * 100 +
        hitValue(hits, 'Great') * 93.75 +
        hitValue(hits, 'Good') * 62.5 +
        hitValue(hits, 'Ok') * 31.25 +
        hitValue(hits, 'Meh') * 15.625;

    return value / total;
}

function v1Accuracy(hits) {
    const total =
        hitValue(hits, 'Perfect') +
        hitValue(hits, 'Great') +
        hitValue(hits, 'Good') +
        hitValue(hits, 'Ok') +
        hitValue(hits, 'Meh') +
        hitValue(hits, 'Miss');

    if (total <= 0) return 0;

    const value =
        hitValue(hits, 'Perfect') * 100 +
        hitValue(hits, 'Great') * 100 +
        hitValue(hits, 'Good') * (200 / 3) +
        hitValue(hits, 'Ok') * (100 / 3) +
        hitValue(hits, 'Meh') * (50 / 3);

    return value / total;
}

function ppScore(hits) {
    return Math.round(
        hitValue(hits, 'Perfect') * 100 +
        hitValue(hits, 'Great') * 93.75 +
        hitValue(hits, 'Good') * 62.5 +
        hitValue(hits, 'Ok') * 31.25 +
        hitValue(hits, 'Meh') * 15.625
    );
}

function bmsScore(hits) {
    return hitValue(hits, 'Perfect') * 2 + hitValue(hits, 'Great');
}

function normalizeMetric(metric, fallback = 'Score') {
    const value = String(metric || '').toLowerCase();
    if (value === 'scorediff' || value === 'score') return 'Score';
    if (value === 'accuracydiff' || value === 'accuracy' || value === 'acc') return 'Acc';
    if (value === 'bms' || value === 'bmsscore') return 'Bms';
    if (value === 'judgediff' || value === 'judge' || value === 'ppacc') return 'PpAcc';
    if (value === 'ppscore') return 'PpScore';
    if (value === 'v1acc') return 'V1Acc';
    if (value === 'off' || value === 'none') return 'Off';
    return fallback;
}

function updateDisplayMetrics(data) {
    if (!data?.displayMetrics) return;
    cache.displayMetrics.main = normalizeMetric(data.displayMetrics.main, cache.displayMetrics.main);
    cache.displayMetrics.sub = normalizeMetric(data.displayMetrics.sub, cache.displayMetrics.sub);
}

function getMetric(metric, frame) {
    const key = normalizeMetric(metric);
    if (!frame || key === 'Off') return { key, value: 0, formatted: { sign: '', value: '' }, prefix: '', suffix: '', visible: false };

    if (key === 'PpAcc') {
        const value = ppAccuracy(cache.hits) - ppAccuracy(frame.hits);
        return { key, value, formatted: formatSignedDecimal(value, 2), prefix: '', suffix: '%', visible: true };
    }

    if (key === 'V1Acc') {
        const value = v1Accuracy(cache.hits) - v1Accuracy(frame.hits);
        return { key, value, formatted: formatSignedDecimal(value, 2), prefix: '', suffix: '%', visible: true };
    }

    if (key === 'PpScore') {
        const value = ppScore(cache.hits) - ppScore(frame.hits);
        return { key, value, formatted: formatSigned(value), prefix: '', suffix: '', visible: true };
    }

    if (key === 'Bms') {
        const value = bmsScore(cache.hits) - bmsScore(frame.hits);
        return { key, value, formatted: formatSigned(value), prefix: '', suffix: '', visible: true };
    }

    if (key === 'Acc') {
        const value = (cache.accuracy - (Number(frame.accuracy) || 0)) * 100;
        return { key, value, formatted: formatSignedDecimal(value, 2), prefix: '', suffix: '%', visible: true };
    }

    const value = cache.score - frame.score;
    return { key: 'Score', value, formatted: formatSigned(value), prefix: '', suffix: '', visible: true };
}

function getLiveMetric(metric) {
    const key = normalizeMetric(metric);
    if (key === 'Off') return { key, value: 0, formatted: { sign: '', value: '' }, prefix: '', suffix: '', visible: false };

    if (key === 'PpAcc') {
        const value = ppAccuracy(cache.hits);
        return { key, value, formatted: { sign: '', value: value.toFixed(2) }, prefix: '', suffix: '%', visible: true };
    }

    if (key === 'V1Acc') {
        const value = v1Accuracy(cache.hits);
        return { key, value, formatted: { sign: '', value: value.toFixed(2) }, prefix: '', suffix: '%', visible: true };
    }

    if (key === 'PpScore') {
        const value = ppScore(cache.hits);
        return { key, value, formatted: { sign: '', value: Math.round(value).toLocaleString('en-US') }, prefix: '', suffix: '', visible: true };
    }

    if (key === 'Bms') {
        const value = bmsScore(cache.hits);
        return { key, value, formatted: { sign: '', value: value.toLocaleString('en-US') }, prefix: '', suffix: '', visible: true };
    }

    if (key === 'Acc') {
        const value = cache.accuracy * 100;
        return { key, value, formatted: { sign: '', value: value.toFixed(2) }, prefix: '', suffix: '%', visible: true };
    }

    return { key: 'Score', value: cache.score, formatted: { sign: '', value: Math.round(cache.score).toLocaleString('en-US') }, prefix: '', suffix: '', visible: true };
}

async function refreshReplayTarget() {
    return loadTimelineInternal(true);
}

async function checkTimelineState(force = false) {
    if (!isPlaying() || cache.client !== 'lazer' || !cache.beatmapChecksum || !cache.osuPath || cache.checkingState) return;

    cache.checkingState = true;
    try {
        const res = await fetch(`http://${LAZER_COMPARE_HOST}/state`);
        if (!res.ok) throw new Error(`/state ${res.status}`);
        const data = await res.json();
        cache.correctionMode = data.correctionMode || cache.correctionMode;
        updateDisplayMetrics(data);

        if (data.beatmapMd5 !== cache.beatmapChecksum) {
            cache.loadedStateKey = '';
            cache.loadKey = '';
            cache.loadBaseKey = '';
            cache.timelineBaseKey = '';
            cache.replayFrames = [];
            updateDisplay();
            return;
        }

        const stateKey = `${getBaseKey()}|${data.timelineKey || data.selectedReplayKey || ''}|${data.replayListVersion || ''}`;
        if (!force && stateKey === cache.loadedStateKey && cache.timelineBaseKey === getBaseKey() && cache.replayFrames.length > 0) {
            updateDisplay();
            return;
        }

        await loadTimelineInternal(true, stateKey, data.correctionMode || cache.correctionMode);
    } catch (err) {
        cache.error = err.message || String(err);
        updateDisplay();
    } finally {
        cache.checkingState = false;
    }
}

async function loadTimelineInternal(forceTargetCheck, stateKey = '', correctionOverride = '') {
    if (!isPlaying() || cache.client !== 'lazer' || !cache.beatmapChecksum || !cache.osuPath || cache.loading) return;

    const baseKey = getBaseKey();
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
            cache.timelineBaseKey = '';
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
        const correctionMode = correctionOverride || await getTimelineMode();
        const key = `${baseKey}|${target.replay.filePath}|${timelineRate.toFixed(4)}|${correctionMode}`;
        if (key === cache.loadKey && cache.replayFrames.length > 0) {
            cache.loadBaseKey = baseKey;
            return;
        }

        cache.replayPath = '';
        updateDisplay();

        const rawKey = `${baseKey}|${target.replay.filePath}|${timelineRate.toFixed(4)}|raw`;
        const rawTimeline = await fetchTimeline(target.replay.filePath, timelineRate, 'raw');
        if (baseKey !== getBaseKey()) return;
        applyTimeline(rawTimeline, baseKey);
        cache.replayPath = target.replay.filePath;
        cache.loadBaseKey = baseKey;
        cache.loadKey = rawKey;
        cache.loadedStateKey = stateKey || rawKey;
        updateDisplay();

        if (correctionMode === 'corrected') {
            const timelineData = await fetchTimeline(target.replay.filePath, timelineRate, correctionMode);
            if (baseKey !== getBaseKey()) return;
            applyTimeline(timelineData, baseKey);
            cache.replayPath = target.replay.filePath;
            cache.loadBaseKey = baseKey;
            cache.loadKey = key;
            cache.loadedStateKey = stateKey || key;
        }
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
        const res = await fetch(`http://${LAZER_COMPARE_HOST}/state`);
        if (!res.ok) return cache.correctionMode;
        const data = await res.json();
        cache.correctionMode = data.correctionMode || cache.correctionMode;
        updateDisplayMetrics(data);
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

function applyTimeline(data, baseKey) {
    const frames = (Array.isArray(data.frames) ? data.frames : [])
        .map((f) => ({
            index: Number(f.index),
            score: Number(f.score),
            accuracy: Number(f.accuracy || 0),
            hits: f.hits || {},
        }))
        .filter((f) => Number.isFinite(f.index) && Number.isFinite(f.score))
        .sort((a, b) => a.index - b.index);

    if (frames.length === 0)
        throw new Error('timeline empty');

    cache.replayFrames = frames;
    cache.timelineBaseKey = baseKey;
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
                { field: 'play', keys: ['score', 'accuracy', 'hits', 'mods'] },
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
                    cache.loadedStateKey = '';
                }

                if (data.folders?.songs) cache.songsFolder = data.folders.songs;
                if (data.folders?.beatmap != null) cache.beatmapFolder = data.folders.beatmap;
                if (data.files?.beatmap) cache.beatmapFile = data.files.beatmap;

                if (data.play?.score != null) cache.score = Number(data.play.score) || 0;
                if (data.play?.accuracy != null) {
                    const value = Number(data.play.accuracy) || 0;
                    cache.accuracy = value > 1 ? value / 100 : value;
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
                        cache.loadedStateKey = '';
                    }
                }

                updateOsuPath(cache);
                if (cache.timelineBaseKey !== getBaseKey() || cache.replayFrames.length === 0) checkTimelineState();
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
setInterval(checkTimelineState, 2000);
updateDisplay();
