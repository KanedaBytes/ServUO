/**
 * What ReportGump escapes, and what it must leave alone.
 *
 * The client's gump HTML parses tags but does NOT decode entities: `&gt;` is drawn as those four
 * characters, not as '>'. The first version escaped '>' and '&' as well as '<', so every NavAudit
 * finding rendered as "BLOCKED 'a' -&gt; 'b'" - and every one of those arrows is a '>'.
 *
 * That failure was invisible from outside the client, which is why this test reads the rule out of
 * the C# and applies it, rather than restating it here. A mirror of the rule would have been
 * written from the same wrong assumption and passed.
 */

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const REPORT_GUMP_CS = path.join(
    __dirname, '..', '..', 'Scripts', 'Custom', 'Core', 'ReportGump.cs');

/**
 * The `.Replace("x", "y")` pairs in ReportGump's Escape method, in order.
 *
 * Scoped to the method body rather than the file, so the doc comment above it - which quotes the
 * broken output, `&gt;` and all - cannot be mistaken for code.
 */
function escapeReplacements() {
    const source = fs.readFileSync(REPORT_GUMP_CS, 'utf8');
    const start = source.indexOf('private static string Escape(string text)');

    assert.ok(start > 0, 'Escape(string) is gone from ReportGump.cs');

    const body = source.slice(start, source.indexOf('\n        }', start));
    const pairs = [];
    const call = /\.Replace\("((?:[^"\\]|\\.)*)",\s*"((?:[^"\\]|\\.)*)"\)/g;

    let match;

    while ((match = call.exec(body)) !== null) {
        pairs.push([match[1], match[2]]);
    }

    return pairs;
}

/** The C#'s own rule, applied to a string. */
function escape(text) {
    return escapeReplacements().reduce((acc, [from, to]) => acc.split(from).join(to), text);
}

test('the report escapes < and nothing else', () => {
    assert.deepStrictEqual(escapeReplacements(), [['<', '&lt;']]);
});

test('an audit arrow survives, because the client never decodes it back', () => {
    // The exact shape of a real finding. This is the bug: every one of these read "-&gt;".
    const line = "BLOCKED 'brit-plaza-9' -> 'brit-bake-1' (8 tiles)";

    assert.strictEqual(escape(line), line);
    assert.ok(!escape(line).includes('&gt;'));
});

test('an ampersand is left alone, or it renders as the literal text &amp;', () => {
    // Escaping this was the same fault one level down: with no entity decoding, "&amp;" is drawn
    // as five characters. Vendor and bot names carry ampersands.
    const line = "Str/Dex/Int: 60 / 80 & rising";

    assert.strictEqual(escape(line), line);
    assert.ok(!escape(line).includes('&amp;'));
});

test('a < is still neutralised, because it opens a tag and eats the rest', () => {
    // The one character that is genuinely dangerous: the parser is looking for a tag opener, and
    // an unescaped one swallows everything after it.
    assert.strictEqual(escape('reach <5 needs review'), 'reach &lt;5 needs review');
});

test('the gump markup ReportGump writes itself is not escaped away', () => {
    // A guard on the surrounding code rather than on Escape: the title and body are wrapped in
    // <basefont> and joined with <br>, and those are built after escaping. If a refactor ever
    // escapes the wrapper too, the report renders as its own source.
    const source = fs.readFileSync(REPORT_GUMP_CS, 'utf8');

    assert.ok(source.includes('<basefont'), 'the report lost its colour markup');
    assert.ok(source.includes('"<br>"'), 'the report lost its line breaks');
});
