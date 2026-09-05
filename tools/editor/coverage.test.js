'use strict';

// node --test tools/editor/coverage.test.js
//
// The coverage overlay is only meaningful if its thresholds are the pathfinder's, so these tests
// pin the two things that would silently make it decoration: the metric (Chebyshev, not
// Euclidean) and the bands (derived from the hop cap, not literals).

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const { REPO_ROOT, FILES } = require('./whitelist.js');

const HOP_CAP = 12;

let coverage;

test.before(async () => {
    coverage = await import('./js/coverage.js');
});

function waypoints() {
    const nav = JSON.parse(fs.readFileSync(FILES.navigation, 'utf8'));
    return nav.waypoints.map((w) => ({ x: w.x, y: w.y }));
}

test('the grid stays within the cell budget and is finer than the hop cap', () => {
    const grid = coverage.compute(waypoints(), HOP_CAP);

    assert.ok(grid, 'no grid was produced');
    assert.ok(grid.cols * grid.rows <= 12000, `grid is ${grid.cols * grid.rows} cells`);
    assert.ok(grid.cell <= HOP_CAP, 'cells are coarser than the thing being measured');
});

test('distance is Chebyshev, not Euclidean', () => {
    // One waypoint at the origin; a cell centre offset diagonally must read as the larger axis,
    // not the hypotenuse. Euclidean would give ~1.41x this.
    const grid = coverage.compute([{ x: 0, y: 0 }], 12);

    let worst = 0;

    for (let r = 0; r < grid.rows; r++) {
        for (let c = 0; c < grid.cols; c++) {
            const cx = grid.minX + c * grid.cell + grid.cell / 2;
            const cy = grid.minY + r * grid.cell + grid.cell / 2;
            const expected = Math.max(Math.abs(cx), Math.abs(cy));

            worst = Math.max(worst, Math.abs(grid.distance[r * grid.cols + c] - expected));
        }
    }

    assert.ok(worst < 1e-6, `distance is not Chebyshev (worst error ${worst})`);
});

test('a well-covered graph reports no gaps, and an isolated point does', () => {
    coverage.compute(waypoints(), HOP_CAP);
    const real = coverage.gapCount();

    // Two waypoints far apart: the space between them must light up.
    coverage.compute([{ x: 0, y: 0 }, { x: 500, y: 0 }], HOP_CAP);

    assert.ok(coverage.gapCount() > 0, 'a 500-tile hole reported no gap');
    assert.ok(real >= 0);
});

test('edge colouring keys off the hop cap', () => {
    assert.match(coverage.edgeColor(8, HOP_CAP), /80, 200, 110/);   // comfortable
    assert.match(coverage.edgeColor(11, HOP_CAP), /255, 200, 50/);  // near the limit
    assert.match(coverage.edgeColor(14, HOP_CAP), /255, 60, 60/);   // over it
});

test('the hop cap in the editor matches the shard config', () => {
    const cfg = fs.readFileSync(path.join(REPO_ROOT, 'Config', 'Custom.cfg'), 'utf8');
    const match = cfg.match(/^NavHopMaxTiles=(\d+)/m);

    assert.ok(match, 'NavHopMaxTiles is not in Config/Custom.cfg');

    const app = fs.readFileSync(path.join(__dirname, 'js', 'app.js'), 'utf8');
    const editor = app.match(/HOP_CAP\s*=\s*(\d+)/);

    assert.ok(editor, 'HOP_CAP is not in app.js');
    assert.strictEqual(
        editor[1],
        match[1],
        'the editor and the shard disagree about the hop cap, so the coverage bands are wrong'
    );
});
