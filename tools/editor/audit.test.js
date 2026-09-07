/**
 * The audit banner's vocabulary.
 *
 * There was no test here at all, which is how "over cap" came to be printed against every
 * occupied edge for as long as occupied edges have existed. The banner read a boolean and the
 * shard had been emitting three kinds since the mobile-aware pass landed.
 */

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

let audit;

test.before(async () => {
    audit = await import('./js/audit.js');
});

const NAV_AUDIT_CS = path.join(
    __dirname, '..', '..', 'Scripts', 'Custom', 'Core', 'Navigation', 'NavAudit.cs');

test('an occupied edge is called occupied, and names what is standing on it', () => {
    const line = audit.auditLine({
        from: 'brit-plaza-9', to: 'brit-bake-1', blocked: false, kind: 'occupied', distance: 8,
        detail: 'Casta (GGBaker) is standing at (1453, 1616)'
    });

    assert.strictEqual(audit.auditLabel({ kind: 'occupied', blocked: false }), 'occupied');

    // The old line called this "over cap" against a 12-tile cap, on an 8-tile edge.
    assert.ok(!line.includes('over cap'), line);

    // The detail is the whole value of the finding and used to be discarded.
    assert.ok(line.includes('Casta (GGBaker) is standing at (1453, 1616)'), line);

    // The distance is near-noise for an occupied edge.
    assert.ok(!line.includes('8 tiles'), line);
});

test('over cap means the hop cap, which is the far kind', () => {
    assert.strictEqual(audit.auditLabel({ kind: 'far', blocked: false }), 'over cap');

    const line = audit.auditLine({
        from: 'town-10', to: 'town-11', blocked: false, kind: 'far', distance: 13
    });

    assert.ok(line.includes('over cap'), line);
    assert.ok(line.includes('13 tiles'), line);
});

test('a blocked edge is the only one that fails the audit', () => {
    const blocked = { from: 'a', to: 'b', blocked: true, kind: 'blocked', distance: 4 };
    const occupied = { from: 'c', to: 'd', blocked: false, kind: 'occupied', distance: 8 };

    assert.strictEqual(audit.auditLabel(blocked), 'BLOCKED');

    // NavAudit.TryRun returns `blocked == 0`, so an all-occupied audit is a pass and the status
    // bar must not go red for it.
    assert.strictEqual(audit.hasBlocked([occupied, occupied]), false);
    assert.strictEqual(audit.hasBlocked([occupied, blocked]), true);
    assert.strictEqual(audit.hasBlocked([]), false);
});

test('a snapshot with no kind still says something true', () => {
    // Older nav-audit.json files pre-date the kind field.
    assert.strictEqual(audit.auditLabel({ blocked: true }), 'BLOCKED');
    assert.strictEqual(audit.auditLabel({ blocked: false }), 'problem');
    assert.strictEqual(audit.auditLabel(null), '?');
});

test('every kind NavAudit emits has a label here', () => {
    // The drift guard. Adding a fourth kind to the C# enum without a label would otherwise show
    // up as the enum name leaking into the banner, which is exactly how this bug read.
    const source = fs.readFileSync(NAV_AUDIT_CS, 'utf8');
    const body = source.slice(source.indexOf('enum NavAuditKind'));
    const block = body.slice(body.indexOf('{') + 1, body.indexOf('}'));

    // Comments first, then split: the enum's own comments contain commas.
    const kinds = block
        .replace(/\/\/.*$/gm, '')
        .split(',')
        .map((entry) => entry.split('=')[0].trim())
        .filter((entry) => /^[A-Za-z]+$/.test(entry))
        .map((entry) => entry.toLowerCase());

    assert.ok(kinds.length >= 3, `parsed no kinds out of NavAudit.cs: ${kinds}`);

    for (const kind of kinds) {
        assert.ok(audit.AUDIT_LABELS[kind],
            `NavAudit emits '${kind}' and audit.js has no label for it`);
    }

    assert.deepStrictEqual(
        Object.keys(audit.AUDIT_LABELS).sort(), [...kinds].sort(),
        'audit.js and NavAuditKind have drifted apart');
});
