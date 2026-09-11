'use strict';

// node --test tools/editor/*.test.js
//
// js/repoint.js is the rule that decides which waypoint a destination or an arrival should name,
// and it had NO tests for as long as it lived inside repoint-arrivals.js - which is part of why it
// took brit-home-jeweler to find out the editor never ran it.
//
// The cases here are the three the rule exists for, and each one is a thing that actually went
// wrong somewhere: keeping a second approach (brit-tan-1's two streets), refusing to re-point onto
// an island (the flood is not decoration), and leaving a stranded record exactly as it was.

const test = require('node:test');
const assert = require('node:assert');

let repoint;
let validate;

test.before(async () => {
    repoint = await import('./js/repoint.js');
    validate = await import('./js/validate.js');
});

/**
 * Two roads and an island.
 *
 *   near (0,0) - mid (6,0) - far (14,0)        one walk component, reachable from `near`
 *   island (2,2)                               no edges at all
 */
function nav() {
    return {
        waypoints: [
            { id: 'near', map: 'Trammel', x: 0, y: 0, z: 0 },
            { id: 'mid', map: 'Trammel', x: 6, y: 0, z: 0 },
            { id: 'far', map: 'Trammel', x: 14, y: 0, z: 0 },
            { id: 'island', map: 'Trammel', x: 2, y: 2, z: 0 },
            { id: 'elsewhere', map: 'Felucca', x: 1, y: 1, z: 0 }
        ],
        edges: [
            { from: 'near', to: 'mid', kind: 'walk' },
            { from: 'mid', to: 'far', kind: 'walk' }
        ],
        destinations: [],
        arrivals: []
    };
}

function repointAt(record, extra) {
    const data = nav();

    // `home` is explicit because the default is the real graph's `uo-britain-bank`, which this
    // fixture does not contain - flooding from an id that is not there reaches nothing at all, and
    // every case below would read as stranded for a reason that has nothing to do with the rule.
    return repoint.repointFor(
        data, record, Object.assign({ map: 'Trammel', home: 'near' }, extra || {}));
}

test('the flood reaches what the edges reach, and nothing else', () => {
    const reachable = repoint.reachableFrom(nav(), 'near');

    assert.deepStrictEqual([...reachable].sort(), ['far', 'mid', 'near']);
});

test('the nearest reachable waypoint goes first', () => {
    const result = repointAt({ x: 5, y: 0, z: 0, waypoints: 'far' });

    assert.strictEqual(result.stranded, false);
    assert.strictEqual(result.changed, true);
    assert.strictEqual(result.nearest, 'mid');
    assert.strictEqual(result.tiles, 1);
    assert.strictEqual(result.waypoints, 'mid far');
});

test('a second approach still inside the cap is KEPT, not replaced', () => {
    // brit-tan-1 fronts two streets and is approached from both; the route takes whichever its
    // search reaches first. Dropping the others would quietly halve a destination's approaches.
    const result = repointAt({ x: 5, y: 0, z: 0, waypoints: 'near far' });

    assert.strictEqual(result.waypoints, 'mid near far');
});

test('a listed waypoint that has fallen outside the cap is dropped', () => {
    const result = repointAt({ x: 0, y: 0, z: 0, waypoints: 'far' });

    assert.strictEqual(result.nearest, 'near');
    assert.strictEqual(result.waypoints, 'near');
});

test('an unreachable waypoint is never chosen, however near it is', () => {
    // `island` is one tile from this record and `near` is two. Trading a long last hop for no
    // route at all is the failure the flood exists to prevent.
    const result = repointAt({ x: 2, y: 1, z: 0, waypoints: 'near' });

    assert.strictEqual(result.nearest, 'near');
    assert.strictEqual(result.waypoints, 'near');
});

test('another facet is not a candidate', () => {
    const result = repointAt({ x: 1, y: 1, z: 0, waypoints: '' }, { map: 'Felucca' });

    // `elsewhere` is on Felucca but unreachable from the Trammel flood, so nothing qualifies.
    assert.strictEqual(result.stranded, true);
    assert.strictEqual(result.nearest, null);
});

test('nothing within the cap is STRANDED, and the field is left exactly as it was', () => {
    // A repoint onto something unreachable is worse than no repoint. This is a road somebody has
    // to author, and repoint-arrivals.js has always refused it for the same reason.
    const result = repointAt({ x: 200, y: 200, z: 0, waypoints: 'far' });

    assert.strictEqual(result.stranded, true);
    assert.strictEqual(result.changed, false);
    assert.strictEqual(result.waypoints, 'far');
});

test('a home that is not in the graph is named, not reported as stranded', () => {
    // The flood from a missing id reaches a set of one, so every record would answer "nothing
    // reachable" - a sentence about the flood dressed up as a sentence about the data. This test
    // exists because the fixture above hit it first: repointFor's default home is the real graph's
    // uo-britain-bank, which no fixture has.
    const result = repoint.repointFor(nav(), { x: 5, y: 0, z: 0, waypoints: 'far' }, {
        map: 'Trammel', home: 'nosuchhome'
    });

    assert.strictEqual(result.homeMissing, 'nosuchhome');
    assert.strictEqual(result.changed, false);
    assert.strictEqual(result.waypoints, 'far');
});

test('a record already pointing at the nearest is not a change', () => {
    const result = repointAt({ x: 6, y: 0, z: 0, waypoints: 'mid' });

    assert.strictEqual(result.changed, false);
});

test('the hop cap is validate.js\'s, not a copy', () => {
    // repoint-arrivals.js used to carry a third, untested literal. coverage.test.js pins
    // validate.js's to Config/Custom.cfg; this pins repoint.js's to validate.js's.
    assert.strictEqual(repoint.HOP_CAP, validate.HOP_CAP);
});

test('farListed names the record\'s OWN approaches that are too far - the jeweler check', () => {
    // The distinction that matters: `validate.js`'s older check asks whether SOME waypoint is
    // within the cap, and for brit-home-jeweler one was, nine tiles from the new position, while
    // every waypoint the record actually named sat 223 tiles back.
    const byId = new Map(nav().waypoints.map((w) => [w.id, w]));
    const far = validate.farListed(
        { x: 0, y: 0, z: 0, waypoints: 'near far' }, 'Trammel', byId, validate.HOP_CAP);

    assert.deepStrictEqual(far, [{ id: 'far', tiles: 14 }]);
});

test('farListed ignores an id naming nothing, or naming another facet', () => {
    // Both are already their own findings, and reporting one twice under two names is what the
    // Problems panel dedupes for.
    const byId = new Map(nav().waypoints.map((w) => [w.id, w]));
    const far = validate.farListed(
        { x: 0, y: 0, z: 0, waypoints: 'nosuchthing elsewhere' }, 'Trammel', byId, validate.HOP_CAP);

    assert.deepStrictEqual(far, []);
});
