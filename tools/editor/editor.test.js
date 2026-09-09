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

test('a point inserted into a road is named after the road, not the zone it landed in', () => {
    // The real one: inserting between town-9 and town-10 while standing in the bank quarter
    // produced 'bank-2', which reads as part of a different road and sorts nowhere near the one
    // it belongs to. The number in a road id is a position, so 10a is the answer whatever the
    // point happens to be standing inside.
    const world = [
        zone('bankquarter', 0, 0, 40, 40, 'bank town'),
        pointShape('wp:town-9', 5, 5),
        pointShape('wp:town-10', 10, 10),
        pointShape('wp:town-11', 20, 20)
    ];

    assert.strictEqual(ids.insertedId('town-10', 'town-11', 'Trammel', 7, 7, world), 'town-10a');

    // A second insert on the same hop keeps counting, and never produces 'town-10aa'.
    const withA = [...world, pointShape('wp:town-10a', 12, 12)];

    assert.strictEqual(ids.insertedId('town-10', 'town-11', 'Trammel', 7, 7, withA), 'town-10b');
    assert.strictEqual(ids.insertedId('town-10a', 'town-11', 'Trammel', 7, 7, withA), 'town-10b');
});

test('an inserted point falls back to the zone scheme when neither neighbour is numbered', () => {
    // 'brit-gate-w' has no position to be after, and 'brit-gate-wa' would read worse than the
    // zone's own name.
    const world = [zone('bankquarter', 0, 0, 40, 40, 'bank town'), pointShape('wp:brit-gate-w', 5, 5)];

    assert.strictEqual(ids.insertedId('brit-gate-w', 'brit-gate-e', 'Trammel', 7, 7, world), 'bank-1');
});

test('an inserted point carries its neighbour display name, or none at all', () => {
    assert.strictEqual(ids.insertedName('Bank road 10', '', 'town-10a'), 'Bank road 10a');
    assert.strictEqual(ids.insertedName('', 'Bank road 11', 'town-10b'), 'Bank road 11b');

    // The common case: unnamed neighbours leave the field unset rather than inventing one.
    assert.strictEqual(ids.insertedName('', '', 'town-10a'), '');
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
    // 'site' is the three-phase one: a point, then a rect, then N points finished with Enter.
    // Adding a kind is deliberately a change in three places - tools.js, app.js and build.js -
    // and this set is the fourth, so a kind the state machine cannot drive fails here first.
    const kinds = new Set([
        'point', 'rect', 'form', 'pair', 'chain', 'owner-then-point', 'site', 'point-chain',
        // Drags a box and creates nothing: the rect is a question for the shard, and the
        // answer is a proposal to accept or discard.
        'adopt-rect']);

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

test('a waypoint wins its own click in the order the editor actually builds', () => {
    // The test above puts the edge FIRST in the array, and that is what let this through: the
    // old hit test returned the first node it touched walking the array in reverse, so with
    // [edge, a, b] the waypoints were reached first and it looked correct. project() emits
    // waypoints before edges, which is the opposite order - and with nav-edges on, every click
    // on a waypoint selected the edge under it and no waypoint could be dragged at all.
    const a = pointShape('wp:a', 0, 0);
    const b = pointShape('wp:b', 40, 0);
    const edge = {
        layer: 'nav-edges', id: 'edge:a>b', kind: 'polyline', map: 'Trammel', label: 'a - b',
        points: [[0, 0, 0], [40, 0, 0]], props: { from: 'a', to: 'b' }
    };

    const visible = new Set(['nav', 'nav-edges']);
    const world = [a, b, edge];

    const onA = shapes.hitTest(view, world, visible, null, 0.5, 0.5);
    assert.strictEqual(onA.shape, a, 'the waypoint, not the edge that ends on it');
    assert.strictEqual(onA.mode, 'move', 'and it must be draggable, not a node grab');

    // The edge is still selectable between its ends, which is the only way to delete one.
    assert.strictEqual(shapes.hitTest(view, world, visible, null, 20.5, 0.5).shape, edge);
});

test('a line beats an area it crosses, whatever the area is', () => {
    // Tiers, not sizes: area only breaks ties between areas. An edge drawn across a nav zone has
    // to stay clickable or there is no way to delete it, however small the zone is.
    const zone = {
        layer: 'nav-zones', id: 'zone:yard', kind: 'rect', map: 'Trammel',
        rect: [0, 0, 40, 40], props: {}
    };
    const edge = {
        layer: 'nav-edges', id: 'edge:a>b', kind: 'polyline', map: 'Trammel',
        points: [[0, 20, 0], [40, 20, 0]], props: {}
    };

    const visible = new Set(['nav-zones', 'nav-edges']);
    const hit = shapes.hitTest(view, [zone, edge], visible, null, 20.5, 20.5);

    assert.strictEqual(hit.shape, edge);
    assert.strictEqual(hit.mode, 'body');

    // Off the line but still inside the zone, the zone.
    assert.strictEqual(shapes.hitTest(view, [zone, edge], visible, null, 20.5, 35).shape, zone);
});

test('a polygon zone can be picked by its inside, not only by a vertex', () => {
    // A poly carries both a bounding rect and its vertices. The old single loop sent every
    // non-rect kind down the node path and then `continue`d, so the insidePolygon test below it
    // was unreachable: a polygon zone could be grabbed by a corner but never by its body.
    const poly = {
        layer: 'nav-zones', id: 'zone:wedge', kind: 'poly', map: 'Trammel',
        rect: [0, 0, 40, 40], points: [[0, 0, 0], [40, 0, 0], [40, 40, 0]], props: {}
    };

    const visible = new Set(['nav-zones']);

    const inside = shapes.hitTest(view, [poly], visible, null, 30, 20);
    assert.strictEqual(inside.shape, poly);
    assert.strictEqual(inside.mode, 'move');

    // A vertex is still a vertex grab.
    assert.strictEqual(shapes.hitTest(view, [poly], visible, null, 0.5, 0.5).mode, 'node');

    // Inside the bounding box but outside the polygon is nothing.
    assert.strictEqual(shapes.hitTest(view, [poly], visible, null, 5, 35), null);
});

test('a drafted rect keeps being drawn after the tool moves on to collecting points', () => {
    // The Site tool drags a zone, then flips the draft to 'points' to collect arrival tiles - and
    // the zone the author had just drawn vanished at the exact moment they were asked to place
    // tiles inside it. `kind` says what the NEXT click collects; `rect` says what has already
    // been drawn. Drawing off `kind` conflated the two.
    const { ctx, calls } = stubContext();
    const view = stubView(1);

    shapes.drawDraft(ctx, view, { kind: 'points', points: [[1475, 1645, 0]], rect: [1470, 1640, 10, 12] });

    const rects = calls.filter((call) => call.name === 'strokeRect');
    assert.strictEqual(rects.length, 1, 'the zone stopped being drawn once the tool moved on');
    assert.deepStrictEqual(rects[0].args, [595, 395, 10, 12]);
});

test('a rect-drag tool draws a rubber band before it has any points', () => {
    // navzone and restricted start with points: [] and dragged out with no rubber band at all,
    // because the draw bailed on an empty points array before it ever looked at the rect.
    const { ctx, calls } = stubContext();

    shapes.drawDraft(ctx, stubView(1), { kind: 'rect', points: [], rect: [1470, 1640, 4, 6] });

    assert.strictEqual(calls.filter((call) => call.name === 'strokeRect').length, 1);
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
            setLineDash: record('setLineDash'),
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
        stroke: () => {}, fill: () => {},
        // draw() brackets every shape in save/restore so the reference layer's line dash cannot
        // leak onto the next shape.
        save: () => {}, restore: () => {}, setLineDash: () => {}
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

// --- the Z that used to be thrown away ------------------------------------------------------------

test('a created record carries the Z it was placed at, not zero', async () => {
    // EVERY record the editor has ever created was written `"z": 0`, because buildShape flattened
    // it - survivable only because navigation.json's Z is advisory and the shard falls back to
    // map.GetAverageZ when the authored one will not fit. The art view now knows the real Z from
    // the renderer's pick map, and the radar view asks for it, so throwing it away is no longer
    // merely wasteful: it would make a placement on the art disagree with what the client shows
    // standing there, which is the acceptance test for the whole feature.
    const { buildShape } = await import('./js/build.js');

    const cases = [
        ['waypoint', { id: 'wp', tags: '', arrivalRange: '0' }, { points: [[1424, 1557, 30]] }, {}],
        ['destination', { id: 'd', name: 'D', type: 'shop' }, { points: [[1424, 1557, 30]] }, {}],
        ['arrival', { exclusive: false }, { points: [[1424, 1557, 30]] },
            { ownerId: 'brit-forge', arrivalIndex: 0 }],
        ['spawner', { id: 'sp', name: 'S', file: 'trammel/GG_Test.xml' },
            { points: [[1424, 1557, 30]] }, {}],
        ['site', { id: 's', name: 'S', type: 'mine' },
            { points: [[1424, 1557, 30]], rect: [1420, 1550, 8, 8] }, { arrivals: [] }]
    ];

    for (const [key, props, draft, context] of cases) {
        const built = buildShape(key, props, 'Trammel', draft, context);
        const shape = Array.isArray(built) ? built[0] : built;

        assert.strictEqual(shape.points[0][2], 30, `${key} flattened the Z`);
    }
});

test("a site's arrivals carry the Z the SHARD measured them at", async () => {
    // Not the pick map's. The reach probe measured canFit at that Z - a bot actually standing
    // there - which is a stronger statement than "this is the surface the renderer drew".
    const { buildShape } = await import('./js/build.js');

    const built = buildShape(
        'site',
        { id: 'face', name: 'Face', type: 'mine' },
        'Trammel',
        { points: [[1451, 1517, 43]], rect: [1445, 1512, 12, 12] },
        { arrivals: [[1451, 1518, 43], [1452, 1519, 45]] });

    const arrivals = built.filter((shape) => shape.layer === 'nav-arrivals');

    assert.strictEqual(arrivals.length, 2);
    assert.strictEqual(arrivals[0].points[0][2], 43);
    assert.strictEqual(arrivals[1].points[0][2], 45);
});

test('a point placed with no Z at all is still written at zero, not undefined', async () => {
    // The radar view with no renderer to ask, and the older drafts in this very file. A missing Z
    // has to be 0 rather than undefined: unproject writes the number it is given, and `"z": null`
    // is not something JsonConfig will read back.
    const { buildShape } = await import('./js/build.js');

    const built = buildShape(
        'waypoint', { id: 'wp', tags: '', arrivalRange: '0' }, 'Trammel', { points: [[10, 20]] }, {});

    assert.strictEqual(built.points[0][2], 0);
});

test('a spawner shape names its file the way fileOf reads it back', async () => {
    // The round trip that was broken: buildShape writes `spawner:<relative>#<guid>` and app.js's
    // fileOf reconstructs the save key by slicing that and prefixing `spawn:`. Handing the field
    // the full key instead gave `spawner:spawn:trammel/...`, fileOf gave `spawn:spawn:trammel/...`,
    // and the save matched no writable file - reporting success having written nothing.
    const { buildShape } = await import('./js/build.js');

    // fileOf, transcribed. Not imported: it lives in app.js, which needs a DOM to load at all.
    const fileOf = (shape) => `spawn:${shape.id.slice('spawner:'.length, shape.id.lastIndexOf('#'))}`;

    for (const typed of ['trammel/GG_Test.xml', 'spawn:trammel/GG_Test.xml']) {
        const built = buildShape(
            'spawner',
            { Name: 'Test', file: typed, Objects2: 'Rabbit', MaxCount: '1' },
            'Trammel', { points: [[10, 20, 0]] }, { uniqueId: 'guid' });

        assert.strictEqual(built.id, 'spawner:trammel/GG_Test.xml#guid',
            `a file typed as '${typed}' must give one id, not two`);
        assert.strictEqual(fileOf(built), 'spawn:trammel/GG_Test.xml');
        assert.ok(whitelist.resolveSpawnFile(fileOf(built)),
            'the key fileOf produces must resolve to a writable file');
    }
});

test('the vocabulary offers spawn files in the form the spawner shape wants', async () => {
    // The other half of the same bug: whitelist.listSpawnFiles returns `spawn:` keys and the
    // field means a relative path, so the vocabulary trims them rather than the form having to.
    const vocabulary = require('./vocabulary.js');
    const readJson = (file) => {
        try {
            return JSON.parse(fs.readFileSync(file, 'utf8'));
        } catch {
            return null;
        }
    };

    for (const file of vocabulary.build(readJson).spawnFiles) {
        assert.ok(!file.startsWith('spawn:'), `${file} must be relative, not a save key`);
        assert.ok(whitelist.resolveSpawnFile(`spawn:${file}`), `spawn:${file} resolves`);
    }
});

test('every file an edit can belong to is a file save() will actually send', () => {
    // filesWithEdits returned a fixed three-name list, and a spawn key is
    // `spawn:<facet>/GG_Thing.xml` - dynamic by construction - so no spawner edit ever survived
    // it. save() loops over what it returns, so an empty list ran zero iterations and fell
    // straight through to "Saved and reloaded". Both the spawner tool and the panel's spawner
    // fields were silently no-ops for as long as they have existed.
    const app = fs.readFileSync(path.join(__dirname, 'js', 'app.js'), 'utf8');
    const body = app.slice(app.indexOf('function filesWithEdits'));

    assert.ok(/startsWith\('spawn:'\)/.test(body.slice(0, 1200)),
        'filesWithEdits must pass spawn keys through, not only the three fixed names');

    // And the guard that would have made either bug visible on the first attempt: edits that
    // resolve to no file at all must be reported, never reported as a successful save.
    assert.ok(/files\.length === 0/.test(app),
        'save() must refuse when there are edits but no file to write them to');
});

test('the layer-count sweep is scoped, because the Bots panel uses the same class', () => {
    // `updateCounts` swept document.querySelectorAll('.count'), and the Bots panel's trailing slot
    // is a span of class `count` too. Those carry no data-layer, so they fell through to the last
    // branch and were set to the number of shapes in layer `undefined` - zero. Every bot row read
    // "0" where its behaviour or its stuck rung should be, for as long as the panel has existed,
    // which means the one thing the README says that slot is for - "a wedged bot is the thing
    // somebody opened this panel to find" - had never once worked.
    //
    // A source assertion rather than a DOM one, because the fault is the SELECTOR: a rendered
    // panel can be made to look right by ordering the two updates, and the next thing added to
    // the sidebar with a count would break it again.
    const app = fs.readFileSync(path.join(__dirname, 'js', 'app.js'), 'utf8');

    assert.ok(!/document\.querySelectorAll\(\s*'\.count'\s*\)/.test(app),
        'the .count sweep must be scoped to the layer list, not the document');
    assert.ok(/dom\.layers\.querySelectorAll\(\s*'\.count'\s*\)/.test(app),
        'the .count sweep is scoped to #layers');
});

test('an arrival written with a range keeps it, and one at zero does not write the key', async () => {
    // The form has asked for a range since the arrival tool existed, and buildShape dropped it -
    // so every arrival the editor created stood on its exact tile whatever was typed. The zero
    // case matters just as much: `range` is Ignore-on-default in the C# record, so writing 0
    // explicitly would put a key on 115 shipped arrivals that have never had one.
    const { buildShape } = await import('./js/build.js');

    const withRange = buildShape(
        'arrival', { range: '2', waypoints: '' }, 'Trammel',
        { points: [[12, 22, 0]] }, { ownerId: 'brit-forge', arrivalIndex: 0 });

    assert.strictEqual(withRange.props.range, 2);

    const onTheTile = buildShape(
        'arrival', { range: '0', waypoints: '' }, 'Trammel',
        { points: [[12, 22, 0]] }, { ownerId: 'brit-bank', arrivalIndex: 0 });

    assert.ok(!('range' in onTheTile.props), 'range 0 is absent, not written');
});

test('a corridor edge carries the corridor tags, and a hand-drawn one carries none', async () => {
    const { buildShape } = await import('./js/build.js');

    const fromCorridor = buildShape(
        'edge', { tags: 'road wilderness' }, 'Trammel',
        { points: [[0, 0, 0], [1, 1, 0]] }, { ids: ['a', 'b'] });

    assert.strictEqual(fromCorridor.props.tags, 'road wilderness');

    const byHand = buildShape(
        'edge', {}, 'Trammel', { points: [[0, 0, 0], [1, 1, 0]] }, { ids: ['a', 'b'] });

    assert.strictEqual(byHand.props.tags, '', 'unchanged from what a hand-drawn edge always had');
});

test('GG_ is applied to a spawner name rather than typed, and never doubled', async () => {
    // The prefix is what [XmlLoad and [XmlUnLoad filter on with an ordinal StartsWith, so a
    // spawner without it is invisible to [GG_Reimport for ever - never swept, never replaced.
    const { buildShape, ggName } = await import('./js/build.js');

    assert.strictEqual(ggName('Bakers'), 'GG_Bakers');
    assert.strictEqual(ggName('GG_Bakers'), 'GG_Bakers', 'typed out of habit, not doubled');
    assert.strictEqual(ggName('  Bakers  '), 'GG_Bakers');

    // Ordinal, like the filter itself: gg_ is not the prefix as far as [XmlLoad is concerned.
    assert.strictEqual(ggName('gg_Bakers'), 'GG_gg_Bakers');

    const built = buildShape(
        'spawner',
        { Name: 'Bakers', file: 'spawn:trammel/GG_Test.xml', Objects2: 'Baker', MaxCount: '1' },
        'Trammel', { points: [[10, 20, 0]] }, { uniqueId: 'x' });

    assert.strictEqual(built.props.Name, 'GG_Bakers');
    assert.strictEqual(built.label, 'GG_Bakers', 'the label is the name that will be written');
});

test('a bot kind writes a spawn string, not a type name', async () => {
    // `kind` is a UI filter for every other kind and is thrown away. For the two bot kinds it is
    // the field that decides what gets written, because there is no PlayerBotFixed class to spawn:
    // a bot spawner spawns a PlayerBot and then TELLS it what to be, through property setters
    // XmlSpawner applies AFTER placement. Upstream reads its spawner's type inside OnAfterSpawn,
    // and that hook runs before the spawn string here (XmlSpawner2.cs:9337 then :9344).
    const { buildShape, botType } = await import('./js/build.js');

    assert.strictEqual(botType('PlayerBotFixed', 'BankSitter'), 'PlayerBot/Role/Fixed/Seed/BankSitter');
    assert.strictEqual(botType('PlayerBotLifecycle', 'Traveler'), 'PlayerBot/Role/Lifecycle/Seed/Traveler');

    // Everything else is untouched - a creature kind spawns the type it names.
    assert.strictEqual(botType('Monster', 'Balron'), 'Balron');
    assert.strictEqual(botType(undefined, 'Baker'), 'Baker');

    const built = buildShape(
        'spawner',
        {
            kind: 'PlayerBotFixed', Name: 'BankCrowd',
            file: 'spawn:trammel/GG_Test.xml', Objects2: 'BankSitter', MaxCount: '3'
        },
        'Trammel', { points: [[10, 20, 0]] }, { uniqueId: 'x' });

    assert.strictEqual(built.entries[0].type, 'PlayerBot/Role/Fixed/Seed/BankSitter');
    assert.strictEqual(built.entries[0].max, '3');

    // NO COLON, anywhere. XmlSpawner splits an <Objects2> entry on ":MX=" and its ten siblings and
    // discards an entry containing one without a word, which is why upstream's own "Crafter:Smith"
    // convention could not be carried across.
    assert.ok(!built.entries[0].type.includes(':'), 'a colon would make the entry vanish silently');
});

test('every tool declares its steps, and the site tool still has three', async () => {
    // The step counter used to be `tool.kind === 'site' ? 3 : 0` in app.js, so every other tool
    // ran with no step guidance at all. It now comes off the tool, which means a tool that grows
    // a phase and forgets to say so is the failure to catch.
    const { TOOLS } = tools;

    for (const [key, tool] of Object.entries(TOOLS)) {
        // Adopt is the exception and stays one: it collects a box and asks the shard a question,
        // and its own hint says the whole of it.
        if (key === 'adopt') {
            continue;
        }

        assert.ok(Array.isArray(tool.steps) && tool.steps.length > 0, `${key} declares its steps`);
    }

    assert.strictEqual(TOOLS.site.steps.length, 3);
    assert.strictEqual(TOOLS.corridor.steps.length, 2);
});

test('no tool field carries its own list of shard values', async () => {
    // The fault this whole arrangement removes: `value: 'shop'` for a destination type, and
    // 'mine or lumber' in a label, are copies of the shard's vocabulary that nothing updates.
    // A field with a finite set names a vocabulary key instead.
    const { TOOLS } = tools;

    const mustBeFed = [
        ['destination', 'type'], ['destination', 'tags'],
        ['site', 'type'], ['site', 'tags'],
        ['waypoint', 'tags'], ['navzone', 'tags'], ['corridor', 'tags'],
        ['route', 'mode'],
        ['spawner', 'kind'], ['spawner', 'Objects2'], ['spawner', 'file']
    ];

    for (const [toolKey, fieldKey] of mustBeFed) {
        const field = TOOLS[toolKey].fields.find((f) => f.key === fieldKey);

        assert.ok(field, `${toolKey}.${fieldKey} exists`);
        assert.ok(field.optionsFrom, `${toolKey}.${fieldKey} is fed from the vocabulary`);
        assert.ok(!field.options, `${toolKey}.${fieldKey} carries no literal list`);
    }

    // The spawner's type list is filtered by the kind above it, the way uo-offline's is
    // (map.html:1102-1107). Without the dependency it is 1,400 names in one combo.
    const type = TOOLS.spawner.fields.find((f) => f.key === 'Objects2');

    assert.strictEqual(type.dependsOn, 'kind');
    assert.strictEqual(type.combo, true, 'typeable: even one kind is hundreds of entries');
});

test('a field is a select only where the shard has a closed set', async () => {
    const { TOOLS } = tools;

    // NavRecords.cs makes a destination type a free token deliberately, so a control stricter
    // than the file format would refuse a type the shard would have accepted. uo-offline reached
    // the same answer: map.html:126 is an <input list>, not a <select>.
    const open = [
        ['destination', 'type'], ['site', 'type'], ['waypoint', 'tags'],
        ['destination', 'tags'], ['navzone', 'tags'], ['corridor', 'tags']
    ];

    for (const [toolKey, fieldKey] of open) {
        const field = TOOLS[toolKey].fields.find((f) => f.key === fieldKey);

        assert.strictEqual(field.combo, true, `${toolKey}.${fieldKey} stays typeable`);
    }

    // Closed: a fourth route mode is a file the shard refuses to load.
    const mode = TOOLS.route.fields.find((f) => f.key === 'mode');

    assert.ok(!mode.combo, 'route mode is a select');
});

test('the corridor asks its form before the first click, and the site after it', async () => {
    // The corridor names every waypoint it mints, so the name has to exist before there is
    // anything to name. The site's id is generated from the zone its centre tile lands in, so its
    // form cannot open until that tile is known.
    const { TOOLS } = tools;

    assert.strictEqual(TOOLS.corridor.formAtStart, true);
    assert.ok(!TOOLS.site.formAtStart);

    const name = TOOLS.corridor.fields.find((f) => f.key === 'name');

    assert.ok(name && name.required, 'the corridor name is required');
});

// --- the seam between the tools and the writer --------------------------------------------------

test('every tool produces a shape unproject can write, with the right fields in the right order', async () => {
    // This is the seam that would fail silently: a shape built in the browser goes straight to
    // unproject, and if the two disagree about a field name the record is written missing it.
    const { buildShape } = await import('./js/build.js');
    const { LAYERS } = shapes;
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
            /\{"name":"test-restricted","map":"Trammel","x":5,"y":6,"width":7,"height":8\}/],

        // The Site tool writes a destination, its zone and its arrivals in one create. Only the
        // destination is matched here; the assertions below check the other two landed, because a
        // site that writes two of its three records is the failure this tool exists to prevent.
        ['site', 'navigation', nav,
            { id: 'test-site', name: 'Test Face', type: 'mine', tags: 'wilderness', waypoints: 'brit-plaza-1' },
            { points: [[30, 40, 0]], rect: [28, 38, 6, 8] }, { arrivals: [[30, 41], [31, 42]] },
            /\{"id":"test-site","name":"Test Face","type":"mine","map":"Trammel","x":30,"y":40,"z":0,"tags":"wilderness","waypoints":"brit-plaza-1"\}/]
    ];

    for (const [key, file, text, props, draft, context, expected] of cases) {
        const built = buildShape(key, props, 'Trammel', draft, context);

        assert.ok(built, `${key} built nothing`);

        // The Site tool builds three records at once, so a case may be a list. Everything else
        // builds one, and is treated as a list of one rather than given a second code path.
        const shapes = Array.isArray(built) ? built : [built];
        const shape = shapes[0];

        // Every record has to name a real layer, and every record of one build has to belong to
        // one file. This is not housekeeping: `fileOf` reads LAYERS[shape.layer], returns null
        // when it misses, and `filesWithEdits` drops a shape whose file is null - so a record
        // with no layer is silently never saved and Save still reports success. The Site tool
        // shipped that way. It builds for three different layers, `common` carried the single
        // LAYER_FOR[key] lookup, there is no 'site' key in that table, and so all four of its
        // records were born with layer undefined. A whole authored work site was lost to it.
        // The unproject assertions below cannot see this, because unproject dispatches on the id
        // prefix and never looks at the layer.
        const files = new Set();

        for (const one of shapes) {
            assert.ok(one.layer, `${key}: ${one.id} was built with no layer`);
            assert.ok(LAYERS[one.layer], `${key}: ${one.id} names layer '${one.layer}', which is not in LAYERS`);
            files.add(LAYERS[one.layer].file);
        }

        assert.strictEqual(files.size, 1,
            `${key} built records for more than one file (${[...files].join(', ')}); a save is scoped to one`);

        const written = unproject(file, text, { creates: shapes });

        assert.match(written, expected, `${key} did not write the record it should have`);

        if (key === 'site') {
            assert.match(written,
                /\{"id":"test-site-face","map":"Trammel","x":28,"y":38,"width":6,"height":8,"tags":"mine wilderness work"\}/,
                'the site did not write its zone');
            assert.match(written,
                /\{"destination":"test-site","x":30,"y":41,"z":0,"exclusive":false,"waypoints":"brit-plaza-1"\}/,
                'the site did not write its arrivals');
        }

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

// --- the art projection ---------------------------------------------------------------------------
//
// Two things change when the base map is isometric, and both are things the radar code took for
// granted: a world rect is no longer a screen rect, and the cursor's world position is no longer
// knowable. The stub below is the smallest view that behaves like the art one - `isArt` true and a
// toScreen that honours Z - which is exactly the interface the drawing and picking code is
// written against.

function stubArtView(scale = 22) {
    const s = scale / 22;

    return {
        scale,
        isArt: true,
        facet: { name: 'Trammel', width: 7168, height: 4096 },

        // LATTICE, like the real one: iso.isoCorner, which is the anchor lifted by the land art's
        // 44 pixels. The -44 is the whole of the fix this stub used to hide - without it,
        // `toScreen(x + 0.5, y + 0.5)` is the tile SOUTH-EAST of (x, y).
        toScreen: (x, y, z = 0) => [
            ((x - y) * 22 - (1475 - 1645) * 22) * s + 600,
            ((x + y) * 22 - z * 4 - 44 - (1475 + 1645) * 22) * s + 400
        ]
    };
}

test('a marker for a tile lands in the middle of that tile, in both projections', () => {
    // The regression. `toScreen(x + 0.5, y + 0.5)` has to be the middle of tile (x, y) in art as
    // well as in radar, because every layer in the editor writes it that way. It was the middle of
    // (x + 1, y + 1) - forty-four pixels low, one tile along (x + y) - because toScreen projected
    // the SPRITE ANCHOR, which hangs 22 pixels below the tile it belongs to.
    const art = stubArtView();

    // A tile's middle is equidistant from the middles of its four diagonal neighbours, and sits
    // exactly half way between the ones north-west and south-east of it. Nothing about that is true
    // of a point half a tile off, so this pins the offset without restating the formula.
    const here = art.toScreen(1424.5, 1557.5, 30);
    const nw = art.toScreen(1423.5, 1556.5, 30);
    const se = art.toScreen(1425.5, 1558.5, 30);

    assert.strictEqual(here[1] - nw[1], se[1] - here[1], 'the middle is not centred between its neighbours');
    assert.strictEqual(here[1], (nw[1] + se[1]) / 2);

    // And one tile in x and one in y at once moves it by exactly one land tile's height, which is
    // the amount the whole editor was out by.
    assert.strictEqual(se[1] - here[1], 44);
});

test('a shape only keeps a click from a bot when it is something you could have dragged', () => {
    // A bot beats a line or an area and loses to a point or a drag handle. The rule that shipped
    // first was "only when nothing else is hit", and `brit-town` is a 324x279 zone covering the
    // whole of Britain - so every click inside it hit the zone body and no bot in the town was
    // clickable at all. `mode` cannot decide this alone: a point marker and a rect interior both
    // answer 'move'.
    const point = { kind: 'point' };
    const rect = { kind: 'rect' };
    const line = { kind: 'polyline' };

    assert.strictEqual(shapes.grabsOverEntity(null), false, 'nothing hit');
    assert.strictEqual(shapes.grabsOverEntity({ shape: rect, mode: 'move' }), false, 'a zone body');
    assert.strictEqual(shapes.grabsOverEntity({ shape: line, mode: 'body' }), false, 'an edge line');

    assert.strictEqual(shapes.grabsOverEntity({ shape: point, mode: 'move' }), true, 'a waypoint');
    assert.strictEqual(shapes.grabsOverEntity({ shape: rect, mode: 'resize' }), true, 'a corner handle');
    assert.strictEqual(shapes.grabsOverEntity({ shape: line, mode: 'node' }), true, 'a polyline node');
});

test('a live bot is picked at its own Z in art, and nowhere near the ground plane', () => {
    // Bots have always DRAWN at their own Z; what they could not do is be clicked at all, in either
    // projection - hitTest walks state.shapes and an entity is not one, so the only way into a bot
    // was the Bots panel. The comparison is the same one hitTest makes for a point, and it needs no
    // pick map: the dot is drawn where it is, so the projected anchor is exact.
    const bot = { kind: 'bot', serial: 1, name: 'Alric', x: 1424, y: 1557, z: 30, map: 'Trammel' };
    const view = stubArtView();

    const [sx, sy] = view.toScreen(1424.5, 1557.5, 30);

    assert.strictEqual(shapes.entityAt(view, [bot], 0, 0, sx, sy), bot);

    // And where a ground-plane inverse would have put it, there is nothing - the same distinction
    // the waypoint test above makes, and for the same reason.
    const [gx, gy] = view.toScreen(1424.5, 1557.5, 0);

    assert.strictEqual(shapes.entityAt(view, [bot], 0, 0, gx, gy), null);
});

test('a bot on another facet, or nowhere near the cursor, is not picked', () => {
    const view = stubArtView();
    const here = { kind: 'bot', serial: 1, x: 1424, y: 1557, z: 30, map: 'Trammel' };
    const elsewhere = { kind: 'bot', serial: 2, x: 1424, y: 1557, z: 30, map: 'Felucca' };

    const [sx, sy] = view.toScreen(1424.5, 1557.5, 30);

    assert.strictEqual(shapes.entityAt(view, [elsewhere], 0, 0, sx, sy), null);
    assert.strictEqual(shapes.entityAt(view, [here], 0, 0, sx + 200, sy), null);
    assert.strictEqual(shapes.entityAt(view, [], 0, 0, sx, sy), null);
});

test('the nearest bot wins when two stand on top of each other', () => {
    const view = stubArtView();
    const near = { kind: 'bot', serial: 1, x: 1424, y: 1557, z: 30, map: 'Trammel' };
    const far = { kind: 'bot', serial: 2, x: 1425, y: 1557, z: 30, map: 'Trammel' };

    const [sx, sy] = view.toScreen(1424.5, 1557.5, 30);

    assert.strictEqual(shapes.entityAt(view, [far, near], 0, 0, sx, sy).serial, 1);
});

test('in radar a bot is picked in world space, with the same slack a waypoint gets', () => {
    const view = stubView(4);
    const bot = { kind: 'bot', serial: 1, x: 100, y: 200, z: 5, map: 'Trammel' };

    assert.strictEqual(shapes.entityAt(view, [bot], 100.5, 200.5), bot);
    assert.strictEqual(shapes.entityAt(view, [bot], 140, 200.5), null);
});

test('a world rect is a rect in radar and four projected corners in art', () => {
    const zone = {
        layer: 'nav-zones', id: 'zone:town', kind: 'rect', map: 'Trammel',
        rect: [1470, 1640, 10, 10], props: {}
    };

    const visible = new Set(['nav-zones']);

    // Radar: unchanged, and still the fillRect/strokeRect pair the rest of these tests pin.
    {
        const { ctx, calls } = stubContext();
        shapes.draw(ctx, stubView(1), [zone], visible, null, null, new Set());

        assert.strictEqual(calls.filter((c) => c.name === 'strokeRect').length, 1);
        assert.strictEqual(calls.filter((c) => c.name === 'lineTo').length, 0);
    }

    // Art: no rect calls at all, and a closed four-corner path instead. A world rect is a diamond
    // here, so `w * view.scale` would have drawn a square in the wrong place at the wrong angle.
    {
        const { ctx, calls } = stubContext();
        shapes.draw(ctx, stubArtView(), [zone], visible, null, null, new Set());

        assert.strictEqual(calls.filter((c) => c.name === 'strokeRect').length, 0);
        assert.strictEqual(calls.filter((c) => c.name === 'moveTo').length, 1);
        assert.strictEqual(calls.filter((c) => c.name === 'lineTo').length, 3);
        assert.strictEqual(calls.filter((c) => c.name === 'closePath').length, 1);
    }
});

test('a point is drawn at its own Z in art, and Z is ignored in radar', () => {
    const high = {
        layer: 'nav', id: 'wp:high', kind: 'point', map: 'Trammel',
        points: [[1475, 1645, 60]], props: {}
    };

    const low = {
        layer: 'nav', id: 'wp:low', kind: 'point', map: 'Trammel',
        points: [[1475, 1645, 0]], props: {}
    };

    const visible = new Set(['nav']);

    const arcsIn = (view, shape) => {
        const { ctx, calls } = stubContext();
        shapes.draw(ctx, view, [shape], visible, null, null, new Set());
        return calls.filter((c) => c.name === 'arc')[0].args;
    };

    // Radar has no Z in its projection at all, so the two land on the same pixel.
    assert.deepStrictEqual(arcsIn(stubView(1), high).slice(0, 2), arcsIn(stubView(1), low).slice(0, 2));

    // Art lifts by 4px per Z at 1:1, so sixty Z is 240 pixels up and the X does not move.
    const artHigh = arcsIn(stubArtView(), high);
    const artLow = arcsIn(stubArtView(), low);

    assert.strictEqual(artHigh[0], artLow[0]);
    assert.strictEqual(artLow[1] - artHigh[1], 60 * 4);
});

test('in art a waypoint is picked by where it was DRAWN, not by the ground under the cursor', () => {
    // The isometric inverse is not a function, so the world position of a click is a ground-plane
    // guess and is off by z*4/44 tiles - about 2.7 where Britain stands. Picking in world space
    // there would select whatever is three tiles north-west of what was clicked. The editor drew
    // this waypoint itself, at its own Z, so the screen comparison is exact.
    const waypoint = {
        layer: 'nav', id: 'wp:forge', kind: 'point', map: 'Trammel',
        points: [[1424, 1557, 30]], props: {}
    };

    const view = stubArtView();
    const visible = new Set(['nav']);

    const [sx, sy] = view.toScreen(1424.5, 1557.5, 30);

    // Clicking where it was drawn takes it, whatever the world coordinates say.
    assert.strictEqual(
        shapes.hitTest(view, [waypoint], visible, null, 0, 0, sx, sy).shape,
        waypoint);

    // And clicking where a ground-plane inverse would have put it does not.
    const [groundX, groundY] = view.toScreen(1424.5, 1557.5, 0);

    assert.strictEqual(
        shapes.hitTest(view, [waypoint], visible, null, 0, 0, groundX, groundY),
        null,
        'the z=30 marker was picked from the z=0 position, which is the bug this replaces');
});

test('in art a zone is picked by its projected diamond, not its world box', () => {
    const zone = {
        layer: 'nav-zones', id: 'zone:town', kind: 'rect', map: 'Trammel',
        rect: [1400, 1600, 100, 100], props: {}
    };

    const view = stubArtView(4);
    const visible = new Set(['nav-zones']);

    const centre = view.toScreen(1450, 1650, 0);
    assert.strictEqual(shapes.hitTest(view, [zone], visible, null, 0, 0, centre[0], centre[1]).shape, zone);

    // The corner of the world box is a corner of the DIAMOND, so a point beyond it in screen space
    // is outside even though a world-space box test would have taken it.
    const outside = view.toScreen(1400, 1600, 0);
    assert.strictEqual(
        shapes.hitTest(view, [zone], visible, null, 0, 0, outside[0], outside[1] - 40),
        null);
});

test('without a screen point, hit testing stays in world space even in art', () => {
    // Every caller in app.js passes one, but pick() and hitTest() are public and the fallback has
    // to be the old behaviour rather than a crash - and radar must be entirely unaffected.
    const waypoint = {
        layer: 'nav', id: 'wp:a', kind: 'point', map: 'Trammel',
        points: [[1475, 1645, 0]], props: {}
    };

    const found = shapes.hitTest(stubArtView(), [waypoint], new Set(['nav']), null, 1475.5, 1645.5);

    assert.strictEqual(found.shape, waypoint);
});
