'use strict';

// The pick sidecar's binary format, from both ends.
//
// The renderer writes it in C# and the browser reads it in JavaScript, and there is no shared
// library between them - only this format. A drift here does not throw: it answers with a world
// tile hundreds of tiles from the one under the cursor, and the symptom is a waypoint appearing
// somewhere nobody clicked. So the fixtures below are built byte by byte from the spec in
// tools/MapExport/PickMap.cs rather than from the decoder's own idea of it.
//
// The C# writer has no unit test - this tool has no test project - and its check is the renderer's
// own `--pick x,y` mode, which decodes a real tile and prints what it found. What is pinned here is
// the reader: that it refuses what it should refuse, and reconstructs what it should reconstruct.

const test = require('node:test');
const assert = require('node:assert');

const HEADER = 16;
const SIZE = 4;

let pickmap;

test.before(async () => {
    pickmap = await import('./js/pickmap.js');
});

/**
 * A pick map, the way PickMap.Write lays one out: a 16-byte header, then four planes of size*size
 * bytes - kind, dx, dy, z+128 - in that order.
 */
function build({ format = 1, size = SIZE, baseX = 1420, baseY = 1550, cells = [] } = {}) {
    const plane = size * size;
    const bytes = new Uint8Array(HEADER + (plane * 4));
    const view = new DataView(bytes.buffer);

    bytes[0] = 0x47;  // G
    bytes[1] = 0x47;  // G
    bytes[2] = 0x50;  // P
    bytes[3] = 0x4B;  // K
    bytes[4] = format;
    bytes[5] = 0;
    bytes[6] = size >> 8;
    bytes[7] = size & 0xFF;
    view.setInt32(8, baseX, false);
    view.setInt32(12, baseY, false);

    for (const { x, y, dx, dy, z, kind } of cells) {
        const index = (y * size) + x;

        bytes[HEADER + index] = kind;
        bytes[HEADER + plane + index] = dx;
        bytes[HEADER + (plane * 2) + index] = dy;
        bytes[HEADER + (plane * 3) + index] = z + 128;
    }

    return bytes.buffer;
}

test('a pixel decodes to the world tile and standing Z the renderer recorded', () => {
    const map = pickmap.decode(build({
        cells: [
            { x: 1, y: 2, dx: 4, dy: 7, z: 30, kind: pickmap.KIND_LAND },
            { x: 3, y: 3, dx: 0, dy: 0, z: -5, kind: pickmap.KIND_ITEM }
        ]
    }));

    assert.strictEqual(map.size, SIZE);
    assert.strictEqual(map.baseX, 1420);
    assert.strictEqual(map.baseY, 1550);

    assert.deepStrictEqual(
        map.at(1, 2),
        { x: 1424, y: 1557, z: 30, kind: pickmap.KIND_LAND, what: 'land' });

    assert.deepStrictEqual(
        map.at(3, 3),
        { x: 1420, y: 1550, z: -5, kind: pickmap.KIND_ITEM, what: 'item' });
});

test('Z is exact over the whole sbyte range, which is why it is stored plus 128', () => {
    // UO's Z is an sbyte, so -128..127 fits a byte with nothing left over and nothing to clamp.
    // The ends are what a naive `z & 0xFF` or a signed read would get wrong, and a cliff top or a
    // dungeon floor is exactly where the ends live.
    for (const z of [-128, -1, 0, 1, 126, 127]) {
        const map = pickmap.decode(build({
            cells: [{ x: 0, y: 0, dx: 0, dy: 0, z, kind: pickmap.KIND_STATIC }]
        }));

        assert.strictEqual(map.at(0, 0).z, z, `z ${z} did not survive the round trip`);
    }
});

test('a pixel nothing was drawn on is null, not tile 0,0', () => {
    // Open sea, or off the edge of the facet. Not an error and not a tile: there is genuinely
    // nothing under the cursor to name, and answering the base coordinate would be a confident
    // wrong answer rather than no answer.
    const map = pickmap.decode(build({
        cells: [{ x: 0, y: 0, dx: 3, dy: 3, z: 0, kind: pickmap.KIND_LAND }]
    }));

    assert.strictEqual(map.at(1, 1), null);
    assert.notStrictEqual(map.at(0, 0), null);
});

test('a pixel outside the tile is null rather than a wrap into the next row', () => {
    const map = pickmap.decode(build({
        cells: [{ x: 0, y: 1, dx: 1, dy: 1, z: 0, kind: pickmap.KIND_LAND }]
    }));

    assert.strictEqual(map.at(SIZE, 0), null, 'past the right edge');
    assert.strictEqual(map.at(-1, 1), null, 'past the left edge');
    assert.strictEqual(map.at(0, SIZE), null, 'past the bottom');
});

test('a file that is not a pick map is refused rather than decoded', () => {
    const notOurs = new Uint8Array(HEADER + 64);
    notOurs[0] = 0x89;   // a PNG signature, which is what would arrive if the route ever crossed

    assert.throws(() => pickmap.decode(notOurs.buffer), /not a pick map/i);
});

test('a truncated file is refused, both at the header and at the planes', () => {
    assert.throws(() => pickmap.decode(new Uint8Array(4).buffer), /truncated/i);

    const short = new Uint8Array(build()).slice(0, HEADER + 8);

    assert.throws(() => pickmap.decode(short.buffer), /truncated/i);
});

test('a format the renderer no longer writes says the cache is stale, and says what to do', () => {
    // The format byte is NOT a cache key: the file is gzipped whole, so it is not readable without
    // inflating it, and changing the format means bumping TileServer.Version. What it buys is that
    // a stale sidecar becomes a sentence somebody can act on rather than picks in the wrong place.
    assert.throws(
        () => pickmap.decode(build({ format: 1 }), 2),
        /stale.*TileServer\.Version|TileServer\.Version.*stale/is);

    // And the current format passes the same check rather than being waved through unchecked.
    assert.ok(pickmap.decode(build({ format: 2 }), 2));
});
