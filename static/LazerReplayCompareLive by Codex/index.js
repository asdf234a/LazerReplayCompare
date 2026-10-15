// Live play and compared replay side by side: the Main and Sub items picked in the app, and every judgement count.

import { OFF, formatItem, hitValue, itemValue, needsPerfect, resolveItem, scaleKeyOf, setItems, timelineFactor } from '../lazerReplayCompareShared/metrics.js';
import { getReplayModsKey } from '../lazerReplayCompareShared/overlayState.js';
import { createSession } from '../lazerReplayCompareShared/session.js';

const $ = (id) => document.getElementById(id);

const defaults = { main: 'score', sub: 'acc' };
const config = { ...defaults };

const HIT_ROWS = [
    ['Perfect', 'MAX'],
    ['Great', '300'],
    ['Good', '200'],
    ['Ok', '100'],
    ['Meh', '50'],
    ['Miss', 'Miss'],
];

const mainItem = () => resolveItem(config.main, defaults.main);
const subItem = () => resolveItem(config.sub, defaults.sub);

const session = createSession({
    name: 'LazerReplayCompareLive',
    onUpdate: render,
    scaleMetric: () => scaleKeyOf(mainItem()),
    needsPerfect: () => needsPerfect(mainItem()) || needsPerfect(subItem()),
    onState: (data) => {
        setItems(data?.items);
        config.main = data?.display?.main?.item ?? config.main;
        config.sub = data?.display?.sub?.item ?? config.sub;
    },
});

function isVisible() {
    return session.isPlaying() || session.isResults();
}

/** The item for a source, formatted ('-' while unknown, hidden when off). */
function absolute(key, source, factor = 1) {
    if (key === OFF) return { text: '', hidden: true };
    const value = source ? itemValue(key, source, factor) : null;
    return { text: value == null ? '-' : formatItem(key, value).text, hidden: false };
}

/** Live minus replay for an item, formatted and toned. */
function difference(key, frame) {
    if (key === OFF) return { text: '', className: 'neutral', hidden: true };
    const { state } = session;
    const live = itemValue(key, session.liveSource());
    const replay = frame ? itemValue(key, session.frameSource(frame), timelineFactor(key, state.scaleMetric, state.scaleFactor)) : null;
    if (live == null || replay == null) return { text: '-', className: 'neutral', hidden: false };

    const value = live - replay;
    const formatted = formatItem(key, value, true);
    const shown = Number(formatted.value.replace(/,/g, '')) === 0 ? 0 : value;
    return { text: formatted.text, className: shown > 0 ? 'positive' : shown < 0 ? 'negative' : 'neutral', hidden: false };
}

function setText(id, value) {
    $(id).textContent = value.text;
    $(id).style.display = value.hidden ? 'none' : '';
}

function renderHits(liveHits, replayHits, showReplay) {
    const grid = $('hitsGrid');

    for (let i = 0; i < HIT_ROWS.length; i++) {
        const [key, label] = HIT_ROWS[i];
        const live = hitValue(liveHits, key);
        const replay = hitValue(replayHits, key);
        const diff = live - replay;

        let row = grid.children[i];
        if (!row) {
            row = document.createElement('div');
            row.className = `hit-cell ${key === 'Miss' ? 'miss' : ''}`;
            row.innerHTML = '<span class="hit-label"></span><span class="hit-live"></span><span class="hit-diff-val"></span><span class="hit-replay"></span>';
            grid.appendChild(row);
        }

        row.children[0].textContent = label;
        if (showReplay && diff !== 0) {
            row.children[1].innerHTML = `${live} <span class="hit-inline-diff ${diff > 0 ? 'positive' : 'negative'}">(${diff > 0 ? '+' : ''}${diff})</span>`;
        } else {
            row.children[1].textContent = live;
        }
        row.children[2].textContent = '';
        row.children[2].className = `hit-diff-val ${showReplay ? diff > 0 ? 'positive' : diff < 0 ? 'negative' : '' : ''}`;
        row.children[3].textContent = showReplay ? replay : '';
    }
}

function render() {
    const visible = isVisible();
    $('panel').className = `panel${visible ? ' visible' : ''}`;
    if (!visible) return;

    const { state } = session;
    const target = state.target;
    $('liveCombo').textContent = `x${state.combo}`;
    $('replayPlayer').textContent = target ? target.player || 'unknown' : state.noReplay ? 'NO REPLAY' : 'REPLAY';
    $('replayLabel').textContent = target ? target.modsText || getReplayModsKey(target).split('|')[0] : state.modsKey.split('|')[0] || 'NM';
    $('targetMode').textContent = state.targetMode === 'selected' ? 'SELECTED' : 'AUTO BEST';
    $('livePlayer').textContent = state.player || 'PLAYER';
    $('liveLabel').textContent = state.modsKey.split('|')[0] || 'NM';

    const main = mainItem();
    const sub = subItem();
    const frame = session.frameAt(state.hitIndex);
    const frameSource = session.frameSource(frame);
    const live = session.liveSource();

    setText('liveScore', absolute(main, live));
    setText('liveAcc', absolute(sub, live));
    setText('replayScore', absolute(main, frameSource, timelineFactor(main, state.scaleMetric, state.scaleFactor)));
    setText('replayAcc', absolute(sub, frameSource, timelineFactor(sub, state.scaleMetric, state.scaleFactor)));

    const mainDiff = difference(main, frame);
    const subDiff = difference(sub, frame);
    $('scoreDiff').textContent = mainDiff.text;
    $('scoreDiff').className = `score-diff ${mainDiff.className}`;
    $('accDiff').textContent = subDiff.text;
    $('accDiff').className = `acc-diff ${subDiff.className}`;
    $('accDiff').style.display = subDiff.hidden ? 'none' : '';
    $('replayCombo').textContent = frame ? `x${frame.combo}` : '-';
    renderHits(state.hits, frame?.hits ?? null, Boolean(frame));

    $('timelineInfo').textContent = state.timelineSource && state.framesKey === session.baseKey()
        ? `${state.timelineSource} - ${state.totalNotes} notes`
        : state.loading
            ? 'loading...'
            : state.noReplay
                ? 'no replay'
                : state.error
                    ? `error: ${state.error}`
                    : '-';
}

render();
