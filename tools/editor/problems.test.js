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

const NAV_WALK_AUDIT_CS = path.join(
    __dirname, '..', '..', 'Scripts', 'Custom', 'Core', 'Navigation', 'NavWalkAudit.cs');

/** One walk-audit row, in the shape the shard writes it. */
function walkRow(over) {
    return {
        kind: 'edge',
        from: 'uo-wp-174',
        to: 'uo-wp-173-s1',
        map: 'Trammel',
        pass: true,
        steps: 12,
        tiles: 11,
        pathTiles: 12,
        ratio: 1.09,
        stepRatio: 1.09,
        seconds: 3.4,
        stopX: 2026,
        stopY: 2832,
        stopZ: 20,
        rungTotal: 0,
        rungs: '',
        ...over
    };
}

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

test('the shard exports the stale-Z records themselves, not only a count', () => {
    // THE 13-VERSUS-0, AS A GUARD. The editor used to decide stale-Z for itself by comparing a
    // stored Z against the pick map's land Z, which flagged every bridge deck, every upper floor
    // and every stock spawner on a second storey - 13 of them, against the shard's 0. There is one
    // rule now and it is NavWalker.TryResolveZ's, exported by [NavAudit as `staleZRecords` and
    // consumed by shapes.setStaleZFlags.
    //
    // If the array goes away the editor badges nothing at all and says so nowhere, which is a
    // quieter regression than the one it replaced. Hence a test that reads the writer.
    const cs = fs.readFileSync(NAV_AUDIT_CS, 'utf8');

    // Matched unquoted. In the C# source the key is written `\"staleZRecords\"`, so the closing
    // quote of the JSON key is preceded by a backslash and `"staleZRecords"` is not a substring of
    // the file at all - a detail that cost a green-looking test once already.
    assert.ok(cs.includes('staleZRecords'),
        'NavAudit no longer writes a "staleZRecords" array; the z? badge would never fire');

    const start = cs.indexOf('staleZRecords');
    const emitted = [...cs.slice(start).matchAll(/\\"([a-zA-Z]+)\\":/g)].map((m) => m[1]);

    // `x` and `y` are the key shapes.js builds its set from; the rest are there so a Problems row
    // can say "stored 28, would stand at 10" without a second round trip.
    for (const field of ['x', 'y', 'z', 'resolvedZ']) {
        assert.ok(emitted.includes(field),
            `NavAudit stopped emitting "${field}" for a stale-Z record. Emitted: ${emitted.join(', ')}`);
    }
});

// --- the walk audit ----------------------------------------------------------------------------

test('only the failures become rows - a sweep is mostly passes and they are not problems', () => {
    const rows = problems.walkAuditRows({
        rows: [walkRow({}), walkRow({ pass: false, cause: 'short-of-goal', endedBy: 'teleport' })]
    });

    assert.strictEqual(rows.length, 1);
    assert.strictEqual(rows[0].kind, 'walk');
    assert.match(rows[0].reason, /short-of-goal \(teleport\)/);
});

test('a walk row jumps to where the walk DIED, not to the goal', () => {
    // The goal is already on the map as the record itself, so jumping there shows the place that
    // is fine. 2026,2832 is the tile the README's ladder-drift trail ends on, and it is the tile
    // somebody has to go and look at.
    const [row] = problems.walkAuditRows({
        rows: [walkRow({ pass: false, cause: 'short-of-goal', endedBy: 'timeout' })]
    });

    assert.strictEqual(row.x, 2026);
    assert.strictEqual(row.y, 2832);
});

test('a blocker a bot may not push is said so, on the row', () => {
    const [row] = problems.walkAuditRows({
        rows: [walkRow({
            pass: false,
            cause: 'short-of-goal',
            endedBy: 'teleport',
            occupied: 'Jeanette (npc) at 1457,1526',
            occupiedUnshovable: true
        })]
    });

    assert.match(row.reason, /Jeanette \(npc\) at 1457,1526/);
    assert.match(row.reason, /may not push/);
});

test('a sweep that has never run contributes nothing and never throws', () => {
    assert.deepStrictEqual(problems.walkAuditRows(null), []);
    assert.deepStrictEqual(problems.walkAuditRows({}), []);
    assert.strictEqual(problems.walkAuditFor(null, 'uo-wp-174'), null);
    assert.strictEqual(problems.walkAuditSummary(null), 'no walk audit yet');
});

test('the header reports the RUN, including what it cost', () => {
    // The counts describe fourteen hundred walks and the list below can only show the failures, so
    // the header is the only place the other 1447 are represented - and the only place the cost of
    // a walk audit is visible at all.
    const summary = problems.walkAuditSummary({
        status: 'done',
        skipped: 2,
        fragile: 4,
        seconds: 372.4,
        probes: 12,
        rows: [walkRow({}), walkRow({ ratio: 2.82 }), walkRow({ pass: false, cause: 'x' })]
    });

    assert.match(summary, /3 walked/);
    assert.match(summary, /1 failed/);
    assert.match(summary, /2 skipped/);
    assert.match(summary, /worst ratio 2\.82/);
    assert.match(summary, /4 passed only after rungs/);
    assert.match(summary, /372s, 12 probes/);
});

test('a running sweep says how far it has got, so it cannot look like a hang', () => {
    assert.match(
        problems.walkAuditSummary({ status: 'running', walked: 300, total: 1450, rows: [] }),
        /walking\.\.\. 300 of 1450/);
});

test("the properties line takes a record's WORST row, and a failure outranks a ratio", () => {
    const walkAudit = {
        rows: [
            walkRow({ from: 'uo-wp-174', ratio: 1.09 }),
            walkRow({ from: 'x', to: 'uo-wp-174', ratio: 3.5 }),
            walkRow({ from: 'uo-wp-174', pass: false, cause: 'short-of-goal', endedBy: 'teleport' })
        ]
    };

    assert.match(problems.walkAuditFor(walkAudit, 'uo-wp-174'), /FAILED short-of-goal/);

    // With the failure gone, the worst of the two ratios wins - not the first, and not an average.
    walkAudit.rows.pop();
    assert.match(problems.walkAuditFor(walkAudit, 'uo-wp-174'), /= 3\.50/);
});

test('an arrival row is found by its destination id as well as its approach', () => {
    const walkAudit = {
        rows: [walkRow({
            kind: 'arrival', from: 'brit-bank-0', to: undefined,
            destination: 'brit-bank', ratio: 2.0
        })]
    };

    assert.ok(problems.walkAuditFor(walkAudit, 'brit-bank'));
    assert.ok(problems.walkAuditFor(walkAudit, 'brit-bank-0'));
});

test('a pass that needed rungs says so - it is not a clean road', () => {
    const line = problems.walkAuditFor(
        { rows: [walkRow({ rungTotal: 3, rungs: 'Repath 2, Sidestep 1' })] }, 'uo-wp-174');

    assert.match(line, /after 3 rung\(s\)/);
});

test('walk rows join the same list as the audit and the badges', () => {
    const rows = problems.problemRows({
        rows: [],
        walkAudit: {
            rows: [walkRow({ pass: false, cause: 'goal-unstandable', endedBy: 'teleport' })]
        }
    });

    assert.strictEqual(rows.length, 1);
    assert.strictEqual(rows[0].kind, 'walk');
});

// --- the walk audit's drift guards -------------------------------------------------------------

test('the shard still emits the fields these rows are built from', () => {
    // THE SAME GUARD THE UNSTANDABLE ROWS HAVE, for the same reason. `cause` and `endedBy` are two
    // different questions - what was wrong with the goal, and how the walk ended - and they were
    // one field until the self-test showed that a deadline shorter than a ladder turn made every
    // hard failure read as "timeout" and named no cause at all. If either stops being written the
    // rows render "undefined" rather than failing.
    const cs = fs.readFileSync(NAV_WALK_AUDIT_CS, 'utf8');

    // Matched WITH the backslashes, and this test failed once for want of them. In the C# source
    // the key is written `\"rows\"`, so the plain string `"rows"` is not a substring of the file
    // at all - the same detail the stale-Z guard above records, met again three tests later.
    const start = cs.indexOf('\\"rows\\"');

    assert.ok(start > 0, 'NavWalkAudit no longer writes a "rows" array');

    // Matched with the backslash, as the stale-Z guard above explains: in the C# source the key is
    // written \"cause\", so "cause" with plain quotes is not a substring of the file at all.
    const emitted = [...cs.slice(start).matchAll(/\\"([a-zA-Z]+)\\":/g)].map((m) => m[1]);

    for (const field of
        ['kind', 'from', 'pass', 'steps', 'tiles', 'ratio', 'seconds', 'stopX', 'stopY', 'stopZ',
            'rungTotal', 'rungs', 'cause', 'endedBy', 'occupied', 'pathTiles', 'stepRatio']) {
        assert.ok(emitted.includes(field),
            `NavWalkAudit stopped emitting "${field}". Emitted: ${emitted.join(', ')}`);
    }
});

test("the walk audit still refuses to count towards the fleet's own instruments", () => {
    // THE PROPERTY THE WHOLE MEASUREMENT RESTS ON. A sweep is ~1450 walks in a few minutes; if a
    // probe counted, window E's failures-per-100-walks would be divided by the audit's own
    // denominator, and its failures would strike edges - a fifteen-minute cost multiplier applied
    // to the fleet's routing by the very thing that was supposed to be observing it.
    const cs = fs.readFileSync(NAV_WALK_AUDIT_CS, 'utf8');

    assert.match(cs, /Walker\.Ledger = false/,
        'the walk audit no longer sets NavWalker.Ledger = false on its probes');
});

test('a probe consents to being walked through, so it cannot appear in its own readings', () => {
    // Twelve probes in one town would otherwise refuse each other's steps exactly as two stock
    // NPCs do, and every one of those would be recorded as an occupied road.
    const cs = fs.readFileSync(NAV_WALK_AUDIT_CS, 'utf8');

    assert.match(cs, /public override bool OnMoveOver\(Mobile m\)\s*\{\s*return true;/,
        'the walk probe no longer consents to being walked through');
});

test('a probe SHOVES like a bot too, which consenting to be walked through does not buy', () => {
    // The other direction, and it was the one that falsified. BotShove keyed its mover branch on
    // PlayerBot and a probe is a plain BaseCreature, so every occupant a real bot walks through -
    // another bot, a GG shopkeeper, a daily-life patron, a stock NPC through MODIFICATIONS entry
    // 5's guard - refused the probe and cost it a rung. An instrument strictly more obstructed
    // than the thing it measures inflates exactly the FRAGILE and BUSY rows a reader acts on.
    const cs = fs.readFileSync(NAV_WALK_AUDIT_CS, 'utf8');

    assert.match(cs, /class WalkAuditProbe : BaseCreature, IBotMover/,
        'the walk probe no longer moves under the bot shove rule');
    assert.match(cs, /public override bool CheckShove\(Mobile shoved\)\s*\{\s*return true;/,
        'the walk probe no longer answers CheckShove the way uo-offline PlayerBot does');
});

// --- cliffs ------------------------------------------------------------------------------------

const cliff = (over) => ({
    waypoint: 'uo-wp-194-s1',
    neighbour: 'uo-wp-194',
    map: 'Trammel',
    x: 2036, y: 2830, z: 0,
    range: 2,
    standable: 14,
    stranded: 11,
    tileX: 2035, tileY: 2832, tileZ: 0,
    tileDistance: 2,
    ...over
});

test('a cliff row jumps to the STRANDED TILE, not to the waypoint', () => {
    // Same rule as a walk row's stop tile: the waypoint is already on the map and is the half that
    // works. The pocket is what somebody has to go and look at.
    const [row] = problems.cliffRows({ cliffsChecked: true, cliffs: [cliff({})] });

    assert.strictEqual(row.kind, 'cliff');
    assert.strictEqual(row.x, 2035);
    assert.strictEqual(row.y, 2832);
    assert.strictEqual(row.shapeId, 'uo-wp-194-s1');
    assert.match(row.reason, /11 of 14 standable approach tile\(s\) within 2/);
    assert.match(row.reason, /though the waypoint itself can/);
});

test('every stranded neighbour of one waypoint survives, because dedupe keys on kind and tile', () => {
    // A pocket is a pocket in every direction, so the usual shape is one sample tile stranding
    // several neighbours. Emitted as separate rows they would key identically in dedupe and all
    // but the first would vanish - silently, which is the worst way for a finding to go missing.
    const rows = problems.cliffRows({
        cliffsChecked: true,
        cliffs: [
            cliff({ neighbour: 'uo-wp-194' }),
            cliff({ neighbour: 'uo-wp-194-s2', stranded: 9 }),
            cliff({ neighbour: 'uo-wp-194-s1-s1', stranded: 4 })
        ]
    });

    assert.strictEqual(rows.length, 1);
    assert.match(rows[0].reason, /uo-wp-194, uo-wp-194-s2, uo-wp-194-s1-s1/);

    // And the worst pair is the one whose numbers and tile the row carries.
    assert.match(rows[0].reason, /11 of 14/);

    const deduped = problems.dedupe(rows);

    assert.strictEqual(deduped.length, 1, 'the grouped row must survive dedupe unchanged');
});

test('a default [NavAudit contributes no cliff rows, and that is not the same as none', () => {
    // Only [NavAudit full runs the scan. An absent array must never read as a clean bill of
    // health, so the flag rather than the array is what gates the rows.
    assert.deepStrictEqual(problems.cliffRows(null), []);
    assert.deepStrictEqual(problems.cliffRows({}), []);
    assert.deepStrictEqual(problems.cliffRows({ cliffs: [cliff({})] }), [],
        'cliffs without cliffsChecked must not be reported');
    assert.strictEqual(problems.cliffRows({ cliffsChecked: true, cliffs: [] }).length, 0);
});
