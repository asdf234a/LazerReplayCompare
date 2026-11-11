// Items the overlays can show, and how their values are worked out.
//
// Built in: score, score_acc, score_acc_log, live_rank, acc, acc_log, stable_acc, lazer_acc, ma_ratio, and the Rivals items
// (rival_rank, rival_gap, rival_reach). Which item each overlay shows, and any custom items (made of judgement counts, from
// the app's metrics.json), come from LazerReplayCompare's /state.

import { rivalStanding } from './rivals.js';

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
// PPacc: every step of a judgement counts, MAX against 300 too.
const PP_VALUES = { Perfect: 320, Great: 300, Good: 200, Ok: 100, Meh: 50 };

// What Acc log can measure: the judgement values, their maximum, and what the smallest loss of a judgement costs.
const ACC_LOG_KINDS = {
    stable: { values: STABLE_VALUES, max: 300, smallestLoss: 1 / 3 },
    lazer: { values: LAZER_VALUES, max: 305, smallestLoss: 5 / 305 },
    pp: { values: PP_VALUES, max: 320, smallestLoss: 20 / 320 },
};

/**
 * Score as a share of the best possible so far (0..1), or null. Exactly 1 while every judgement so far is a MAX, whatever
 * the two scores say (they can differ a little by then), and never above 1.
 */
function scoreShare(s) {
    const total = totalHits(s.hits);
    if (total > 0 && hitValue(s.hits, 'Perfect') === total) return 1;
    return s.perfect > 0 ? Math.min((Number(s.score) || 0) / s.perfect, 1) : null;
}

// How many 300s a run of nothing but MAX stands for on a log scale: e^-gamma, the mean of log(rate) after seeing none,
// for a Poisson rate with a flat prior.
const GREATS_IN_ALL_MAX = Math.exp(-0.5772156649015329);

/** Share of the score lost with `great` 300s among `total` judgements, nothing else but MAX, in the game being played. */
function lostToGreats(great, total) {
    const greats = great / total;
    if (stableClient) return greats / 32;
    const accuracy = 1 - greats / 61;
    return 0.85 * (1 - Math.pow(accuracy, 2 + 2 * accuracy));
}

/**
 * Share of the score lost so far, or null. Exactly as many 300s as GREATS_IN_ALL_MAX while every judgement is a MAX, so
 * it is never zero.
 */
function lostShare(s) {
    const total = totalHits(s.hits);
    if (total > 0 && hitValue(s.hits, 'Perfect') === total) return lostToGreats(GREATS_IN_ALL_MAX, total);
    const share = scoreShare(s);
    return share == null || share >= 1 ? null : 1 - share;
}

/** Share of a million that Score log's base lost: the fixed score's without that place, or if that place lost nothing. */
function baseLostShare(s) {
    const score = baseMode === 'rank' ? Number(s.rankScores?.[basePlace - 1]) : 0;
    return score > 0 && score < 1000000 ? 1 - score / 1000000 : 1 - baseScore / 1000000;
}

/**
 * Share of the accuracy lost so far, or null. Never zero: nothing lost counts as GREATS_IN_ALL_MAX of the smallest loss a
 * judgement can cost, the same way Score log treats all MAX.
 */
function lostAccuracy(s) {
    const total = totalHits(s.hits);
    if (total <= 0) return null;
    const kind = ACC_LOG_KINDS[accLogKind];
    const lost = 1 - weighted(s.hits, kind.values) / (kind.max * total);
    return lost > 1e-12 ? lost : (GREATS_IN_ALL_MAX * kind.smallestLoss) / total;
}

/**
 * A source is what an item is evaluated on: the live play or a replay frame, as
 * { score, accuracy (0..1), hits, perfect (score if every judgement so far were MAX, or null), rankScores (the beatmap's
 * leaderboard, best first, or nothing), rankOthers (the same without the player's own scores), and for the Rivals items:
 * index (judgements so far), perfectTotal (the best possible score of the whole beatmap), rivals (see rivals.js), self
 * (the replay file a frame belongs to, which is not raced against itself) }.
 * `scaleKey` names the app's metric a scaled timeline is stretched by (see timelineFactor).
 */
const BUILT_IN = {
    score: { name: 'Score', format: 'integer', scaleKey: 'Score', value: (s) => Number(s.score) || 0 },
    score_acc: {
        name: 'Score acc',
        format: 'percent',
        scaleKey: 'Score',
        perfect: true,
        value: (s) => {
            const share = scoreShare(s);
            return share == null ? null : share * 100;
        },
    },
    // 0 at its base (999,000 unless picked otherwise), +10 each time the score lost is halved (10 at 999,500). All MAX so
    // far has nothing lost, so it counts as GREATS_IN_ALL_MAX 300s: higher the longer it lasts, and the first 300 takes it
    // down by about 8.
    score_acc_log: {
        name: 'Score log',
        format: 'decimal',
        scaleKey: 'Score',
        perfect: true,
        rank: () => baseMode === 'rank',
        value: (s) => {
            const lost = lostShare(s);
            return lost == null ? null : 10 * Math.log2(baseLostShare(s) / lost);
        },
    },
    // Where the score would land at this pace (Score acc x 1,000,000), among other players: a tie stays behind them. Nothing
    // below 50th.
    live_rank: {
        name: 'Live rank',
        format: 'rank',
        scaleKey: 'Score',
        perfect: true,
        rank: () => true,
        value: (s) => {
            const share = scoreShare(s);
            if (share == null || !s.rankOthers?.length) return null;
            const expected = Math.floor(share * 1000000 + 1e-6);
            const place = s.rankOthers.filter((score) => score >= expected).length + 1;
            return place <= 50 ? place : null;
        },
    },
    acc: { name: 'Acc', format: 'percent', scaleKey: 'Acc', value: (s) => (Number(s.accuracy) || 0) * 100 },
    // 0 at its base accuracy (95% unless picked otherwise), +10 each time the accuracy lost is halved.
    acc_log: {
        name: 'Acc log',
        format: 'decimal',
        scaleKey: 'Score',
        value: (s) => {
            const lost = lostAccuracy(s);
            return lost == null ? null : 10 * Math.log2((1 - accLogBase / 100) / lost);
        },
    },
    stable_acc: { name: 'Stable acc', format: 'percent', scaleKey: 'V1Acc', hits: true, value: (s) => countAccuracy(s.hits, STABLE_VALUES, 300) },
    lazer_acc: { name: 'Lazer acc', format: 'percent', scaleKey: 'LazerAcc', hits: true, value: (s) => countAccuracy(s.hits, LAZER_VALUES, 305) },
    // MAX ÷ 300; with no 300 yet it divides by one, so it reads the MAX count until the first 300
    ma_ratio: {
        name: 'MAX : 300',
        format: 'decimal',
        scaleKey: 'Score',
        value: (s) => {
            const max = hitValue(s.hits, 'Perfect');
            const great = hitValue(s.hits, 'Great');
            return max + great > 0 ? max / Math.max(great, 1) : null;
        },
    },
    // The Rivals items: the place among the app's rivals (/rivals) at this note, always by score; nothing with no rivals.
    // A tie leaves the rival ahead. Like Live rank they are worked out for whatever source they are given: Live is the
    // play's standing, Replay the compared replay's (among the other rivals), Diff one minus the other.
    rival_rank: {
        name: 'Rival rank',
        format: 'rank',
        scaleKey: 'Score',
        perfect: true,
        rivals: true,
        value: (s) => rivalStanding(s)?.place ?? null,
    },
    // score minus the score of the rival directly above (-1,410); in first place, the lead over the second (+1,410)
    rival_gap: {
        name: 'Next rival',
        format: 'integer',
        signed: true,
        scaleKey: 'Score',
        perfect: true,
        rivals: true,
        value: (s) => rivalStanding(s)?.gap ?? null,
    },
    // 1 (OK) while the score so far plus the best possible score of the notes left is above the final score of the rival
    // directly above, or in first place; 0 (X) once it isn't
    rival_reach: {
        name: 'Next rival reachable',
        format: 'flag',
        scaleKey: 'Score',
        perfect: true,
        rivals: true,
        value: (s) => {
            const reachable = rivalStanding(s)?.reachable;
            return reachable == null ? null : reachable ? 1 : 0;
        },
    },
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

// osu!lazer never shows a rounded-up percentage (its FormatAccuracy cuts it off at two decimals); osu!stable rounds.
let cutPercentages = true;
let stableClient = false;
// what Score log measures from, picked in the app: a fixed score, or a place (1..50) on the beatmap's leaderboard
let baseMode = 'score';
let baseScore = 999000;
let basePlace = 50;
// what Acc log measures, and the accuracy (in percent) where it is 0, picked in the app
let accLogKind = 'lazer';
let accLogBase = 95;

/** The game being played ('lazer' or 'stable'): percentages are shown the way it shows them. */
export function setClient(client) {
    cutPercentages = client !== 'stable';
    stableClient = client === 'stable';
}

/** What Acc log measures: { kind: 'stable' | 'lazer' | 'pp', percent }, picked in the app. */
export function setAccBase(base) {
    if (!base) return;
    if (ACC_LOG_KINDS[base.kind]) accLogKind = base.kind;
    const percent = Number(base.percent);
    if (percent > 0 && percent < 100) accLogBase = percent;
}

/** What Score log measures from: { mode: 'score' | 'rank', score, rank }, picked in the app. */
export function setScoreBase(base) {
    if (!base) return;
    baseMode = base.mode === 'rank' ? 'rank' : 'score';
    const score = Math.round(Number(base.score));
    if (score > 0 && score < 1000000) baseScore = score;
    const place = Math.round(Number(base.rank));
    if (place >= 1 && place <= 50) basePlace = place;
}

/**
 * The value as shown: an osu!lazer percentage (not a difference) cut off at two decimals, so 98.769 shows 98.76 like
 * the game's own display; anything else unchanged (rounded by the format).
 */
export function shownValue(key, value, signed = false) {
    const number = Number(value) || 0;
    return cutPercentages && !signed && isPercent(key) ? Math.floor(number * 100 + 1e-6) / 100 : number;
}

function normalize(name) {
    return String(name ?? '').toLowerCase().replace(/[^a-z0-9]/g, '');
}

/**
 * Replaces the custom items with the app's (the non-built-in entries of /state's `items`:
 * { key, name, format, weights, average, per, add, log, pow, scale, round }). Ignored when missing.
 */
export function setItems(list) {
    if (!Array.isArray(list)) return;
    const next = {};
    for (const item of list) {
        if (!item || item.builtIn || typeof item.key !== 'string' || !item.weights) continue;
        const scale = Number(item.scale) || 1;
        const add = Number(item.add) || 0;
        const logBase = Number(item.log) || 0;
        const power = Number(item.pow) || 0;
        next[item.key] = {
            name: String(item.name || item.key),
            format: item.format === 'percent' || item.format === 'decimal' ? item.format : 'integer',
            // scaled timelines stretch the score; these keep the replay's own judgement counts
            scaleKey: 'Score',
            round: Boolean(item.round),
            value: (s) => {
                const total = totalHits(s.hits);
                let value = weighted(s.hits, item.weights) + add;
                if (item.per) {
                    const divisor = weighted(s.hits, item.per) + add;
                    if (divisor <= 0) return null;
                    value /= divisor;
                } else if (item.average) {
                    if (total <= 0) return null;
                    value /= total;
                }

                if (logBase > 0 && logBase !== 1) {
                    if (total <= 0 || value <= 0) return null;
                    value = Math.log(value) / Math.log(logBase);
                }

                // keeps the sign: a logarithm is negative below 1, where a fractional power has no value
                if (power > 0 && power !== 1) value = Math.sign(value) * Math.pow(Math.abs(value), power);

                return value * scale;
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

/** Whether the item measures from the beatmap's leaderboard. */
export function needsRank(key) {
    return Boolean(definition(key)?.rank?.());
}

/** Whether the item is about the app's rivals (/rivals). */
export function needsRivals(key) {
    return Boolean(definition(key)?.rivals);
}

/** Whether the item's own value carries a sign ("+" when positive, coloured like a difference). */
export function isSigned(key) {
    return Boolean(definition(key)?.signed);
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

/** Whether the item is shown with two decimals (percentages and ratios). */
export function hasDecimals(key) {
    const format = definition(key)?.format;
    return format === 'percent' || format === 'decimal';
}

/**
 * Formats a display-unit value: { sign, value, suffix, text }. `signed` adds "+" to positive values. Integers are
 * rounded (and signed by the rounded value); percentages and ratios keep two decimals (see shownValue); a flag is OK
 * (1) or X (0), and its difference a whole number.
 */
export function formatItem(key, value, signed = false) {
    const number = shownValue(key, value, signed);

    if (definition(key)?.format === 'flag' && !signed) {
        const text = number >= 0.5 ? 'OK' : 'X';
        return { sign: '', value: text, suffix: '', text };
    }

    signed = signed || isSigned(key);

    if (definition(key)?.format === 'rank' && !signed) {
        const text = `#${Math.round(number)}`;
        return { sign: '', value: text, suffix: '', text };
    }

    if (hasDecimals(key)) {
        const text = Math.abs(number).toFixed(2);
        const shown = Number(text) === 0 ? 0 : number;
        const sign = shown > 0 ? (signed ? '+' : '') : shown < 0 ? '-' : '';
        const suffix = isPercent(key) ? '%' : '';
        return { sign, value: text, suffix, text: `${sign}${text}${suffix}` };
    }

    const rounded = Math.round(number);
    const sign = rounded > 0 ? (signed ? '+' : '') : rounded < 0 ? '-' : '';
    const text = Math.abs(rounded).toLocaleString('en-US');
    return { sign, value: text, suffix: '', text: `${sign}${text}` };
}
