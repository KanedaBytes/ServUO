// Helpers for the live snapshot panel.
//
// Here rather than in app.js because app.js reaches for the canvas at module scope, being the page
// entry point, so nothing in it can be imported by a test. That has already cost this editor two
// invisible bugs; a pure function with unit boundaries and a clock-skew case does not need to be
// the third.

/** How stale a snapshot has to be before the line is coloured. Three missed writes at the 2s default. */
export const STALE_AFTER_SECONDS = 6;

/**
 * How long ago a snapshot was written, or null when the shard did not date it.
 *
 * The age is the whole point of showing it. A sequence number that has stopped moving looks exactly
 * like a quiet town until you know when it last moved.
 */
export function snapshotAge(utc, now = Date.now()) {
    const written = Date.parse(utc);

    if (Number.isNaN(written)) {
        return null;
    }

    // Clamped at zero. The shard writes the timestamp from its own clock, and a machine a second
    // ahead of this one would otherwise read "-1s ago" - which looks like a bug in the editor
    // rather than a difference between two clocks.
    const seconds = Math.max(0, Math.round((now - written) / 1000));

    return { seconds, text: format(seconds), stale: seconds > STALE_AFTER_SECONDS };
}

function format(seconds) {
    if (seconds < 60) {
        return `${seconds}s ago`;
    }

    if (seconds < 3600) {
        return `${Math.floor(seconds / 60)}m ago`;
    }

    return `${Math.floor(seconds / 3600)}h ago`;
}

/** The Live panel's line: what is on the map, which snapshot it came from, and how old that is. */
export function liveStatusText(live, fallbackCount, now = Date.now()) {
    if (!live || !live.running) {
        return { text: 'live: off', stale: false };
    }

    const count = live.count ?? fallbackCount;
    const age = snapshotAge(live.utc, now);

    return {
        text: `live: ${count} @ seq ${live.sequence}${age === null ? '' : `, ${age.text}`}`,
        stale: age !== null && age.stale
    };
}
