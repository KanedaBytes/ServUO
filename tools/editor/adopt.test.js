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

// ---- rebase: removals and rewrites ----------------------------------------------------------
//
// A rebase proposal is the only one that asks Save to DELETE something of ours, so these hold the
// two halves of that to account: which shapes go, and what the records that named them say instead.

/** `split()` plus the two fields a rebase adds. */
function rebased() {
    const proposal = split();

    proposal.rebase = true;
    proposal.mergeRadius = 6;
    proposal.removals = [
        { id: 'brit-old-1', into: 'uo-a', x: 12, y: 11, tiles: 2 },
        { id: 'brit-old-2', into: 'uo-a-s1', x: 22, y: 13, tiles: 3 }
    ];
    proposal.removedEdges = ['edge:brit-old-1>brit-old-2', 'edge:brit-keep>brit-old-1'];
    proposal.rewrites = [
        {
            kind: 'destination', shape: 'dest:brit-shop-x', owner: 'brit-shop-x',
            from: 'brit-old-1', to: 'uo-a'
        },
        {
            kind: 'route', shape: 'route:brit-loop', owner: 'brit-loop',
            from: 'brit-old-1 brit-old-2', to: 'uo-a uo-a-s1'
        }
    ];

    return proposal;
}

test('removals delete the edges first, then the waypoints', () => {
    // Edges first so each is resolved while both its ends still exist, and edges AT ALL because a
    // dangling edge id is only a warning here - left behind, it would reload clean and sit in the
    // file naming a record that no longer exists.
    assert.deepStrictEqual(adopt.removals(rebased()), [
        'edge:brit-old-1>brit-old-2',
        'edge:brit-keep>brit-old-1',
        'wp:brit-old-1',
        'wp:brit-old-2'
    ]);
});

test('rewrites become shape updates carrying only the waypoints field', () => {
    // The shape id comes from the shard, so accepting a proposal is a map onto `updates` rather
    // than a second matching-up of records - and nothing but the approach list is touched, so a
    // rewrite cannot quietly move a record's position, name or flags.
    assert.deepStrictEqual(adopt.rewrites(rebased()), [
        { id: 'dest:brit-shop-x', props: { waypoints: 'uo-a' } },
        { id: 'route:brit-loop', props: { waypoints: 'uo-a uo-a-s1' } }
    ]);
});

test('an ordinary proposal has no removals and no rewrites', () => {
    // The whole ordinary flow has to be unchanged by this, and "unchanged" means the two new
    // lists come back empty rather than undefined - a caller spreads them into a save payload.
    const proposal = split();

    assert.deepStrictEqual(adopt.removals(proposal), []);
    assert.deepStrictEqual(adopt.rewrites(proposal), []);
    assert.ok(!/REBASE/.test(adopt.summary(proposal, 1).join('\n')));
});

test('the banner names the relinks that keep the neighbours of a removed waypoint attached', () => {
    const proposal = rebased();
    proposal.relinks = ['brit-keep>uo-a'];

    assert.match(adopt.summary(proposal, 5).join('\n'),
        /1 edge\(s\) walked to keep the neighbours of a removed waypoint on the road: brit-keep>uo-a/);
});

test('a withdrawn removal says so, because keeping our waypoint is the visible outcome', () => {
    // A relink that would not walk takes the removal back rather than stranding the neighbour.
    const proposal = rebased();
    proposal.withdrawn = ['brit-old-2 is KEPT: the relink from one of its neighbours to uo-a-s1 would not walk'];

    assert.match(adopt.summary(proposal, 5).join('\n'), /WITHDRAWN: brit-old-2 is KEPT/);
});

test('the banner names every removal, its distance, and what it folded into', () => {
    const text = adopt.summary(rebased(), 5).join('\n');

    assert.match(text, /REBASE: 2 waypoint\(s\) of ours stand on the road being proposed/);
    assert.match(text, /within 6 tiles of it/);
    assert.match(text, /brit-old-1 \(12,11\) -> uo-a, 2 tile\(s\) from the new road/);
    assert.match(text, /2 record\(s\) named a removed waypoint and are re-pointed/);
    assert.match(text, /route brit-loop: "brit-old-1 brit-old-2" -> "uo-a uo-a-s1"/);
});

test('removals with nothing naming them say so rather than printing an empty list', () => {
    // A road of ours nothing pointed at is the common case for a plain street, and a banner that
    // fell silent there would read as "the rewrites are missing" rather than "there were none".
    const proposal = rebased();
    proposal.rewrites = [];

    const text = adopt.summary(proposal, 5).join('\n');

    assert.match(text, /No destination, arrival or route named any of them\./);
});
