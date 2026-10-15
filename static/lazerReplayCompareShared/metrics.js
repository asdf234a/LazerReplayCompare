// Items the overlays can show, and how their values are worked out.
//
// Built in: score, score_acc, acc, stable_acc, lazer_acc. More items, made of judgement counts, can be added in
// metrics.json next to this file; every overlay picks up changes to it while running.

export const OFF = 'off';

export const HIT_ALIASES = {
    Perfect: ['perfect', 'geki', 'Geki', '320'],
    Great: ['great', '300'],
    Good: ['good', 'katu', 'Katu', '200'],
    Ok: ['ok', '100'],
    Meh: ['meh', '50'],
    Miss: ['miss', '0'],
};

const JUDGEMENTS = ['Perfect', 'Great', 'Good', 'Ok', 'Meh', 'Miss'];

export function hitValue(hits, key) {
    if (!hits) return 0;
    if (hits[key] != null) return Number(hits[key]) || 0;
    for (const alias of HIT_ALIASES[key] || []) {
        if (hits[alias] != null) return Number(hits[alias]) || 0;
    }
    return 0;
}

function totalHits(hits) {
    return JUDGEMENTS.reduce((sum, judgement) => sum + hitValue(hits, judgement), 0);
}

function weighted(hits, weights) {
    let sum = 0;
    for (const [judgement, weight] of Object.entries(weights || {})) sum += hitValue(hits, judgement) * (Number(weight) || 0);
    return sum;
}

/** Judgement counts as an accuracy with these values per judgement (out of `max`), in percent; null before any. */
function countAccuracy(hits, values, max) {
    const total = totalHits(hits);
    return total > 0 ? (weighted(hits, values) / (max * total)) * 100 : null;
}

// osu!stable's osu!mania accuracy: MAX and 300 both count fully.
const STABLE_VALUES = { Perfect: 300, Great: 300, Good: 200, Ok: 100, Meh: 50 };
// osu!lazer's osu!mania accuracy: MAX is worth 305.
const LAZER_VALUES = { Perfect: 305, Great: 300, Good: 200, Ok: 100, Meh: 50 };

/**
 * A source is what an item is evaluated on: the live play or a replay frame, as
 * { score, accuracy (0..1), hits, perfect (score if every judgement so far were MAX, or null) }.
 * `scaleKey` names the app's metric a scaled timeline is stretched by (see timelineFactor).
 */
const BUILT_IN = {
    score: { name: 'Score', format: 'integer', scaleKey: 'Score', value: (s) => Number(s.score) || 0 },
    score_acc: {
        name: 'Score acc',
        format: 'percent',
        scaleKey: 'Score',
        perfect: true,
        value: (s) => (s.perfect > 0 ? (Number(s.score) || 0) / s.perfect * 100 : null),
    },
    acc: { name: 'Acc', format: 'percent', scaleKey: 'Acc', value: (s) => (Number(s.accuracy) || 0) * 100 },
    stable_acc: { name: 'Stable acc', format: 'percent', scaleKey: 'V1Acc', hits: true, value: (s) => countAccuracy(s.hits, STABLE_VALUES, 300) },
    lazer_acc: { name: 'Lazer acc', format: 'percent', scaleKey: 'LazerAcc', hits: true, value: (s) => countAccuracy(s.hits, LAZER_VALUES, 305) },
};

// older names still accepted in configs
const ALIASES = {
    scorediff: 'score',
    scoreaccuracy: 'score_acc',
    accuracy: 'acc',
    accuracydiff: 'acc',
    v1acc: 'stable_acc',
    stableaccuracy: 'stable_acc',
    lazeraccuracy: 'lazer_acc',
    none: OFF,
};

let custom = {};

function normalize(name) {
    return String(name ?? '').toLowerCase().replace(/[^a-z0-9]/g, '');
}

/** Replaces the custom items with metrics.json's ({ "metrics": { key: { name, weights, average, format, scale } } }). */
export function setCustomItems(json) {
    const next = {};
    for (const [key, item] of Object.entries(json?.metrics || {})) {
        if (!item || typeof item !== 'object' || !item.weights) continue;
        const scale = Number(item.scale) || 1;
        next[key] = {
            name: String(item.name || key),
            format: item.format === 'percent' ? 'percent' : 'integer',
            // scaled timelines stretch the score; these keep the replay's own judgement counts
            scaleKey: 'Score',
            round: Boolean(item.round),
            value: (s) => {
                const sum = weighted(s.hits, item.weights);
                if (!item.average) return sum * scale;
                const total = totalHits(s.hits);
                return total > 0 ? (sum / total) * scale : null;
            },
        };
    }
    custom = next;
}

function definition(key) {
    return BUILT_IN[key] || custom[key] || null;
}

/** The item a config names (any case, with or without "_"), OFF, or `fallback` when unknown. */
export function resolveItem(name, fallback = 'score') {
    const wanted = normalize(name);
    if (!wanted) return fallback;
    if (wanted === 'off' || ALIASES[wanted] === OFF) return OFF;

    for (const key of [...Object.keys(BUILT_IN), ...Object.keys(custom)]) {
        if (normalize(key) === wanted) return key;
    }

    return ALIASES[wanted] || fallback;
}

export function itemName(key) {
    return definition(key)?.name ?? key;
}

/** Whether the item needs the perfect scores (/perfect). */
export function needsPerfect(key) {
    return Boolean(definition(key)?.perfect);
}

/** The app metric a scaled timeline should be stretched by for this item. */
export function scaleKeyOf(key) {
    return definition(key)?.scaleKey ?? 'Score';
}

/**
 * Factor a scaled timeline asks consumers to apply to an item made of judgement counts (the app rescales score and
 * accuracy inside the frames itself).
 */
export function timelineFactor(key, scaleMetric, scaleFactor) {
    const item = definition(key);
    return item?.hits && scaleMetric && scaleMetric.toLowerCase() === item.scaleKey.toLowerCase() ? Number(scaleFactor) || 1 : 1;
}

/** The item's value for a source, in display units (percent items as 0..100); null when it can't be worked out yet. */
export function itemValue(key, source, factor = 1) {
    const item = definition(key);
    if (!item || !source) return null;

    let value = item.value(source);
    if (value == null || !Number.isFinite(value)) return null;
    if (item.round) value = Math.round(value);
    return value * factor;
}

export function isPercent(key) {
    return definition(key)?.format === 'percent';
}

/**
 * Formats a display-unit value: { sign, value, suffix, text }. `signed` adds "+" to positive values. Integers are
 * rounded (and signed by the rounded value); percentages keep two decimals.
 */
export function formatItem(key, value, signed = false) {
    const number = Number(value) || 0;

    if (isPercent(key)) {
        const text = Math.abs(number).toFixed(2);
        const shown = Number(text) === 0 ? 0 : number;
        const sign = shown > 0 ? (signed ? '+' : '') : shown < 0 ? '-' : '';
        return { sign, value: text, suffix: '%', text: `${sign}${text}%` };
    }

    const rounded = Math.round(number);
    const sign = rounded > 0 ? (signed ? '+' : '') : rounded < 0 ? '-' : '';
    const text = Math.abs(rounded).toLocaleString('en-US');
    return { sign, value: text, suffix: '', text: `${sign}${text}` };
}

// --- config files ---

/** Parses JSON that may carry // and /* *\/ comments (outside strings). */
export function parseConfig(text) {
    let out = '';
    for (let i = 0; i < text.length; i++) {
        const char = text[i];
        if (char === '"') {
            const start = i;
            for (i++; i < text.length && text[i] !== '"'; i++) if (text[i] === '\\') i++;
            out += text.slice(start, i + 1);
        } else if (char === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') i++;
            out += '\n';
        } else if (char === '/' && text[i + 1] === '*') {
            i += 2;
            while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) i++;
            i++;
        } else {
            out += char;
        }
    }

    // trailing commas are a common slip when editing by hand
    return JSON.parse(out.replace(/,(\s*[}\]])/g, '$1'));
}

/**
 * Loads a config file now and again every `interval` ms, calling `onChange` with the parsed object whenever its text
 * changes (so editing it takes effect without reloading the overlay). A missing or broken file keeps the last one.
 */
export function watchConfig(url, onChange, interval = 2000) {
    let last = null;

    const load = async () => {
        try {
            const res = await fetch(`${url}?t=${Date.now()}`, { cache: 'no-store' });
            if (!res.ok) return;
            const text = await res.text();
            if (text === last) return;
            const parsed = parseConfig(text);
            last = text;
            onChange(parsed);
        } catch (err) {
            console.warn(`[LazerReplayCompare] ${url}:`, err.message || err);
        }
    };

    load();
    setInterval(load, interval);
}

watchConfig(new URL('./metrics.json', import.meta.url).href, setCustomItems, 5000);
