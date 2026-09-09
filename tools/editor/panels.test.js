'use strict';

// node --test tools/editor/*.test.js
//
// The left column's two remembered settings: which sections are open, and how wide it is.
//
// Only two things here are worth a test, and neither is the DOM plumbing. The first is that the
// default width and the CSS variable it is meant to match have not drifted apart - they are
// written down in two files and nothing else would notice. The second is the clamp, which is the
// only arithmetic in the module and the only thing that can leave the column in a state you
// cannot drag back out of.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const EDITOR_ROOT = __dirname;

/** panels.js is an ES module and reads window at call time, so the import is dynamic. */
async function panels(innerWidth = 1600) {
    globalThis.window = { innerWidth, addEventListener() {} };

    return import(
        new URL('file:///' + path.join(EDITOR_ROOT, 'js', 'panels.js').replace(/\\/g, '/')));
}

test('the default width is the one style.css ships', async () => {
    const { DEFAULT_WIDTH } = await panels();
    const css = fs.readFileSync(path.join(EDITOR_ROOT, 'style.css'), 'utf8');
    const match = /--sidebar:\s*(\d+)px/.exec(css);

    assert.ok(match, 'style.css declares --sidebar');

    // Both exist because the column has to have a width before any script runs - the CSS value is
    // what a first paint uses and the JS value is what a double-click resets to. Different
    // numbers would mean the reset moved the column somewhere it had never been.
    assert.strictEqual(Number(match[1]), DEFAULT_WIDTH);
});

test('the clamp keeps the column grabbable at both ends', async () => {
    const { clampWidth, MIN_WIDTH, MAX_FRACTION } = await panels(1600);

    assert.strictEqual(clampWidth(0), MIN_WIDTH);
    assert.strictEqual(clampWidth(-500), MIN_WIDTH);
    assert.strictEqual(clampWidth(9999), Math.round(1600 * MAX_FRACTION));
    assert.strictEqual(clampWidth(400), 400);

    // Fractional pixels come out of a pointer position on a scaled display; a width has to be a
    // whole number or the map's box is fractional too and every tile lands half a pixel out.
    assert.strictEqual(clampWidth(320.6), 321);
});

test('a window narrower than the minimum still yields the minimum', async () => {
    // 40% of a 300px window is 120, below MIN_WIDTH. The max must not win that argument: a column
    // clamped to 120 has no room for its own drag handle, and the window can be widened again.
    const { clampWidth, MIN_WIDTH } = await panels(300);

    assert.strictEqual(clampWidth(260), MIN_WIDTH);
    assert.strictEqual(clampWidth(50), MIN_WIDTH);
});

test('every section in index.html is a details with an id to remember it by', async () => {
    const html = fs.readFileSync(path.join(EDITOR_ROOT, 'index.html'), 'utf8');
    const aside = html.slice(html.indexOf('<aside'), html.indexOf('</aside>'));

    const groups = [...aside.matchAll(/<details class="grp" id="([a-z-]+)"/g)].map((m) => m[1]);

    assert.ok(groups.length >= 8, `expected every sidebar section to be collapsible, found ${groups.length}`);
    assert.strictEqual(new Set(groups).size, groups.length, 'section ids are unique');

    // The id IS the storage key (see panels.js), so a section without one silently forgets its
    // state, and two sharing one would toggle each other.
    assert.ok(!/<section\b/.test(aside), 'no <section> left un-converted in the sidebar');
});
