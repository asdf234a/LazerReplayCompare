import { startSingleItemOverlay } from '../lazerReplayCompareShared/singleItemOverlay.js';

// What it shows is picked in the app (Compare page → Main).
startSingleItemOverlay({ name: 'LazerReplayCompare Main', role: 'main', defaults: { item: 'score', show: 'diff' } });
