'use strict';

// The isometric projection, pinned.
//
// The formula exists twice - tools/MapExport/IsoTransform.cs and tools/editor/js/iso.js - because
// one side renders the art and the other places the layers on it. Nothing stops those drifting,
// and the symptom would not be an error: it would be every waypoint sitting a couple of tiles off
// the building it belongs to, which reads as "the nav data is wrong" rather than "the projection
// is wrong". So the numbers here are checked against the literals in Ultima/Multis.cs, which is
// where both copies came from, and against the C# constants by reading the file.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const REPO = path.resolve(__dirname, '..', '..');

let iso;

test.before(async () => {
    iso = await import('./js/iso.js');
});

// --- the formula ----------------------------------------------------------------------------------

test('the anchor is Multis.cs\'s own, term for term', () => {
    // Ultima/Multis.cs:502-506:
    //     int px = (x - y) * 22;
    //     int py = (x + y) * 22;
    //     py -= tiles[i].Z << 2;
    const x = 1424;
    const y = 1557;
    const z = 30;

    assert.deepStrictEqual(iso.worldToIso(x, y, z), {
        ix: (x - y) * 22,
        iy: (x + y) * 22 - (z << 2)
    });
});

test('a sprite hangs by its bottom centre, and land is not a special case', () => {
    // Ultima/Multis.cs:505-507: px -= bmp.Width / 2; py -= bmp.Height.
    assert.deepStrictEqual(iso.spriteTopLeft(1000, 2000, 44, 44), { x: 978, y: 1956 });
    assert.deepStrictEqual(iso.spriteTopLeft(1000, 2000, 21, 67), { x: 990, y: 1933 });

    // The land diamond is 44x44, so its top-left is the anchor less (22, 44) - which is the same
    // rule with the land size substituted, not a second rule.
    const { ix, iy } = iso.worldToIso(10, 10, 0);
    assert.deepStrictEqual(
        iso.spriteTopLeft(ix, iy, iso.LAND_ART_SIZE, iso.LAND_ART_SIZE),
        { x: ix - 22, y: iy - 44 });
});

test('the inverse recovers the tile it was given, at the Z it was given', () => {
    for (const [x, y, z] of [[0, 0, 0], [1424, 1557, 30], [7167, 4095, -128], [100, 900, 127]]) {
        const { ix, iy } = iso.worldToIso(x, y, z);
        const back = iso.isoToWorld(ix, iy, z);

        assert.ok(Math.abs(back.x - x) < 1e-9, `x ${back.x} != ${x}`);
        assert.ok(Math.abs(back.y - y) < 1e-9, `y ${back.y} != ${y}`);
    }
});

test('the inverse at the wrong Z is wrong by z*4/44 tiles, in both axes', () => {
    // The number the pick map exists to replace, worth having as an assertion rather than as a claim
    // in a comment. Britain stands near z 30, and this is what a placement from the ground-plane
    // inverse would have been out by - which is why the readout marks that estimate with a ~ rather
    // than showing it as a measurement.
    const { ix, iy } = iso.worldToIso(1424, 1557, 30);
    const ground = iso.isoToWorld(ix, iy, 0);

    const drift = (30 * iso.Z_STEP) / (2 * iso.HALF_HEIGHT);

    assert.ok(Math.abs(ground.x - (1424 - drift)) < 1e-9);
    assert.ok(Math.abs(ground.y - (1557 - drift)) < 1e-9);
    assert.ok(Math.abs(drift - 2.727) < 0.001, `drift is ${drift}, the README says about 2.7`);
});

test('at z=0 the projection is the linear map isoMatrix describes', () => {
    // The radar underlay is drawn by handing this matrix to setTransform and reusing the existing
    // world-space tile loop, so it has to BE the projection rather than resemble it.
    const scale = 3;
    const [a, b, c, d] = iso.isoMatrix(scale);
    const s = scale / iso.HALF_WIDTH;

    const at = (x, y) => {
        const { ix, iy } = iso.worldToIso(x, y, 0);
        return [ix * s, iy * s];
    };

    const origin = at(0, 0);
    const alongX = at(1, 0);
    const alongY = at(0, 1);

    assert.ok(Math.abs((alongX[0] - origin[0]) - a) < 1e-9, 'dsx/dx');
    assert.ok(Math.abs((alongX[1] - origin[1]) - b) < 1e-9, 'dsy/dx');
    assert.ok(Math.abs((alongY[0] - origin[0]) - c) < 1e-9, 'dsx/dy');
    assert.ok(Math.abs((alongY[1] - origin[1]) - d) < 1e-9, 'dsy/dy');
});

// --- stretched land -------------------------------------------------------------------------------

test("a level tile's four corners are exactly the flat art's four vertices", () => {
    // THE INVARIANT THE WHOLE FEATURE RESTS ON. Stretching may only move ground that is genuinely
    // sloped; a flat tile has to land on the same pixels it landed on before there was a quad at
    // all. If this drifts, every level tile in the world moves and nobody would guess why.
    const x = 1424;
    const y = 1557;
    const z = 30;

    const { ix, iy } = iso.worldToIso(x, y, z);   // where the 44x44 art's bottom centre goes
    const quad = iso.landQuad(x, y, z, z, z, z);

    assert.deepStrictEqual(quad.north, { ix: ix, iy: iy - 44 });
    assert.deepStrictEqual(quad.east, { ix: ix + 22, iy: iy - 22 });
    assert.deepStrictEqual(quad.south, { ix: ix, iy: iy });
    assert.deepStrictEqual(quad.west, { ix: ix - 22, iy: iy - 22 });
});

test('neighbouring tiles share their corner vertices exactly', () => {
    // Why the quads tile watertight and the rasteriser needs no seam filling: the south corner of
    // one tile IS the north corner of the tile south-east of it, at the same integer pixel. Held at
    // a shared Z, because two tiles only agree about a corner they both own.
    const z = 12;
    const here = iso.landQuad(100, 200, z, z, z, z);
    const southEast = iso.landQuad(101, 201, z, z, z, z);

    assert.deepStrictEqual(here.south, southEast.north);

    const east = iso.landQuad(101, 200, z, z, z, z);
    assert.deepStrictEqual(here.east, east.north);
});

test('a corner rises four pixels per Z, and only vertically', () => {
    const flat = iso.isoCorner(500, 400, 0);
    const raised = iso.isoCorner(500, 400, 25);

    assert.strictEqual(raised.ix, flat.ix, 'Z must not move a corner sideways');
    assert.strictEqual(flat.iy - raised.iy, 25 * iso.Z_STEP);
});

test('a sloped tile is a quad with three distinct x values and four distinct y values', () => {
    // The shape the rasteriser has to cope with. x is fixed by the lattice - only three columns
    // exist - so every distortion a cliff produces is vertical, which is why two triangles are
    // enough and why a 30z step (120 pixels) can invert the quad rather than merely skew it.
    const quad = iso.landQuad(1449, 1521, 39, 35, 20, 30);

    const xs = new Set([quad.north.ix, quad.east.ix, quad.south.ix, quad.west.ix]);
    const ys = new Set([quad.north.iy, quad.east.iy, quad.south.iy, quad.west.iy]);

    assert.strictEqual(xs.size, 3);
    assert.strictEqual(ys.size, 4);

    // north and south share a column; east and west are 22 either side of it.
    assert.strictEqual(quad.north.ix, quad.south.ix);
    assert.strictEqual(quad.east.ix - quad.north.ix, iso.HALF_WIDTH);
    assert.strictEqual(quad.north.ix - quad.west.ix, iso.HALF_WIDTH);
});

// --- the grid -------------------------------------------------------------------------------------

test('the whole facet lands inside the canvas, sprite bleed included', () => {
    const width = 7168;
    const height = 4096;

    const canvasW = iso.canvasWidth(width, height);
    const canvasH = iso.canvasHeight(width, height);

    // The four extreme columns, each drawn as a 44x44 land diamond at the most extreme Z.
    const corners = [[0, 0], [width - 1, 0], [0, height - 1], [width - 1, height - 1]];

    for (const [x, y] of corners) {
        for (const z of [-128, 127]) {
            const { ix, iy } = iso.worldToIso(x, y, z);
            const left = ix - iso.originX(height) - iso.LAND_ART_SIZE / 2;
            const top = iy - iso.originY() - iso.LAND_ART_SIZE;

            assert.ok(left >= 0, `${x},${y},${z} runs off the left at ${left}`);
            assert.ok(top >= 0, `${x},${y},${z} runs off the top at ${top}`);
            assert.ok(left + iso.LAND_ART_SIZE <= canvasW, `${x},${y},${z} runs off the right`);
            assert.ok(top + iso.LAND_ART_SIZE <= canvasH, `${x},${y},${z} runs off the bottom`);
        }
    }
});

test('Trammel is levels 0-10 and the deepest is one pixel per iso pixel', () => {
    assert.strictEqual(iso.maxLevel(7168, 4096), 10);
    assert.strictEqual(iso.divisor(10, 10), 1);
    assert.strictEqual(iso.divisor(9, 10), 2);
    assert.strictEqual(iso.divisor(7, 10), 8);
});

test('four levels are art and the closest two carry floors', () => {
    const max = iso.maxLevel(7168, 4096);

    assert.strictEqual(iso.minArtLevel(max), max - 3);

    assert.ok(iso.hasFloors(max, max));
    assert.ok(iso.hasFloors(max - 1, max));
    assert.ok(!iso.hasFloors(max - 2, max), 'a storey is five pixels at 1:4; there is nothing to peel');
    assert.ok(!iso.hasFloors(max - 3, max));
});

// --- the eyeball check, as a number ---------------------------------------------------------------

test('brit-forge lands on a known tile and pixel', () => {
    // Data/Custom/navigation.json:1298 - the forge in the smithy yard, Trammel 1424,1557,z30. The
    // session's acceptance check is that its marker sits on the forge in the art view; this is the
    // arithmetic half of it, so a regression in the projection fails here rather than by eye.
    const found = iso.tileFor(1424, 1557, 30, 10, 10, 4096);

    assert.deepStrictEqual(found, {
        canvasX: 87186,
        canvasY: 66486,
        tileX: 340,
        tileY: 259,
        pixelX: 146,
        pixelY: 182
    });
});

test('the same point is the same place at every art level', () => {
    // Zooming must not move the world. The canvas position is level-independent by construction,
    // so this asserts the tile/pixel split stays consistent with it.
    const max = 10;

    for (let level = iso.minArtLevel(max); level <= max; level++) {
        const found = iso.tileFor(1424, 1557, 30, level, max, 4096);
        const span = iso.TILE_SIZE * iso.divisor(level, max);

        assert.strictEqual(found.tileX, Math.floor(found.canvasX / span));
        assert.strictEqual(found.tileY, Math.floor(found.canvasY / span));
        assert.ok(found.pixelX >= 0 && found.pixelX < iso.TILE_SIZE);
        assert.ok(found.pixelY >= 0 && found.pixelY < iso.TILE_SIZE);
    }
});

// --- reading the pick map -------------------------------------------------------------------------

test('canvasToTile is tileFor run backwards, at every art level', () => {
    // The pair is what makes a pick map a lookup instead of an inverse. tileFor says where a world
    // point lands in the pyramid; canvasToTile says which pyramid pixel a canvas point is. A drift
    // between them would put every click one tile out, uniformly, which is exactly the kind of
    // wrongness that reads as bad data.
    const max = iso.maxLevel(7168, 4096);

    for (const [x, y, z] of [[1424, 1557, 30], [1475, 1645, 20], [0, 0, 0], [7167, 4095, -5]]) {
        for (let level = iso.minArtLevel(max); level <= max; level++) {
            const forward = iso.tileFor(x, y, z, level, max, 4096);
            const back = iso.canvasToTile(forward.canvasX, forward.canvasY, level, max);

            assert.deepStrictEqual(
                back,
                {
                    tileX: forward.tileX, tileY: forward.tileY,
                    pixelX: forward.pixelX, pixelY: forward.pixelY
                },
                `${x},${y},${z} at level ${level}`);
        }
    }
});

test('a canvas pixel names the same tile at every zoom, which is why one pick map is enough', () => {
    // The whole argument for rendering pick maps at the deepest level only. A canvas pixel is a
    // facet-global number; the camera decides how many SCREEN pixels it is worth and nothing else.
    // So the pixel under the cursor at 1:8 is the same pixel it would be at 1:1, and the 1:1
    // sidecar answers both.
    const max = iso.maxLevel(7168, 4096);
    const deep = iso.tileFor(1424, 1557, 30, max, max, 4096);

    for (let level = iso.minArtLevel(max); level < max; level++) {
        const coarse = iso.tileFor(1424, 1557, 30, level, max, 4096);

        assert.strictEqual(coarse.canvasX, deep.canvasX, `canvas x moved at level ${level}`);
        assert.strictEqual(coarse.canvasY, deep.canvasY, `canvas y moved at level ${level}`);

        const answered = iso.canvasToTile(coarse.canvasX, coarse.canvasY, max, max);

        assert.deepStrictEqual(
            { tileX: answered.tileX, tileY: answered.tileY },
            { tileX: deep.tileX, tileY: deep.tileY },
            `the deepest pick tile is not reachable from level ${level}`);
    }
});

test('a canvas pixel off the north-west of the facet is outside the pyramid, not inside tile 0', () => {
    // `%` on a negative in JavaScript keeps the sign, so a truncating version of this would put a
    // cursor dragged off the top-left into tile -0 at a positive pixel - a confident answer about a
    // tile that does not exist. Floored, it stays negative and the caller drops it.
    const max = iso.maxLevel(7168, 4096);
    const off = iso.canvasToTile(-1, -1, max, max);

    assert.ok(off.tileX < 0 && off.tileY < 0, 'a negative canvas pixel is not tile 0');
});

// --- a rect on the ground -------------------------------------------------------------------------

test('a world rect is traced at the ground under its corners, or on the plane without one', () => {
    // A zone carries no Z in its schema and none is being added: where the land is under each
    // corner is a question with an answer, so it is asked. Until it was, a zone in Britain's upper
    // town sat about 2.7 tiles uphill of the land it covers - and hitTest used the same z=0 corners,
    // so the diamond you clicked was not the diamond you saw.
    const path = [];
    const ctx = {
        beginPath() {}, closePath() {},
        moveTo(x, y) { path.push([x, y]); },
        lineTo(x, y) { path.push([x, y]); }
    };

    // The camera stub from editor.test.js, small enough to keep here rather than share: art
    // projection, scale 22, honouring Z.
    const view = {
        isArt: true,
        toScreen: (x, y, z = 0) => [(x - y) * 22, (x + y) * 22 - (z * 4)]
    };

    iso.traceWorldRect(ctx, view, 1470, 1640, 10, 10);
    const flat = path.splice(0, path.length);

    iso.traceWorldRect(ctx, view, 1470, 1640, 10, 10, () => 30);
    const lifted = path.splice(0, path.length);

    assert.strictEqual(flat.length, 4);
    assert.strictEqual(lifted.length, 4);

    for (let i = 0; i < 4; i++) {
        assert.strictEqual(lifted[i][0], flat[i][0], 'lifting a corner must not move it sideways');
        assert.strictEqual(lifted[i][1], flat[i][1] - 120, 'z 30 is 120 pixels up, four per Z');
    }
});

test('a corner whose ground Z is not known yet stays on the plane rather than vanishing', () => {
    // landz.at answers null until the batch comes back. Null has to read as "not yet" and draw at
    // zero for a frame, not as NaN - a zone that disappears while its corners are in flight would
    // be a worse bug than the one this fixes.
    const path = [];
    const ctx = {
        beginPath() {}, closePath() {},
        moveTo(x, y) { path.push([x, y]); },
        lineTo(x, y) { path.push([x, y]); }
    };

    const view = { isArt: true, toScreen: (x, y, z = 0) => [(x - y) * 22, (x + y) * 22 - (z * 4)] };

    iso.traceWorldRect(ctx, view, 1470, 1640, 10, 10, () => null);

    assert.strictEqual(path.length, 4);
    assert.ok(path.every(([x, y]) => Number.isFinite(x) && Number.isFinite(y)));
});

test('radar is untouched by a ground Z, because a rect there is still a rect', () => {
    let touched = false;
    const ctx = { beginPath() { touched = true; }, closePath() {}, moveTo() {}, lineTo() {} };

    assert.strictEqual(
        iso.traceWorldRect(ctx, { isArt: false }, 0, 0, 1, 1, () => 30),
        false);

    assert.strictEqual(touched, false, 'radar must not even begin a path');
});

// --- the two copies -------------------------------------------------------------------------------

test('every constant matches tools/MapExport/IsoTransform.cs', () => {
    // Read rather than trusted. The C# side is the one that renders the pixels, so if these ever
    // disagree the art is right and the editor is wrong - which is the failure that looks like bad
    // nav data instead of a bad projection.
    const source = fs.readFileSync(
        path.join(REPO, 'tools', 'MapExport', 'IsoTransform.cs'), 'utf8');

    const constant = (name) => {
        const match = source.match(new RegExp(`public const int ${name} = ([^;]+);`));
        assert.ok(match, `IsoTransform.cs has no ${name}`);
        // The values are either literals or a product of two, e.g. 128 * ZStep.
        return Function(`"use strict"; const ZStep = 4; const MaxSpriteHeight = 512;
            const ZLift = 128 * ZStep; return (${match[1]});`)();
    };

    assert.strictEqual(constant('HalfWidth'), iso.HALF_WIDTH);
    assert.strictEqual(constant('HalfHeight'), iso.HALF_HEIGHT);
    assert.strictEqual(constant('ZStep'), iso.Z_STEP);
    assert.strictEqual(constant('LandArtSize'), iso.LAND_ART_SIZE);
    assert.strictEqual(constant('MaxSpriteHeight'), iso.MAX_SPRITE_HEIGHT);
    assert.strictEqual(constant('ZLift'), iso.Z_LIFT);
    assert.strictEqual(constant('HeadroomTop'), iso.HEADROOM_TOP);
    assert.strictEqual(constant('HeadroomBottom'), iso.HEADROOM_BOTTOM);
    assert.strictEqual(constant('ArtLevelDepth'), iso.ART_LEVEL_DEPTH);
    assert.strictEqual(constant('FloorLevelDepth'), iso.FLOOR_LEVEL_DEPTH);
});

test('the pick sidecar format the browser decodes is the one the renderer writes', () => {
    // PickMap.Format is not a cache key - the file is gzipped whole, so the byte is not readable
    // without inflating it, and changing the format means bumping TileServer.Version. What this
    // pins is the two ends of the decode: the header size and the magic have to be the same on both
    // sides or a sidecar reads as garbage rather than as an error.
    const source = fs.readFileSync(path.join(REPO, 'tools', 'MapExport', 'PickMap.cs'), 'utf8');

    const header = source.match(/public const int HeaderSize = (\d+);/);
    assert.ok(header, 'PickMap.cs has no HeaderSize');
    assert.strictEqual(Number(header[1]), 16, 'js/pickmap.js decodes a 16-byte header');

    // "GGPK", one byte per character, in the order the decoder checks them.
    assert.ok(
        source.includes("{ (byte)'G', (byte)'G', (byte)'P', (byte)'K' }"),
        'PickMap.cs no longer writes the magic js/pickmap.js looks for');
});

test('the floor names are the ones the renderer parses', () => {
    // FloorRules.TryParse in IsoTileRenderer.cs is what a tile request is matched against, and
    // artrenderer.js refuses anything else. A fourth stop added on one side only would 400.
    const source = fs.readFileSync(
        path.join(REPO, 'tools', 'MapExport', 'IsoTileRenderer.cs'), 'utf8');

    for (const floor of iso.FLOORS) {
        assert.ok(source.includes(`case "${floor}":`), `IsoTileRenderer.cs does not parse '${floor}'`);
    }
});
