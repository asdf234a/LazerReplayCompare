export const RATE_EPSILON = 0.0001;
export const IGNORED_GAMEPLAY_MODS = new Set(['MR', 'MIRROR']);

export function normalizeAcronym(acronym) {
    return String(acronym || '').trim().toUpperCase();
}

export function normalizeRate(rate) {
    const value = Number(rate);
    return Number.isFinite(value) && value > 0 ? value : 1;
}

export function parseRateValue(value) {
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

export function defaultRateForMods(acronyms) {
    if (acronyms.includes('DT') || acronyms.includes('NC')) return 1.5;
    if (acronyms.includes('HT') || acronyms.includes('DC')) return 0.75;
    return 1;
}

export function buildModsKey(acronyms, rate) {
    const mods = [...new Set(acronyms.map(normalizeAcronym).filter((mod) => mod && !IGNORED_GAMEPLAY_MODS.has(mod)))].sort().join('+') || 'NM';
    return `${mods}|${normalizeRate(rate).toFixed(4)}`;
}

export function getRateFromSettings(settings) {
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

export function getCurrentModsKey(mods) {
    const modArray = Array.isArray(mods?.array) ? mods.array : [];
    const acronyms = modArray.map((mod) => normalizeAcronym(mod.acronym));
    const settingsRate = modArray.map((mod) => getRateFromSettings(mod.settings)).find((v) => v != null);
    const rate = settingsRate ?? normalizeRate(mods?.rate);
    return buildModsKey(acronyms, rate !== 1 ? rate : defaultRateForMods(acronyms));
}

export function getReplayModsKey(replay) {
    const mods = Array.isArray(replay?.mods) ? replay.mods : [];
    const acronyms = mods.map((mod) => normalizeAcronym(mod.acronym));
    const rate = mods.map((mod) => getRateFromSettings(mod.settings)).find((v) => v != null)
        ?? defaultRateForMods(acronyms);
    return buildModsKey(acronyms, rate);
}

export function isSameRate(replay, currentModsKey) {
    const replayRate = Number(getReplayModsKey(replay).split('|')[1]);
    const currentRate = Number(currentModsKey.split('|')[1]);
    return Math.abs(replayRate - currentRate) <= RATE_EPSILON;
}

export function chooseReplayTarget(replaysData, currentModsKey, selectedMode, autoMode) {
    const replays = Array.isArray(replaysData.replays) ? replaysData.replays : [];
    const selected = replaysData.selectedReplay;

    if (selected) {
        return { replay: selected, mode: selectedMode, error: '' };
    }

    const replay = replays
        .filter((entry) => isSameRate(entry, currentModsKey))
        .sort((a, b) => Number(b.score || 0) - Number(a.score || 0))[0] ?? null;

    return { replay, mode: autoMode, error: replay ? '' : 'no same-rate replay' };
}

export function getHitIndex(hits) {
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

export function updateOsuPath(cache) {
    if (!cache.songsFolder || !cache.beatmapFile) {
        cache.osuPath = '';
        return;
    }

    cache.osuPath = cache.client === 'lazer'
        ? `${cache.songsFolder}\\${cache.beatmapFile}`
        : `${cache.songsFolder}\\${cache.beatmapFolder || ''}\\${cache.beatmapFile}`;
}
