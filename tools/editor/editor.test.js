'use strict';

// node --test tools/editor/*.test.js
//
// The parts of the browser editor that are pure enough to test without a browser: id generation,
// the tool table, and the hit-testing rules. Everything here would otherwise only be checked by
// clicking, and two of these are rules that were silently wrong in 5a.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const whitelist = require('./whitelist.js');
const { FILES, WRITABLE, REPO_ROOT } = whitelist;
const { project } = require('./project.js');

let ids;
let shapes;
let tools;

test.before(async () => {
    // tools.js is imported here on purpose. In 5a it bound the modal at module scope, so importing
    // it outside a browser threw and nothing imported it - which is how it came to be dead code
    // that nobody noticed. If this file loads, that cannot happen again.
    [ids, shapes, tools] = await Promise.all([
        import('./js/ids.js'),
        import('./js/shapes.js'),
        import('./js/tools.js')
    ]);
});

function realShapes() {
    return project({
        navigation: JSON.parse(fs.readFileSync(FILES.navigation, 'utf8')),
        dailyLife: JSON.parse(fs.readFileSync(FILES.dailyLife, 'utf8')),
        restrictedZones: JSON.parse(fs.readFileSync(FILES.restrictedZones, 'utf8'))
    });
}

function zone(id, x, y, width, height, tags) {
    return {
        layer: 'nav-zones', id: `zone:${id}`, kind: 'rect', map: 'Trammel', label: id,
        rect: [x, y, width, height], props: { id, tags }, fields: []
    };
}

// --- ids.js: the [NavMark scheme ------------------------------------------------------------------

test('an auto id takes its prefix from the smallest zone containing the point', () => {
    const world = [
        zone('outer', 0, 0, 100, 100, 'town britain guarded'),
        zone('inner', 10, 10, 20, 20, 'market busy')
    ];

    assert.strictEqual(ids.prefixAt(15, 15, 'Trammel', world), 'market');
    assert.strictEqual(ids.prefixAt(50, 50, 'Trammel', world), 'town');
    assert.strictEqual(ids.prefixAt(500, 500, 'Trammel', world), 'wp');
});

test('the first zone in file order wins a tie, as Nav.SmallestZoneAt does', () => {
    // Strictly-less-than in the C#, so two zones of equal area do not swap places depending on
    // which happens to be enumerated last. Nested town rectangles make this reachable.
    const world = [
        zone('first', 0, 0, 10, 10, 'alpha'),
        zone('second', 0, 0, 10, 10, 'beta')
    ];

    assert.strictEqual(ids.prefixAt(5, 5, 'Trammel', world), 'alpha');
});

test('a zone with no tags falls back to its own id, and a wrong facet does not count', () => {
    const untagged = [zone('quarry', 0, 0, 10, 10, '')];

    assert.strictEqual(ids.prefixAt(5, 5, 'Trammel', untagged), 'quarry');
    assert.strictEqual(ids.prefixAt(5, 5, 'Felucca', untagged), 'wp');
});

test('an auto id skips ids that are taken, across kinds and ignoring case', () => {
    // Stricter than the shard, deliberately: Nav.TryRoute accepts an id naming a waypoint OR a
    // destination, so letting the two collide would be genuinely ambiguous.
    const world = [
        zone('z', 0, 0, 100, 100, 'town'),
        { layer: 'nav', id: 'wp:town-1', kind: 'point', map: 'Trammel', points: [[1, 1, 0]], props: {} },
        { layer: 'nav-destinations', id: 'dest:TOWN-2', kind: 'point', map: 'Trammel', points: [[1, 1, 0]], props: {} }
    ];

    assert.strictEqual(ids.nextId(5, 5, 'Trammel', world), 'town-3');
});

test('auto ids on the real data land in the zone they were clicked in', () => {
    const world = realShapes();
    const zones = world.filter((s) => s.layer === 'nav-zones');

    assert.ok(zones.length > 0, 'the fixture needs nav zones');

    for (const shape of zones) {
        const [x, y, width, height] = shape.rect;
        const prefix = ids.prefixAt(x + Math.floor(width / 2), y + Math.floor(height / 2), 'Trammel', world);
        const smallest = ids.zoneAt(x + Math.floor(width / 2), y + Math.floor(height / 2), 'Trammel', world);

        assert.strictEqual(prefix, String(smallest.props.tags).split(' ')[0]);
        assert.ok(smallest.rect[2] * smallest.rect[3] <= width * height,
            'a zone centre resolved to a zone larger than the one it is in');
    }

    assert.match(ids.nextId(1475, 1645, 'Trammel', world), /^[a-z]+-\d+$/);
});

// --- tools.js -------------------------------------------------------------------------------------

test('every tool names a real layer and a kind the state machine handles', () => {
    const kinds = new Set(['point', 'rect', 'form', 'pair', 'chain', 'owner-then-point']);

    for (const [key, tool] of Object.entries(tools.TOOLS)) {
        assert.ok(shapes.LAYERS[tool.layer], `${key} names an unknown layer '${tool.layer}'`);
        assert.ok(kinds.has(tool.kind), `${key} has an unknown kind '${tool.kind}'`);

        if (tool.picks) {
            assert.ok(shapes.LAYERS[tool.picks], `${key} picks an unknown layer '${tool.picks}'`);
            assert.ok(tool.hint2 || tool.kind === 'pair' || tool.kind === 'chain',
                `${key} collects a shape first but never says so`);
        }
    }
});

test('no tool creates a daily-life record, because adding one stays a JSON edit', () => {
    for (const [key, tool] of Object.entries(tools.TOOLS)) {
        assert.notStrictEqual(tool.layer, 'dailylife', `${key} would create a daily-life record`);
    }
});

// --- shapes.js ------------------------------------------------------------------------------------

test('every editable layer names the file it is saved to, and the request that reloads it', () => {
    for (const [name, layer] of Object.entries(shapes.LAYERS)) {
        // Layers the editor draws but never writes carry no file and no reload.
        if (shapes.READ_ONLY_LAYERS.has(name)) {
            assert.strictEqual(layer.file, null, `${name} is read-only but names a file`);
            assert.strictEqual(layer.reload, null, `${name} is read-only but names a reload`);
            continue;
        }

        // GG spawners are the one writable family that is many files rather than one, so the file
        // is carried in each shape's own id and the layer names only the request.
        if (name === 'spawners') {
            assert.strictEqual(layer.file, null, 'the spawner file comes from the shape id');
            assert.strictEqual(layer.reload, 'spawn-reload');
            assert.strictEqual(
                whitelist.reloadFor('spawn:trammel/GG_DailyLife.xml'), layer.reload,
                'the layer and the whitelist disagree about how a spawn file reloads');
            continue;
        }

        assert.ok(WRITABLE[layer.file], `${name} saves to '${layer.file}', which is not writable`);
        assert.strictEqual(layer.reload, WRITABLE[layer.file],
            `${name} reloads with '${layer.reload}' but its file reloads with '${WRITABLE[layer.file]}'`);
    }
});

test('the spawner layers are what the projector actually emits', () => {
    const spawners = require('./spawners.js');
    const fs2 = require('fs');

    const projected = spawners.project([{
        key: 'spawn:trammel/GG_DailyLife.xml',
        relative: 'trammel/GG_DailyLife.xml',
        text: fs2.readFileSync(
            path.join(REPO_ROOT, 'Spawns', 'Custom', 'trammel', 'GG_DailyLife.xml'), 'utf8')
    }]);

    assert.ok(projected.length > 0);

    for (const shape of projected) {
        assert.ok(shapes.LAYERS[shape.layer], `${shape.layer} is not a layer`);
        assert.ok(Array.isArray(shape.entries), 'a spawner shape carries no entry list');
        assert.match(shape.id, /^spawner:[a-z]+\/GG_[A-Za-z0-9_-]+\.xml#[0-9a-f-]{36}$/);
    }

});

const view = { scale: 1, facet: { name: 'Trammel' }, toScreen: (x, y) => [x, y] };

function pointShape(id, x, y, layer = 'nav') {
    return { layer, id, kind: 'point', map: 'Trammel', label: id, points: [[x, y, 0]], props: {} };
}

test('clicking an edge line selects the edge, and never steals a waypoint click', () => {
    // An edge's nodes sit exactly on the waypoints it joins, so before the segment test an edge
    // could not be selected at all - which left no way to delete one.
    const a = pointShape('wp:a', 0, 0);
    const b = pointShape('wp:b', 40, 0);
    const edge = {
        layer: 'nav-edges', id: 'edge:a>b', kind: 'polyline', map: 'Trammel', label: 'a - b',
        points: [[0, 0, 0], [40, 0, 0]], props: { from: 'a', to: 'b' }
    };

    const visible = new Set(['nav', 'nav-edges']);
    const world = [edge, a, b];

    const middle = shapes.hitTest(view, world, visible, null, 20.5, 0.5);
    assert.strictEqual(middle.shape, edge);
    assert.strictEqual(middle.mode, 'body');

    // On top of a waypoint, the waypoint wins.
    const onA = shapes.hitTest(view, world, visible, null, 0.5, 0.5);
    assert.strictEqual(onA.shape, a);

    // Well off the line, nothing.
    assert.strictEqual(shapes.hitTest(view, world, visible, null, 20.5, 30), null);
});

test('a hidden layer is not hit-tested, so an invisible edge cannot be picked', () => {
    const edge = {
        layer: 'nav-edges', id: 'edge:a>b', kind: 'polyline', map: 'Trammel',
        points: [[0, 0, 0], [40, 0, 0]], props: {}
    };

    assert.strictEqual(shapes.hitTest(view, [edge], new Set(['nav']), null, 20.5, 0.5), null);
});

// --- drawing --------------------------------------------------------------------------------------
//
// A canvas is the one thing node cannot give us, so these record the calls instead of the pixels.
// That is enough for the two failures that actually happened: a pass that never ran, and a pass
// that ran and rejected everything.

function stubContext() {
    const calls = [];
    const record = (name) => (...args) => calls.push({ name, args });

    return {
        calls,
        ctx: {
            canvas: { clientWidth: 1200, clientHeight: 800 },
            globalAlpha: 1, font: '', textAlign: 'left',
            fillStyle: '', strokeStyle: '', lineWidth: 1,

            // Six pixels a character is close enough to the 11px font to make the collision
            // arithmetic meaningful, and it makes an expected box width something you can work out
            // on paper when a test fails.
            measureText: (text) => ({ width: text.length * 6 }),

            fillText: record('fillText'),
            fillRect: record('fillRect'),
            strokeRect: record('strokeRect'),
            arc: record('arc'),
            beginPath: record('beginPath'),
            moveTo: record('moveTo'),
            lineTo: record('lineTo'),
            closePath: record('closePath'),
            stroke: record('stroke'),
            fill: record('fill'),
            save: record('save'),
            restore: record('restore'),
            setTransform: record('setTransform')
        }
    };
}

function stubView(scale) {
    return {
        scale,
        facet: { name: 'Trammel' },
        toScreen: (x, y) => [(x - 1475) * scale + 600, (y - 1645) * scale + 400]
    };
}

/** Well apart, so nothing in these fixtures collides and a missing label means a missing label. */
function labelFixture() {
    return [
        {
            layer: 'nav', id: 'wp:a', kind: 'point', map: 'Trammel', label: 'brit-plaza-1',
            points: [[1475, 1645, 20]], props: {}
        },
        {
            layer: 'nav-destinations', id: 'dest:bank', kind: 'point', map: 'Trammel',
            label: 'Britain Bank', points: [[1495, 1665, 0]], props: {}
        },
        {
            layer: 'nav-zones', id: 'zone:town', kind: 'rect', map: 'Trammel', label: 'brit-town',
            rect: [1420, 1600, 120, 100], props: {}
        }
    ];
}

function textsDrawn(calls) {
    return calls.filter((call) => call.name === 'fillText').map((call) => call.args[0]);
}

test('every label above its layer threshold is drawn', () => {
    // The reported bug was none at all, at any zoom. scale 4 clears every labelAt in the table.
    const { ctx, calls } = stubContext();
    const shapes_ = labelFixture();

    shapes.draw(ctx, stubView(4), shapes_, new Set(shapes.LAYER_ORDER), null, null, null);

    assert.deepStrictEqual(
        textsDrawn(calls).sort(),
        ['Britain Bank', 'brit-plaza-1', 'brit-town']);
});

test('a label below its layer threshold waits, unless it is selected or hovered', () => {
    // One shape at a time, so this measures the threshold rule and nothing else. Two shapes at
    // this zoom would also be testing collision, which is the next test's job.
    const view = stubView(0.2);
    const visible = new Set(shapes.LAYER_ORDER);
    const [waypoint, , zone] = labelFixture();

    const drawnWith = (world, selected, hovered) => {
        const { ctx, calls } = stubContext();

        shapes.draw(ctx, view, world, visible, selected, hovered, null);

        return textsDrawn(calls);
    };

    // 0.2 is under nav's floor of 0.6 and over nav-zones' of 0.1.
    assert.deepStrictEqual(drawnWith([waypoint], null, null), []);
    assert.deepStrictEqual(drawnWith([zone], null, null), ['brit-town']);

    // Selection and hover override the floor, which is what makes a zoomed-out click still tell
    // you what you picked.
    assert.deepStrictEqual(drawnWith([waypoint], waypoint, null), ['brit-plaza-1']);
    assert.deepStrictEqual(drawnWith([waypoint], null, waypoint), ['brit-plaza-1']);
});

test('a label with nowhere to go is skipped, not stacked on top of the winner', () => {
    // Zoomed out, a 120x100 zone is 24 by 20 screen pixels with a 60px label, sitting under the
    // selected waypoint's. All four candidate positions collide, and the rule is that the loser
    // draws nothing at all: half a pile is still a pile.
    const { ctx, calls } = stubContext();
    const [waypoint, , zone] = labelFixture();

    shapes.draw(ctx, stubView(0.2), [waypoint, zone], new Set(shapes.LAYER_ORDER),
        waypoint, null, null);

    assert.deepStrictEqual(textsDrawn(calls), ['brit-plaza-1']);

    // Zoom in and there is room for both, which is what makes skipping acceptable rather than a
    // permanent loss.
    const roomy = stubContext();

    shapes.draw(roomy.ctx, stubView(4), [waypoint, zone], new Set(shapes.LAYER_ORDER),
        waypoint, null, null);

    assert.deepStrictEqual(textsDrawn(roomy.calls).sort(), ['brit-plaza-1', 'brit-town']);
});

test('two labels on the same tile do not overlap, and neither is silently lost', () => {
    const { ctx, calls } = stubContext();
    const stacked = [
        {
            layer: 'nav', id: 'wp:a', kind: 'point', map: 'Trammel', label: 'aaaa',
            points: [[1475, 1645, 0]], props: {}
        },
        {
            layer: 'nav', id: 'wp:b', kind: 'point', map: 'Trammel', label: 'bbbb',
            points: [[1475, 1645, 0]], props: {}
        }
    ];

    shapes.draw(ctx, stubView(4), stacked, new Set(['nav']), null, null, null);

    const drawn = calls.filter((call) => call.name === 'fillText');

    assert.strictEqual(drawn.length, 2, 'one of two exactly-stacked labels was dropped');
    assert.notDeepStrictEqual(
        [drawn[0].args[1], drawn[0].args[2]],
        [drawn[1].args[1], drawn[1].args[2]],
        'both labels were drawn in the same place');
});

test('the live layer draws one marker per entity', () => {
    const { ctx, calls } = stubContext();
    const entities = [
        { serial: 1, kind: 'staff', name: 'Korlan', map: 'Trammel', x: 1475, y: 1645, z: 10 },
        { serial: 2, kind: 'vendor', name: 'Casta', map: 'Trammel', x: 1476, y: 1646, z: 20 },
        { serial: 3, kind: 'actor', name: 'Perrin', map: 'Trammel', x: 1477, y: 1647, z: 0 }
    ];

    shapes.drawEntities(ctx, stubView(4), entities);

    assert.strictEqual(calls.filter((call) => call.name === 'arc').length, entities.length);
});

test('an entity on another facet or off screen is culled, and an unknown kind still draws', () => {
    const { ctx, calls } = stubContext();

    shapes.drawEntities(ctx, stubView(4), [
        { serial: 1, kind: 'staff', map: 'Felucca', x: 1475, y: 1645, z: 0 },
        { serial: 2, kind: 'staff', map: 'Trammel', x: 9000, y: 9000, z: 0 },
        // A kind the editor has never heard of is still a live mobile; it draws in the fallback
        // colour rather than vanishing.
        { serial: 3, kind: 'something-new', map: 'Trammel', x: 1475, y: 1645, z: 0 }
    ]);

    assert.strictEqual(calls.filter((call) => call.name === 'arc').length, 1);
});

test('a shape with no geometry does not take the rest of the frame down with it', () => {
    // The bug both of the above were reported as. A daily-life record is kind 'form' with neither
    // points nor rect, drawShape fell through to its point branch and threw, and the draw loop died
    // mid-frame - so the label pass never ran, and the entity pass, which comes after it, never ran
    // either. One missing check, two layers invisible.
    const { ctx, calls } = stubContext();
    const view = stubView(4);
    const shapes_ = [
        ...labelFixture(),
        {
            layer: 'dailylife', id: 'dl:settings', kind: 'form', map: 'Trammel',
            label: 'Daily life settings', props: {}, fields: []
        },
        {
            layer: 'dailylife', id: 'shopkeeper:baker', kind: 'form', map: 'Trammel',
            label: 'baker (GGBaker)', props: {}, fields: []
        }
    ];

    assert.doesNotThrow(
        () => shapes.draw(ctx, view, shapes_, new Set(shapes.LAYER_ORDER), null, null, null));

    // The labelled shapes still get their labels, and the geometry-less records get none: they are
    // edited in the side panel and are not on the map at all.
    assert.deepStrictEqual(
        textsDrawn(calls).sort(),
        ['Britain Bank', 'brit-plaza-1', 'brit-town']);

    assert.strictEqual(shapes.hasGeometry(shapes_[3]), false);
    assert.strictEqual(shapes.hasGeometry(shapes_[0]), true);

    // And a click cannot reach one either - hitTest walked the same undefined points array.
    assert.doesNotThrow(
        () => shapes.hitTest(view, shapes_, new Set(shapes.LAYER_ORDER), null, 1475.5, 1645.5));
});

test('the real projected shapes all draw without throwing', () => {
    // The fixtures above are hand-written; this is the actual file, which is where the geometry-less
    // records came from in the first place.
    const { ctx } = stubContext();
    const world = realShapes();

    assert.ok(world.some((shape) => !shapes.hasGeometry(shape)),
        'the real data no longer contains a geometry-less record - this test has stopped testing');

    assert.doesNotThrow(
        () => shapes.draw(ctx, stubView(4), world, new Set(shapes.LAYER_ORDER), null, null, null));
});

// --- the live panel -------------------------------------------------------------------------------

test('the live line carries the snapshot age, and says when it has gone stale', async () => {
    const { liveStatusText, snapshotAge, STALE_AFTER_SECONDS } = await import('./js/live.js');
    const now = Date.parse('2026-09-05T22:00:30.000Z');
    const at = (seconds) => new Date(now - seconds * 1000).toISOString();

    assert.deepStrictEqual(
        liveStatusText({ running: true, count: 10, sequence: 147, utc: at(2) }, 0, now),
        { text: 'live: 10 @ seq 147, 2s ago', stale: false });

    assert.strictEqual(
        liveStatusText({ running: true, count: 10, sequence: 147, utc: at(90) }, 0, now).text,
        'live: 10 @ seq 147, 1m ago');

    assert.strictEqual(
        liveStatusText({ running: true, count: 10, sequence: 147, utc: at(7200) }, 0, now).text,
        'live: 10 @ seq 147, 2h ago');

    // Off is off; there is no age to show and no reason to colour it.
    assert.deepStrictEqual(liveStatusText(null, 0, now), { text: 'live: off', stale: false });
    assert.deepStrictEqual(liveStatusText({ running: false }, 0, now),
        { text: 'live: off', stale: false });

    // A snapshot with no timestamp still shows its count and sequence rather than nothing.
    assert.strictEqual(
        liveStatusText({ running: true, count: 3, sequence: 9, utc: null }, 0, now).text,
        'live: 3 @ seq 9');

    // Stale on either side of the boundary, so the threshold is the documented one.
    assert.strictEqual(liveStatusText({ running: true, sequence: 1, utc: at(STALE_AFTER_SECONDS) }, 0, now).stale, false);
    assert.strictEqual(liveStatusText({ running: true, sequence: 1, utc: at(STALE_AFTER_SECONDS + 1) }, 0, now).stale, true);

    // A shard clock a second ahead of this one reads "0s ago", not "-1s ago" - which would look
    // like a bug here rather than a difference between two machines.
    assert.strictEqual(snapshotAge(at(-1), now).text, '0s ago');
    assert.strictEqual(snapshotAge('not a date', now), null);
});

test('at the default Britain view the labels are landmarks, not a fifth of everything', () => {
    // The thresholds are measured, not guessed. Before them, 206 labels were eligible at this zoom
    // and 39 fitted - collision culled 81% in a priority order no viewer can perceive, which is
    // what "the map is anonymous" actually described. Lowering thresholds makes that worse by
    // adding competitors; the fix is to let fewer things compete at each zoom.
    const world = realShapes();
    const visible = new Set(shapes.LAYER_ORDER);
    const view = {
        scale: 2, facet: { name: 'Trammel' },
        toScreen: (x, y) => [(x - 1475) * 2 + 800, (y - 1645) * 2 + 450]
    };

    const eligible = (scale) => world.filter((shape) => {
        const layer = shapes.LAYERS[shape.layer];
        return shape.label && layer && scale >= (layer.labelAt === undefined ? 0.35 : layer.labelAt);
    });

    const atTown = eligible(2);
    const layers = new Set(atTown.map((shape) => shape.layer));

    // Only the frame and the landmarks compete at town scale.
    assert.deepStrictEqual([...layers].sort(), ['nav-destinations', 'nav-zones']);

    // Waypoint ids arrive when you are close enough to be working on the graph, arrivals later
    // still - they cluster four and five deep around one destination.
    assert.ok(eligible(3).some((s) => s.layer === 'nav'), 'waypoints never become eligible');
    assert.ok(!eligible(3).some((s) => s.layer === 'nav-arrivals'), 'arrivals arrive too early');
    assert.ok(eligible(6).some((s) => s.layer === 'nav-arrivals'), 'arrivals never arrive');

    // And most of what is eligible actually gets drawn, which is the point of the whole change.
    let drawn = 0;
    const ctx = {
        canvas: { clientWidth: 1600, clientHeight: 900 }, globalAlpha: 1, font: '', textAlign: 'left',
        fillStyle: '', strokeStyle: '', lineWidth: 1,
        measureText: (t) => ({ width: t.length * 6 }),
        fillText: () => drawn++, fillRect: () => {}, strokeRect: () => {}, arc: () => {},
        beginPath: () => {}, moveTo: () => {}, lineTo: () => {}, closePath: () => {},
        stroke: () => {}, fill: () => {}
    };

    shapes.draw(ctx, view, world, visible, null, null, null);

    assert.ok(drawn / atTown.length > 0.6,
        `only ${drawn} of ${atTown.length} eligible labels were drawn`);
});

test('the shard-written snapshots are readable and not writable', () => {
    // entities, health, spawners and nav-audit are all written BY the shard. An editor that could
    // overwrite one could lie to itself about the live world.
    for (const name of ['entities', 'health', 'spawnerState', 'navAudit']) {
        assert.ok(FILES[name], `${name} is not a known file`);
        assert.strictEqual(whitelist.resolveSave(name), null, `${name} should not be writable`);
    }
});

// --- the seam between the tools and the writer --------------------------------------------------

test('every tool produces a shape unproject can write, with the right fields in the right order', async () => {
    // This is the seam that would fail silently: a shape built in the browser goes straight to
    // unproject, and if the two disagree about a field name the record is written missing it.
    const { buildShape } = await import('./js/build.js');
    const { unproject } = require('./project.js');

    const nav = fs.readFileSync(FILES.navigation, 'utf8');
    const zones = '{\n  "zones": []\n}\n';

    const cases = [
        ['waypoint', 'navigation', nav,
            { id: 'test-wp', tags: 'town road', arrivalRange: '3' },
            { points: [[10, 20, 0]] }, {},
            /\{"id":"test-wp","map":"Trammel","x":10,"y":20,"z":0,"arrivalRange":3,"tags":"town road"\}/],

        ['destination', 'navigation', nav,
            { id: 'test-dest', name: 'Test', type: 'shop', tags: 'service', waypoints: 'brit-plaza-1' },
            { points: [[11, 21, 0]] }, {},
            /\{"id":"test-dest","name":"Test","type":"shop","map":"Trammel","x":11,"y":21,"z":0,"tags":"service","waypoints":"brit-plaza-1"\}/],

        ['arrival', 'navigation', nav,
            { exclusive: true, waypoints: 'brit-plaza-1' },
            { points: [[12, 22, 0]] }, { ownerId: 'brit-bank', arrivalIndex: 4 },
            /\{"destination":"brit-bank","x":12,"y":22,"z":0,"exclusive":true,"waypoints":"brit-plaza-1"\}/],

        ['edge', 'navigation', nav,
            {},
            { points: [[0, 0, 0], [1, 1, 0]] }, { ids: ['brit-plaza-1', 'brit-plaza-3'] },
            /\{"from":"brit-plaza-1","to":"brit-plaza-3","kind":"walk","tags":""\}/],

        ['route', 'navigation', nav,
            { id: 'test-route', mode: 'oneway' },
            { points: [[0, 0, 0], [1, 1, 0]] }, { ids: ['brit-plaza-1', 'brit-plaza-2'] },
            /\{"id":"test-route","map":"Trammel","mode":"oneway","waypoints":"brit-plaza-1 brit-plaza-2"\}/],

        ['navzone', 'navigation', nav,
            { id: 'test-zone', tags: 'town' },
            { rect: [1, 2, 3, 4] }, {},
            /\{"id":"test-zone","map":"Trammel","x":1,"y":2,"width":3,"height":4,"tags":"town"\}/],

        ['restricted', 'restrictedZones', zones,
            { name: 'test-restricted' },
            { rect: [5, 6, 7, 8] }, {},
            /\{"name":"test-restricted","map":"Trammel","x":5,"y":6,"width":7,"height":8\}/]
    ];

    for (const [key, file, text, props, draft, context, expected] of cases) {
        const shape = buildShape(key, props, 'Trammel', draft, context);

        assert.ok(shape, `${key} built nothing`);

        const written = unproject(file, text, { creates: [shape] });

        assert.match(written, expected, `${key} did not write the record it should have`);

        // And it survives a round trip back into a shape, so the editor can carry on editing it.
        assert.doesNotThrow(() => unproject(file, written, { updates: [shape] }), `${key} update`);
    }
});

// --- the wiring -----------------------------------------------------------------------------------

test('every element the editor looks up exists in index.html', () => {
    // The closest thing to a smoke test without a browser. A missing id is not a subtle bug: it is
    // a TypeError on the first click, or a listener that silently never fires - and index.html and
    // the JS are edited in different sittings.
    const html = fs.readFileSync(path.join(__dirname, 'index.html'), 'utf8');
    const present = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((match) => match[1]));

    for (const file of ['app.js', 'tools.js']) {
        const source = fs.readFileSync(path.join(__dirname, 'js', file), 'utf8');
        const wanted = [
            ...source.matchAll(/getElementById\('([^']+)'\)/g),
            ...source.matchAll(/\$\('([^']+)'\)/g),
            ...source.matchAll(/setAttribute\('list', '([^']+)'\)/g),
            ...source.matchAll(/fill\('([a-z-]+-list)'/g)
        ].map((match) => match[1]);

        for (const id of wanted) {
            assert.ok(present.has(id), `${file} looks up #${id}, which index.html does not define`);
        }
    }
});

test('every datalist a tool field names is declared', () => {
    const html = fs.readFileSync(path.join(__dirname, 'index.html'), 'utf8');
    const present = new Set([...html.matchAll(/<datalist id="([^"]+)"/g)].map((match) => match[1]));

    for (const [key, tool] of Object.entries(tools.TOOLS)) {
        for (const field of tool.fields) {
            if (field.list) {
                assert.ok(present.has(field.list),
                    `${key}.${field.key} uses the datalist '${field.list}', which does not exist`);
            }
        }
    }
});

test('the modules the page loads all resolve', async () => {
    // index.html loads app.js only; everything else arrives through its import graph. app.js itself
    // cannot be imported here - it reaches for the canvas at module scope, being the entry point -
    // so this walks the graph by reading the import lines instead.
    const seen = new Set();
    const queue = ['app.js'];

    while (queue.length > 0) {
        const file = queue.pop();

        if (seen.has(file)) {
            continue;
        }

        seen.add(file);

        const source = fs.readFileSync(path.join(__dirname, 'js', file), 'utf8');

        for (const match of source.matchAll(/from '\.\/([^']+)'/g)) {
            assert.ok(fs.existsSync(path.join(__dirname, 'js', match[1])),
                `${file} imports ./${match[1]}, which does not exist`);

            queue.push(match[1]);
        }
    }

    assert.ok(seen.has('validate.js') && seen.has('tools.js') && seen.has('ids.js'),
        'the import graph does not reach the new modules');
});

test('the smallest rectangle wins, so a district cannot swallow the town inside it', () => {
    const district = {
        layer: 'nav-zones', id: 'zone:district', kind: 'rect', map: 'Trammel',
        rect: [0, 0, 210, 170], props: {}
    };
    const yard = {
        layer: 'nav-zones', id: 'zone:yard', kind: 'rect', map: 'Trammel',
        rect: [10, 10, 20, 20], props: {}
    };

    const hit = shapes.hitTest(view, [yard, district], new Set(['nav-zones']), null, 15, 15);

    assert.strictEqual(hit.shape, yard);
});
