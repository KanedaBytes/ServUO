'use strict';

// node --test tools/editor/*.test.js
//
// validate.js is a replica of checks that live in C#, so the two things worth testing are that it
// does not fire on real data, and that each rule lands in the RIGHT TIER. The tier is the part
// that is easy to get wrong and expensive to get wrong: calling a warning fatal blocks a legal
// edit, and calling a fatal a warning tells someone their save reloaded when it did not.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const { REPO_ROOT, FILES } = require('./whitelist.js');
const { TEMPLATES } = require('./project.js');

let validate;

test.before(async () => {
    validate = await import('./js/validate.js');
});

function shipped() {
    return {
        navigation: JSON.parse(fs.readFileSync(FILES.navigation, 'utf8')),
        dailyLife: JSON.parse(fs.readFileSync(FILES.dailyLife, 'utf8')),
        restrictedZones: JSON.parse(fs.readFileSync(FILES.restrictedZones, 'utf8'))
    };
}

function baseNav() {
    return {
        schemaVersion: 1,
        costTags: [{ tag: 'road', multiplier: 0.9 }],
        waypoints: [
            { id: 'a', map: 'Trammel', x: 0, y: 0, z: 0, arrivalRange: 0, tags: 'road' },
            { id: 'b', map: 'Trammel', x: 5, y: 0, z: 0, arrivalRange: 0, tags: 'road' }
        ],
        edges: [{ from: 'a', to: 'b', kind: 'walk', tags: 'road' }],
        destinations: [
            { id: 'd', name: 'D', type: 'bank', map: 'Trammel', x: 1, y: 1, z: 0, tags: '', waypoints: 'a' }
        ],
        arrivals: [
            { destination: 'd', x: 1, y: 1, z: 0, exclusive: false, waypoints: 'a' },
            { destination: 'd', x: 2, y: 1, z: 0, exclusive: false, waypoints: 'a' }
        ],
        zones: [{ id: 'z', map: 'Trammel', x: 0, y: 0, width: 10, height: 10, tags: 'town' }],
        routes: [{ id: 'r', map: 'Trammel', mode: 'cycle', waypoints: 'a b' }],
        selfTests: []
    };
}

function baseDaily() {
    return {
        schemaVersion: 1,
        anchor: 'd',
        tavern: { destination: 'd', patronCount: 5 },
        chatter: [],
        watchChatter: [],
        watch: [{ id: 'north', route: 'r' }],
        townsfolk: [{ id: 'p', name: 'P', title: 'the courier', body: 'male', route: 'r' }],
        shopkeepers: [
            { id: 's', spawner: 'GG_S', stockSpawner: 'V#1', vendor: 'GGS', shop: 'd', home: 'd', closes: true }
        ]
    };
}

/** Runs one mutation of the fixture and returns both tiers as plain message arrays. */
function check(mutate, extra) {
    const nav = baseNav();
    const daily = baseDaily();
    const zones = { zones: [] };

    if (mutate) {
        mutate(nav, daily, zones);
    }

    const result = validate.validate(
        { navigation: nav, dailyLife: daily, restrictedZones: zones }, extra || {});

    return {
        fatal: result.fatal.map((p) => p.message),
        warnings: result.warnings.map((p) => p.message),
        // The third tier, exposed here as well as the other two so a rule that lands in the wrong
        // one fails a test rather than going quiet - which is the whole point of this file.
        notes: (result.notes || []).map((p) => p.message),
        raw: result
    };
}

test('the fixture itself is clean, or every other test below means nothing', () => {
    const { fatal, warnings } = check(null);

    assert.deepStrictEqual(fatal, []);
    assert.deepStrictEqual(warnings, []);
});

test('the shipped files produce no fatals and no warnings', () => {
    // Back to zero. This briefly expected one known warning - brit-forge reporting four exclusive
    // arrivals and no shared ones - which was the cost of making all four exclusive to stop
    // NavArrivals scattering a smith off its anvil. The `exact` flag repaid it: exactness and
    // exclusivity are separate now, so two of the four are shared again and the warning is gone.
    //
    // The shard agrees: health.json's Nav.Data check reads Ok, not Warn. If this ever fires, the
    // question is whether validate.js grew a wrong rule or the data grew a real problem - and the
    // answer is in the message, which is phrased the same way the shard phrases it.
    const { fatal, warnings } = validate.validate(shipped(), {});

    assert.deepStrictEqual(fatal.map((p) => p.message), []);
    assert.deepStrictEqual(warnings.map((p) => p.message), []);
});

// ---- the anti-drift guards ---------------------------------------------------------------------

test('the key lists here match the record templates in project.js', () => {
    // project.js is CommonJS and validate.js is a browser ES module, so neither can import the
    // other. This test is what keeps the two copies honest.
    const pairs = {
        costTags: 'costTag', waypoints: 'waypoint', edges: 'edge', destinations: 'destination',
        arrivals: 'arrival', zones: 'zone', routes: 'route',
        watch: 'watchPost', townsfolk: 'townsfolk', shopkeepers: 'shopkeeper',
        restrictedZones: 'restrictedZone'
    };

    for (const [section, template] of Object.entries(pairs)) {
        assert.deepStrictEqual(validate.KEYS[section], TEMPLATES[template].keys, section);
    }
});

test('the geometry key lists here match the record templates in project.js', () => {
    const pairs = {
        wp: 'waypoint', edge: 'edge', dest: 'destination', arr: 'arrival', zone: 'zone',
        route: 'route', costTag: 'costTag', restricted: 'restrictedZone',
        shopkeeper: 'shopkeeper', watchpost: 'watchPost', townsperson: 'townsfolk'
    };

    for (const [prefix, template] of Object.entries(pairs)) {
        assert.deepStrictEqual(
            validate.GEOMETRY_KEYS[prefix], TEMPLATES[template].geometry, prefix);
    }
});

test('the hop cap matches the shard config', () => {
    const cfg = fs.readFileSync(path.join(REPO_ROOT, 'Config', 'Custom.cfg'), 'utf8');
    const match = cfg.match(/^NavHopMaxTiles=(\d+)/m);

    assert.ok(match, 'NavHopMaxTiles is not in Config/Custom.cfg');
    assert.strictEqual(String(validate.HOP_CAP), match[1]);
});

// ---- fatal: the reload will be refused ---------------------------------------------------------

test('a duplicate id is fatal, and case does not make it a new record', () => {
    assert.match(check((nav) => nav.waypoints.push({ ...baseNav().waypoints[0], id: 'A' })).fatal.join('|'),
        /waypoints\[2\] duplicates the id 'A'/);
});

test('an id outside the allowed characters is fatal', () => {
    assert.match(check((nav) => { nav.waypoints[0].id = 'has space'; }).fatal.join('|'),
        /waypoints\[0\] has an invalid or missing id 'has space'/);
});

test('an unknown facet is fatal and lists the ones that work', () => {
    assert.match(check((nav) => { nav.zones[0].map = 'Sosaria'; }).fatal.join('|'),
        /zones\[0\] map 'Sosaria' is not a valid facet\. Valid: Felucca, Trammel/);
});

test('a zone with no area is fatal', () => {
    assert.match(check((nav) => { nav.zones[0].width = 0; }).fatal.join('|'),
        /zones\[0\] bounds must have a positive width and height \(found 0x10\)/);
});

test('an edge to itself is fatal', () => {
    assert.match(check((nav) => { nav.edges[0].to = 'a'; }).fatal.join('|'),
        /edges\[0\] links 'a' to itself/);
});

test('an unknown edge kind or route mode is fatal', () => {
    assert.match(check((nav) => { nav.edges[0].kind = 'fly'; }).fatal.join('|'),
        /edges\[0\] kind 'fly' is not 'walk' or 'gate'/);

    assert.match(check((nav) => { nav.routes[0].mode = 'loop'; }).fatal.join('|'),
        /routes\[0\] mode 'loop' is not 'cycle', 'oneway' or 'pingpong'/);
});

test('a route with one waypoint is fatal', () => {
    assert.match(check((nav) => { nav.routes[0].waypoints = 'a'; }).fatal.join('|'),
        /routes\[0\] needs at least two waypoints/);
});

test('a destination with no name or no type is fatal', () => {
    assert.match(check((nav) => { nav.destinations[0].name = ''; }).fatal.join('|'),
        /destinations\[0\] has no display name/);

    assert.match(check((nav) => { nav.destinations[0].type = ''; }).fatal.join('|'),
        /destinations\[0\] has an invalid or missing type ''/);
});

test('a negative arrival range is fatal', () => {
    assert.match(check((nav) => { nav.waypoints[0].arrivalRange = -1; }).fatal.join('|'),
        /waypoints\[0\] arrivalRange must not be negative \(found -1\)/);
});

test('an unknown key is fatal, standing in for MissingMemberHandling.Error', () => {
    // The shard's version of this is a Newtonsoft parse message with a line and a position in it.
    // This is the highest-value rule in the file for exactly that reason.
    assert.match(check((nav) => { nav.waypoints[0].noteS = 'typo'; }).fatal.join('|'),
        /waypoints\[0\] has the unknown key 'noteS'/);
});

test('a schemaVersion from the future is fatal', () => {
    assert.match(check((nav) => { nav.schemaVersion = 2; }).fatal.join('|'),
        /schemaVersion 2 is newer than this build understands \(1\)/);
});

test('a watch post with both or neither of route and destinations is fatal', () => {
    assert.match(check((nav, daily) => { daily.watch[0].destinations = 'd'; }).fatal.join('|'),
        /watch\[0\] must have exactly one of 'route' or 'destinations' \(found both\)/);

    assert.match(check((nav, daily) => { delete daily.watch[0].route; }).fatal.join('|'),
        /watch\[0\] must have exactly one of 'route' or 'destinations' \(found neither\)/);
});

test('an unknown townsfolk body is fatal', () => {
    assert.match(check((nav, daily) => { daily.townsfolk[0].body = 'other'; }).fatal.join('|'),
        /townsfolk\[0\] body 'other'; expected male, female or random/);
});

test('two shopkeepers on one spawner is fatal', () => {
    assert.match(
        check((nav, daily) => daily.shopkeepers.push({ ...baseDaily().shopkeepers[0], id: 's2' }))
            .fatal.join('|'),
        /shopkeepers\[1\] duplicates the spawner 'GG_S'/);
});

test('a negative patron count is fatal', () => {
    assert.match(check((nav, daily) => { daily.tavern.patronCount = -1; }).fatal.join('|'),
        /tavern patronCount must not be negative \(found -1\)/);
});

test('a duplicate restricted zone name is fatal', () => {
    assert.match(
        check((nav, daily, zones) => {
            zones.zones.push({ name: 'x', map: 'Trammel', x: 0, y: 0, width: 1, height: 1 });
            zones.zones.push({ name: 'X', map: 'Trammel', x: 0, y: 0, width: 1, height: 1 });
        }).fatal.join('|'),
        /zones\[1\] duplicates the name 'X'/);
});

// ---- warning: it loads, but something is wrong with it -----------------------------------------

test('a dangling edge id is a WARNING, not a rejection', () => {
    // This is the one the brief has backwards, and it is worth a test of its own. NavGraph.AddEdge
    // drops the edge and the rest of the graph loads; an editor that refused the save here would
    // be blocking an edit the shard would have accepted.
    const { fatal, warnings } = check((nav) => { nav.edges[0].to = 'ghost'; });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'), /edge 'a' -> 'ghost' dropped: no waypoint 'ghost'/);
});

test('an over-cap hop is a warning that names the cap it broke', () => {
    const { fatal, warnings } = check((nav) => { nav.waypoints[1].x = 40; });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'),
        /edge 'a' -> 'b' is 40 tiles, over the 12-tile hop cap/);
    assert.match(warnings.join('|'),
        /route 'r' leg 'a' -> 'b' is 40 tiles, over the 12-tile hop cap/);
});

test('a cycle route closing leg is checked, because it is walked like any other', () => {
    // Found by running the real shard against the real file and comparing: it reported four
    // warnings where this file reported three. A cycle walks from its last waypoint back to its
    // first; a ping-pong retraces legs it already has. NavigationSystem.CheckRoutes:518.
    const closing = (mode) => check((nav) => {
        nav.waypoints.push({ id: 'c', map: 'Trammel', x: 6, y: 0, z: 0, arrivalRange: 0, tags: '' });
        nav.edges.push({ from: 'b', to: 'c', kind: 'walk', tags: '' });
        nav.routes[0].mode = mode;
        nav.routes[0].waypoints = 'a b c';
        nav.waypoints[0].x = -80;          // a is now far from c, and adjacent to nothing else
        nav.edges[0].from = 'b';
        nav.edges[0].to = 'a';
    }).warnings.join('|');

    assert.match(closing('cycle'), /route 'r' leg 'c' -> 'a' is 86 tiles/);
    assert.doesNotMatch(closing('oneway'), /leg 'c' -> 'a'/);
    assert.doesNotMatch(closing('pingpong'), /leg 'c' -> 'a'/);
});

test('the hop cap in a message is the one that was passed in, not a literal', () => {
    const { warnings } = check((nav) => { nav.waypoints[1].x = 40; }, { hopCap: 20 });

    assert.match(warnings.join('|'), /over the 20-tile hop cap/);
});

test('a cross-facet walk edge is a warning that suggests the fix', () => {
    const { fatal, warnings } = check((nav) => { nav.waypoints[1].map = 'Felucca'; });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'),
        /walk edge 'a' -> 'b' dropped: it crosses facets \(Trammel to Felucca\)\. Use kind "gate"\./);
});

test('a destination with no arrivals is a warning', () => {
    const { fatal, warnings } = check((nav) => { nav.arrivals = []; });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'), /destination 'd' has no arrival points/);
});

test('a destination out of reach of the graph is a warning', () => {
    // 200 tiles from either waypoint, which is what moving a record without its road looks like.
    const { fatal, warnings } = check((nav) => {
        nav.destinations[0].x = 200;
        nav.arrivals.forEach((a) => { a.x = 200; });
    });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'), /destination 'd' has no arrival point within 12 tiles/);
});

test('...unless it is tagged pending-road, and then it is a note', () => {
    // THE TIER IS THE TEST. A gap the author has signed for must not sit in `warnings`, because a
    // warning that is expected for a session or two teaches everybody to skim the list, and the
    // real warning underneath goes with it. It must also not vanish: a gap nobody can see is a gap
    // nobody closes. So: out of warnings, into notes, still saying the same thing.
    const report = check((nav) => {
        nav.destinations[0].x = 200;
        nav.destinations[0].tags = 'pending-road';
        nav.arrivals.forEach((a) => { a.x = 200; });
    });

    assert.deepStrictEqual(report.fatal, []);
    assert.deepStrictEqual(report.warnings, []);
    assert.match(report.notes.join('|'),
        /destination 'd' has no arrival point within 12 tiles of a waypoint \(pending-road\)/);
});

test('an arrival for a destination that is not there is a warning', () => {
    const { fatal, warnings } = check((nav) => { nav.arrivals[0].destination = 'ghost'; });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'), /arrival at 1,1 dropped: no destination 'ghost'/);
});

test('a route naming a waypoint that is not there is a warning', () => {
    const { fatal, warnings } = check((nav) => { nav.routes[0].waypoints = 'a ghost'; });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'), /route 'r' is unusable: no waypoint 'ghost'/);
});

test('a waypoint with no edges is a warning', () => {
    const { fatal, warnings } = check((nav) => {
        nav.waypoints.push({ id: 'lonely', map: 'Trammel', x: 2, y: 2, z: 0, arrivalRange: 0, tags: '' });
    });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'), /waypoint 'lonely' has no edges/);
});

test('a guard post with nowhere for a second guard is a warning', () => {
    const { warnings } = check((nav) => { nav.arrivals[0].exclusive = true; });

    assert.match(warnings.join('|'),
        /destination 'd' has 1 exclusive arrival point\(s\) but only 1 shared one\(s\)/);
});

test('a daily life id naming nothing in the nav data is a warning', () => {
    const { fatal, warnings } = check((nav, daily) => { daily.shopkeepers[0].home = 'ghost'; });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'),
        /shopkeepers\[0\] names 'ghost', which is neither a destination, a waypoint nor a route/);
});

test('destinations in two unconnected pieces of the graph are a warning', () => {
    const { fatal, warnings } = check((nav) => {
        nav.waypoints.push({ id: 'island', map: 'Trammel', x: 100, y: 100, z: 0, arrivalRange: 0, tags: '' });
        nav.waypoints.push({ id: 'island2', map: 'Trammel', x: 101, y: 100, z: 0, arrivalRange: 0, tags: '' });
        nav.edges.push({ from: 'island', to: 'island2', kind: 'walk', tags: '' });
        nav.destinations.push({
            id: 'far', name: 'Far', type: 'bank', map: 'Trammel', x: 100, y: 100, z: 0,
            tags: '', waypoints: 'island'
        });
        nav.arrivals.push(
            { destination: 'far', x: 100, y: 100, z: 0, exclusive: false, waypoints: 'island' },
            { destination: 'far', x: 101, y: 101, z: 0, exclusive: false, waypoints: 'island' });
    });

    assert.deepStrictEqual(fatal, []);
    assert.match(warnings.join('|'),
        /Trammel destinations span 2 disconnected walk components/);
});

// ---- the shapes the browser actually holds -----------------------------------------------------

test('validating the editor shapes agrees with validating the file', async () => {
    // The browser has shapes, not a document, so this is the reduction it validates through. If it
    // drifted from what project() emits, the live preview would disagree with the save.
    const { project } = require('./project.js');
    const files = shipped();
    const shapes = project(files);

    for (const [file, key] of [['navigation', 'navigation'], ['restrictedZones', 'restrictedZones']]) {
        const rebuilt = validate.plainFromShapes(file, shapes);
        const original = files[key];

        for (const section of Object.keys(rebuilt)) {
            if (!Array.isArray(rebuilt[section])) continue;

            // The sections the editor has no shapes for come back empty, on purpose - see
            // UNPROJECTED. They are also the sections the editor cannot edit.
            if (validate.UNPROJECTED.includes(section)) {
                assert.deepStrictEqual(rebuilt[section], [], `${section} should not be projected`);
                continue;
            }

            assert.strictEqual(
                rebuilt[section].length, (original[section] || []).length,
                `${file}.${section} lost or gained records on the way back`);
        }
    }

    const result = validate.validate(
        {
            navigation: validate.plainFromShapes('navigation', shapes),
            dailyLife: validate.plainFromShapes('dailyLife', shapes),
            restrictedZones: validate.plainFromShapes('restrictedZones', shapes)
        },
        {});

    assert.deepStrictEqual(result.fatal.map((p) => p.message), []);
});

test('a finding carries the shape it belongs to, which the shard cannot tell us', () => {
    const { raw } = check((nav) => { nav.zones[0].width = 0; });
    const problem = raw.fatal.find((p) => p.where === 'zones[0]');

    assert.strictEqual(problem.shapeId, 'zone:z');
});
