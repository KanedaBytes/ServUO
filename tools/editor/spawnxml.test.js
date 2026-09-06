'use strict';

// node --test tools/editor/*.test.js
//
// The spawn XML is the second format this editor writes, and the first it did not design. The
// fidelity bar is the same as the JSON's - a save that touches one field must change one field -
// but the corpus is far stranger: 6,805 spawn blocks across two line-ending conventions, two
// trailing-newline conventions, commented-out identity fields, duplicated elements, and one
// spawner carrying a whole equipment expression in angle brackets.
//
// So the headline test is not a fixture. It is every spawn file in the repo.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const xml = require('./spawnxml.js');
const objects2 = require('./objects2.js');
const { REPO_ROOT } = require('./whitelist.js');

/** Every spawn file in the tree: stock, custom and revamped. */
function everySpawnFile() {
    const files = [];

    for (const dir of ['Spawns', 'RevampedSpawns', path.join('Spawns', 'Custom', 'trammel')]) {
        const full = path.join(REPO_ROOT, dir);

        if (!fs.existsSync(full)) {
            continue;
        }

        for (const name of fs.readdirSync(full)) {
            if (name.toLowerCase().endsWith('.xml')) {
                files.push(path.join(full, name));
            }
        }
    }

    return files;
}

function read(file) {
    return fs.readFileSync(file, 'utf8');
}

const GG = path.join(REPO_ROOT, 'Spawns', 'Custom', 'trammel', 'GG_DailyLife.xml');

// ---- the round trip --------------------------------------------------------------------------

test('every spawn file in the repo round-trips byte for byte', () => {
    const files = everySpawnFile();

    assert.ok(files.length >= 15, 'the tree should have more spawn files than this');

    let blocks = 0;

    for (const file of files) {
        const text = read(file);
        const doc = xml.parse(text);

        blocks += doc.blocks.length;

        assert.strictEqual(xml.stringify(doc), text, path.basename(file));
    }

    assert.ok(blocks > 6000, `only ${blocks} blocks parsed - the corpus should be much larger`);
});

test('both line-ending and trailing-newline conventions survive, because both are in the tree', () => {
    // Spawns/*.xml are CRLF with no trailing newline; Spawns/Custom/*.xml are LF with one. Both
    // are LF in git's object store - core.autocrlf converts on checkout and the custom files have
    // not been checked out since they were written. A writer that picked a convention would
    // rewrite whole files it was asked to touch one field of.
    const stock = read(path.join(REPO_ROOT, 'Spawns', 'trammel.xml'));
    const custom = read(GG);

    assert.ok(stock.includes('\r\n'), 'the stock fixture is no longer CRLF');
    assert.ok(stock.endsWith('</Spawns>'), 'the stock fixture gained a trailing newline');

    for (const text of [stock, custom]) {
        assert.strictEqual(xml.stringify(xml.parse(text)), text);
    }
});

// ---- editing ------------------------------------------------------------------------------------

test('setting a field rewrites that element and nothing else', () => {
    const text = read(GG);
    const doc = xml.parse(text);

    xml.set(doc.blocks[0], 'CentreX', 1234);

    const after = xml.stringify(doc);
    const changed = diffLines(text, after);

    assert.deepStrictEqual(changed.removed.map((l) => l.trim()), ['<CentreX>1450</CentreX>']);
    assert.deepStrictEqual(changed.added.map((l) => l.trim()), ['<CentreX>1234</CentreX>']);
});

test('a boolean is written the way .NET writes one', () => {
    const doc = xml.parse(read(GG));

    xml.set(doc.blocks[0], 'IsRunning', false);
    assert.strictEqual(xml.get(doc.blocks[0], 'IsRunning'), 'False');

    xml.set(doc.blocks[0], 'IsRunning', true);
    assert.strictEqual(xml.get(doc.blocks[0], 'IsRunning'), 'True');
});

test('an empty string is <Elem />, and removing is not the same as emptying', () => {
    // The reader treats them differently: an absent <Name> defaults to "Spawner", an empty one is "".
    const doc = xml.parse(read(GG));
    const block = doc.blocks[0];

    xml.set(block, 'Objects2', '');
    assert.match(block.raw, /<Objects2 \/>/);
    assert.strictEqual(xml.get(block, 'Objects2'), '');
    assert.strictEqual(xml.has(block, 'Objects2'), true);

    xml.remove(block, 'Objects2');
    assert.strictEqual(xml.has(block, 'Objects2'), false);
    assert.strictEqual(xml.get(block, 'Objects2'), undefined);
    assert.doesNotMatch(block.raw, /Objects2/);
});

test('a new field lands where the shard writer would have put it, not at the end', () => {
    const doc = xml.parse(read(GG));
    const block = doc.blocks[0];

    xml.set(block, 'SpeechTrigger', 'Animals');

    const order = xml.names(block);
    const at = order.indexOf('SpeechTrigger');

    assert.ok(at > order.indexOf('TriggerProbability'), 'SpeechTrigger placed too early');
    assert.ok(at < order.indexOf('InContainer'), 'SpeechTrigger placed too late');

    // And every name is still in COLUMNS order overall.
    let previous = -1;

    for (const name of order) {
        const index = xml.COLUMNS.indexOf(name);
        assert.ok(index > previous, `${name} is out of the writer's column order`);
        previous = index;
    }
});

test('a made block is in column order regardless of the order it was given in', () => {
    const block = xml.makeBlock([
        ['Objects2', 'GGBaker:MX=1'],
        ['Name', 'GG_Test'],
        ['CentreZ', 20],
        ['Map', 'Trammel'],
        ['IsRunning', true]
    ], '\n');

    assert.deepStrictEqual(xml.names(block), ['Name', 'Map', 'CentreZ', 'IsRunning', 'Objects2']);
    assert.throws(() => xml.makeBlock([['Nope', 1]]), /not a spawner field/);
});

test('adding and removing a block leaves every other byte alone', () => {
    const text = read(GG);
    const doc = xml.parse(text);
    const block = xml.makeBlock([
        ['Name', 'GG_Test'], ['UniqueId', '11111111-2222-3333-4444-555555555555'],
        ['Map', 'Trammel'], ['CentreX', 1], ['CentreY', 2], ['CentreZ', 3],
        ['Objects2', 'GGBaker:MX=1:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1']
    ], doc.newline);

    xml.push(doc, block);

    const grown = xml.stringify(doc);

    assert.ok(grown.startsWith(text.slice(0, text.lastIndexOf('</Points>'))),
        'appending a block disturbed the blocks before it');
    assert.match(grown, /<Name>GG_Test<\/Name>/);

    // And taking it back out returns the original exactly.
    const back = xml.parse(grown);

    xml.removeAt(back, xml.indexOfBlock(back, back.blocks.find((b) => xml.get(b, 'Name') === 'GG_Test')));

    assert.strictEqual(xml.stringify(back), text, 'add-then-remove did not return the file');
});

test('a file emptied of spawners is refused rather than written', () => {
    // [XmlSave writes a zero-byte file when nothing matches, and ds.ReadXml then throws on it - so
    // this would produce a file the shard can no longer load, reporting only "Error reading xml".
    const doc = xml.parse(read(path.join(REPO_ROOT, 'Spawns', 'Custom', 'trammel', 'GG_OldMarta.xml')));

    xml.removeAt(doc, 0);

    assert.throws(() => xml.stringify(doc), /cannot be written/);
});

// ---- the strange corpus ---------------------------------------------------------------------------

test('a commented-out UniqueId is preserved, reported, and never mistaken for a field', () => {
    // Forty-seven blocks in trammel.xml have `<!--guid-->` exactly where <UniqueId> belongs, so
    // those spawners duplicate on every import instead of replacing.
    const doc = xml.parse(read(path.join(REPO_ROOT, 'Spawns', 'trammel.xml')));
    const commented = doc.blocks.filter((b) => !xml.has(b, 'UniqueId'));

    assert.strictEqual(commented.length, 47);

    const said = xml.findings(doc).filter((line) => line.includes('UniqueId commented out'));

    assert.strictEqual(said.length, 47);
    assert.match(said[0], /is duplicated on every import rather than replaced/);
});

test('an element inside a comment is not read as a field', () => {
    const doc = xml.parse(
        '<Spawns>\n  <Points>\n    <Name>a</Name>\n    <!--<CentreX>999</CentreX>-->\n'
        + '    <CentreX>7</CentreX>\n  </Points>\n</Spawns>\n');

    assert.strictEqual(xml.get(doc.blocks[0], 'CentreX'), '7');
    assert.strictEqual(xml.stringify(doc).includes('<!--<CentreX>999</CentreX>-->'), true);
});

test('a duplicated element is reported, and the first one is the one that is read', () => {
    // Sixteen blocks in trammel.xml carry <MinDelay> twice and no <MaxDelay> - a hand-edit where
    // the second should have been MaxDelay.
    const doc = xml.parse(read(path.join(REPO_ROOT, 'Spawns', 'trammel.xml')));
    const said = xml.findings(doc).filter((line) => line.includes('<MinDelay> elements'));

    assert.strictEqual(said.length, 16);

    const affected = doc.blocks.find((b) => xml.count(b, 'MinDelay') > 1);

    assert.strictEqual(xml.get(affected, 'MinDelay'), '5');
    assert.strictEqual(xml.has(affected, 'MaxDelay'), false);
});

test('entities are decoded on the way in and encoded the way XmlTextWriter does on the way out', () => {
    // XmlTextWriter escapes '>' in text where a modern writer does not; one Eodon spawner has it.
    const doc = xml.parse('<Spawns>\n  <Points>\n    <Name>a &lt;b&gt; c</Name>\n  </Points>\n</Spawns>\n');

    assert.strictEqual(xml.get(doc.blocks[0], 'Name'), 'a <b> c');

    xml.set(doc.blocks[0], 'Name', 'x <y> & z');

    assert.match(doc.blocks[0].raw, /<Name>x &lt;y&gt; &amp; z<\/Name>/);
    assert.strictEqual(xml.get(doc.blocks[0], 'Name'), 'x <y> & z');
});

// ---- Objects2 ---------------------------------------------------------------------------------

test('every Objects2 string in the tree round-trips verbatim', () => {
    let strings = 0;
    let entries = 0;

    for (const file of everySpawnFile()) {
        for (const block of xml.parse(read(file)).blocks) {
            const raw = xml.get(block, 'Objects2');

            if (raw === undefined) {
                continue;
            }

            strings++;

            const list = objects2.parse(raw);

            entries += list.length;

            assert.strictEqual(objects2.stringify(list), raw, `${path.basename(file)} ${raw.slice(0, 60)}`);
        }
    }

    assert.ok(strings > 6000 && entries > 20000, `only ${strings} strings / ${entries} entries`);
});

test('a multi-entry list splits on :OBJ= and rebuilds in the writer field order', () => {
    const raw =
        'GGProvisioner:MX=1:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1'
        + ':OBJ=Cobbler:MX=2:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1';

    const list = objects2.parse(raw);

    assert.deepStrictEqual(list.map((e) => e.type), ['GGProvisioner', 'Cobbler']);
    assert.deepStrictEqual(list.map((e) => e.MX), ['1', '2']);

    // Forced rebuild, not the verbatim path, so this pins the field order too.
    assert.strictEqual(objects2.stringify(list.map((e) => ({ ...e, edited: true }))), raw);
});

test('a type name carrying a key token is refused, because the shard would eat it silently', () => {
    assert.match(objects2.checkType('Foo:MX=9'), /discard the whole entry/);
    assert.match(objects2.checkType('Foo:SB=9'), /misparse the entry/);
    assert.match(objects2.checkType('Foo:OBJ=Bar'), /separates one entry from the next/);
    assert.match(objects2.checkType(''), /needs a type name/);

    // Legal, and all of these occur in the real data: constructor args, property paths, angle
    // brackets, and a bare colon that is not a key token.
    assert.strictEqual(objects2.checkType('Lyle/CantWalk/False'), null);
    assert.strictEqual(objects2.checkType('TribeWarrior,Barako/Z/100'), null);
    assert.strictEqual(objects2.checkType('xmlquestnpc/EQUIP/<robe/loottype/blessed>'), null);

    assert.throws(() => objects2.makeEntry('Foo:MX=9'), /discard the whole entry/);
});

test('ClearOnAdvance defaults to false only when no kills are needed', () => {
    // XmlSpawner2.cs:12756-12759, and worth pinning because it is the one default that is not a
    // constant.
    assert.strictEqual(objects2.parse('Foo:MX=1:KL=0')[0].CA, false);
    assert.strictEqual(objects2.parse('Foo:MX=1:KL=3')[0].CA, true);
    assert.strictEqual(objects2.parse('Foo:MX=1:KL=0:CA=1')[0].CA, '1');
});

test('a new entry gets the writer defaults', () => {
    assert.strictEqual(
        objects2.stringify([objects2.makeEntry('GGBaker')]),
        'GGBaker:MX=1:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1');
});

test('a created spawner gets the spawn list the tool collected, not an empty one', () => {
    // Found by creating one against the live shard: the create tool supplies `entries` and no
    // Objects2 string, and createShape only read props.Objects2 - so the new spawner was written
    // with `<Objects2 />`, which the shard reads as spawning nothing and then will not even start.
    // The file looked fine; only the world said otherwise.
    const spawners = require('./spawners.js');
    const written = spawners.unproject(
        'trammel/GG_OldMarta.xml',
        read(path.join(REPO_ROOT, 'Spawns', 'Custom', 'trammel', 'GG_OldMarta.xml')),
        {
            creates: [{
                id: 'spawner:trammel/GG_OldMarta.xml#11111111-2222-3333-4444-555555555555',
                layer: 'spawners', kind: 'point', map: 'Trammel', label: 'GG_TestRat',
                points: [[1470, 1650, 20]],
                props: { Name: 'GG_TestRat', MaxCount: '2', IsRunning: 'True' },
                entries: [{ type: 'Rat', max: '2' }]
            }]
        });

    assert.match(written, /<Objects2>Rat:MX=2:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1<\/Objects2>/);
    assert.doesNotMatch(written, /<Objects2 \/>/, 'the new spawner spawns nothing');
    assert.match(written, /<IsRunning>True<\/IsRunning>/);
});

test('Objects2 anomalies in the stock data are reported rather than tolerated in silence', () => {
    // Two entries in trammel.xml repeat a key; one in underworld.xml stops three keys early. All
    // harmless to the shard, and invisible until something looks.
    const found = [];

    for (const file of everySpawnFile()) {
        for (const block of xml.parse(read(file)).blocks) {
            const raw = xml.get(block, 'Objects2');

            if (raw) {
                found.push(...objects2.findings(raw));
            }
        }
    }

    assert.ok(found.some((line) => /has ':PR=' 2 times/.test(line)));
    assert.ok(found.some((line) => /is missing 'DX', 'SP', 'PR'/.test(line)));
});

/** Which whole lines a write added and removed. */
function diffLines(before, after) {
    const counts = new Map();

    for (const line of before.split('\n')) counts.set(line, (counts.get(line) || 0) + 1);
    for (const line of after.split('\n')) counts.set(line, (counts.get(line) || 0) - 1);

    const removed = [];
    const added = [];

    for (const [line, n] of counts) {
        for (let i = 0; i < n; i++) removed.push(line);
        for (let i = 0; i < -n; i++) added.push(line);
    }

    return { added, removed };
}
