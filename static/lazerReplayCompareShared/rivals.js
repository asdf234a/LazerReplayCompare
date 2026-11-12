// Rivals: the replays and leaderboard scores a play is raced against (the app's /rivals), and where the play stands
// among them at a point of the song. Shared by the Pacemaker overlay and the Rivals items (metrics.js).
//
// Nothing here touches the page or the network, so it can be run with Node.
//
// A rival is { id, name, source ('replay' | 'online'), filePath, score, accuracy (0..1), date, isTarget, isUser,
// frames (its timeline: [{ index, score, accuracy }], or null) }.
// A point of the song is { index (judgements so far), perfect (best possible score so far), perfectTotal (best possible
// score of the whole beatmap) }.

/** The /rivals list as rivals (without timelines); entries without a score are dropped. */
export function toRivals(list) {
    return (Array.isArray(list) ? list : [])
        .filter((entry) => entry && Number.isFinite(Number(entry.score)))
        .map((entry, i) => ({
            id: String(entry.id || `${entry.source || ''}:${entry.filePath || entry.name || ''}:${i}`),
            name: String(entry.name || 'unknown'),
            source: entry.source === 'replay' && entry.filePath ? 'replay' : 'online',
            filePath: String(entry.filePath || ''),
            score: Number(entry.score) || 0,
            accuracy: Number(entry.accuracy) || 0,
            date: String(entry.date || ''),
            modsText: String(entry.modsText || ''),
            isTarget: Boolean(entry.isTarget),
            isUser: Boolean(entry.isUser),
            frames: null,
        }));
}

/** The frame at a note (the last one with index ≤ `index`), or null before the first. */
export function frameAtIndex(frames, index) {
    let lo = 0, hi = (frames?.length ?? 0) - 1, found = null;
    while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        if (frames[mid].index <= index) { found = frames[mid]; lo = mid + 1; }
        else hi = mid - 1;
    }
    return found;
}

/**
 * How much of the beatmap's best possible score could be had by this point (0..1), or null while that isn't known. A
 * score with no timeline is taken to have come in at this even pace: the same idea as Live rank, which holds a score so
 * far against final scores through its share of the best possible so far.
 */
export function paceShare(point) {
    const total = Number(point?.perfectTotal);
    const soFar = Number(point?.perfect);
    if (!(total > 0) || !Number.isFinite(soFar)) return null;
    return Math.max(0, Math.min(1, soFar / total));
}

/**
 * A rival at a point of the song: { score, accuracy, estimated }, or null when it can't be placed yet. With a timeline it
 * is the replay's own score and accuracy at that note; without one (an online score, or a replay whose timeline isn't
 * there yet) it is the final score at an even pace, with the final accuracy.
 */
export function rivalAt(rival, point) {
    if (rival.frames?.length) {
        const frame = frameAtIndex(rival.frames, Number(point?.index) || 0);
        return frame ? { score: frame.score, accuracy: frame.accuracy, estimated: false } : { score: 0, accuracy: 1, estimated: false };
    }

    const pace = paceShare(point);
    return pace == null ? null : { score: Math.round(rival.score * pace), accuracy: rival.accuracy, estimated: true };
}

/**
 * Everyone in order at a point of the song, best first, by 'score' or 'accuracy'. A tie leaves the rival ahead of the
 * player (as Live rank does), and rivals that tie keep the order they came in.
 *
 * @param {object[]} rivals
 * @param {{ score: number, accuracy: number }} player   The play being placed (accuracy 0..1).
 * @param {{ index: number, perfect: number, perfectTotal: number }} point
 * @returns {{ rows: object[], playerIndex: number, place: number, above: object | null, below: object | null }}
 *   rows: { isPlayer, rival, score, accuracy, value, estimated, place, diff (the player's value minus this row's) }.
 */
export function standings(rivals, player, point, by = 'score') {
    const field = by === 'accuracy' ? 'accuracy' : 'score';
    const playerRow = { isPlayer: true, rival: null, score: Number(player?.score) || 0, accuracy: Number(player?.accuracy) || 0, estimated: false };
    playerRow.value = playerRow[field];

    const rows = [];
    for (const rival of rivals || []) {
        const at = rivalAt(rival, point);
        if (at) rows.push({ isPlayer: false, rival, score: at.score, accuracy: at.accuracy, value: at[field], estimated: at.estimated });
    }

    // a stable sort: equal rivals stay in the order given
    rows.sort((a, b) => b.value - a.value);
    let playerIndex = rows.findIndex((row) => row.value < playerRow.value);
    if (playerIndex < 0) playerIndex = rows.length;
    rows.splice(playerIndex, 0, playerRow);

    rows.forEach((row, i) => {
        row.place = i + 1;
        row.diff = playerRow.value - row.value;
    });

    return {
        rows,
        playerIndex,
        place: playerIndex + 1,
        above: rows[playerIndex - 1] ?? null,
        below: rows[playerIndex + 1] ?? null,
    };
}

/**
 * The rows to show when there is room for `max` (3 or more): all of them when they fit; otherwise the player, the two
 * rows directly above (who is next to catch), and the top of the list from first place down for the room that is left.
 * A player near the top simply gets the first `max` rows.
 */
export function windowRows(rows, playerIndex, max) {
    const size = Math.max(3, Math.floor(Number(max)) || 3);
    if (rows.length <= size) return rows.slice();

    const shown = new Set();
    for (let i = Math.max(0, playerIndex - 2); i <= playerIndex; i++) shown.add(i);
    // from first place down, then (when the player is near the top) on below the player
    for (let i = 0; i < rows.length && shown.size < size; i++) shown.add(i);

    return [...shown].sort((x, y) => x - y).map((i) => rows[i]);
}

/**
 * The most the player can still end with: the score so far plus the best possible score of the notes left. (An estimate
 * in osu!lazer, where what a note is worth also depends on the combo and accuracy kept until it.) Null while unknown.
 */
export function bestPossibleFinal(score, point) {
    const total = Number(point?.perfectTotal);
    const soFar = Number(point?.perfect);
    if (!(total > 0) || !Number.isFinite(soFar)) return null;
    return (Number(score) || 0) + Math.max(0, total - soFar);
}

/**
 * Whether the player can still end ahead of a final score. Ending level is not ahead: a tie leaves the rival in front.
 * Null while unknown.
 */
export function canPass(score, finalScore, point) {
    const best = bestPossibleFinal(score, point);
    return best == null ? null : best > Number(finalScore);
}

/**
 * What the Rivals items show for an item source (see metrics.js): { place, gap, reachable }, or null with no rivals
 * placed. Always by score.
 *   place      the source's place among the rivals (1 = first)
 *   gap        its score minus the score of the rival directly above at this point (0 or less); in first place, its
 *              lead over the second (0 or more)
 *   reachable  whether it can still end ahead of the final score of the rival directly above; true in first place
 */
export function rivalStanding(source) {
    // a replay is not raced against itself
    const rivals = (source?.rivals || []).filter((rival) => !source.self || rival.filePath !== source.self);
    if (rivals.length === 0) return null;

    const point = { index: source.index, perfect: source.perfect, perfectTotal: source.perfectTotal };
    const result = standings(rivals, source, point, 'score');
    if (result.rows.length < 2) return null;

    const next = result.above ?? result.below;
    return {
        place: result.place,
        gap: result.rows[result.playerIndex].value - next.value,
        reachable: result.above ? canPass(source.score, result.above.rival.score, point) : true,
    };
}
