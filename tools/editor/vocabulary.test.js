'use strict';

// node --test tools/editor/*.test.js
//
// The vocabulary: half the shard's report of what it loaded, half derived from the files here.
//
// What is worth asserting is the SEAM, not the arithmetic. Three things can go wrong and all
// three are silent:
//
//   1. The shard is down and the whole thing refuses instead of degrading. A create form that
//      will not open without the shard is a worse editor than one with free-text fields.
//   2. The folder scan classifies a vendor. It must not get a say - BaseVendor is an exact answer
//      from the type chain and a folder is a guess, so a vendor that happens to live under
//      Mobiles/NPCs must still come back as a Vendor and not an NPC.
//   3. A type the shard loaded that the scan never saw disappears. It has to land somewhere and
//      be counted, because creature subclasses live outside Scripts/Mobiles and there are a lot
//      of them.
//
// Runs against a temp tree via GG_EDITOR_ROOT, like bridge.test.js and for the same reason.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');

const SOURCE_ROOT = path.resolve(__dirname, '..', '..');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'gg-vocab-'));

fs.mkdirSync(path.join(root, 'Data', 'Custom'), { recursive: true });
fs.mkdirSync(path.join(root, 'Data', 'Live'), { recursive: true });
fs.mkdirSync(path.join(root, 'Spawns', 'Custom', 'trammel'), { recursive: true });
fs.mkdirSync(path.join(root, 'Scripts', 'Mobiles', 'NPCs'), { recursive: true });
fs.mkdirSync(path.join(root, 'Scripts', 'Mobiles', 'Normal'), { recursive: true });

for (const name of ['navigation.json', 'bots.json']) {
    fs.copyFileSync(
        path.join(SOURCE_ROOT, 'Data', 'Custom', name), path.join(root, 'Data', 'Custom', name));
}

fs.copyFileSync(
    path.join(SOURCE_ROOT, 'Spawns', 'Custom', 'trammel', 'GG_DailyLife.xml'),
    path.join(root, 'Spawns', 'Custom', 'trammel', 'GG_DailyLife.xml'));

// A miniature Scripts/Mobiles. Alchemist is under NPCs and IS a vendor, which is the case that
// decides whether the folder scan is allowed to override the type chain.
write('Scripts/Mobiles/NPCs/Alchemist.cs', 'public class Alchemist : BaseVendor { }');
write('Scripts/Mobiles/NPCs/Aeluva.cs', 'public class Aeluva : BaseCreature { }');
write('Scripts/Mobiles/Normal/Balron.cs', 'public class Balron : BaseCreature { }');
write('Scripts/Mobiles/Normal/Alligator.cs', 'public class Alligator : BaseCreature { }');

// The name-and-file mismatch ServUO is full of: the class is not what the file is called, which is
// why the scan reads contents rather than trusting file names.
write('Scripts/Mobiles/NPCs/AlelleTheAborist.cs', 'public sealed class Alelle : BaseCreature { }');

function write(relative, text) {
    const file = path.join(root, relative);

    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text, 'utf8');
}

function writeShardReport(report) {
    fs.writeFileSync(
        path.join(root, 'Data', 'Live', 'vocabulary.json'), JSON.stringify(report), 'utf8');
}

function clearShardReport() {
    fs.rmSync(path.join(root, 'Data', 'Live', 'vocabulary.json'), { force: true });
}

process.env.GG_EDITOR_ROOT = root;

const vocabulary = require('./vocabulary.js');

function readJson(file) {
    try {
        return JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch (error) {
        if (error.code === 'ENOENT') {
            return null;
        }

        throw error;
    }
}

const FULL_REPORT = {
    utc: '2026-09-08T00:00:00.0000000Z',
    creatures: ['Aeluva', 'Alelle', 'Alligator', 'Balron', 'Bogle'],
    vendors: ['Alchemist'],
    facets: ['Felucca', 'Trammel'],
    siteTypes: ['mine', 'lumber'],
    botClasses: ['Warrior', 'Miner'],
    botTiers: ['Novice', 'Grandmaster'],
    crafterTypes: ['Smith'],
    edgeKinds: ['walk', 'gate'],
    routeModes: ['cycle', 'oneway', 'pingpong'],
    legacyBotClasses: ['Crafter']
};

test('with the shard down it still answers, marked', () => {
    clearShardReport();

    const data = vocabulary.build(readJson);

    assert.strictEqual(data.shardSeen, false);

    // The half that comes from files is all there, which is the point: the forms open and their
    // tag and type combos are populated from navigation.json and bots.json.
    assert.ok(data.destinationTypes.length > 0, 'destination types come from the files');
    assert.ok(data.waypointTags.length > 0, 'waypoint tags come from the files');
    assert.ok(data.spawnFiles.length > 0, 'spawn files come from the directory');

    // And the shard's half is empty rather than absent, so a caller can render it without
    // checking for undefined first.
    assert.deepStrictEqual(data.siteTypes, []);
    assert.deepStrictEqual(data.botClasses, []);
    assert.deepStrictEqual(data.spawnTypes, { Monster: [], NPC: [], Vendor: [] });

    // Every kind is still offered, at zero. An absent kind would look like a form that had not
    // finished loading; `Monster (0)` says what is actually true.
    assert.deepStrictEqual(data.spawnKinds.map((k) => k.key), ['Monster', 'NPC', 'Vendor']);
    assert.deepStrictEqual(data.spawnKinds.map((k) => k.count), [0, 0, 0]);
});

test('the folder scan gets no say over whether a type is a vendor', () => {
    writeShardReport(FULL_REPORT);

    const data = vocabulary.build(readJson);

    // Alchemist.cs sits under Mobiles/NPCs. If the folder decided, it would be an NPC.
    assert.deepStrictEqual(data.spawnTypes.Vendor, ['Alchemist']);
    assert.ok(!data.spawnTypes.NPC.includes('Alchemist'));
    assert.ok(!data.spawnTypes.Monster.includes('Alchemist'));
});

test('NPCs come from the folder and everything else is a monster', () => {
    writeShardReport(FULL_REPORT);

    const data = vocabulary.build(readJson);

    // Aeluva and Alelle are declared under NPCs; Alelle in a file with a different name, which is
    // the case a file-name index would have got wrong.
    assert.deepStrictEqual(data.spawnTypes.NPC.sort(), ['Aeluva', 'Alelle']);
    assert.ok(data.spawnTypes.Monster.includes('Balron'));
    assert.ok(data.spawnTypes.Monster.includes('Alligator'));
});

test('a type the scan never saw falls to Monster and is counted', () => {
    writeShardReport(FULL_REPORT);

    const data = vocabulary.build(readJson);

    // Bogle is in the shard's list and in none of the temp tree's files - which is the real case:
    // creature subclasses live under Scripts/Engines and Scripts/Services too.
    assert.ok(data.spawnTypes.Monster.includes('Bogle'));
    assert.strictEqual(data.spawnUnclassified, 1);

    // The counts on the kinds are the lengths of the lists, so a form label cannot disagree with
    // the list behind it.
    const byKey = Object.fromEntries(data.spawnKinds.map((k) => [k.key, k.count]));

    assert.strictEqual(byKey.Monster, data.spawnTypes.Monster.length);
    assert.strictEqual(byKey.NPC, data.spawnTypes.NPC.length);
    assert.strictEqual(byKey.Vendor, data.spawnTypes.Vendor.length);
});

test('the shard passes its enums through untouched', () => {
    writeShardReport(FULL_REPORT);

    const data = vocabulary.build(readJson);

    assert.strictEqual(data.shardSeen, true);
    assert.deepStrictEqual(data.siteTypes, ['mine', 'lumber']);
    assert.deepStrictEqual(data.routeModes, ['cycle', 'oneway', 'pingpong']);
    assert.deepStrictEqual(data.edgeKinds, ['walk', 'gate']);
    assert.deepStrictEqual(data.legacyBotClasses, ['Crafter']);
});

test('destination types are what is in use plus what the bot layer weights', () => {
    writeShardReport(FULL_REPORT);

    const data = vocabulary.build(readJson);
    const types = data.destinationTypes.map((entry) => entry.value);

    // In navigation.json and weighted by bots.json.
    assert.ok(types.includes('bank'));
    assert.ok(types.includes('shop'));

    // Weighted by bots.json whether or not anything uses it yet - which is exactly the one you
    // are about to author.
    assert.ok(types.includes('lumber'), 'a weighted-but-unused type is still offered');

    // Ranked by use, so what you nearly always want is not nineteen rows down for want of an
    // earlier letter. shop is the commonest type in the shipped file.
    assert.strictEqual(data.destinationTypes[0].value, 'shop');
    assert.ok(data.destinationTypes[0].count > 1);
});

test('tags are ranked by use and merged across sources without duplicates', () => {
    writeShardReport(FULL_REPORT);

    const data = vocabulary.build(readJson);
    const values = data.destinationTags.map((entry) => entry.value);

    assert.strictEqual(new Set(values).size, values.length, 'no tag appears twice');
    assert.ok(values.includes('britain'), 'a town tag from bots.json towns');
    assert.ok(values.includes('craft'), 'a tag bots.json weights');

    // Counts descend: a merged list that lost its order would still contain the right values and
    // would be no better than an alphabetical one.
    const counts = data.destinationTags.map((entry) => entry.count);

    for (let i = 1; i < counts.length; i++) {
        assert.ok(counts[i] <= counts[i - 1], 'ranked by use');
    }
});

test('the scan re-reads when the tree changes', () => {
    writeShardReport({ ...FULL_REPORT, creatures: ['Newcomer'] });

    assert.ok(vocabulary.build(readJson).spawnTypes.Monster.includes('Newcomer'));

    write('Scripts/Mobiles/NPCs/Newcomer.cs', 'public class Newcomer : BaseCreature { }');

    // Keyed on the tree's file count and newest mtime, so a file added outside the editor is
    // picked up on the next request rather than on a restart.
    assert.ok(vocabulary.build(readJson).spawnTypes.NPC.includes('Newcomer'));
});
