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

const { FILES, WRITABLE } = require('./whitelist.js');
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
        if (name === 'entities') {
            assert.strictEqual(layer.file, null);
            continue;
        }

        assert.ok(WRITABLE[layer.file], `${name} saves to '${layer.file}', which is not writable`);
        assert.strictEqual(layer.reload, WRITABLE[layer.file],
            `${name} reloads with '${layer.reload}' but its file reloads with '${WRITABLE[layer.file]}'`);
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
