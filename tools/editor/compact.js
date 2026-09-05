'use strict';

// A raw-preserving JSON parser and a writer that reproduces JsonConfig.SerializeCompact exactly.
//
// Why not JSON.parse and JSON.stringify: the layout rule is only half the problem. Newtonsoft
// keeps the difference between the JSON tokens 3 and 3.0 - one is an integer, the other a float -
// and writes them back as it found them. JavaScript has one number type, so JSON.parse('3.0')
// followed by JSON.stringify gives '3', and the file changes the first time the editor saves it.
// That is not hypothetical: costTags carries {"multiplier":3.0}.
//
// So scalars keep the exact source text they were parsed from, and anything the editor did not
// touch is written back byte-for-byte. Only edited values are re-formatted.
//
// The layout rule itself, from JsonConfig.SerializeCompact:
//   - a container whose children are ALL scalars is written on one line, compactly
//   - otherwise one child per line, indented two spaces per level
//   - the document ends with a newline

const OBJECT = 'object';
const ARRAY = 'array';
const SCALAR = 'scalar';

class ParseError extends Error {}

function parse(text) {
    const state = { text, index: 0 };

    skipWhitespace(state);

    const node = parseValue(state);

    skipWhitespace(state);

    if (state.index < text.length) {
        throw new ParseError(`Trailing content at offset ${state.index}`);
    }

    return node;
}

function parseValue(state) {
    const c = state.text[state.index];

    if (c === '{') return parseObject(state);
    if (c === '[') return parseArray(state);

    return parseScalar(state);
}

function parseObject(state) {
    expect(state, '{');
    skipWhitespace(state);

    const entries = [];

    if (state.text[state.index] === '}') {
        state.index++;
        return { kind: OBJECT, entries };
    }

    for (;;) {
        skipWhitespace(state);

        const start = state.index;
        parseString(state);
        const rawKey = state.text.slice(start, state.index);

        skipWhitespace(state);
        expect(state, ':');
        skipWhitespace(state);

        entries.push({ rawKey, key: JSON.parse(rawKey), value: parseValue(state) });

        skipWhitespace(state);

        const next = state.text[state.index];

        if (next === ',') {
            state.index++;
            continue;
        }

        if (next === '}') {
            state.index++;
            return { kind: OBJECT, entries };
        }

        throw new ParseError(`Expected ',' or '}' at offset ${state.index}`);
    }
}

function parseArray(state) {
    expect(state, '[');
    skipWhitespace(state);

    const items = [];

    if (state.text[state.index] === ']') {
        state.index++;
        return { kind: ARRAY, items };
    }

    for (;;) {
        skipWhitespace(state);
        items.push(parseValue(state));
        skipWhitespace(state);

        const next = state.text[state.index];

        if (next === ',') {
            state.index++;
            continue;
        }

        if (next === ']') {
            state.index++;
            return { kind: ARRAY, items };
        }

        throw new ParseError(`Expected ',' or ']' at offset ${state.index}`);
    }
}

function parseScalar(state) {
    const start = state.index;
    const c = state.text[state.index];

    if (c === '"') {
        parseString(state);
    } else {
        while (state.index < state.text.length && !',]}\r\n\t '.includes(state.text[state.index])) {
            state.index++;
        }
    }

    if (state.index === start) {
        throw new ParseError(`Expected a value at offset ${start}`);
    }

    const raw = state.text.slice(start, state.index);

    return { kind: SCALAR, raw, value: JSON.parse(raw) };
}

function parseString(state) {
    expect(state, '"');

    while (state.index < state.text.length) {
        const c = state.text[state.index];

        if (c === '\\') {
            state.index += 2;
            continue;
        }

        state.index++;

        if (c === '"') {
            return;
        }
    }

    throw new ParseError('Unterminated string');
}

function expect(state, character) {
    if (state.text[state.index] !== character) {
        throw new ParseError(`Expected '${character}' at offset ${state.index}`);
    }

    state.index++;
}

function skipWhitespace(state) {
    while (state.index < state.text.length && ' \t\r\n'.includes(state.text[state.index])) {
        state.index++;
    }
}

/** True when every child is a scalar, so the container fits on one line. */
function isFlat(node) {
    if (node.kind === OBJECT) {
        return node.entries.every((entry) => entry.value.kind === SCALAR);
    }

    if (node.kind === ARRAY) {
        return node.items.every((item) => item.kind === SCALAR);
    }

    return false;
}

/** The one-line form: no space after ':' or ','. */
function writeInline(node) {
    if (node.kind === SCALAR) {
        return node.raw;
    }

    if (node.kind === OBJECT) {
        return '{' + node.entries.map((e) => `${e.rawKey}:${writeInline(e.value)}`).join(',') + '}';
    }

    return '[' + node.items.map(writeInline).join(',') + ']';
}

function writeNode(node, indent) {
    if (node.kind === SCALAR || isFlat(node)) {
        return writeInline(node);
    }

    const pad = '  '.repeat(indent + 1);
    const close = '  '.repeat(indent);

    if (node.kind === OBJECT) {
        const body = node.entries
            .map((e) => `${pad}${e.rawKey}: ${writeNode(e.value, indent + 1)}`)
            .join(',\n');

        return `{\n${body}\n${close}}`;
    }

    const body = node.items.map((i) => `${pad}${writeNode(i, indent + 1)}`).join(',\n');

    return `[\n${body}\n${close}]`;
}

function stringify(node) {
    return writeNode(node, 0) + '\n';
}

// ---- tree helpers, used by unproject ----

function get(node, key) {
    if (node.kind !== OBJECT) return undefined;
    const entry = node.entries.find((e) => e.key === key);
    return entry ? entry.value : undefined;
}

function plain(node) {
    if (node === undefined) return undefined;
    if (node.kind === SCALAR) return node.value;
    if (node.kind === ARRAY) return node.items.map(plain);

    const result = {};
    for (const entry of node.entries) result[entry.key] = plain(entry.value);
    return result;
}

/**
 * Replaces a scalar's value, keeping Newtonsoft's integer/float distinction.
 *
 * A field that was 3.0 stays a float when it becomes 4, because the C# writer would have written
 * 4.0 - the JSON token type is part of what round-trips, not just the numeric value.
 */
function setScalar(node, value) {
    if (node.kind !== SCALAR) {
        throw new Error('Not a scalar');
    }

    const wasFloat = typeof node.value === 'number' && /[.eE]/.test(node.raw);

    node.value = value;

    if (typeof value === 'number' && wasFloat && Number.isInteger(value)) {
        node.raw = value.toFixed(1);
    } else {
        node.raw = JSON.stringify(value);
    }

    return node;
}

module.exports = { parse, stringify, isFlat, get, plain, setScalar, OBJECT, ARRAY, SCALAR };
