// A small live leaderboard: the play and its rivals (the app's /rivals) in order at this point of the song.
//
// A rival with a replay is where that replay was at this note; one without (a leaderboard score, or a replay whose
// timeline is still being built) moves at an even pace towards its final score, and says "est.".

import { formatItem } from '../lazerReplayCompareShared/metrics.js';
import { standings, windowRows } from '../lazerReplayCompareShared/rivals.js';
import { createSession } from '../lazerReplayCompareShared/session.js';

// Settings. Either can also be given in the overlay's address: ?rows=6&by=accuracy
const MAX_ROWS = 8;             // rows shown at most (2 or more): the first place and the rows around you
const COMPARE_BY = 'score';     // 'score' or 'accuracy'

const query = new URLSearchParams(location.search);
const maxRows = Math.max(2, Math.floor(Number(query.get('rows'))) || MAX_ROWS);
const compareBy = /^acc/i.test(query.get('by') || COMPARE_BY) ? 'accuracy' : 'score';
// the item the numbers are formatted as
const item = compareBy === 'accuracy' ? 'acc' : 'score';

const board = document.getElementById('board');
const rowElements = new Map();  // row key → element

const session = createSession({
    name: 'LazerReplayCompare Pacemaker',
    onUpdate: render,
    // an even pace needs the best possible score so far
    needsPerfect: () => true,
    needsRivals: () => true,
    rivalMetric: () => (compareBy === 'accuracy' ? 'Acc' : 'Score'),
});

function shortDate(text) {
    const date = new Date(text);
    if (!text || Number.isNaN(date.getTime())) return '';
    const two = (value) => String(value).padStart(2, '0');
    return `${date.getFullYear()}-${two(date.getMonth() + 1)}-${two(date.getDate())}`;
}

/** The small line under a name: what the row is. */
function noteOf(row) {
    if (row.isPlayer) return 'live';
    const rival = row.rival;
    if (rival.source !== 'replay') return 'online · est.';
    const parts = [rival.isTarget ? 'target' : 'replay', shortDate(rival.date)];
    if (row.estimated) parts.push('est.');
    return parts.filter(Boolean).join(' · ');
}

function setText(element, text) {
    if (element.textContent !== text) element.textContent = text;
}

function createRow() {
    const element = document.createElement('div');
    element.innerHTML = '<span class="place"></span>'
        + '<span class="who"><span class="name"></span><span class="note"></span></span>'
        + '<span class="numbers"><span class="value"></span><span class="diff"></span></span>';
    return element;
}

function hide() {
    board.classList.remove('visible');
}

function render() {
    const { state } = session;
    const rivals = session.isPlaying() ? session.rivals() : [];
    if (rivals.length === 0) {
        hide();
        return;
    }

    const live = session.liveSource();
    const result = standings(rivals, live, live, compareBy);
    // only the play itself: no rival can be placed yet
    if (result.rows.length < 2) {
        hide();
        return;
    }

    const rows = windowRows(result.rows, result.playerIndex, maxRows);
    const shown = new Set();

    rows.forEach((row, position) => {
        const key = row.isPlayer ? 'player' : `rival:${row.rival.id}`;
        shown.add(key);

        let element = rowElements.get(key);
        const added = !element;
        if (added) {
            element = createRow();
            rowElements.set(key, element);
        }

        const place = element.querySelector('.place');
        const name = element.querySelector('.name');
        const note = element.querySelector('.note');
        const value = element.querySelector('.value');
        const diff = element.querySelector('.diff');
        const unit = compareBy === 'accuracy' ? 100 : 1;
        setText(place, String(row.place));
        setText(name, row.isPlayer ? state.rivalsPlayer || state.player || 'You' : row.rival.name);
        setText(value, formatItem(item, row.value * unit).text);
        setText(note, noteOf(row));

        let tone = 'neutral';
        if (row.isPlayer) {
            setText(diff, '');
        } else {
            // you minus the rival, as the Diff of the other overlays: positive (green) when you are ahead of it
            const formatted = formatItem(item, row.diff * unit, true);
            const zero = Number(formatted.value.replace(/,/g, '')) === 0;
            tone = zero ? 'neutral' : row.diff > 0 ? 'positive' : 'negative';
            setText(diff, formatted.text);
        }

        const className = `row${position === 0 ? ' first' : ''}${row.isPlayer ? ' player' : ''}${!row.isPlayer && row.rival.isTarget ? ' target' : ''}`;
        if (element.className !== className) element.className = className;
        const diffClass = `diff ${tone}`;
        if (diff.className !== diffClass) diff.className = diffClass;

        // rows are placed by hand so a change of order slides
        const transform = `translateY(calc(var(--row-height) * ${position}))`;
        if (element.style.transform !== transform) element.style.transform = transform;
        if (added) board.appendChild(element);
    });

    for (const [key, element] of rowElements) {
        if (shown.has(key)) continue;
        element.remove();
        rowElements.delete(key);
    }

    board.style.setProperty('--rows', String(rows.length));
    board.classList.add('visible');
}

render();
