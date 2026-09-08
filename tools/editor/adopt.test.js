'use strict';

// node --test tools/editor/*.test.js
//
// The rule for which proposed records Save writes. It decides whether an adopted road ends at the
// edge of the map, and until this file it was tested by nothing: it lived in app.js, which is
// browser-only. Fixtures rather than the live proposal, because the point of each case is a shape
// the Britain-Trinsic box may never produce again now that its five edges walk.

const test = require('node:test');
const assert = require('node:assert');

let adopt;

test.before(async () => {
    adopt = await import('./js/adopt.js');
});

/** A proposal in the shard's shape: two roads, one of which reaches our graph and one does not. */
function split() {
    return {
        status: 'done', map: 'Trammel', links: 1, blocked: false, reachable: 2,
        skipped: { authored: 3, region: 0, noArrival: 0 },
        waypoints: [
            { id: 'uo-a', map: 'Trammel', x: 10, y: 10, z: 0, arrivalRange: 0, tags: 'road', source: 'uo-offline' },
            { id: 'uo-a-s1', map: 'Trammel', x: 20, y: 10, z: 0, arrivalRange: 0, tags: 'road', source: 'uo-offline' },
            { id: 'uo-island', map: 'Trammel', x: 90, y: 90, z: 0, arrivalRange: 0, tags: 'road', source: 'uo-offline' },
            { id: 'uo-island-2', map: 'Trammel', x: 99, y: 90, z: 0, arrivalRange: 0, tags: 'road', source: 'uo-offline' },
            { id: 'uo-lonely', map: 'Trammel', x: 50, y: 50, z: -15, arrivalRange: 0, tags: 'road', source: 'uo-offline' }
        ],
        edges: [
            { from: 'brit-gate-w', to: 'uo-a', kind: 'walk', tags: 'road', source: 'uo-offline', join: true },
            { from: 'uo-a', to: 'uo-a-s1', kind: 'walk', tags: 'road', source: 'uo-offline' },
            { from: 'uo-island', to: 'uo-island-2', kind: 'walk', tags: 'road', source: 'uo-offline' }
        ],
        destinations: [
            { id: 'uo-inn', name: 'An Inn', type: 'inn', map: 'Trammel', x: 21, y: 11, z: 0, tags: '', waypoints: 'uo-a-s1', source: 'uo-offline' }
        ],
        arrivals: [
            { destination: 'uo-inn', x: 21, y: 12, z: 5, refZ: 0, exclusive: false, waypoints: 'uo-a-s1', source: 'uo-offline' }
        ],
        failures: [{ from: 'uo-a-s1', to: 'uo-island', reason: 'no walkable road from 20,10 to 90,90', fromX: 20, fromY: 10, toX: 90, toY: 90 }],
        stranded: ['uo-lonely'],
        unreachable: ['uo-island', 'uo-island-2'],
        folded: ['uo-twin>uo-a'],
        corrected: ['uo-inn arrival 21,12: 0 -> 5'],
        corridors: [
            { destination: 'uo-inn', from: 'uo-a-s1', to: 'uo-uo-inn-s1', x: 21, y: 12, tiles: 14, review: false, walked: true },
            { destination: 'uo-forge', from: 'brit-gate-w', to: 'uo-uo-forge-s1', x: 3, y: 40, tiles: 26, review: true, walked: false }
        ],
        skippedNoReach: [],
        islands: ['2 of 5 proposed waypoint(s) cannot reach the existing graph and will not be written.']
    };
}

test('save writes the component that reaches the graph and drops the rest', () => {
    const created = adopt.survivors(split());
    const ids = created.map((shape) => shape.id);

    // The road that joins ours, whole: both waypoints, the join edge whose far end is one of
    // OURS (and so is in neither list), and the edge between them.
    assert.ok(ids.includes('wp:uo-a'));
    assert.ok(ids.includes('wp:uo-a-s1'));
    assert.ok(ids.includes('edge:brit-gate-w>uo-a'), 'the join edge must survive: its far end is ours');
    assert.ok(ids.includes('edge:uo-a>uo-a-s1'));

    // The island, gone with every edge touching it - and the stranded record.
    assert.ok(!ids.includes('wp:uo-island'));
    assert.ok(!ids.includes('wp:uo-island-2'));
    assert.ok(!ids.includes('edge:uo-island>uo-island-2'));
    assert.ok(!ids.includes('wp:uo-lonely'));

    // Destinations and arrivals as the shard pruned them, refZ carried and absent when absent.
    assert.ok(ids.includes('dest:uo-inn'));
    const arrival = created.find((shape) => shape.id === 'arr:uo-inn#0');
    assert.strictEqual(arrival.props.refZ, 0, 'a refZ of 0 is a value, not an absence');
    const waypoint = created.find((shape) => shape.id === 'wp:uo-a');
    assert.ok(!('refZ' in waypoint.props), 'refZ must be absent, not undefined, on an uncorrected record');
});

test('a proposal that reaches us in part is not blocked, and the banner carries both counts', () => {
    const proposal = split();

    assert.strictEqual(adopt.blocked(proposal), false);

    const lines = adopt.summary(proposal, adopt.survivors(proposal).length);
    const text = lines.join('\n');

    assert.match(text, /2 waypoint\(s\) reach the existing graph and will be written; 2 cannot and are DROPPED/);
    assert.match(text, /reference layer, dashed/);
    assert.match(text, /1 edge\(s\) could not be walked/);
    assert.match(text, /1 waypoint\(s\) had no surviving edge/);
    assert.match(text, /folded into it: uo-twin>uo-a/);
    assert.match(text, /corrected to where the walker stood .*uo-inn arrival 21,12: 0 -> 5/);
    assert.match(text, /Save to accept, or Discard/);

    // Every corridor with its length and start; a long one flagged, a failed one said so.
    assert.match(text, /2 corridor\(s\) walked to destinations/);
    assert.match(text, /uo-inn: 14 tile\(s\) from uo-a-s1 to 21,12, walked$/m);
    assert.match(text, /uo-forge: 26 tile\(s\) from brit-gate-w to 3,40, FAILED .* - REVIEW: over two hops/);
});

test('a proposal that reaches nothing is blocked, whatever the join count says', () => {
    // `links` counts joins QUEUED; a join whose walk failed still counted. The shard now decides
    // `blocked` from what its flood reached, and the editor takes that flag and nothing else.
    const proposal = split();
    proposal.links = 1;
    proposal.blocked = true;
    proposal.reachable = 0;
    proposal.unreachable = ['uo-a', 'uo-a-s1', 'uo-island', 'uo-island-2'];
    proposal.islands.unshift('NO JOIN WAS MADE.');

    assert.strictEqual(adopt.blocked(proposal), true);
    assert.deepStrictEqual(adopt.survivors(proposal).filter((shape) => shape.layer === 'nav'), []);

    const text = adopt.summary(proposal, 0).join('\n');
    assert.match(text, /NO JOIN WAS MADE/);
    assert.match(text, /cannot be saved: Discard it/);
});

test('a proposal with nothing dropped says so in one line', () => {
    const proposal = split();
    proposal.unreachable = [];
    proposal.stranded = [];
    proposal.reachable = 5;
    proposal.islands = [];

    const created = adopt.survivors(proposal);
    assert.strictEqual(created.filter((shape) => shape.layer === 'nav').length, 5);

    const text = adopt.summary(proposal, created.length).join('\n');
    assert.match(text, /All 5 waypoint\(s\) reach the existing graph/);
    assert.ok(!/DROPPED/.test(text));
});

test('an older proposal without the reachability fields still renders', () => {
    // The shard writes `blocked`, `reachable` and `unreachable` now; a file from before it did
    // is a proposal with nothing dropped and nothing to say about reach.
    const proposal = split();
    delete proposal.blocked;
    delete proposal.reachable;
    delete proposal.unreachable;

    assert.strictEqual(adopt.blocked(proposal), false);
    assert.ok(adopt.survivors(proposal).some((shape) => shape.id === 'wp:uo-island'));

    const text = adopt.summary(proposal, 1).join('\n');
    assert.ok(!/will be written|All \d+ waypoint/.test(text), 'no reach counts without the fields');
});
