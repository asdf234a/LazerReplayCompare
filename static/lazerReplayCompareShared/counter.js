// An animated number for the Main and Sub overlays: counts to its new value and colours the sign of a difference.

import { formatItem, isPercent } from './metrics.js';

export function createCounter(element) {
    let shown = false;
    let current = 0;        // in hundredths for percentages
    let key = '';
    let animation = 0;

    const scaled = (itemKey, value) => (isPercent(itemKey) ? Math.round(value * 100) : Math.round(value));
    const unscaled = (itemKey, value) => (isPercent(itemKey) ? value / 100 : value);

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
            const target = scaled(itemKey, value);
            const tone = !signed ? 'neutral' : target > 0 ? 'positive' : target < 0 ? 'negative' : 'neutral';
            element.className = `counter visible ${tone}`;

            const id = ++animation;
            if (!shown || itemKey !== key || target === current) {
                current = target;
                key = itemKey;
                shown = true;
                render(itemKey, target, signed);
                return;
            }

            const from = current;
            const duration = Math.min(360, 130 + Math.min(10, Math.abs(target - from)) * 22);
            const started = performance.now();
            current = target;

            const tick = (now) => {
                if (id !== animation) return;
                const progress = Math.min(1, (now - started) / duration);
                const eased = 1 - Math.pow(1 - progress, 3);
                render(itemKey, progress < 1 ? Math.round(from + (target - from) * eased) : target, signed);
                if (progress < 1) requestAnimationFrame(tick);
            };
            requestAnimationFrame(tick);
        },

        hide() {
            animation++;
            shown = false;
            element.className = 'counter';
        },
    };
}
