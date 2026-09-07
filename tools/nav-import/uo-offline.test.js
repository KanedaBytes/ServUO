/**
 * The uo-offline conversion.
 *
 * Split deliberately: the pure conversion is driven by fixtures, and the invariants are asserted
 * against the COMMITTED reference file. The source data lives outside the repo, on one machine, so
 * a test that read it would pass or fail depending on whose checkout it was.
 */

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const uo = require('./uo-offline.js');

const REFERENCE = path.join(
    __dirname, '..', '..', 'Data', 'Custom', 'reference', 'uo-offline-nav.trammel.json');

/** Ours are `[a-z0-9-]`, checked by NavIds.IsValid at load. */
const ID = /^[a-z0-9-]+$/;

/**
 * Every `Type` their destinations.json actually contains, as censused at the pinned commit.
 *
 * Hardcoded on purpose: this is the drift guard. A re-import against a newer uo-offline that adds
 * a type must fail here rather than convert it to nothing - NavRecords rejects a destination with
 * no type, and the failure would otherwise surface a step later with nothing to say which record.
 */
const SOURCE_TYPES = [
    'Bank', 'Dock', 'Forge', 'Graveyard', 'Healer', 'Inn', 'Moongate', 'Shrine', 'Stables',
    'VendorAlchemist', 'VendorBowyer', 'VendorCarpenter', 'VendorMage', 'VendorProvisioner',
    'VendorSmith', 'VendorTailor', 'VendorWeaponer',
    'DungeonAscend', 'DungeonDescend', 'DungeonEntrance', 'DungeonRoom',
    'LumberSpot', 'MiningSpot'
];

// --- the mapping ----------------------------------------------------------------------------

test('every type their data carries is mapped or explicitly dropped', () => {
    for (const type of SOURCE_TYPES) {
        assert.ok(
            uo.TYPES[type] || uo.DROPPED_TYPES.has(type),
            `'${type}' is neither mapped nor dropped - a destination would convert with no type`);
    }
});

test('a mapped type produces a type our schema will accept', () => {
    for (const [source, mapped] of Object.entries(uo.TYPES)) {
        assert.match(mapped.type, ID, `${source} maps to an invalid type '${mapped.type}'`);
    }
});

test('a vendor becomes a shop plus its trade, not a type per trade', () => {
    // Their enum conflates category with mechanism - the comparison doc's objection to it. Ours
    // splits, so consumers filter on type and read the trade off the tags.
    assert.strictEqual(uo.TYPES.VendorSmith.type, 'shop');
    assert.ok(uo.TYPES.VendorSmith.tags.includes('smith'));
    assert.strictEqual(uo.TYPES.VendorTailor.type, 'shop');
    assert.strictEqual(uo.TYPES.Dock.type, 'dock');
});

// --- ids ------------------------------------------------------------------------------------

test('a name becomes a usable id, and a repeat gets its own', () => {
    assert.strictEqual(uo.slugify('Britain Bank'), 'britain-bank');
    assert.strictEqual(uo.slugify("Trinsic's West Gate!"), 'trinsic-s-west-gate');
    assert.strictEqual(uo.slugify('WP 157'), 'wp-157');
    assert.strictEqual(uo.slugify('---'), 'wp');

    const taken = new Set();

    assert.strictEqual(uo.uniqueId('britain-bank', taken), 'britain-bank');
    assert.strictEqual(uo.uniqueId('britain-bank', taken), 'britain-bank-2');
    assert.strictEqual(uo.uniqueId('britain-bank', taken), 'britain-bank-3');
});

// --- the region split -----------------------------------------------------------------------

test('the dungeon and Lost Lands split falls in the empty band', () => {
    // The strip is dense to y 2099 and resumes at 3000; nothing is authored between. Any cut in
    // that band gives an identical answer, which is what makes it a boundary and not a tuning knob.
    assert.ok(uo.LOSTLANDS_Y > 2099 && uo.LOSTLANDS_Y < 3000);

    assert.strictEqual(uo.regionTag(1434, 1697), '');
    assert.strictEqual(uo.regionTag(uo.DUNGEON_X - 1, 500), '');
    assert.strictEqual(uo.regionTag(uo.DUNGEON_X, 500), 'dungeon');
    assert.strictEqual(uo.regionTag(5500, 3100), 'lostlands');
});

// --- conversion -----------------------------------------------------------------------------

function fixture() {
    return {
        waypoints: [
            { Name: 'A', X: 100, Y: 100, Z: 0, Connects: ['B', 'C'] },
            { Name: 'B', X: 110, Y: 100, Z: 0, Connects: ['A'], ArrivalRange: 1 },
            { Name: 'C', X: 5200, Y: 500, Z: 0, Connects: ['A'], _note: 'in a dungeon' }
        ],
        destinations: [
            {
                Name: 'A Bank', X: 100, Y: 101, Z: 0, Type: 'Bank', City: 'Britain',
                NearestWaypoint: 'A',
                Arrivals: [{ X: 102, Y: 101, Z: 0, Waypoints: ['B'] }]
            },
            { Name: 'A Rock', X: 300, Y: 300, Z: 0, Type: 'MiningSpot', City: '', NearestWaypoint: 'A' },
            {
                Name: 'A Cave', X: 200, Y: 200, Z: 0, Type: 'DungeonEntrance', City: '',
                NearestWaypoint: 'A', Dungeon: 'Despise', Level: 1
            }
        ],
        zones: [{ Name: 'A Yard', Kind: 'Area', Points: [[0, 0], [10, 0], [10, 10]] }]
    };
}

test('an edge is written once per pair, however many times they name it', () => {
    // Theirs is embedded and symmetrised on load: A lists B and B lists A. Ours is one record per
    // pair, so the fan-out has to collapse or every edge would be authored twice.
    const { doc } = uo.convert(fixture());

    assert.strictEqual(doc.edges.length, 2);
    assert.deepStrictEqual(
        doc.edges.map((e) => `${e.from}>${e.to}`).sort(), ['uo-a>uo-b', 'uo-a>uo-c']);

    for (const edge of doc.edges) {
        assert.strictEqual(edge.kind, 'walk');
        assert.strictEqual(edge.tags, '', 'a guessed road tag would reweight the search silently');
    }
});

test('a gather spot is dropped and says so; a dungeon entrance is kept and tagged', () => {
    const { doc, report } = uo.convert(fixture());

    assert.ok(!doc.destinations.some((d) => d.name === 'A Rock'));
    assert.ok(report.unmapped.some((line) => line.includes('A Rock')));

    // The Dungeon field wins over the coordinate: an ENTRANCE sits on the overworld, and ten of
    // theirs do. A strip rule alone misses every one.
    const cave = doc.destinations.find((d) => d.name === 'A Cave');

    assert.ok(cave.x < uo.DUNGEON_X);
    assert.ok(cave.tags.split(' ').includes('dungeon'));
});

test('arrivals flatten out of the destination and keep their approach waypoints', () => {
    const { doc } = uo.convert(fixture());
    const arrival = doc.arrivals.find((a) => a.destination === 'uo-a-bank');

    assert.ok(arrival, 'the arrival did not survive the flattening');
    assert.strictEqual(arrival.exclusive, false);
    assert.deepStrictEqual(arrival.waypoints.split(' ').sort(), ['uo-a', 'uo-b']);
});

test('a polygon becomes a poly zone with a bounding box our schema can reject on', () => {
    const { doc } = uo.convert(fixture());
    const zone = doc.zones.find((z) => z.id === 'uo-a-yard');

    assert.strictEqual(zone.shape, 'poly');
    assert.strictEqual(zone.points, '0,0 10,0 10,10');
    assert.deepStrictEqual(
        [zone.x, zone.y, zone.width, zone.height], [0, 0, 11, 11]);
});

test('the facet is stamped, because their records carry none', () => {
    const { doc } = uo.convert(fixture(), { map: 'Felucca' });

    assert.ok(doc.waypoints.every((w) => w.map === 'Felucca'));
    assert.ok(doc.destinations.every((d) => d.map === 'Felucca'));
});

test('every reference id is namespaced, so it cannot collide with an authored one', () => {
    // The reason this exists: their waypoints are named `WP 157`, which slugifies to `wp-157`,
    // and `wp-<n>` is exactly what our corridor tool mints. Before the prefix, 23 reference ids
    // collided with ids already in navigation.json - including `wp-1`, ours on the west road at
    // 1381,1750 and theirs a different tile at 1564,1687.
    const { doc } = uo.convert(fixture());

    for (const key of ['waypoints', 'destinations', 'zones']) {
        for (const record of doc[key]) {
            assert.ok(record.id.startsWith(uo.PREFIX), `${record.id} is not namespaced`);
        }
    }
});

// --- the committed reference file ------------------------------------------------------------

test('no reference id collides with one already in navigation.json', () => {
    // The guard that makes "Adopt never overwrites an authored record" structural rather than a
    // check somebody has to remember to write.
    const doc = JSON.parse(fs.readFileSync(REFERENCE, 'utf8'));
    const ours = JSON.parse(fs.readFileSync(
        path.join(__dirname, '..', '..', 'Data', 'Custom', 'navigation.json'), 'utf8'));

    const authored = new Set();

    for (const key of ['waypoints', 'destinations', 'zones']) {
        for (const record of ours[key]) {
            authored.add(record.id);
        }
    }

    const clash = [];

    for (const key of ['waypoints', 'destinations', 'zones']) {
        for (const record of doc[key]) {
            if (authored.has(record.id)) {
                clash.push(record.id);
            }
        }
    }

    assert.deepStrictEqual(clash, [], 'adopting one of these would overwrite authored data');
});

test('the shipped reference file is well formed and internally consistent', () => {
    const raw = fs.readFileSync(REFERENCE);

    // LF, like every other Data/Custom file. CRLF here would be a silent diff on every checkout.
    assert.ok(!raw.includes('\r\n'), 'the reference file has CRLF line endings');

    const doc = JSON.parse(raw.toString('utf8'));
    const ids = new Set();

    for (const key of ['waypoints', 'destinations', 'zones']) {
        for (const record of doc[key]) {
            assert.match(record.id, ID, `${key} id '${record.id}' is not a valid nav id`);
            assert.ok(!ids.has(record.id), `id '${record.id}' is used twice`);
            ids.add(record.id);
        }
    }

    // Nav.TryRoute resolves an id that may name a waypoint OR a destination, so uniqueness has to
    // hold across kinds and not merely within one.
    assert.strictEqual(
        ids.size, doc.waypoints.length + doc.destinations.length + doc.zones.length);

    for (const edge of doc.edges) {
        assert.ok(ids.has(edge.from), `edge names unknown waypoint '${edge.from}'`);
        assert.ok(ids.has(edge.to), `edge names unknown waypoint '${edge.to}'`);
        assert.notStrictEqual(edge.from, edge.to, 'an edge joins a waypoint to itself');
    }

    const destinations = new Set(doc.destinations.map((d) => d.id));

    for (const arrival of doc.arrivals) {
        assert.ok(destinations.has(arrival.destination),
            `arrival names unknown destination '${arrival.destination}'`);
    }
});

test('the shipped reference file matches the census it was converted from', () => {
    const doc = JSON.parse(fs.readFileSync(REFERENCE, 'utf8'));
    const tag = (name) => doc.waypoints.filter((w) => w.tags === name).length;

    // Measured at the pinned commit. A re-import that moves these has either taken new upstream
    // data or changed the split, and both are worth stopping for.
    assert.strictEqual(doc.waypoints.length, 3952);
    assert.strictEqual(doc.edges.length, 4291);
    assert.strictEqual(doc.destinations.length, 485, '488 less the three gather spots');
    assert.strictEqual(tag('dungeon'), 1988);
    assert.strictEqual(tag('lostlands'), 43);
    assert.strictEqual(tag(''), 1921);

    // The reason the whole Adopt step exists, asserted rather than asserted-about. Their legs are
    // authored against a 38-tile cap; ours is 12, because ServUO's FastAStarAlgorithm searches a
    // 38-tile box centred on the MIDPOINT and a detour leaves it. More than half of these edges
    // cannot be handed to our walker as they stand, which is why Adopt re-walks and subdivides.
    const at = new Map(doc.waypoints.map((w) => [w.id, w]));
    const over = doc.edges.filter((edge) => {
        const a = at.get(edge.from);
        const b = at.get(edge.to);

        return Math.max(Math.abs(a.x - b.x), Math.abs(a.y - b.y)) > 12;
    }).length;

    assert.strictEqual(over, 2405);
    assert.ok(over / doc.edges.length > 0.5, 'the over-cap majority is why Adopt walks every edge');
});

test('the reference file is one record per line, not one scalar per line', () => {
    // Their format pays ~30 lines per waypoint; ours pays one. At 3952 waypoints that is the
    // difference between a 9k-line file and a 120k-line one.
    const lines = fs.readFileSync(REFERENCE, 'utf8').split('\n');

    assert.ok(lines.length < 12000, `${lines.length} lines - the compact layout was lost`);

    const first = lines.find((line) => line.trim().startsWith('{"id"'));

    assert.ok(first, 'no record found on a line of its own');
    assert.doesNotThrow(() => JSON.parse(first.trim().replace(/,$/, '')));
});
