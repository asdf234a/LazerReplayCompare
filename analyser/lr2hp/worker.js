// One chart at a time: { id, text, settings } -> { id, result } or { id, error }
import { estimate } from "./engine.js";

self.onmessage = async (event) => {
    const { id, text, settings } = event.data || {};
    try {
        const result = await estimate(text, settings || {});
        self.postMessage({ id, result });
    } catch (error) {
        self.postMessage({ id, error: String(error?.message || error) });
    }
};
