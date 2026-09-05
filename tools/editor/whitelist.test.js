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
