// The Main and Sub overlays: one item each, picked in the LazerReplayCompare app (Compare page).
//
// item: score | score_acc | acc | stable_acc | lazer_acc | a custom item from the app's metrics.json | off
// show: diff   – live minus the compared replay at the same note
//       value  – the live value
//       target – the compared replay's value at the same note

import { createCounter } from './counter.js';
import { OFF, itemValue, needsPerfect, resolveItem, scaleKeyOf, setItems, timelineFactor } from './metrics.js';
import { createSession } from './session.js';

const SHOW_MODES = ['diff', 'value', 'target'];

/**
 * @param {object} options
 * @param {string} options.name                              Overlay name (console messages).
 * @param {'main' | 'sub'} options.role                      Which of the app's choices it follows.
 * @param {{ item: string, show: string }} options.defaults  Used until the app answers.
 */
export function startSingleItemOverlay({ name, role, defaults }) {
    const display = { ...defaults };
    const counter = createCounter(document.querySelector('#counter'));

    const currentItem = () => resolveItem(display.item, defaults.item);

    const session = createSession({
        name,
        onUpdate: render,
        // a scaled timeline is stretched by what this overlay shows
        scaleMetric: () => scaleKeyOf(currentItem()),
        needsPerfect: () => needsPerfect(currentItem()),
        onState: (data) => {
            setItems(data?.items);
            const chosen = data?.display?.[role];
            if (!chosen) return;
            display.item = chosen.item ?? display.item;
            display.show = SHOW_MODES.includes(chosen.show) ? chosen.show : display.show;
        },
    });

    function render() {
        const { state } = session;
        const key = currentItem();
        if (key === OFF || !session.isPlaying()) {
            counter.hide();
            return;
        }

        if (display.show === 'value') {
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

        if (display.show === 'target') {
            counter.show(key, replay, false);
            return;
        }

        const live = itemValue(key, session.liveSource());
        if (live == null) counter.hide();
        else counter.show(key, live - replay, true);
    }

    render();
}
