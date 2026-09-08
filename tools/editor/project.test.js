'use strict';

// node --test tools/editor/*.test.js
//
// The point of this file is the golden round trip. SerializeCompact's layout rule now exists
// twice - once in C# in JsonConfig, once in JS in compact.js - and nothing but this test stops
// them drifting. When they drift, the symptom in 5b is not an error: it is a quietly reformatted
// or corrupted data file.
//
// The goldens are written by the shard itself with [NavExportGolden, through the same writer
// every real save uses, so they are what the shard would actually emit rather than what someone
// hand-typed.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const compact = require('./compact.js');
const { project, unproject, tokens } = require('./project.js');
const { REPO_ROOT, FILES } = require('./whitelist.js');

const GOLDEN = path.join(REPO_ROOT, 'Data', 'Custom', 'golden');

const goldens = [
    ['navigation', path.join(GOLDEN, 'navigation.golden.json'), FILES.navigation],
    ['britain-daily-life', path.join(GOLDEN, 'britain-daily-life.golden.json'), FILES.dailyLife]
];

test('the goldens exist - run [NavExportGolden if this fails', () => {
    for (const [name, goldenPath] of goldens) {
        assert.ok(fs.existsSync(goldenPath), `${name} golden is missing: ${goldenPath}`);
    }
});

for (const [name, goldenPath] of goldens) {
    test(`${name}: the JS writer reproduces the C# writer byte-for-byte`, () => {
        const golden = fs.readFileSync(goldenPath, 'utf8');

        assert.strictEqual(
            compact.stringify(compact.parse(golden)),
            golden,
            'compact.js and JsonConfig.SerializeCompact have drifted'
        );
    });

    test(`${name}: unproject with no edits is the identity`, () => {
        const golden = fs.readFileSync(goldenPath, 'utf8');

        assert.strictEqual(unproject(name === 'navigation' ? 'navigation' : 'dailyLife', golden, {}), golden);
    });

    test(`${name}: the shipped file is canonical`, () => {
        const golden = fs.readFileSync(goldenPath, 'utf8');
        const shipped = fs.readFileSync(goldens.find((g) => g[1] === goldenPath)[2], 'utf8');

        assert.strictEqual(
            shipped,
            golden,
            'the shipped file is not what the shard would write - re-run [NavExportGolden and copy it over'
        );
    });
}

test('an integer written as a float stays a float', () => {
    // The case that broke first: Newtonsoft writes 3.0, JSON.stringify writes 3.
    const source = '{\n  "costTags": [\n    {"tag":"danger","multiplier":3.0}\n  ]\n}\n';

    assert.strictEqual(compact.stringify(compact.parse(source)), source);
});

test('editing a float keeps it a float', () => {
    const node = compact.parse('{"multiplier":3.0}');
    compact.setScalar(compact.get(node, 'multiplier'), 4);

    assert.strictEqual(compact.stringify(node), '{"multiplier":4.0}\n');
});

test('a container of scalars goes on one line, a nested one does not', () => {
    assert.strictEqual(compact.stringify(compact.parse('{"a":1,"b":"x"}')), '{"a":1,"b":"x"}\n');

    assert.strictEqual(
        compact.stringify(compact.parse('{"a":{"b":{"c":1}}}')),
        '{\n  "a": {\n    "b": {"c":1}\n  }\n}\n'
    );
});

test('space-separated lists split, and an absent one is empty rather than a crash', () => {
    assert.deepStrictEqual(tokens('town road bank'), ['town', 'road', 'bank']);
    assert.deepStrictEqual(tokens(''), []);
    assert.deepStrictEqual(tokens(undefined), []);
});

test('projection produces shapes with numeric geometry, not strings', () => {
    const nav = JSON.parse(fs.readFileSync(FILES.navigation, 'utf8'));
    const daily = JSON.parse(fs.readFileSync(FILES.dailyLife, 'utf8'));

    const shapes = project({ navigation: nav, dailyLife: daily, restrictedZones: { zones: [] } });

    assert.ok(shapes.length > 0, 'no shapes were produced');

    for (const shape of shapes) {
        assert.ok(shape.id.includes(':'), `id is not <kind>:<id>: ${shape.id}`);
        assert.ok(shape.map, `shape has no facet: ${shape.id}`);

        // shapes.js does `shape.rect[0] += dx`; a string there concatenates instead of adding.
        for (const value of shape.rect || []) {
            assert.strictEqual(typeof value, 'number', `rect is not numeric on ${shape.id}`);
        }

        for (const point of shape.points || []) {
            for (const value of point) {
                assert.strictEqual(typeof value, 'number', `point is not numeric on ${shape.id}`);
            }
        }
    }
});

test('every waypoint, destination and zone is projected', () => {
    const nav = JSON.parse(fs.readFileSync(FILES.navigation, 'utf8'));
    const shapes = project({ navigation: nav });

    const count = (layer) => shapes.filter((s) => s.layer === layer).length;

    assert.strictEqual(count('nav'), nav.waypoints.length);
    assert.strictEqual(count('nav-destinations'), nav.destinations.length);
    assert.strictEqual(count('nav-zones'), nav.zones.length);
    assert.strictEqual(count('nav-arrivals'), nav.arrivals.length);
});

test('unknown fields survive a round trip', () => {
    const source = '{\n  "waypoints": [\n    {"id":"a","map":"Trammel","x":1,"y":2,"z":3,"somethingNew":"keep me"}\n  ]\n}\n';

    assert.strictEqual(unproject('navigation', source, {}), source);

    const shapes = require('./project.js').projectNavigation(JSON.parse(source));

    assert.strictEqual(shapes[0].props.somethingNew, 'keep me',
        'an unmodelled field was dropped instead of carried into props');
});

test('unproject moves a point and rejects an id that no longer exists', () => {
    const source = '{\n  "waypoints": [\n    {"id":"a","map":"Trammel","x":1,"y":2,"z":3}\n  ]\n}\n';

    const moved = unproject('navigation', source,
        { updates: [{ id: 'wp:a', kind: 'point', points: [[10, 20, 3]] }] });

    assert.match(moved, /"x":10,"y":20,"z":3/);

    assert.throws(
        () => unproject('navigation', source,
            { updates: [{ id: 'wp:ghost', kind: 'point', points: [[1, 1, 1]] }] }),
        /No record for shape id/
    );
});

// ---- the write side -------------------------------------------------------------------------

const { TEMPLATES, fileForShape } = require('./project.js');

function navGolden() {
    return fs.readFileSync(path.join(GOLDEN, 'navigation.golden.json'), 'utf8');
}

/** Which whole lines a write added and removed. A save should touch as few as it possibly can. */
function lineDiff(before, after) {
    const was = before.split('\n');
    const now = after.split('\n');
    const counts = new Map();

    for (const line of was) counts.set(line, (counts.get(line) || 0) + 1);
    for (const line of now) counts.set(line, (counts.get(line) || 0) - 1);

    const removed = [];
    const added = [];

    for (const [line, n] of counts) {
        for (let i = 0; i < n; i++) removed.push(line);
        for (let i = 0; i < -n; i++) added.push(line);
    }

    return { added, removed };
}

test('every template matches the field order the C# writer actually produced', () => {
    // The anti-drift guard for record CREATION, the counterpart to the golden round trip for
    // record editing. A subsequence rather than an exact match, because an optional field like
    // `note` is legitimately absent from most records.
    const root = compact.parse(navGolden());
    const sections = {
        waypoints: 'waypoint', edges: 'edge', destinations: 'destination',
        arrivals: 'arrival', zones: 'zone', routes: 'route', costTags: 'costTag'
    };

    for (const [section, templateName] of Object.entries(sections)) {
        const keys = TEMPLATES[templateName].keys;

        for (const record of compact.get(root, section).items) {
            let at = -1;

            for (const key of compact.keys(record)) {
                const next = keys.indexOf(key, at + 1);

                assert.notStrictEqual(next, -1,
                    `${section} record has '${key}' out of template order, or not in the template`);

                at = next;
            }
        }
    }
});

test('creating a record adds exactly one line and changes no other byte', () => {
    const before = navGolden();
    const after = unproject('navigation', before, {
        creates: [{
            id: 'wp:test-new', kind: 'point', map: 'Trammel', points: [[100, 200, 5]],
            props: { id: 'test-new', tags: 'town road' }
        }]
    });

    const { added, removed } = lineDiff(before, after);
    const created =
        '    {"id":"test-new","map":"Trammel","x":100,"y":200,"z":5,"arrivalRange":0,"tags":"town road"}';

    // Two lines change, and the second one has to: appending to a JSON array puts a comma on the
    // record that used to be last. Nothing else in the file moves.
    assert.strictEqual(removed.length, 1);
    assert.deepStrictEqual(added.sort(), [created, removed[0] + ','].sort());
});

test('deleting a record removes exactly one line and changes no other byte', () => {
    const before = navGolden();
    const after = unproject('navigation', before, { deletes: ['wp:brit-plaza-1'] });
    const { added, removed } = lineDiff(before, after);

    assert.deepStrictEqual(added, []);
    assert.strictEqual(removed.length, 1);
    assert.match(removed[0], /"id":"brit-plaza-1"/);
});

test('a batch applies the same however the editor happened to order it', () => {
    const edits = {
        deletes: ['wp:brit-plaza-4'],
        updates: [{ id: 'wp:brit-plaza-5', kind: 'point', map: 'Trammel', points: [[1, 2, 3]] }],
        creates: [{
            id: 'wp:test-a', kind: 'point', map: 'Trammel', points: [[7, 8, 9]],
            props: { id: 'test-a' }
        }]
    };

    const forwards = unproject('navigation', navGolden(), edits);
    const backwards = unproject('navigation', navGolden(), {
        deletes: [...edits.deletes].reverse(),
        updates: [...edits.updates].reverse(),
        creates: [...edits.creates].reverse()
    });

    assert.strictEqual(forwards, backwards);
});

test('an arrival is the nth of its destination, not the nth in the file', () => {
    const before = navGolden();
    const nav = JSON.parse(before);
    const mine = nav.arrivals.filter((a) => a.destination === 'brit-square');

    assert.ok(mine.length >= 2, 'the fixture needs a destination with two arrivals');

    const after = unproject('navigation', before, {
        updates: [{ id: 'arr:brit-square#1', kind: 'point', points: [[500, 600, 7]] }]
    });

    const moved = JSON.parse(after).arrivals.filter((a) => a.destination === 'brit-square');

    assert.deepStrictEqual(
        [moved[1].x, moved[1].y, moved[1].z], [500, 600, 7]);
    assert.deepStrictEqual(
        [moved[0].x, moved[0].y, moved[0].z], [mine[0].x, mine[0].y, mine[0].z]);
});

test('deleting one arrival and moving a later one in the same batch moves the right one', () => {
    // By index this is the bug: removing #0 renumbers #1 to #0 before the move is applied.
    const before = navGolden();
    const was = JSON.parse(before).arrivals.filter((a) => a.destination === 'brit-square');

    const after = unproject('navigation', before, {
        deletes: ['arr:brit-square#0'],
        updates: [{ id: 'arr:brit-square#1', kind: 'point', points: [[500, 600, 7]] }]
    });

    const now = JSON.parse(after).arrivals.filter((a) => a.destination === 'brit-square');

    assert.strictEqual(now.length, was.length - 1);
    assert.deepStrictEqual([now[0].x, now[0].y, now[0].z], [500, 600, 7]);
});

test('a shape saved into the wrong file is refused, section name collision and all', () => {
    // zone: and restricted: both name a section called "zones", in two different files.
    assert.strictEqual(fileForShape({ id: 'zone:brit-town' }), 'navigation');
    assert.strictEqual(fileForShape({ id: 'restricted:x' }), 'restrictedZones');

    assert.throws(
        () => unproject('restrictedZones', '{"zones":[]}\n',
            { updates: [{ id: 'zone:brit-town', kind: 'rect', rect: [1, 2, 3, 4] }] }),
        /belongs to navigation, not restrictedZones/);
});

test('a derived marker is refused by name rather than written somewhere plausible', () => {
    assert.strictEqual(fileForShape({ id: 'marker:shop:baker' }), null);

    assert.throws(
        () => unproject('dailyLife', '{"anchor":"a"}\n',
            { updates: [{ id: 'marker:shop:baker', kind: 'point', points: [[1, 2, 3]] }] }),
        /is derived from the navigation data/);
});

test('a polyline drag writes nothing, because its line is derived from its ids', () => {
    const before = navGolden();

    for (const shape of [
        { id: 'route:brit-watch-north', kind: 'polyline', map: 'Trammel', points: [[1, 1, 1], [2, 2, 2]] },
        { id: 'edge:brit-plaza-1>brit-plaza-2', kind: 'polyline', map: 'Trammel', points: [[1, 1, 1], [2, 2, 2]] }
    ]) {
        assert.strictEqual(unproject('navigation', before, { updates: [shape] }), before,
            `${shape.id} wrote geometry it does not own`);
    }
});

test('a prop the shard has never heard of is refused here rather than by Newtonsoft', () => {
    assert.throws(
        () => unproject('navigation', navGolden(), {
            updates: [{ id: 'wp:brit-plaza-1', kind: 'point', props: { closesAt: 9 } }]
        }),
        /waypoints has no field 'closesAt'/);
});

test('a watch post switches from a route to destinations, keeping one line', () => {
    // route and destinations are both NullValueHandling.Ignore, so this is a key removal plus a
    // key insertion - not something unproject could do before.
    const before = fs.readFileSync(path.join(GOLDEN, 'britain-daily-life.golden.json'), 'utf8');
    const after = unproject('dailyLife', before, {
        updates: [{
            id: 'watchpost:north', kind: 'form',
            props: { id: 'north', route: '', destinations: 'brit-guard-west' }
        }]
    });

    const { added, removed } = lineDiff(before, after);

    assert.strictEqual(removed.length, 1);
    assert.deepStrictEqual(added, ['    {"id":"north","destinations":"brit-guard-west"},']);
    assert.strictEqual(JSON.parse(after).watch[0].route, undefined);
});

test('the daily life singleton writes the anchor and both tavern values', () => {
    const before = fs.readFileSync(path.join(GOLDEN, 'britain-daily-life.golden.json'), 'utf8');
    const after = unproject('dailyLife', before, {
        updates: [{
            id: 'dl:settings', kind: 'form',
            props: { anchor: 'brit-bank', 'tavern.patronCount': 9 }
        }]
    });

    const daily = JSON.parse(after);

    assert.strictEqual(daily.anchor, 'brit-bank');
    assert.strictEqual(daily.tavern.patronCount, 9);
    assert.strictEqual(daily.tavern.destination, JSON.parse(before).tavern.destination);

    assert.throws(
        () => unproject('dailyLife', before, {
            updates: [{ id: 'dl:settings', kind: 'form', props: { nope: 1 } }]
        }),
        /dl:settings has no field 'nope'/);
});

test('creating a record that is already there is refused', () => {
    assert.throws(
        () => unproject('navigation', navGolden(), {
            creates: [{
                id: 'wp:brit-plaza-1', kind: 'point', map: 'Trammel', points: [[1, 2, 3]],
                props: { id: 'brit-plaza-1' }
            }]
        }),
        /wp:brit-plaza-1 already exists/);
});

test('the first restricted zone flips its empty array to the expanded form', () => {
    const before = '{\n  "zones": []\n}\n';
    const after = unproject('restrictedZones', before, {
        creates: [{
            id: 'restricted:no-mining', kind: 'rect', map: 'Trammel', rect: [10, 20, 30, 40],
            props: { name: 'no-mining' }
        }]
    });

    assert.strictEqual(
        after,
        '{\n  "zones": [\n    {"name":"no-mining","map":"Trammel","x":10,"y":20,"width":30,"height":40}\n  ]\n}\n');
});

// --- source: where an adopted record came from -----------------------------------------------

test('an adopted record carries its source, and a hand-authored one carries nothing', () => {
    // `source` is what keeps a record accepted from uo-offline tellable from one measured against
    // this shard's map. The two have different warranties: ours were flood-filled and audited
    // here, an adopted one had its edges re-walked at adopt time and nothing else.
    const base = JSON.parse(fs.readFileSync(FILES.navigation, 'utf8'));

    const adopted = {
        layer: 'nav', id: 'wp:uo-trinsic-gate', kind: 'point', map: 'Trammel',
        points: [[1900, 2700, 0]],
        props: { id: 'uo-trinsic-gate', tags: 'road', arrivalRange: 0, source: 'uo-offline' },
        fields: []
    };

    const written = unproject('navigation', JSON.stringify(base, null, 2), { creates: [adopted] });

    assert.match(written, /"id":"uo-trinsic-gate"[^}]*"source":"uo-offline"/);

    // And absent - not empty - on anything authored here, or the first save would stamp
    // `"source":""` onto every existing record and turn a one-line edit into a whole-file diff.
    const authored = {
        layer: 'nav', id: 'wp:hand-made', kind: 'point', map: 'Trammel',
        points: [[1400, 1600, 0]],
        props: { id: 'hand-made', tags: 'road', arrivalRange: 0 },
        fields: []
    };

    const plain = unproject('navigation', JSON.stringify(base, null, 2), { creates: [authored] });

    assert.match(plain, /"id":"hand-made"/);
    assert.ok(!/"id":"hand-made"[^}]*"source"/.test(plain), 'source was written on an authored record');
});

test('a corrected Z keeps the reference Z as refZ, and zero is a real refZ', () => {
    // uo-offline stores the water's Z for every generated dock waypoint (uo-wp-990 at -15 under a
    // deck at -2). Adopt writes the Z the walker stood on and keeps theirs as refZ, so a diff
    // against the reference file explains the change. A refZ of 0 is a value, not an absence.
    const base = JSON.parse(fs.readFileSync(FILES.navigation, 'utf8'));

    // Ids that are not in the shipped file - the real pier records are, since the adopt landed.
    const pier = {
        layer: 'nav', id: 'wp:uo-test-pier', kind: 'point', map: 'Trammel',
        points: [[2069, 2856, -2]],
        props: { id: 'uo-test-pier', tags: 'road', arrivalRange: 0, source: 'uo-offline', refZ: -15 },
        fields: []
    };

    const zero = {
        layer: 'nav-arrivals', id: 'arr:uo-test-dock#0', kind: 'point', map: 'Trammel',
        points: [[2072, 2848, -2]],
        props: { destination: 'uo-test-dock', exclusive: false, waypoints: 'uo-test-pier', source: 'uo-offline', refZ: 0 },
        fields: []
    };

    const written = unproject('navigation', JSON.stringify(base, null, 2), { creates: [pier, zero] });

    assert.match(written, /"id":"uo-test-pier"[^}]*"z":-2[^}]*"refZ":-15/);
    assert.match(written, /"destination":"uo-test-dock"[^}]*"z":-2[^}]*"refZ":0/);

    // And absent on anything that was not corrected.
    const plain = {
        layer: 'nav', id: 'wp:uo-test-shore', kind: 'point', map: 'Trammel',
        points: [[2054, 2855, 0]],
        props: { id: 'uo-test-shore', tags: 'road', arrivalRange: 0, source: 'uo-offline' },
        fields: []
    };

    const untouched = unproject('navigation', JSON.stringify(base, null, 2), { creates: [plain] });

    assert.ok(!/"id":"uo-test-shore"[^}]*"refZ"/.test(untouched), 'refZ was written on an uncorrected record');
});
