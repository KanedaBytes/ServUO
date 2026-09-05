'use strict';

// node --test tools/editor/*.test.js
//
// compact.js can now build records, not just edit them, and a built record has to come out looking
// exactly like one JsonConfig.SerializeCompact wrote - same key order, same one-line layout, same
// integer/float tokens. The golden is the only thing that knows what that looks like, so these
// tests rebuild real records out of the golden's own values and demand the bytes back.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const compact = require('./compact.js');
const { REPO_ROOT } = require('./whitelist.js');

const GOLDEN = path.join(REPO_ROOT, 'Data', 'Custom', 'golden', 'navigation.golden.json');

function goldenRoot() {
    return compact.parse(fs.readFileSync(GOLDEN, 'utf8'));
}

/** The exact source line for the first record of a section, without its indent or comma. */
function firstRecordLine(section) {
    const text = fs.readFileSync(GOLDEN, 'utf8');
    const lines = text.split('\n');
    const start = lines.findIndex((line) => line.trim() === `"${section}": [`);

    assert.notStrictEqual(start, -1, `no section "${section}" in the golden`);

    return lines[start + 1].trim().replace(/,$/, '');
}

test('a rebuilt record is byte-identical to the one the C# writer produced', () => {
    for (const section of ['waypoints', 'edges', 'destinations', 'arrivals', 'zones', 'routes']) {
        const record = compact.get(goldenRoot(), section).items[0];
        const rebuilt = compact.object(Object.entries(compact.plain(record)));

        assert.strictEqual(
            compact.stringify(rebuilt).trimEnd(),
            firstRecordLine(section),
            `${section} does not rebuild`);
    }
});

test('key order is the order given, not the order of the values', () => {
    const record = compact.object([['z', 1], ['a', 2], ['m', 3]]);

    assert.deepStrictEqual(compact.keys(record), ['z', 'a', 'm']);
    assert.strictEqual(compact.stringify(record).trimEnd(), '{"z":1,"a":2,"m":3}');
});

test('a created float stays a float, and so does one that is edited later', () => {
    // costTags.multiplier is the field this exists for: Newtonsoft writes 3.0, JSON.stringify
    // writes 3, and the difference is a whole-file diff on the first save.
    const record = compact.object([['tag', 'road'], ['multiplier', compact.scalar(3, { float: true })]]);

    assert.strictEqual(compact.stringify(record).trimEnd(), '{"tag":"road","multiplier":3.0}');

    compact.set(record, 'multiplier', 4);

    assert.strictEqual(compact.stringify(record).trimEnd(), '{"tag":"road","multiplier":4.0}');
});

test('set inserts a new key where it is told, not merely at the end', () => {
    const record = compact.object([['id', 'a'], ['x', 1]]);

    compact.set(record, 'map', 'Trammel', { after: 'id' });
    compact.set(record, 'note', 'why');
    compact.set(record, 'first', true, { before: 'id' });

    assert.deepStrictEqual(compact.keys(record), ['first', 'id', 'map', 'x', 'note']);
});

test('set on an existing key updates it rather than adding a second one', () => {
    const record = compact.object([['id', 'a'], ['x', 1]]);

    compact.set(record, 'x', 9);

    assert.deepStrictEqual(compact.keys(record), ['id', 'x']);
    assert.strictEqual(compact.stringify(record).trimEnd(), '{"id":"a","x":9}');
});

test('remove takes exactly the named key and leaves the layout valid', () => {
    const record = compact.object([['a', 1], ['b', 2], ['c', 3]]);

    assert.strictEqual(compact.remove(record, 'b'), true);
    assert.strictEqual(compact.remove(record, 'nope'), false);
    assert.strictEqual(compact.stringify(record).trimEnd(), '{"a":1,"c":3}');
});

test('pushing an object into an empty array flips it from inline to expanded', () => {
    // restricted-zones.json ships as {"zones": []}, so this is literally its first save.
    const root = compact.parse('{\n  "zones": []\n}\n');

    assert.strictEqual(compact.stringify(root), '{\n  "zones": []\n}\n');

    compact.push(compact.get(root, 'zones'), compact.object([['name', 'a'], ['x', 1]]));

    assert.strictEqual(
        compact.stringify(root),
        '{\n  "zones": [\n    {"name":"a","x":1}\n  ]\n}\n');
});

test('removing the last item returns the array to its inline empty form', () => {
    const root = compact.parse('{\n  "zones": [\n    {"name":"a"}\n  ]\n}\n');
    const zones = compact.get(root, 'zones');
    const removed = compact.removeAt(zones, 0);

    assert.strictEqual(compact.plain(removed).name, 'a');
    assert.strictEqual(compact.stringify(root), '{\n  "zones": []\n}\n');
});

test('a record is deleted by identity, so an earlier delete cannot renumber a later one', () => {
    const root = compact.parse('{"a":[{"i":0},{"i":1},{"i":2}]}');
    const items = compact.get(root, 'a');
    const second = items.items[1];
    const third = items.items[2];

    compact.removeAt(items, compact.indexOfNode(items, second));
    compact.removeAt(items, compact.indexOfNode(items, third));

    // By index, removing 1 then 2 would take the wrong second record: the array renumbers under it.
    assert.deepStrictEqual(compact.plain(items), [{ i: 0 }]);
});

test('nothing built here writes a null', () => {
    // A null in a data file is a load error the shard reports as a Newtonsoft message. The rule is
    // that an absent value means an omitted key, so undefined has to be refused at the door.
    assert.throws(() => compact.scalar(undefined), /omit the key/);
    assert.throws(() => compact.object([['a', undefined]]), /omit the key/);
});

test('empty containers round-trip unchanged', () => {
    // An empty object or array is not a scalar, so a container holding one is expanded - which is
    // what IsFlat does in C# too, since JObject is not a JValue.
    for (const text of ['{}\n', '[]\n', '{\n  "a": {},\n  "b": []\n}\n']) {
        assert.strictEqual(compact.stringify(compact.parse(text)), text);
    }
});
