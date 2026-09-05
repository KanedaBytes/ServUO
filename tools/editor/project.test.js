'use strict';

// node --test tools/editor
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

        assert.strictEqual(unproject([], golden), golden);
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

    assert.strictEqual(unproject([], source), source);

    const shapes = require('./project.js').projectNavigation(JSON.parse(source));

    assert.strictEqual(shapes[0].props.somethingNew, 'keep me',
        'an unmodelled field was dropped instead of carried into props');
});

test('unproject moves a point and rejects an id that no longer exists', () => {
    const source = '{\n  "waypoints": [\n    {"id":"a","map":"Trammel","x":1,"y":2,"z":3}\n  ]\n}\n';

    const moved = unproject([{ id: 'wp:a', kind: 'point', points: [[10, 20, 3]] }], source);

    assert.match(moved, /"x":10,"y":20,"z":3/);

    assert.throws(
        () => unproject([{ id: 'wp:ghost', kind: 'point', points: [[1, 1, 1]] }], source),
        /No record for shape id/
    );
});
