// Where the ground is, for the things that are drawn on it and carry no Z of their own.
//
// A ZONE IS A RECTANGLE OF GROUND. It has x, y, width and height in the schema and nothing else,
// and nothing is being added: where the ground is happens to be a question with an answer, so it is
// asked rather than stored. Storing it would mean a number in the file that goes stale the moment
// anybody re-terraforms, and that the shard would have no use for - `RestrictedZoneSystem` and
// `Nav.SmallestZoneAt` both test a rectangle, not a box.
//
// Until this existed, a zone in art view projected on the ground plane and sat about 2.7 tiles
// uphill of the land it covers, in Britain's upper town and anywhere else standing above z 0. Worse
// than looking wrong: `hitTest` used the same z=0 corners, so the diamond you clicked was not the
// diamond you saw.
//
// THE ANSWER IS THE RENDERER'S, and it is the same one the pick map records for land - the shard's
// own `map.GetAverageZ`, through a single accessor on the C# side (IsoTileRenderer.StandingZ). So a
// zone corner and a waypoint dropped on the same tile cannot disagree.
//
// BATCHED PER FRAME. Every request made before the next animation frame goes out as one query, so a
// draw pass over eight zones is one round trip rather than thirty-two, and a repeat is free. The
// cap matches the renderer's own.

import { api } from './api.js';

/** The same cap TileServer.LandZ and the bridge enforce. */
const MAX_BATCH = 256;

/** How long to wait before asking again after a failure, so a dead renderer is not hammered. */
const RETRY_MS = 5000;

const known = new Map();
const asked = new Set();

let queued = [];
let scheduled = false;
let unavailable = null;
let nextRetry = 0;
let onAnswer = null;
let waiters = [];

/** What to call when an answer arrives - a redraw, so a zone settles onto the ground it covers. */
export function configure(callback) {
    onAnswer = callback;
}

/** Why there is no ground Z, or null. */
export function problem() {
    return unavailable;
}

export function reset() {
    known.clear();
    asked.clear();
    queued = [];
    unavailable = null;
    nextRetry = 0;
}

/**
 * The Z of the land at a tile, or null if it is not known yet - and asks, if it is not.
 *
 * Null rather than 0 on purpose. Zero is a real Z, and a caller that cannot tell "the sea" from "I
 * have not been told yet" draws the second as the first: a zone snapping from the ground plane up
 * to the hillside is a frame of movement, but a zone that stays on the ground plane forever looks
 * like the feature not working.
 */
export function at(x, y) {
    const key = `${x},${y}`;

    if (known.has(key)) {
        return known.get(key);
    }

    if (!asked.has(key) && Date.now() >= nextRetry) {
        asked.add(key);
        queued.push([x, y, key]);
        schedule();
    }

    return null;
}

/**
 * The Z at a tile if it has already been asked about, and NOTHING otherwise.
 *
 * What the radar readout uses. `at` would be the obvious call and would be wrong: the readout runs
 * on every mousemove, so it would queue a tile per frame and send a query per frame - down the same
 * serial stdin channel the art tiles are rendered on, where sixty small questions a second would
 * sit in front of the picture somebody is waiting for. A hover is not worth that; a placement is,
 * and `resolve` below is what a placement calls.
 */
export function peek(x, y) {
    const answer = known.get(`${x},${y}`);

    return answer === undefined ? null : answer;
}

/**
 * The Z at a tile, waiting one batch for it if it is not known yet.
 *
 * What a PLACEMENT uses in radar view, where there is no pick map to read and the cursor knows only
 * x and y. It resolves null rather than throwing when there is no renderer: a placement must never
 * fail because MapExport has not been built, and a record with `z: 0` is exactly what the editor
 * wrote for its whole life before this.
 */
export function resolve(x, y) {
    const now = at(x, y);

    if (now !== null || (unavailable && Date.now() < nextRetry)) {
        return Promise.resolve(now);
    }

    return new Promise((done) => {
        waiters.push(() => done(at(x, y)));
        schedule();
    });
}

/**
 * The four corner Zs of a world rectangle, as a function of a corner - the shape `traceWorldRect`
 * and the rect hit test both take.
 *
 * A function rather than an array, because the two callers walk the corners in different orders
 * (`rectHandles` is NW, NE, SW, SE; the traced quad is NW, NE, SE, SW) and an array would be one
 * transposition away from a zone drawn on one hillside and clicked on another.
 */
export const groundAt = (x, y) => at(x, y);

/** True once every corner of a rect is known, so a caller can decide whether to wait a frame. */
export function ready([x, y, width, height]) {
    return at(x, y) !== null
        && at(x + width, y) !== null
        && at(x + width, y + height) !== null
        && at(x, y + height) !== null;
}

function schedule() {
    if (scheduled) {
        return;
    }

    scheduled = true;

    // requestAnimationFrame rather than a timer: the requests are made from the draw pass, so the
    // natural moment to send them is when that pass is over. Guarded because the node tests import
    // this module with no window at all.
    const soon = typeof requestAnimationFrame === 'function'
        ? requestAnimationFrame
        : (fn) => setTimeout(fn, 0);

    soon(flush);
}

function flush() {
    scheduled = false;

    const batch = queued.splice(0, MAX_BATCH);

    if (batch.length === 0) {
        wake();
        return;
    }

    if (queued.length > 0) {
        schedule();
    }

    api.landZ(batch.map(([x, y]) => [x, y])).then(
        (body) => {
            if (!body || !Array.isArray(body.z)) {
                fail(batch, (body && body.reason) || 'The renderer did not answer.');
                wake();
                return;
            }

            batch.forEach(([, , key], index) => known.set(key, body.z[index]));

            unavailable = null;
            wake();

            if (onAnswer) {
                onAnswer();
            }
        },
        (error) => {
            fail(batch, error.message);
            wake();
        });
}

/** Releases everything waiting on this batch, answered or not. */
function wake() {
    const pending = waiters;

    waiters = [];

    for (const waiter of pending) {
        waiter();
    }
}

/**
 * A batch that could not be answered. The tiles are UN-asked so a later hover retries them, but not
 * before RETRY_MS: a renderer that is down should cost one query every five seconds, not one per
 * frame of a draw pass that keeps asking about the same eight zones.
 */
function fail(batch, reason) {
    unavailable = reason;
    nextRetry = Date.now() + RETRY_MS;

    for (const [, , key] of batch) {
        asked.delete(key);
    }
}
