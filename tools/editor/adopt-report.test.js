'use strict';

// node --test tools/editor/*.test.js
//
// The per-town adopt report. What it must get right is the ATTRIBUTION - which town a record, an
// edge and a failure are counted against - and that a skip box is reported as skipped rather than
// failed. Fixtures, because the live proposal is thousands of records and a test that re-derives
// its own expectations from it proves nothing.

const test = require('node:test');
const assert = require('node:assert');

const { report, townOf, reasonClass } = require('./adopt-report.js');

let adopt;

test.before(async () => {
    adopt = await import('./js/adopt.js');
});

const reference = {
    destinations: [
        { id: 'moonglow-bank', tags: 'service moonglow', x: 4470, y: 1170 },
        { id: 'yew-bank', tags: 'service yew', x: 650, y: 820 },
        { id: 'uo-dungeon-thing', tags: 'dungeon', x: 5200, y: 100 }
    ]
};

function proposal() {
    return {
        status: 'done', map: 'Trammel', blocked: false,
        skipped: { authored: 4, region: 2, noArrival: 1, existing: 3 },
        skipBoxes: [{ name: 'wind', x: 5120, y: 0, width: 260, height: 210, waypoints: 0, destinations: 0 }],
        waypoints: [
            { id: 'uo-m1', map: 'Trammel', x: 4460, y: 1200, z: 0 },
            { id: 'uo-m1-s1', map: 'Trammel', x: 4462, y: 1210, z: 0 },
            { id: 'uo-y1', map: 'Trammel', x: 660, y: 830, z: 0 },
            { id: 'uo-far', map: 'Trammel', x: 2000, y: 400, z: 0 },
            { id: 'uo-cut', map: 'Trammel', x: 670, y: 840, z: 0 }
        ],
        edges: [
            { from: 'gate-moonglow', to: 'uo-m1', kind: 'walk' },
            { from: 'uo-m1', to: 'uo-m1-s1', kind: 'walk' },
            { from: 'uo-y1', to: 'uo-far', kind: 'walk' }
        ],
        destinations: [
            { id: 'moonglow-bank', name: 'Bank', type: 'bank', map: 'Trammel', x: 4470, y: 1170, z: 0, tags: 'service moonglow', waypoints: 'uo-m1' }
        ],
        arrivals: [],
        failures: [
            { from: 'uo-y1', to: 'uo-x', reason: 'flood ok, engine refused: 1,2,3 -> 4,5,6 is not walkable', fromX: 660, fromY: 830 },
            { from: 'uo-y1', to: 'uo-z', reason: 'flood ok, engine refused: 7,8,9 -> 1,1,1 is not walkable', fromX: 660, fromY: 830 },
            { from: 'uo-m1', to: 'uo-q', reason: 'no walkable road from 1,2 to 3,4', fromX: 4460, fromY: 1200 }
        ],
        stranded: [],
        unreachable: ['uo-cut'],
        skippedNoReach: ['yew-inn']
    };
}

test('a town is the last tag of a reference destination with a City, and never a uo- record', () => {
    assert.strictEqual(townOf({ id: 'skara-brae-bank', tags: 'service skara-brae' }), 'skara-brae');
    assert.strictEqual(townOf({ id: 'uo-some-cave', tags: 'dungeon' }), null);
});

test('failure reasons lose their coordinates so one fault counts once', () => {
    assert.strictEqual(
        reasonClass('flood ok, engine refused: 1,2,3 -> 4,5,6 is not walkable'),
        'flood ok, engine refused');
    assert.strictEqual(reasonClass('no walkable road from 1,2 to 3,4'), 'no walkable road from x,y to x,y');
});

test('records, edges and failures are counted against the nearest town, the rest is wilderness', () => {
    const p = proposal();
    const { rows, total } = report(p, reference, adopt.survivors(p));
    const byTown = Object.fromEntries(rows.map((row) => [row.town, row]));

    assert.strictEqual(byTown.moonglow.waypoints, 2);
    assert.strictEqual(byTown.moonglow.destinations, 1);
    assert.strictEqual(byTown.moonglow.edges, 2, 'the join from our gate counts where its proposed end is');
    assert.strictEqual(byTown.moonglow.failed, 1);

    assert.strictEqual(byTown.yew.waypoints, 1, 'the dropped waypoint is not counted as adopted');
    assert.strictEqual(byTown.yew.dropped, 1);
    assert.strictEqual(byTown.yew.failed, 2);
    assert.strictEqual(byTown.yew.reasons.get('flood ok, engine refused'), 2);

    assert.strictEqual(byTown.wilderness.waypoints, 1, 'uo-far is past the town radius of everything');

    assert.strictEqual(total.failed, 3);
});

test('skip boxes are listed as skipped, with the refusal counts beside them', () => {
    const p = proposal();
    const text = report(p, reference, adopt.survivors(p)).lines.join('\n');

    assert.match(text, /\*\*Skipped, not failed:\*\*/);
    assert.match(text, /`wind` 5120,0 260x210: 0 waypoint\(s\), 0 destination\(s\)/);
    assert.match(text, /3 already in navigation.json/);
});
