/**
 * The Problems panel's list.
 *
 * Tested against the module rather than the rendered DOM, for the reason audit.js's header gives:
 * the formatting it replaced lived inside a click handler and was wrong for a year with nothing
 * able to catch it. Everything here is pure, so everything here is testable.
 *
 * The drift guard at the bottom reads the C# back, the way audit.test.js reads NavAuditKind: the
 * fields NavAudit's snapshot writer emits for an unstandable arrival are this list's whole
 * vocabulary, and a field added there and forgotten here would silently render as a row with no
 * reason in it.
 */

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

let problems;

test.before(async () => {
    problems = await import('./js/problems.js');
});

const NAV_AUDIT_CS = path.join(
    __dirname, '..', '..', 'Scripts', 'Custom', 'Core', 'Navigation', 'NavAudit.cs');

const NAV_RESAMPLE_CS = path.join(
    __dirname, '..', '..', 'Scripts', 'Custom', 'Core', 'Navigation', 'NavResampleZ.cs');

// --- the sources -------------------------------------------------------------------------------

test('an unstandable arrival keeps the shard\'s own reason, verbatim', () => {
    const reason = "arrival 'brit-bank' 1432,1692 z 0 (land 0, solid - nothing fits here at all)"
        + " - blocked by 0x0034 'brick wall'; nearest standable 1431,1691,0 (1 tile away)";

    const [row] = problems.unstandableRows([
        { destination: 'brit-bank', x: 1432, y: 1692, z: 0, landZ: 0, reason }
    ]);

    // Not re-derived here. The blocker's name and the nearest standable tile are facts the shard
    // measured against the real map, and this layer has no map.
    assert.strictEqual(row.reason, reason);
    assert.strictEqual(row.x, 1432);
    assert.strictEqual(row.y, 1692);
    assert.strictEqual(row.kind, 'unstandable');
});

test('an older snapshot with no reason still gets a sentence rather than an empty row', () => {
    const [row] = problems.unstandableRows([
        { destination: 'brit-square', x: 1475, y: 1641, z: 20, landZ: 20 }
    ]);

    assert.ok(row.reason.includes('1475,1641'), row.reason);
    assert.ok(row.reason.includes('z 20'), row.reason);
});

test('only a blocked edge is fatal; occupancy is a note and over-cap a warning', () => {
    const rows = problems.edgeRows([
        { from: 'a', to: 'b', blocked: true, kind: 'blocked', distance: 7 },
        { from: 'c', to: 'd', blocked: false, kind: 'far', distance: 19 },
        { from: 'e', to: 'f', blocked: false, kind: 'occupied', distance: 8, detail: 'Sam at (1,2)' }
    ]);

    assert.deepStrictEqual(rows.map((r) => r.severity), ['fatal', 'warning', 'note']);

    // Occupancy is a fact about right now - the shard's own contract calls it a warning and never
    // fails the audit on it, so it must not shout here either.
    assert.strictEqual(rows[2].reason, 'Sam at (1,2)');

    // An edge has two ends and therefore no tile to jump to.
    assert.strictEqual(rows[0].x, null);
});

test('a pending-road note is listed, and is not a warning', () => {
    const rows = problems.validationRows({
        fatal: [],
        warnings: [{ severity: 'warning', message: 'w', shapeId: 's1' }],
        notes: [{ severity: 'note', message: 'brit-home-baker (pending-road)', shapeId: 's2' }]
    });

    const note = rows.find((row) => row.severity === 'note');

    assert.ok(note, 'the note tier must survive into the list');
    assert.strictEqual(note.kind, 'pending');
    assert.strictEqual(note.shapeId, 's2');
});

test('badges become rows carrying the record id, not just a coordinate', () => {
    const shape = { id: 'brit-tink-1', kind: 'point', points: [[1420, 1650, 11]] };

    const rows = problems.badgeRows([{ shape, unstandable: false, staleZ: true }]);

    assert.strictEqual(rows.length, 1);
    assert.strictEqual(rows[0].kind, 'stale-z');
    assert.strictEqual(rows[0].shapeId, 'brit-tink-1');
    assert.ok(rows[0].reason.includes('11'), rows[0].reason);
});

// --- the whole list ----------------------------------------------------------------------------

test('the audit\'s row wins over the badge for the same tile', () => {
    // Both sources report the same arrival: the audit by coordinate, the badge by record. Listing
    // it twice would make twenty arrivals read as forty, and the audit's row is the one carrying
    // the blocker's name.
    const shape = { id: 'arr', kind: 'point', points: [[1432, 1692, 0]] };

    const rows = problems.problemRows({
        audit: {
            unstandable: [{ destination: 'brit-bank', x: 1432, y: 1692, z: 0, landZ: 0, reason: 'brick wall' }],
            problems: []
        },
        flagged: [{ shape, unstandable: true, staleZ: false }],
        shapeAt: () => 'arr'
    });

    const unstandable = rows.filter((row) => row.kind === 'unstandable');

    assert.strictEqual(unstandable.length, 1);
    assert.strictEqual(unstandable[0].reason, 'brick wall');
});

test('worst first, and stable within a tier', () => {
    const rows = problems.problemRows({
        audit: {
            unstandable: [],
            problems: [
                { from: 'a', to: 'b', blocked: false, kind: 'occupied', distance: 1, detail: 'x' },
                { from: 'c', to: 'd', blocked: true, kind: 'blocked', distance: 2 },
                { from: 'e', to: 'f', blocked: false, kind: 'far', distance: 3 },
                { from: 'g', to: 'h', blocked: false, kind: 'far', distance: 4 }
            ]
        }
    });

    assert.deepStrictEqual(rows.map((r) => r.severity), ['fatal', 'warning', 'warning', 'note']);

    // Graph order inside a tier, because that is the order somebody walking the map meets them in.
    assert.deepStrictEqual(rows.slice(1, 3).map((r) => r.label), ['e -> f', 'g -> h']);
});

test('every source is independently absent without throwing', () => {
    // Each of these is genuinely missing in a real session: the audit has not been run, the shard
    // is down, the landz batch has not arrived.
    assert.deepStrictEqual(problems.problemRows(), []);
    assert.deepStrictEqual(problems.problemRows({}), []);
    assert.deepStrictEqual(problems.problemRows({ audit: {} }), []);
    assert.deepStrictEqual(problems.problemRows({ audit: { unstandable: null, problems: null } }), []);
    assert.deepStrictEqual(problems.problemRows({ validation: { fatal: [], warnings: [], notes: [] } }), []);
    assert.deepStrictEqual(problems.problemRows({ flagged: [{ shape: null }] }), []);
});

test('the summary counts by kind and says so when there is nothing', () => {
    assert.strictEqual(problems.problemSummary([]), 'nothing flagged');
    assert.strictEqual(
        problems.problemSummary([{ kind: 'unstandable' }, { kind: 'unstandable' }, { kind: 'far' }]),
        '2 unstandable, 1 far');
});

// --- the drift guard ---------------------------------------------------------------------------

test('every field the shard emits for an unstandable arrival is one this list knows about', () => {
    const cs = fs.readFileSync(NAV_AUDIT_CS, 'utf8');

    // The unstandable block in WriteSnapshot, from its opening to the closing of the array.
    const start = cs.indexOf('"unstandable"');

    assert.ok(start > 0, 'NavAudit no longer writes an "unstandable" array');

    const block = cs.slice(start, cs.indexOf('"staleZ"'));
    const emitted = [...block.matchAll(/\\"([a-zA-Z]+)\\":/g)].map((m) => m[1]);

    assert.ok(emitted.length > 0, 'could not read the emitted field names out of NavAudit.cs');

    // `reason` is the one this list actually renders; the rest are the structured half, kept so a
    // future row can use them without another round trip to the shard. If the shard stops emitting
    // `reason` the rows silently fall back to a generated sentence, which is a quiet regression.
    assert.ok(emitted.includes('reason'),
        `NavAudit stopped emitting "reason"; problems.js would fall back to a generated sentence. Emitted: ${emitted.join(', ')}`);
});

test('the shard still measures a nearest standable tile, which is what makes a row actionable', () => {
    const cs = fs.readFileSync(NAV_RESAMPLE_CS, 'utf8');

    assert.ok(cs.includes('NearestDistance'),
        'NavResampleZ.Stranded no longer reports a nearest standable tile');
    assert.ok(cs.includes('BlockerName'),
        'NavResampleZ.Stranded no longer names the blocking static');
});
