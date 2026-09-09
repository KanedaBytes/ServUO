'use strict';

// node --test tools/editor/*.test.js
//
// That the browser code actually loads.
//
// This exists because it did not, and 161 green tests said nothing. `js/app.js` shipped with an
// unterminated string literal - a `.join('` with a real newline inside single quotes where `'\n'`
// was meant - and the editor loaded completely blank: no layers, no health, no Create section.
// Every other test passed the whole time, because they require() the Node-side modules
// (bridge, project, compact, whitelist) and never touch the browser module graph at all.
//
// THE TRAP, and the reason this file checks the way it does:
//
//     node --check js/app.js        -> exits 0, silently, on a file with a syntax error
//     node --check <same file>.mjs  -> exits 1 and points at the line
//
// `--check` treats a `.js` file as CommonJS. When the CJS parse hits a top-level `import` it
// falls back to module detection, and that path does not surface the syntax error. So a
// hand-rolled "node --check every file" loop - the obvious guard, and one that was actually run -
// reports all clear on a file the browser cannot parse. Every file therefore gets copied to a
// temporary `.mjs` before being checked.
//
// Two layers, because they catch different things:
//
//   1. PARSE - every file, as a real ES module. Catches the syntax error above.
//   2. IMPORT - the real graph, through a DOM shim. Also catches a missing export (a link error,
//      which is not a syntax error and which layer 1 cannot see) and anything that throws at
//      module scope.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const EDITOR_ROOT = __dirname;
const JS_DIR = path.join(EDITOR_ROOT, 'js');

/** Every browser-side source file: js/ plus the Node-side modules at the editor root. */
function sourceFiles() {
    const files = [];

    for (const name of fs.readdirSync(JS_DIR)) {
        if (name.endsWith('.js')) {
            files.push(path.join(JS_DIR, name));
        }
    }

    for (const name of fs.readdirSync(EDITOR_ROOT)) {
        if (name.endsWith('.js') && !name.endsWith('.test.js')) {
            files.push(path.join(EDITOR_ROOT, name));
        }
    }

    return files;
}

test('every editor source parses as an ES module', () => {
    const files = sourceFiles();

    // A guard on the guard: if the glob ever silently matches nothing, an empty loop would pass.
    assert.ok(files.length >= 10, `expected to find the editor sources, found ${files.length}`);

    const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'editor-parse-'));

    try {
        for (const file of files) {
            // The .mjs extension is the whole point - see THE TRAP above.
            const probe = path.join(scratch, path.basename(file, '.js') + '.mjs');

            fs.copyFileSync(file, probe);

            try {
                execFileSync(process.execPath, ['--check', probe], { stdio: 'pipe' });
            } catch (error) {
                const detail = String(error.stderr || error.message)
                    .split('\n')
                    .slice(0, 6)
                    .join('\n');

                assert.fail(
                    `${path.relative(EDITOR_ROOT, file)} does not parse as an ES module:\n${detail}`
                );
            }
        }
    } finally {
        fs.rmSync(scratch, { recursive: true, force: true });
    }
});

test('the browser module graph imports, app.js included', () => {
    // app.js reaches for the canvas at module scope, so importing it needs a DOM. The shim is
    // deliberately dumb: it is here to let the module body run to completion, not to model a
    // browser. If a future change needs more of one, widen it - the alternative is going back to
    // not knowing whether the editor loads.
    const shim = `
        const el = () => ({
            style: { setProperty() {}, getPropertyValue: () => '' },
            dataset: {}, children: [], value: '', textContent: '', innerHTML: '', hidden: false,
            offsetWidth: 260, offsetHeight: 24, clientWidth: 800, clientHeight: 600,
            classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } },
            appendChild() {}, removeChild() {}, remove() {}, append() {}, addEventListener() {},
            removeEventListener() {}, setAttribute() {}, getAttribute() { return null; },
            querySelector: () => el(), querySelectorAll: () => [], closest: () => null,
            setPointerCapture() {}, releasePointerCapture() {},
            get parentElement() { return el(); },
            getContext: () => ({ save() {}, restore() {}, clearRect() {}, fillRect() {} }),
            getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 600 })
        });

        globalThis.document = {
            getElementById: () => el(), querySelector: () => el(), querySelectorAll: () => [],
            createElement: () => el(), addEventListener() {}, body: el(), documentElement: el()
        };
        globalThis.window = {
            addEventListener() {}, removeEventListener() {}, devicePixelRatio: 1,
            innerWidth: 1600, innerHeight: 900,
            location: { href: 'http://127.0.0.1:8081/', search: '' },
            matchMedia: () => ({ matches: false, addEventListener() {} }),
            localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
            requestAnimationFrame: () => 0
        };
        globalThis.localStorage = globalThis.window.localStorage;
        globalThis.requestAnimationFrame = () => 0;
        globalThis.getComputedStyle = () => ({ getPropertyValue: () => '260px' });
        // panels.js reopens a section when something inside it stops being hidden. The shim only
        // has to let the constructor and observe() run; nothing here ever mutates.
        globalThis.MutationObserver = class { observe() {} disconnect() {} };
        globalThis.fetch = async () => ({
            ok: true, status: 200, json: async () => ({}), text: async () => ''
        });

        const url = (p) => new URL('file:///' + p.replace(/\\\\/g, '/'));

        await import(url(${JSON.stringify(path.join(JS_DIR, 'app.js'))}));

        for (const mod of ${JSON.stringify(
            fs.readdirSync(JS_DIR).filter((n) => n.endsWith('.js') && n !== 'app.js')
                .map((n) => path.join(JS_DIR, n))
        )}) {
            await import(url(mod));
        }

        console.log('OK');
    `;

    let output;

    try {
        output = execFileSync(process.execPath, ['--input-type=module', '-e', shim], {
            stdio: 'pipe',
            encoding: 'utf8'
        });
    } catch (error) {
        const detail = String(error.stderr || error.message)
            .split('\n')
            .slice(0, 8)
            .join('\n');

        assert.fail(`the browser module graph does not import:\n${detail}`);
    }

    assert.match(output, /OK/);
});

test('shapes.js colours every entity kind the shard can emit', () => {
    // The kinds are decided by LiveMapSnapshot.KindOf on the shard side and coloured here. An
    // unknown kind falls back to white rather than throwing, so a drift between the two is
    // invisible in the editor - which is exactly why it is asserted rather than trusted.
    const source = fs.readFileSync(path.join(JS_DIR, 'shapes.js'), 'utf8');
    const block = /const ENTITY_COLORS = \{([\s\S]*?)\};/.exec(source);

    assert.ok(block, 'ENTITY_COLORS not found in shapes.js');

    for (const kind of ['player', 'staff', 'vendor', 'actor', 'bot', 'creature']) {
        assert.match(
            block[1],
            new RegExp(`\\b${kind}\\s*:`),
            `ENTITY_COLORS has no colour for the '${kind}' kind`
        );
    }
});

test('every bot behaviour the shard registers has a colour', () => {
    // Read from the C# registry rather than from a list kept here, which would be the same drift
    // with an extra step. A behaviour added on the shard and forgotten in the editor draws as an
    // ordinary bot - indistinguishable from a deliberate choice, and so never noticed.
    const source = fs.readFileSync(path.join(JS_DIR, 'shapes.js'), 'utf8');
    const registry = fs.readFileSync(
        path.join(EDITOR_ROOT, '..', '..', 'Scripts', 'Custom', 'Bots', 'BotBehaviors.cs'), 'utf8');

    const block = /export const BEHAVIOR_COLORS = \{([\s\S]*?)\};/.exec(source);

    assert.ok(block, 'BEHAVIOR_COLORS not found in shapes.js');

    // The registry is a dictionary initialiser - `{ "Traveler", () => new TravelerBehavior() }` -
    // so the name is matched against that shape rather than against a Register() call.
    const names = [...registry.matchAll(/\{\s*"([A-Za-z]+)"\s*,\s*\(\)\s*=>/g)].map((match) => match[1]);

    assert.ok(names.length >= 4, `only found ${names.length} behaviours in BotBehaviors.cs`);

    for (const name of names) {
        assert.match(
            block[1],
            new RegExp(`\\b${name}\\s*:`),
            `BEHAVIOR_COLORS has no colour for '${name}', which BotBehaviors registers`
        );
    }
});
