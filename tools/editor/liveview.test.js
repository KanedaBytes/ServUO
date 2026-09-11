'use strict';

// node --test tools/editor/*.test.js
//
// js/liveview.js holds the two decisions the timer-redrawn panels get wrong when they are made
// inline in a handler: "was the reader at the bottom?" and "has anything actually changed?". Both
// were inline, and both were wrong - the console feed snapped to the bottom on every poll, and the
// bot card rebuilt its whole body every two seconds and collapsed the [BotInfo block with it.
//
// The cases below are the ones that decide correctness rather than the ones that are easy to write:
// the fractional-pixel slack, and the fields the signature must not omit.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

let liveview;

test.before(async () => {
    liveview = await import('./js/liveview.js');
});

test('a box with nothing to scroll is at the bottom', () => {
    // A fresh feed, and the one that must not report "the user has scrolled up" about a reader who
    // has never touched it.
    assert.strictEqual(liveview.atBottom(0, 100, 240), true);
});

test('scrolled to the end is at the bottom, exactly', () => {
    assert.strictEqual(liveview.atBottom(760, 1000, 240), true);
});

test('scrolled up is NOT at the bottom', () => {
    assert.strictEqual(liveview.atBottom(200, 1000, 240), false);
});

test('a fractional pixel short still counts as the bottom', () => {
    // THE CASE THE SLACK EXISTS FOR. None of these three numbers is reliably an integer - a
    // fractional device pixel ratio, a zoom level or a sub-pixel line height all leave the sum a
    // shade under scrollHeight when the box is visibly at the end. Comparing for equality reads
    // "scrolled up" about somebody who has not moved, which is this bug in the other direction.
    assert.strictEqual(liveview.atBottom(759.6, 1000, 240), true);
    assert.strictEqual(liveview.atBottom(757, 1000, 240), true);

    // ...but a real scroll up is still a real scroll up.
    assert.strictEqual(liveview.atBottom(740, 1000, 240), false);
});

test('nonsense measurements follow rather than freeze', () => {
    // A detached or unrendered element answers NaN. Following is the safe default: the old
    // behaviour, which is wrong only for a reader who is not there.
    assert.strictEqual(liveview.atBottom(NaN, 1000, 240), true);
    assert.strictEqual(liveview.elementAtBottom(null), true);
});

test('the signature changes when any drawn field changes', () => {
    const bot = {
        serial: 7, name: 'Alaric', class: 'Fencer', tier: 'Adept', behavior: 'BankSitter',
        role: 'Fixed', status: 'at trinsic-bank', stepMs: 400, mounted: false,
        dest: 'trinsic-bank', stuck: null
    };
    const base = liveview.botDetailSignature(bot, null);

    for (const field of liveview.BOT_DETAIL_FIELDS) {
        const changed = { ...bot, [field]: 'something else' };

        assert.notStrictEqual(
            liveview.botDetailSignature(changed, null), base,
            `the signature ignores '${field}', so the card would stop updating when it changes`);
    }
});

test('the signature ignores visitLeft, deliberately', () => {
    // It counts down every two seconds, so including it would make the signature differ on every
    // poll for exactly the bots somebody is most likely to be reading about. app.js updates that
    // one number in place instead - and this test is what stops somebody "fixing" the omission.
    const bot = { serial: 7, name: 'A', behavior: 'Shopper', visitLeft: 168 };

    assert.strictEqual(
        liveview.botDetailSignature(bot, null),
        liveview.botDetailSignature({ ...bot, visitLeft: 12 }, null));
});

test('the signature notices a new log event', () => {
    const bot = { serial: 7, name: 'A' };
    const one = { events: [{ utc: '2026-09-11T10:00:00Z', text: 'a' }] };
    const two = { events: [...one.events, { utc: '2026-09-11T10:00:02Z', text: 'b' }] };

    assert.notStrictEqual(
        liveview.botDetailSignature(bot, one), liveview.botDetailSignature(bot, two));
});

test('every field the card draws is one the signature covers', () => {
    // The hazard is omission, and it is invisible: a field rendered but not compared is a card
    // that silently stops updating. Read off app.js rather than listed here, which would be the
    // same drift with an extra step.
    const app = fs.readFileSync(path.join(__dirname, 'js', 'app.js'), 'utf8');
    const body = /function fillBotDetail\(host, bot\) \{([\s\S]*?)\n\}/.exec(app);

    assert.ok(body, 'fillBotDetail not found in app.js');

    const drawn = new Set([...body[1].matchAll(/\bbot\.([a-zA-Z]+)/g)].map((m) => m[1]));

    // visitLeft is the documented exception above; serial addresses the record rather than being
    // drawn from it.
    drawn.delete('visitLeft');

    for (const field of drawn) {
        assert.ok(
            liveview.BOT_DETAIL_FIELDS.includes(field),
            `fillBotDetail reads bot.${field} but the signature does not compare it`);
    }
});

test('app.js asks before it scrolls, and remembers the expanded block by serial', () => {
    // Source-level, in the house style of editor.test.js's .count-sweep test: these two live inside
    // handlers the suite has no DOM to run, and both were unconditional before.
    const app = fs.readFileSync(path.join(__dirname, 'js', 'app.js'), 'utf8');

    assert.match(app, /const follow = elementAtBottom\(box\);/,
        'the feed writes without first asking whether the reader was at the bottom');
    assert.ok(
        !/\n\s*consoleBox\.scrollTop = consoleBox\.scrollHeight;/.test(app),
        'the old unconditional console auto-scroll is still there');
    assert.match(app, /details\.open = state\.botInfoOpen\.has\(bot\.serial\);/,
        'the [BotInfo block no longer restores its expanded state by serial');
});
