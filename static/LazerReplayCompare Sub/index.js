import { startSingleItemOverlay } from '../lazerReplayCompareShared/singleItemOverlay.js';

// What it shows is picked in the app (Compare page → Sub).
startSingleItemOverlay({ name: 'LazerReplayCompare Sub', role: 'sub', defaults: { item: 'acc', show: 'value' } });
