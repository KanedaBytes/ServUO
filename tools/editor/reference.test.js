/**
 * The reference projection: what /api/reference hands the browser.
 *
 * Driven against the committed reference file, which is why these are invariants rather than
 * fixtures - the point of most of them is that the layer stays cheap enough to pan with.
 */

const test = require('node:test');
const assert = require('node:assert');

const reference = require('./reference.js');
const whitelist = require('./whitelist.js');

/** Britain, which is where our authored data and theirs overlap most. */
const BRITAIN = { x: 1400, y: 1600, width: 200, height: 200 };

test('the reference is read-only: the save endpoint cannot name it', () => {
    // The whole safety story in one assertion. resolveSave serves the WRITABLE table and the spawn
    // files; `reference` is in neither, so there is no spelling of a save request that reaches it.
    assert.ok(whitelist.FILES.reference, 'the reference file is not on the read whitelist');
    assert.strictEqual(whitelist.resolveSave('reference'), null);
    assert.ok(!Object.prototype.hasOwnProperty.call(whitelist.WRITABLE, 'reference'));
});

test('a region toggle that is off costs nothing on the wire', () => {
    // The reason regions go in the query rather than being filtered in the browser: the dungeon
    // half is 1988 waypoints, and serialising them to have the browser drop them is the work the
    // bbox exists to avoid.
    const everywhere = { x: 0, y: 0, width: 8192, height: 8192 };

    const overworld = reference.project(everywhere, { regions: ['overworld'] });
    const withDungeons = reference.project(everywhere, { regions: ['overworld', 'dungeon'] });

    assert.ok(withDungeons.shapes.length > overworld.shapes.length,
        'asking for dungeons returned no more than not asking');

    const none = reference.project(everywhere, { regions: [] });

    assert.strictEqual(none.shapes.length, 0);
});

test('a box returns what is in it, and nothing like the whole facet', () => {
    const inBox = reference.project(BRITAIN, { regions: ['overworld'] });
    const all = reference.project(null, { regions: ['overworld'] });

    assert.ok(inBox.shapes.length > 0, 'Britain came back empty');
    assert.ok(inBox.shapes.length < all.shapes.length / 5,
        'the bbox barely narrowed anything, so panning would be as costly as not');

    // Every waypoint and destination returned is either in the box or the far end of an edge that
    // leaves it - a road that stopped dead at the edge of the view would look like a broken graph.
    for (const shape of inBox.shapes) {
        if (shape.layer !== 'reference-destinations') {
            continue;
        }

        const [x, y] = shape.points[0];

        assert.ok(reference.inBox(x, y, BRITAIN), `${shape.id} is outside the box it was asked for`);
    }
});

test('an edge that leaves the box brings its far waypoint with it', () => {
    const out = reference.project(BRITAIN, { regions: ['overworld'] });
    const ids = new Set(out.shapes.filter((s) => s.layer === 'reference').map((s) => s.id));

    let leaving = 0;

    for (const shape of out.shapes) {
        if (shape.layer !== 'reference-edges') {
            continue;
        }

        // Both ends must be present as drawable points, or the line hangs from nothing.
        assert.ok(ids.has(`ref:${shape.props.from}`), `${shape.id} has no 'from' waypoint`);
        assert.ok(ids.has(`ref:${shape.props.to}`), `${shape.id} has no 'to' waypoint`);

        const [ax, ay] = shape.points[0];
        const [bx, by] = shape.points[1];

        if (!reference.inBox(ax, ay, BRITAIN) || !reference.inBox(bx, by, BRITAIN)) {
            leaving++;
        }
    }

    assert.ok(leaving > 0, 'no edge left the box, so this case was never exercised');
});

test('every edge carries its length, because most of them are over the cap', () => {
    // The one number an author needs before adopting a road: 56% of these are longer than
    // Custom.NavHopMaxTiles and will be subdivided rather than copied.
    const out = reference.project(BRITAIN, { regions: ['overworld'] });
    const edges = out.shapes.filter((s) => s.layer === 'reference-edges');

    assert.ok(edges.length > 0);

    for (const edge of edges) {
        const [ax, ay] = edge.points[0];
        const [bx, by] = edge.points[1];

        assert.strictEqual(
            edge.props.tiles, Math.max(Math.abs(ax - bx), Math.abs(ay - by)));
    }
});

test('reference shapes are namespaced so they cannot be confused with authored ones', () => {
    const out = reference.project(BRITAIN, { regions: ['overworld'] });

    for (const shape of out.shapes) {
        assert.ok(shape.id.startsWith('ref:'), `${shape.id} is not a reference id`);
        assert.ok(shape.layer.startsWith('reference'), `${shape.id} is on layer ${shape.layer}`);
    }
});

test('a record on another facet is not drawn', () => {
    const out = reference.project(BRITAIN, { regions: ['overworld'], facet: 'Felucca' });

    // The file is stamped Trammel, so asking for Felucca is asking for nothing - which is the
    // behaviour that keeps a later Felucca conversion from doubling every road.
    assert.strictEqual(out.shapes.length, 0);
});

test('the summary counts the whole file, not the box', () => {
    const counts = reference.summary();

    assert.strictEqual(counts.available, true);
    assert.strictEqual(counts.overworld, 1921);
    assert.strictEqual(counts.dungeon, 1988);
    assert.strictEqual(counts.lostlands, 43);
    assert.strictEqual(counts.destinations, 485);
});
