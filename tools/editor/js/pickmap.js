// The pick map, in the browser: which world tile the cursor is actually on.
//
// Named pickmap rather than pick because shapes.js already exports a `pick` - hit testing a shape
// the editor drew. Two different questions: that one asks what is selected, this one asks where
// the cursor is.
//
// WHAT THIS REPLACES. The isometric projection has no inverse - a screen pixel names a world tile
// only once a Z is assumed, and assuming the ground plane is wrong by z*4/44 tiles, about 2.7 where
// Britain stands. So the art view could select things the editor had drawn itself (it knew where it
// put them) but could not place, drag or draw anything new, because none of those know a Z. The
// renderer knows: it had the answer in its hand while it was drawing. A pick map is that answer,
// recorded per pixel by the same painter's pass and the same floor filter as the picture - so a
// roof at the All stop picks the roof, and the floor under it picks the floor at Ground.
//
// ONE SIDECAR ANSWERS EVERY ZOOM. Pick maps exist only at the deepest level, because the lookup
// goes through a FACET-GLOBAL CANVAS PIXEL, which is the same number whatever the camera is doing.
// See iso.canvasToTile and tools/MapExport/PickMap.cs.
//
// FETCHED, NOT DRAWN. The bytes come through fetch().arrayBuffer(), gzipped on the wire and
// inflated by the browser, so they are exact. An image would have to come back through a canvas,
// and that path premultiplies alpha and applies colour management - a pick map that is nearly right
// is a waypoint three tiles from where it was clicked.
//
// LAZY, one tile at a time. A pick map is only wanted for tiles somebody puts the cursor on, which
// is a small fraction of what gets drawn; a 256-pixel sidecar covers about twelve world tiles at
// 1:1, so crossing into a new one is a rare event and costs about 13 ms of renderer time once,
// ever. Neighbours are deliberately NOT prefetched: the renderer is serial and its queue is LIFO,
// so eight speculative tiles would sit in front of the one the cursor is actually on.

import * as iso from './iso.js';

/** "GGPK" - the sidecar's magic, checked so a wrong file is a refusal rather than a wrong answer. */
const MAGIC = [0x47, 0x47, 0x50, 0x4B];

/** Bytes before the first plane. Must match PickMap.HeaderSize. */
const HEADER = 16;

export const KIND_NONE = 0;
export const KIND_LAND = 1;
export const KIND_STATIC = 2;
export const KIND_ITEM = 3;

const KIND_NAMES = { 1: 'land', 2: 'static', 3: 'item' };

/** key -> { state: 'loading' | 'ready' | 'error', map, reason } */
const cache = new Map();

/**
 * The last thing that went wrong, or null.
 *
 * Kept rather than thrown because the caller is a mousemove: a pick that cannot be had has to
 * degrade to the ground-plane estimate and say so once, not raise on every frame.
 */
let lastProblem = null;

export function problem() {
    return lastProblem;
}

/**
 * Forgets everything. A new world-item snapshot is a whole new set of URLs, so the entries under
 * the old one are dead weight rather than wrong - but there can be tens of thousands of them over a
 * long session, and they are 256 KB each.
 */
export function reset() {
    cache.clear();
    lastProblem = null;
}

export function cached() {
    return cache.size;
}

/**
 * Where a pick map for a canvas pixel lives, or null when the view cannot address one.
 *
 * The floor is the EFFECTIVE floor - what is actually drawn - rather than the slider's position, so
 * the pick agrees with the picture at zooms where the slider has gone inactive.
 */
function locate(view, canvasX, canvasY) {
    if (!view.art || !view.facet) {
        return null;
    }

    const level = view.art.pickLevel;

    if (!Number.isInteger(level)) {
        return null;
    }

    const at = iso.canvasToTile(
        canvasX, canvasY, level, view.artMaxLevel, view.art.tileSize || iso.TILE_SIZE);

    if (at.tileX < 0 || at.tileY < 0) {
        return null;
    }

    const layer = view.items ? `items-${view.items.id}` : 'map';
    const floor = view.effectiveFloor;
    const key = `${view.facet.name}/v${view.art.version}/${layer}/${floor}/${level}/${at.tileX}/${at.tileY}`;

    return { key, url: `/tiles/iso/${key}.pick`, pixelX: at.pixelX, pixelY: at.pixelY };
}

/**
 * What the renderer recorded at a canvas pixel, or null.
 *
 * Null means three different things and the caller treats them alike - the tile is still in flight,
 * the tile could not be had, or nothing was drawn at that pixel (open sea, or off the facet). All
 * three mean "no exact answer", and the readout falls back to the estimate and marks it.
 */
export function at(view, canvasX, canvasY) {
    const where = locate(view, canvasX, canvasY);

    if (!where) {
        return null;
    }

    const entry = cache.get(where.key);

    if (!entry || entry.state !== 'ready') {
        return null;
    }

    return entry.map.at(where.pixelX, where.pixelY);
}

/**
 * Asks for the pick map covering a canvas pixel, if it is not already asked for.
 *
 * Fire and forget: `onLoaded` is called once, when a fetch this call started has arrived, so the
 * readout can correct itself under a cursor that has stopped moving.
 */
export function request(view, canvasX, canvasY, onLoaded) {
    const where = locate(view, canvasX, canvasY);

    if (!where || cache.has(where.key)) {
        return;
    }

    load(view, where).then(
        () => { if (onLoaded) { onLoaded(); } },
        () => { if (onLoaded) { onLoaded(); } });
}

/**
 * The pick at a canvas pixel, waiting for its tile if it has to.
 *
 * What a CLICK uses. A click is rare and a pick map is about 13 ms, so waiting is imperceptible and
 * is the only answer that is never wrong; a mousemove uses `at` instead and never waits. In
 * practice the wait is already over, because the mousemove that positioned the cursor asked for
 * this same tile.
 */
export async function resolve(view, canvasX, canvasY) {
    const where = locate(view, canvasX, canvasY);

    if (!where) {
        return null;
    }

    let entry = cache.get(where.key);

    if (!entry) {
        entry = await load(view, where);
    } else if (entry.state === 'loading') {
        await entry.pending.catch(() => {});
        entry = cache.get(where.key);
    }

    return entry && entry.state === 'ready' ? entry.map.at(where.pixelX, where.pixelY) : null;
}

function load(view, where) {
    const entry = { state: 'loading', map: null, reason: null };

    entry.pending = fetch(where.url)
        .then((response) => {
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }

            return response.arrayBuffer();
        })
        .then((buffer) => {
            entry.map = decode(buffer, view.art.pickFormat);
            entry.state = 'ready';
            lastProblem = null;
            return entry;
        })
        .catch((error) => {
            entry.state = 'error';
            entry.reason = error.message;
            lastProblem = error.message;
            return entry;
        });

    cache.set(where.key, entry);

    return entry.pending;
}

/**
 * The bytes into planes, or a throw.
 *
 * The format byte is checked against what the renderer said in its handshake. It is NOT a cache
 * key - the file is gzipped whole, so the byte is not readable without inflating it - and changing
 * the format means bumping TileServer.Version, which orphans the sidecars along with the tiles they
 * sit beside. Checking it here is what turns a stale cache into a sentence somebody can act on
 * rather than picks three hundred tiles away.
 */
export function decode(buffer, expectedFormat) {
    const bytes = new Uint8Array(buffer);

    if (bytes.length < HEADER) {
        throw new Error('The pick map is truncated.');
    }

    for (let i = 0; i < MAGIC.length; i++) {
        if (bytes[i] !== MAGIC[i]) {
            throw new Error('That is not a pick map.');
        }
    }

    const format = bytes[4];

    if (expectedFormat !== undefined && format !== expectedFormat) {
        throw new Error(
            `The pick cache is stale - format ${format}, the renderer writes ${expectedFormat}. `
                + 'Bump TileServer.Version or delete tools/editor/tiles.');
    }

    const size = (bytes[6] << 8) | bytes[7];
    const view = new DataView(buffer);
    const baseX = view.getInt32(8, false);
    const baseY = view.getInt32(12, false);
    const plane = size * size;

    if (bytes.length < HEADER + (plane * 4)) {
        throw new Error('The pick map is truncated.');
    }

    const kind = bytes.subarray(HEADER, HEADER + plane);
    const dx = bytes.subarray(HEADER + plane, HEADER + (plane * 2));
    const dy = bytes.subarray(HEADER + (plane * 2), HEADER + (plane * 3));
    const z = bytes.subarray(HEADER + (plane * 3), HEADER + (plane * 4));

    return {
        size,
        format,
        baseX,
        baseY,

        at(pixelX, pixelY) {
            if (pixelX < 0 || pixelY < 0 || pixelX >= size || pixelY >= size) {
                return null;
            }

            const index = (pixelY * size) + pixelX;

            // Nothing was drawn here - open sea, or off the edge of the facet. Not an error, and
            // not a tile: there is genuinely nothing under the cursor to name.
            if (kind[index] === KIND_NONE) {
                return null;
            }

            return {
                x: baseX + dx[index],
                y: baseY + dy[index],
                // Z is an sbyte, stored +128, so this is exact over the whole range.
                z: z[index] - 128,
                kind: kind[index],
                what: KIND_NAMES[kind[index]] || 'none'
            };
        }
    };
}
