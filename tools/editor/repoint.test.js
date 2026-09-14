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

// ---------------------------------------------------------------------------------------------
// ROUTABILITY IS THE GATE, DISTANCE IS THE TIEBREAK.
//
// The shape below is brit-inn-central's, reduced to what made it wrong: a road waypoint two tiles
// from the destination's CENTRE that cannot reach its arrival, and a door waypoint eight tiles from
// that centre that can. Judged by centre distance the road wins and the record is left naming a
// waypoint no bot can use; judged by routes to the arrivals the door wins, which is the answer.

/**
 * A sealed centre.
 *
 *   road (0,0) --- door (0,10)          one component, flooded from `road`
 *   inn centre (0,2)                    2 tiles from road, 8 from door
 *   inn arrival (0,8)                   8 tiles from road, 2 from door - both inside the cap
 *
 * Both waypoints pass the cap for both records, so the cap cannot separate them. Only the oracle
 * can, which is the point: this is the case the old rule got wrong with every instrument green.
 */
function sealed() {
    return {
        waypoints: [
            { id: 'road', map: 'Trammel', x: 0, y: 0, z: 0 },
            { id: 'door', map: 'Trammel', x: 0, y: 10, z: 0 }
        ],
        edges: [{ from: 'road', to: 'door', kind: 'walk' }],
        destinations: [
            { id: 'inn', map: 'Trammel', x: 0, y: 2, z: 0, waypoints: 'road' }
        ],
        arrivals: [
            { destination: 'inn', x: 0, y: 8, z: 0, waypoints: 'road' }
        ]
    };
}

/** An oracle over tiles: every pair not named answers false. `null` is for "would not say". */
function oracle(allowed) {
    const ok = new Set(allowed);

    return (a, b) => ok.has(`${a.x},${a.y}>${b.x},${b.y}`);
}

function repointInn(data, extra) {
    return repoint.repointFor(
        data, data.destinations[0],
        Object.assign({ map: 'Trammel', home: 'road' }, extra || {}));
}

test('a waypoint by a sealed centre is refused when it cannot route to the arrivals', () => {
    // `road` is the nearest thing to the centre and the record already names it. It reaches the
    // centre and nothing else, which is exactly what a centre inside a building is worth.
    const data = sealed();
    const result = repointInn(data, {
        routes: oracle(['0,10>0,8', '0,8>0,10'])
    });

    assert.strictEqual(result.stranded, false);
    assert.strictEqual(result.nearest, 'door');
    assert.strictEqual(result.waypoints, 'door');
    assert.strictEqual(result.verified, true);
    assert.strictEqual(result.changed, true);
});

test('the nearest waypoint is still chosen when it does route to every arrival', () => {
    // The gate is not a preference for far waypoints. With both approaches routable the nearest
    // wins, which is the rule the tool has always had - now standing on an answer rather than an
    // assumption.
    const data = sealed();
    const result = repointInn(data, {
        routes: oracle(['0,0>0,8', '0,8>0,0', '0,10>0,8', '0,8>0,10'])
    });

    assert.strictEqual(result.nearest, 'road');
    assert.strictEqual(result.verified, true);
    assert.strictEqual(result.changed, false, 'it already named road, so nothing moved');
});

test('distance orders the survivors, and only the survivors', () => {
    // Three candidates. The nearest cannot route, so it is out; of the two that can, the nearer
    // one wins. That is the whole of "a tiebreaker, not a gate".
    const data = sealed();

    data.waypoints.push({ id: 'lane', map: 'Trammel', x: 0, y: 14, z: 0 });
    data.edges.push({ from: 'door', to: 'lane', kind: 'walk' });

    const result = repointInn(data, {
        routes: oracle([
            '0,10>0,8', '0,8>0,10',   // door,  8 tiles from the centre
            '0,14>0,8', '0,8>0,14'    // lane, 12 tiles from the centre, also routable
        ])
    });

    assert.strictEqual(result.nearest, 'door', 'door is nearer to the centre than lane');
    assert.strictEqual(result.waypoints, 'door', 'lane was never listed, so it is not kept');
});

test('with no oracle the rule falls back to distance and marks itself unverified', () => {
    // The editor has to keep working with the shard down, and this is what that costs: the old
    // answer, labelled. A caller that writes files refuses on it; repoint-arrivals.js does.
    const result = repointInn(sealed());

    assert.strictEqual(result.nearest, 'road', 'distance alone still prefers the sealed centre');
    assert.strictEqual(result.verified, false);
});

test('an oracle that will not answer is undecided, not a refusal', () => {
    // null means nobody asked the engine, which must not read as "no route" - that would strand
    // every record the moment a probe timed out.
    const result = repointInn(sealed(), { routes: () => null });

    assert.strictEqual(result.stranded, false);
    assert.strictEqual(result.nearest, 'road');
    assert.strictEqual(result.verified, false);
});

test('the CLI asks the engine on the bot class, at the stock budget, through nav-hop-probe', () => {
    // A source assertion rather than a behavioural one, in this file's neighbours' style
    // (coverage.test.js pins HOP_CAP the same way): the thing worth pinning is WHICH question the
    // CLI asks, and running it for real would need a shard.
    //
    // Three claims, each of which has a reason to be wrong later:
    //   - `bot`, because a creature walks over the movable impassables a bot goes round, which is
    //     297 expansions against 301 on the provisioner hop;
    //   - no `budget` word, because the fleet plans at the stock 300 and a probe at any other
    //     number answers about a shard nobody runs;
    //   - `nav-hop-probe` and not `nav-hop`, because nav-hop goes through MovementPath, which
    //     fails any goal within one tile - the best approach a record could have.
    const source = require('node:fs').readFileSync(
        require('node:path').join(__dirname, 'repoint-arrivals.js'), 'utf8');

    assert.match(source, /\$\{tileKey\(a\)\} \$\{tileKey\(b\)\}`\)\.join\(' '\)\} bot`/,
        'the probe body must end in the bot class');
    assert.ok(!/budget \$\{/.test(source) && !/ budget \d/.test(source),
        'no budget word: stock is what the fleet plans at');
    assert.match(source, /ask\(port, 'nav-hop-probe'/,
        'nav-hop cannot answer this - see the header');
});
