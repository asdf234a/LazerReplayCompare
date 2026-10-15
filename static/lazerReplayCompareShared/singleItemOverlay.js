// The Main and Sub overlays: one item, as set in the overlay's own config.json.
//
// config.json:
//   "item": score | score_acc | acc | stable_acc | lazer_acc | a key from lazerReplayCompareShared/metrics.json | off
//   "show": diff   – live minus the compared replay at the same note
//           value  – the live value
//           target – the compared replay's value at the same note
// Changes to config.json apply while the overlay runs.

import { createCounter } from './counter.js';
import { OFF, itemValue, needsPerfect, resolveItem, scaleKeyOf, timelineFactor, watchConfig } from './metrics.js';
import { createSession } from './session.js';

const SHOW_MODES = ['diff', 'value', 'target'];

/**
 * @param {object} options
 * @param {string} options.name                          Overlay name (console messages).
 * @param {{ item: string, show: string }} options.defaults  Used until config.json says otherwise.
 */
export function startSingleItemOverlay({ name, defaults }) {
    const config = { ...defaults };
    const counter = createCounter(document.querySelector('#counter'));

    const currentItem = () => resolveItem(config.item, defaults.item);

    const session = createSession({
        name,
        onUpdate: render,
        // a scaled timeline is stretched by what this overlay shows
        scaleMetric: () => scaleKeyOf(currentItem()),
        needsPerfect: () => needsPerfect(currentItem()),
    });

    function render() {
        const { state } = session;
        const key = currentItem();
        if (key === OFF || !session.isPlaying()) {
            counter.hide();
            return;
        }

        if (config.show === 'value') {
            const value = itemValue(key, session.liveSource());
            if (value == null) counter.hide();
            else counter.show(key, value, false);
            return;
        }

        const frame = state.hitIndex > 0 ? session.frameAt(state.hitIndex) : null;
        const factor = timelineFactor(key, state.scaleMetric, state.scaleFactor);
        const replay = frame ? itemValue(key, session.frameSource(frame), factor) : null;
        if (replay == null) {
            counter.hide();
            return;
        }

        if (config.show === 'target') {
            counter.show(key, replay, false);
            return;
        }

        const live = itemValue(key, session.liveSource());
        if (live == null) counter.hide();
        else counter.show(key, live - replay, true);
    }

    watchConfig(new URL('config.json', location.href).href, (json) => {
        config.item = json?.item ?? defaults.item;
        config.show = SHOW_MODES.includes(String(json?.show).toLowerCase()) ? String(json.show).toLowerCase() : defaults.show;
        session.refresh();
        render();
    });

    // custom items from metrics.json may arrive after the config that names them
    setInterval(render, 1000);
    render();
}
