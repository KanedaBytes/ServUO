'use strict';

// node --test tools/editor/*.test.js
//
// The sandbox is the claim that the bridge cannot be steered anywhere it should not go. 5b widens
// it from "writes only request tokens" to "also writes three data files", so the boundary is worth
// asserting rather than describing.

const test = require('node:test');
const assert = require('node:assert');
const path = require('path');

const whitelist = require('./whitelist.js');

test('the three data files are writable and nothing else is', () => {
    for (const name of ['navigation', 'dailyLife', 'restrictedZones']) {
        assert.strictEqual(whitelist.resolveSave(name), whitelist.FILES[name], name);
    }

    // The shard writes these. An editor that could overwrite a snapshot could lie to itself about
    // the live world.
    for (const name of ['entities', 'health']) {
        assert.strictEqual(whitelist.resolveSave(name), null, name);
    }
});

test('a spawn file is addressed by key, and the pattern is the gate', () => {
    // The one writable thing that is a family rather than a fixed name, so the one place a caller
    // supplies any part of a path. Matched against a pattern and refused, never sanitised - and
    // then run back through isAllowed regardless.
    assert.ok(whitelist.resolveSave('spawn:trammel/GG_DailyLife.xml'));
    assert.strictEqual(whitelist.spawnRelative('spawn:trammel/GG_DailyLife.xml'), 'trammel/GG_DailyLife.xml');

    // A file that does not exist yet still resolves: that is the first save of a new one.
    assert.ok(whitelist.resolveSave('spawn:trammel/GG_Brand_New.xml'));

    for (const name of [
        'spawn:../../etc/passwd.xml',            // traversal
        'spawn:trammel/../../../etc/passwd.xml', // traversal past a legal-looking start
        'spawn:trammel/notgg.xml',               // no GG_ prefix, so [XmlLoad would not filter it
        'spawn:trammel/GG_a.txt',                // not xml
        'spawn:TRAMMEL/GG_a.xml',                // the layout is lowercase facet directories
        'spawn:trammel/sub/GG_a.xml',            // one level only, matching the layout
        'spawn:GG_a.xml',                        // no facet directory
        'spawn:', 'spawn:trammel/', 'stock:trammel'
    ]) {
        assert.strictEqual(whitelist.resolveSave(name), null, name);
        assert.strictEqual(whitelist.spawnRelative(name), null, name);
    }
});

test('a spawn file reloads per file, never with the whole-tree sweep', () => {
    // [GG_Reimport deletes every GG_ spawner in the world and respawns them, so using it per save
    // would make editing one file empty and refill the whole town.
    assert.strictEqual(whitelist.reloadFor('spawn:trammel/GG_DailyLife.xml'), 'spawn-reload');
    assert.strictEqual(whitelist.reloadFor('navigation'), 'nav-reload');
    assert.strictEqual(whitelist.reloadFor('entities'), null);
});

test('the spawn files on disk are listed as keys the save endpoint accepts', () => {
    const keys = whitelist.listSpawnFiles();

    assert.ok(keys.length >= 2, 'the tree should have at least the two GG files');

    for (const key of keys) {
        assert.ok(whitelist.resolveSave(key), `${key} was listed but cannot be saved`);
        assert.match(key, /^spawn:[a-z][a-z0-9_-]*\/GG_[A-Za-z0-9_-]+\.xml$/);
    }
});

test('a save name is a key, not a path, so there is nothing to traverse', () => {
    for (const name of [
        '../navigation', '..\\navigation', 'Data/Custom/navigation.json',
        '/etc/passwd', '', 'constructor', '__proto__', 'toString'
    ]) {
        assert.strictEqual(whitelist.resolveSave(name), null, JSON.stringify(name));
    }
});

test('every writable file names a reload request', () => {
    for (const [name, request] of Object.entries(whitelist.WRITABLE)) {
        assert.ok(whitelist.FILES[name], `${name} is writable but has no file`);
        assert.ok(whitelist.resolveToken(request), `${request} is not a legal token name`);
    }
});

test('the backup sits beside the file it backs up', () => {
    assert.strictEqual(
        whitelist.resolveBackup('navigation'), whitelist.FILES.navigation + '.bak');
    assert.strictEqual(whitelist.resolveBackup('entities'), null);
});

test('token and ack names are matched against a pattern, not cleaned up', () => {
    assert.ok(whitelist.resolveToken('nav-reload'));
    assert.ok(whitelist.resolveAck('nav-reload'));

    for (const name of ['../escape', 'Nav-Reload', '9lives', 'has space', 'a'.repeat(65), '']) {
        assert.strictEqual(whitelist.resolveToken(name), null, JSON.stringify(name));
        assert.strictEqual(whitelist.resolveAck(name), null, JSON.stringify(name));
    }
});

test('static serving stays inside the editor directory', () => {
    assert.strictEqual(
        whitelist.resolveStatic('/'), path.join(__dirname, 'index.html'));
    assert.ok(whitelist.resolveStatic('/js/app.js'));

    // Outside the roots, or a file type the editor has no business serving.
    assert.strictEqual(whitelist.resolveStatic('/../../Server/Main.cs'), null);
    assert.strictEqual(whitelist.resolveStatic('/%2e%2e/%2e%2e/Server/Main.cs'), null);
    assert.strictEqual(whitelist.resolveStatic('/bridge.cfg'), null);
});
