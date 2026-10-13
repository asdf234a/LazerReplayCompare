// Comparison metrics shared by the overlays.
//
// Every metric is evaluated from a definition sent by LazerReplayCompare (`metrics` in /state, also /metrics), which
// comes from MetricRegistry in LazerReplayCompare.Core. Adding a metric there makes it available here without overlay
// changes. Until the app has answered, only Score and Acc are known: they read the game's own values and need no
// formula.

export const OFF = 'Off';

export const HIT_ALIASES = {
    Perfect: ['perfect', 'geki', 'Geki', '320'],
    Great: ['great', '300'],
    Good: ['good', 'katu', 'Katu', '200'],
    Ok: ['ok', '100'],
    Meh: ['meh', '50'],
    Miss: ['miss', '0'],
};

const JUDGEMENTS = ['Perfect', 'Great', 'Good', 'Ok', 'Meh', 'Miss'];

const FALLBACK_METRICS = [
    { key: 'Score', name: 'Score', source: 'score', format: 'integer', displayScale: 1, aliases: ['ScoreDiff'] },
    { key: 'Acc', name: 'Acc', source: 'accuracy', format: 'percent', displayScale: 100, maximum: 1, aliases: ['Accuracy', 'AccuracyDiff'] },
];

let definitions = FALLBACK_METRICS;

export function hitValue(hits, key) {
    if (!hits) return 0;
    if (hits[key] != null) return Number(hits[key]) || 0;
    for (const alias of HIT_ALIASES[key] || []) {
        if (hits[alias] != null) return Number(hits[alias]) || 0;
    }
    return 0;
}

/** Replaces the known metrics with the app's definitions (ignored when missing or malformed). */
export function setMetricDefinitions(list) {
    if (!Array.isArray(list) || list.length === 0) return;
    const valid = list.filter((metric) => metric && typeof metric.key === 'string' && typeof metric.source === 'string');
    if (valid.length > 0) definitions = valid;
}

export function getMetricDefinitions() {
    return definitions;
}

function findDefinition(key) {
    return definitions.find((metric) => metric.key === key);
}

/** Canonical key for a metric name or alias, OFF for "off"/"none", otherwise `fallback`. */
export function resolveMetric(name, fallback = 'Score') {
    const value = String(name || '').toLowerCase();
    if (!value) return fallback;
    if (value === 'off' || value === 'none') return OFF;

    const match = definitions.find((metric) =>
        metric.key.toLowerCase() === value || (metric.aliases || []).some((alias) => String(alias).toLowerCase() === value));
    return match ? match.key : fallback;
}

function rawValue(metric, source) {
    if (!source) return 0;
    if (metric.source === 'score') return Number(source.score) || 0;
    if (metric.source === 'accuracy') return Number(source.accuracy) || 0;

    const hits = source.hits || {};
    let sum = 0;
    for (const [judgement, weight] of Object.entries(metric.weights || {})) sum += hitValue(hits, judgement) * weight;

    if (!metric.average) return sum;
    const total = JUDGEMENTS.reduce((acc, judgement) => acc + hitValue(hits, judgement), 0);
    return total > 0 ? sum / total : 0;
}

/**
 * Scale factor a scaled timeline asks consumers to apply to `key`. Score and accuracy are rescaled inside the frames
 * by the app, so only hit-based metrics use the factor.
 */
export function timelineFactor(key, scaleMetric, scaleFactor) {
    const metric = findDefinition(key);
    return metric && metric.source === 'hits' && scaleMetric === key ? Number(scaleFactor) || 1 : 1;
}

/** The metric's value for a source ({ score, accuracy, hits }) in display units (accuracy as 0..100). */
export function metricValue(key, source, factor = 1) {
    const metric = findDefinition(key);
    if (!metric) return 0;

    let value = rawValue(metric, source);
    if (metric.round) value = Math.round(value);
    return value * factor * (Number(metric.displayScale) || 1);
}

/** Live value minus replay frame value, in display units. */
export function metricDiff(key, live, frame, frameFactor = 1) {
    return metricValue(key, live) - metricValue(key, frame, frameFactor);
}

export function isPercentMetric(key) {
    return findDefinition(key)?.format === 'percent';
}

/**
 * Formats a display-unit value: { sign, value, suffix, text }. `signed` adds "+" to positive values. Integers are
 * rounded (and signed by the rounded value); percentages keep two decimals.
 */
export function formatMetric(key, value, signed = false) {
    const number = Number(value) || 0;

    if (isPercentMetric(key)) {
        const sign = number > 0 ? (signed ? '+' : '') : number < 0 ? '-' : '';
        const text = Math.abs(number).toFixed(2);
        return { sign, value: text, suffix: '%', text: `${sign}${text}%` };
    }

    const rounded = Math.round(number);
    const sign = rounded > 0 ? (signed ? '+' : '') : rounded < 0 ? '-' : '';
    const text = Math.abs(rounded).toLocaleString('en-US');
    return { sign, value: text, suffix: '', text: `${sign}${text}` };
}
