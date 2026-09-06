'use strict';

// A source-preserving reader and writer for XmlSpawner's spawn XML.
//
// Same philosophy as compact.js and a separate module for the same reason compact.js exists at
// all: the point is not "parse XML", it is "reproduce what the shard's own writer produced, byte
// for byte, while changing one field". compact.js encodes one specific JSON layout rule; XML has
// no such rule to encode, so bending it would leave one module explaining two formats.
//
// WHAT THE SHARD WRITES. `DataSet.WriteXml` (XmlSpawner2.cs:7566), which means:
//
//   - <Spawns> root, one <Points> per spawner, child ELEMENTS only - no attributes anywhere
//   - no XML declaration, no BOM, no schema, no namespaces
//   - two-space indent, one element per line
//   - True/False capitalised (Boolean.ToString)
//   - an empty string is <Elem />; an ABSENT field is no element at all, and the two mean
//     different things to the reader
//   - '>' escaped as &gt; in text, which XmlTextWriter does and XDocument does not
//
// SO NOTHING IS RESERIALISED. Every <Points> block is kept as its raw source slice, along with the
// text between blocks, and a field edit rewrites only that element's inner text inside the slice.
// Untouched blocks - and untouched fields in edited blocks - are byte-identical by construction,
// which is what carries the seventeen optional elements, the one <SpeechTrigger> in the whole tree,
// and any column a future XmlSpawner adds without this file knowing about it.
//
// NOTHING STRUCTURAL IS REFUSED, because the real data is stranger than the writer is. Forty-seven
// blocks in trammel.xml have their <UniqueId> COMMENTED OUT - `<!--guid-->` sitting exactly where
// the element belongs - so those spawners duplicate on every import instead of replacing. Comments
// round-trip for free here, and `parse` reports them as findings rather than throwing, because a
// reader that refused the file would just mean the editor could not show anyone the problem.
//
// LINE ENDINGS ARE MEASURED, NEVER ASSUMED. Spawns/trammel.xml is CRLF with no trailing newline;
// Spawns/Custom/trammel/GG_OldMarta.xml is LF with one. Both are LF in git's object store -
// core.autocrlf converts on checkout and the custom files have simply not been checked out since
// they were written. A writer that picked either convention would rewrite whole files it was only
// asked to touch one field of.

const INDENT_FIELD = '    ';

class SpawnXmlError extends Error {}

/**
 * Parses a spawn document into a header, raw <Points> blocks each with the text that follows it,
 * and a footer. Every byte of the input lands in exactly one of those.
 */
function parse(text) {
    if (typeof text !== 'string') {
        throw new SpawnXmlError('Not a spawn document');
    }

    const open = text.indexOf('<Spawns>');

    if (open === -1) {
        throw new SpawnXmlError(
            'No <Spawns> root. A spawn file with nothing in it is written as zero bytes, and the '
            + "shard's own reader throws on it too.");
    }

    const close = text.lastIndexOf('</Spawns>');

    if (close === -1) {
        throw new SpawnXmlError('No </Spawns> close');
    }

    const blocks = [];
    const starts = [];
    const ends = [];

    let cursor = open + '<Spawns>'.length;

    for (;;) {
        const start = text.indexOf('<Points>', cursor);

        if (start === -1 || start > close) {
            break;
        }

        const end = text.indexOf('</Points>', start);

        if (end === -1) {
            throw new SpawnXmlError('A <Points> block is not closed');
        }

        starts.push(start);
        ends.push(end + '</Points>'.length);
        cursor = ends[ends.length - 1];
    }

    for (let i = 0; i < starts.length; i++) {
        blocks.push({
            raw: text.slice(starts[i], ends[i]),
            // The text between this block and the next, kept verbatim rather than regenerated.
            // Reconstructing it is how a writer silently reformats a file it was asked to edit one
            // field of - which this got wrong on its first run.
            tail: text.slice(ends[i], i + 1 < starts.length ? starts[i + 1] : close)
        });
    }

    return {
        header: text.slice(0, starts.length > 0 ? starts[0] : close),
        footer: text.slice(close),
        blocks,
        newline: text.includes('\r\n') ? '\r\n' : '\n'
    };
}

/**
 * Things about the file worth telling someone, none of which stop it being read.
 *
 * This is the only place in the editor that can see these at all, which is the argument for
 * reporting rather than tolerating in silence.
 */
function findings(doc) {
    const out = [];

    doc.blocks.forEach((block, index) => {
        const where = `Points[${index}]`;
        const name = get(block, 'Name');
        const label = name === undefined ? where : `${where} '${name}'`;

        if (!has(block, 'UniqueId')) {
            out.push(/<!--\s*[0-9a-fA-F-]{36}\s*-->/.test(block.raw)
                ? `${label} has its UniqueId commented out, so it is duplicated on every import `
                    + 'rather than replaced'
                : `${label} has no UniqueId, so it is duplicated on every import rather than replaced`);
        }

        for (const field of COLUMNS) {
            const n = count(block, field);

            if (n > 1) {
                out.push(`${label} has ${n} <${field}> elements; only the first is read`);
            }
        }
    });

    return out;
}

/** Every element name in a block, in file order. */
function names(block) {
    return [...scannable(block).matchAll(/<([A-Za-z_][\w.-]*)\s*(?:\/>|>)/g)]
        .map((match) => match[1])
        .filter((name) => name !== 'Points');
}

/**
 * The block with comments and CDATA blanked to spaces, so element scanning cannot match inside one
 * while every index still lines up with `raw`.
 */
function scannable(block) {
    return block.raw.replace(/<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>/g,
        (match) => ' '.repeat(match.length));
}

/**
 * The decoded text of an element, `''` for `<Elem />`, or undefined when absent.
 *
 * Absent and empty are different: the reader defaults an absent <Name> to "Spawner" and takes an
 * empty one as "". Sixteen blocks in trammel.xml carry <MinDelay> twice, so this deliberately
 * returns the FIRST - which is what the shard's reader would get - and `count` reports the rest.
 */
function get(block, name) {
    const match = matchOf(block, name);
    return match ? decode(match.inner) : undefined;
}

function has(block, name) {
    return matchOf(block, name) !== null;
}

function count(block, name) {
    return [...scannable(block).matchAll(elementPattern(name))].length;
}

function elementPattern(name) {
    // Either a self-closing <Elem /> or a matched pair. Non-greedy, and a spawner field never
    // nests, so this cannot swallow a sibling.
    return new RegExp(`([ \\t]*)<${name}\\s*/>|([ \\t]*)<${name}>([\\s\\S]*?)</${name}>`, 'g');
}

function matchOf(block, name) {
    const match = elementPattern(name).exec(scannable(block));

    if (!match) {
        return null;
    }

    return {
        index: match.index,
        length: match[0].length,
        indent: match[1] !== undefined ? match[1] : match[2],
        // Read from raw, not the blanked copy, so a comment inside a value survives verbatim.
        inner: match[3] === undefined
            ? ''
            : block.raw.substr(match.index + match[0].indexOf('>') + 1, match[3].length)
    };
}

/**
 * Sets an element's text, inserting it in emission order if it is not there.
 *
 * `undefined` removes the element; `''` writes `<Elem />`, which is what the shard's writer emits
 * for an empty string and is not the same thing as removing it.
 */
function set(block, name, value) {
    if (value === undefined) {
        return remove(block, name);
    }

    const text = typeof value === 'boolean' ? (value ? 'True' : 'False') : String(value);
    const rendered = text === '' ? `<${name} />` : `<${name}>${encode(text)}</${name}>`;
    const match = matchOf(block, name);

    if (match) {
        block.raw =
            block.raw.slice(0, match.index) + match.indent + rendered
            + block.raw.slice(match.index + match.length);

        return block;
    }

    return insert(block, name, rendered);
}

/** Puts a new element where the shard's writer would have put it: COLUMNS order, nothing else. */
function insert(block, name, rendered) {
    const at = COLUMNS.indexOf(name);

    if (at === -1) {
        throw new SpawnXmlError(`'${name}' is not a spawner field`);
    }

    const newline = block.raw.includes('\r\n') ? '\r\n' : '\n';

    for (const later of COLUMNS.slice(at + 1)) {
        const match = matchOf(block, later);

        if (match) {
            block.raw =
                block.raw.slice(0, match.index)
                + `${match.indent}${rendered}${newline}`
                + block.raw.slice(match.index);

            return block;
        }
    }

    // Nothing after it in the order, so it goes on its own line before </Points>.
    const close = block.raw.lastIndexOf('</Points>');
    const indent = block.raw.slice(block.raw.lastIndexOf('\n', close) + 1, close);

    block.raw =
        block.raw.slice(0, close)
        + `${INDENT_FIELD}${rendered}${newline}${indent}`
        + block.raw.slice(close);

    return block;
}

function remove(block, name) {
    const match = matchOf(block, name);

    if (!match) {
        return block;
    }

    // Take the whole line, so removing a field does not leave a blank one where it was.
    let end = match.index + match.length;

    while (end < block.raw.length && (block.raw[end] === '\r' || block.raw[end] === '\n')) {
        end++;
    }

    block.raw = block.raw.slice(0, match.index) + block.raw.slice(end);

    return block;
}

/**
 * A new block, written in the shard's emission order.
 *
 * `pairs` is [name, value] - order there is ignored, COLUMNS decides. `undefined` omits the element
 * entirely, which is how the seventeen optional fields stay optional.
 */
function makeBlock(pairs, newline = '\r\n') {
    const values = new Map(pairs);

    for (const name of values.keys()) {
        if (!COLUMNS.includes(name)) {
            throw new SpawnXmlError(`'${name}' is not a spawner field`);
        }
    }

    const lines = ['<Points>'];

    for (const name of COLUMNS) {
        if (!values.has(name) || values.get(name) === undefined) {
            continue;
        }

        const value = values.get(name);
        const text = typeof value === 'boolean' ? (value ? 'True' : 'False') : String(value);

        lines.push(text === ''
            ? `${INDENT_FIELD}<${name} />`
            : `${INDENT_FIELD}<${name}>${encode(text)}</${name}>`);
    }

    lines.push('  </Points>');

    return { raw: lines.join(newline), tail: '' };
}

/**
 * Appends a block, giving it the same separator every other block in the file uses.
 *
 * Copied from the previous last block rather than assembled, for the same reason nothing else here
 * is assembled: the file gets to keep its own conventions.
 */
function push(doc, block) {
    const last = doc.blocks[doc.blocks.length - 1];

    if (last) {
        block.tail = last.tail;
        last.tail = last.tail || doc.newline + '  ';
    } else {
        block.tail = doc.newline;
    }

    doc.blocks.push(block);

    return doc;
}

function removeAt(doc, index) {
    const removed = doc.blocks.splice(index, 1)[0];

    // The last block's tail is what carries the gap down to </Spawns>, so a delete at the end must
    // hand that gap to whoever is last now.
    if (index === doc.blocks.length && doc.blocks.length > 0) {
        doc.blocks[doc.blocks.length - 1].tail = removed.tail;
    }

    return removed;
}

/** By identity, so a delete earlier in a batch cannot renumber one later in it. */
function indexOfBlock(doc, block) {
    return doc.blocks.indexOf(block);
}

/**
 * Back to bytes.
 *
 * A document with no blocks is refused rather than written. `[XmlSave` writes a zero-byte file when
 * nothing matches (XmlSpawner2.cs:7562) and `ds.ReadXml` then throws on it, so a file the editor
 * emptied would be one the shard can no longer load - reporting only "Error reading xml file",
 * with nothing pointing back here.
 */
function stringify(doc) {
    if (doc.blocks.length === 0) {
        throw new SpawnXmlError(
            'A spawn file with no spawners cannot be written - the shard reads it as a broken file. '
            + 'Delete the file instead.');
    }

    return doc.header + doc.blocks.map((block) => block.raw + block.tail).join('') + doc.footer;
}

// XmlTextWriter escapes exactly these in text content, and notably escapes '>' where a modern
// writer would not. Quotes are not escaped in text.
function encode(text) {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function decode(text) {
    return text
        .replace(/&lt;/g, '<').replace(/&gt;/g, '>')
        .replace(/&quot;/g, '"').replace(/&apos;/g, "'")
        .replace(/&amp;/g, '&');
}

/**
 * The writer's column order, transcribed from XmlSpawner2.cs:7319-7389.
 *
 * This is the ONLY thing that decides where an element goes in the file - the writer's assignment
 * order is irrelevant, which is why Team comes before Amount here despite being assigned after it.
 * Forty are always written; the rest are omitted when null.
 */
const COLUMNS = [
    'Name', 'UniqueId', 'Map', 'X', 'Y', 'Width', 'Height',
    'CentreX', 'CentreY', 'CentreZ', 'Range', 'MaxCount',
    'MinDelay', 'MaxDelay', 'DelayInSec', 'Duration', 'DespawnTime',
    'ProximityRange', 'ProximityTriggerSound', 'ProximityTriggerMessage',
    'ObjectPropertyName', 'ObjectPropertyItemName', 'SetPropertyItemName',
    'ItemTriggerName', 'NoItemTriggerName', 'MobTriggerName', 'MobPropertyName',
    'PlayerPropertyName', 'TriggerProbability', 'SpeechTrigger', 'SkillTrigger',
    'InContainer', 'ContainerX', 'ContainerY', 'ContainerZ',
    'MinRefractory', 'MaxRefractory', 'TODStart', 'TODEnd', 'TODMode', 'KillReset',
    'ExternalTriggering', 'SequentialSpawning', 'RegionName',
    'AllowGhostTriggering', 'AllowNPCTriggering', 'SpawnOnTrigger', 'ConfigFile',
    'SmartSpawning', 'TickReset', 'WayPoint', 'Team', 'Amount',
    'IsGroup', 'IsRunning', 'IsHomeRangeRelative', 'Objects2'
];

module.exports = {
    parse, stringify, findings, names, get, has, count, set, remove,
    makeBlock, push, removeAt, indexOfBlock,
    encode, decode, COLUMNS, SpawnXmlError
};
