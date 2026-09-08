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
    // This is the reason placing and dragging are refused in art view, so it is worth having as a
    // number rather than as a claim in a comment. Britain stands near z 30.
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

test('the floor names are the ones the renderer parses', () => {
    // FloorRules.TryParse in IsoTileRenderer.cs is what a tile request is matched against, and
    // artrenderer.js refuses anything else. A fourth stop added on one side only would 400.
    const source = fs.readFileSync(
        path.join(REPO, 'tools', 'MapExport', 'IsoTileRenderer.cs'), 'utf8');

    for (const floor of iso.FLOORS) {
        assert.ok(source.includes(`case "${floor}":`), `IsoTileRenderer.cs does not parse '${floor}'`);
    }
});
