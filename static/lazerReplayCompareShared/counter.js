// An animated number for the Main and Sub overlays: counts to its new value and colours the sign of a difference.

import { formatItem, hasDecimals, isSigned, shownValue } from './metrics.js';

export function createCounter(element) {
    let shown = false;
    let current = 0;        // in hundredths for items with decimals
    let key = '';
    let animation = 0;
    // a count under way: where the number stands, and whether it is shown as a difference
    let counting = false;
    let countingSigned = false;
    let standing = 0;

    const scaled = (itemKey, value) => (hasDecimals(itemKey) ? Math.round(value * 100) : Math.round(value));
    const unscaled = (itemKey, value) => (hasDecimals(itemKey) ? value / 100 : value);

    function render(itemKey, value, signed) {
        const formatted = formatItem(itemKey, unscaled(itemKey, value), signed);
        element.innerHTML = `<span class="sign">${formatted.sign}</span><span class="number">${formatted.value}${formatted.suffix}</span>`;
    }

    return {
        /**
         * @param {string} itemKey
         * @param {number} value      display units (percent as 0..100)
         * @param {boolean} signed    a difference: "+" on positive values, sign coloured by `tone`
         */
        show(itemKey, value, signed) {
            const target = scaled(itemKey, shownValue(itemKey, value, signed));
            // an item that is a difference itself (Next rival) is coloured like one
            const tone = !signed && !isSigned(itemKey) ? 'neutral' : target > 0 ? 'positive' : target < 0 ? 'negative' : 'neutral';
            element.className = `counter visible ${tone}`;

            // Shown again with the value it is already counting to (an overlay redraws on every message, and more than
            // once for some): the count goes on instead of being cut short.
            if (shown && itemKey === key && target === current && counting && signed === countingSigned) return;

            const id = ++animation;
            if (!shown || itemKey !== key || target === current) {
                current = target;
                key = itemKey;
                shown = true;
                counting = false;
                render(itemKey, target, signed);
                return;
            }

            // (from where the number stands now: a count still under way is taken over, not jumped to its end)
            const from = counting ? standing : current;
            const duration = Math.min(360, 130 + Math.min(10, Math.abs(target - from)) * 22);
            const started = performance.now();
            current = target;
            counting = true;
            countingSigned = signed;
            standing = from;

            const tick = (now) => {
                if (id !== animation) return;
                const progress = Math.min(1, (now - started) / duration);
                const eased = 1 - Math.pow(1 - progress, 3);
                standing = progress < 1 ? Math.round(from + (target - from) * eased) : target;
                render(itemKey, standing, signed);
                if (progress < 1) requestAnimationFrame(tick);
                else counting = false;
            };
            requestAnimationFrame(tick);
        },

        hide() {
            animation++;
            shown = false;
            counting = false;
            element.className = 'counter';
        },
    };
}
