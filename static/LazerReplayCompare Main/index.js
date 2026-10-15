import { startSingleItemOverlay } from '../lazerReplayCompareShared/singleItemOverlay.js';

// What it shows is set in config.json.
startSingleItemOverlay({ name: 'LazerReplayCompare Main', defaults: { item: 'score', show: 'diff' } });
