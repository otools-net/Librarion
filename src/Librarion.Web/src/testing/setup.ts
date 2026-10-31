/*
 * What the unit tests' DOM (jsdom) lacks and the app uses (ported from SFE). Loaded before every spec (angular.json,
 * test > setupFiles). Angular runs Vitest with `isolate: false`: the spec files of a worker share one global scope and
 * this file runs again for each of them, so whatever it installs is replaced as a whole (never `??=` next to state of
 * this module), and what specs change on globals is put back after each test (agents/docs/development/testing-client.md).
 */

/** Matches of the media queries, set by tests: `setMediaQuery('(prefers-color-scheme: light)', true)`. */
const mediaQueries = new Map<string, { matches: boolean; listeners: Set<(event: MediaQueryListEvent) => void> }>();

function mediaQuery(query: string) {
    let entry = mediaQueries.get(query);
    if (!entry)
        mediaQueries.set(query, entry = { matches: false, listeners: new Set() });
    return entry;
}

window.matchMedia = (query: string): MediaQueryList => {
    const entry = mediaQuery(query);
    return {
        media: query,
        get matches() { return entry.matches; },
        onchange: null,
        addEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) => entry.listeners.add(listener),
        removeEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) => entry.listeners.delete(listener),
        addListener: (listener: (event: MediaQueryListEvent) => void) => entry.listeners.add(listener),
        removeListener: (listener: (event: MediaQueryListEvent) => void) => entry.listeners.delete(listener),
        dispatchEvent: () => true,
    } as unknown as MediaQueryList;
};

(globalThis as { setMediaQuery?: (query: string, matches: boolean) => void }).setMediaQuery = (query, matches) => {
    const entry = mediaQuery(query);
    entry.matches = matches;
    for (const listener of entry.listeners)
        listener({ matches, media: query } as MediaQueryListEvent);
};

Element.prototype.scrollIntoView = function () { /* nothing to scroll in jsdom */ };
window.scrollTo = () => { /* nothing to scroll in jsdom */ };

beforeEach(() => {
    // Storage and media queries outlive a spec file too: each test starts from none.
    localStorage.clear();
    sessionStorage.clear();
    for (const entry of mediaQueries.values()) {
        entry.matches = false;
        entry.listeners.clear();
    }
    delete document.documentElement.dataset['theme'];
});
