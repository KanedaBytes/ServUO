'use strict';

// The <Objects2> micro-format: what a spawner actually spawns.
//
//   GGBaker:MX=1:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1
//
// Entries are joined by the literal `:OBJ=` (XmlSpawner2.cs:12466). The leading segment is a full
// XmlSpawner type string - a type name, optional constructor args after a comma, and optional
// `/property/value` pairs - and everything after it is eleven fixed keys.
//
// THERE IS NO ESCAPING. None at all: no quoting, no backslash, no encoding. That makes two rules
// load-bearing rather than pedantic, and both fail SILENTLY on the shard:
//
//   - a type string containing `:MX=` splits the entry into three parts, and the reader discards
//     the whole entry (XmlSpawner2.cs:12701-12704) - a spawner that simply stops spawning
//   - a type string containing any other key token is misparsed, because GetParm searches the
//     ENTIRE entry for the first occurrence and reads to the next colon (XmlSpawner2.cs:12614)
//
// So `checkType` refuses those rather than writing something that vanishes. A bare `:` is fine and
// is left alone; it is only the `:KEY=` tokens that collide.
//
// An unedited entry is re-emitted from its own source text, so a file the editor opened and saved
// without touching the spawn list is byte-identical even if some value was written in a form this
// module would not have chosen.

const KEYS = ['MX', 'SB', 'RT', 'TO', 'KL', 'RK', 'CA', 'DN', 'DX', 'SP', 'PR'];

const SEPARATOR = ':OBJ=';

class Objects2Error extends Error {}

/**
 * Splits an <Objects2> string into entries.
 *
 * An empty string is zero entries, which is a real state - ten stock spawners are written as
 * `<Objects2 />` and spawn nothing.
 */
function parse(text) {
    if (!text) {
        return [];
    }

    return splitOn(text, SEPARATOR).map((raw, index) => parseEntry(raw, index));
}

/** Literal-substring split, matching BaseXmlSpawner.SplitString rather than String.Split. */
function splitOn(text, separator) {
    const parts = [];

    let from = 0;

    for (;;) {
        const at = text.indexOf(separator, from);

        if (at === -1) {
            parts.push(text.slice(from));
            return parts;
        }

        parts.push(text.slice(from, at));
        from = at + separator.length;
    }
}

function parseEntry(raw, index) {
    const at = raw.indexOf(':MX=');
    const type = at === -1 ? raw : raw.slice(0, at);
    const entry = { raw, type, index };

    for (const key of KEYS) {
        entry[key] = readParm(raw, key);
    }

    // CA defaults to true, EXCEPT when no kills are needed, where it defaults to false
    // (XmlSpawner2.cs:12756-12759). Modelled rather than normalised: writing an explicit CA where
    // the file had none would change bytes for no reason.
    if (entry.CA === undefined) {
        entry.CA = entry.KL !== undefined && Number(entry.KL) !== 0;
    }

    return entry;
}

/** As GetParm does: find the first `:KEY=` anywhere in the entry, read to the next colon. */
function readParm(raw, key) {
    const at = raw.indexOf(`:${key}=`);

    if (at === -1) {
        return undefined;
    }

    const from = at + key.length + 2;
    const to = raw.indexOf(':', from);

    return to === -1 ? raw.slice(from) : raw.slice(from, to);
}

/**
 * A type string that cannot be written, or null when it can.
 *
 * Returns the reason rather than a boolean because the reason is the whole value: "this will be
 * silently dropped by the shard" is not something anyone should have to work out from a spawner
 * that quietly stopped working.
 */
function checkType(type) {
    if (typeof type !== 'string' || type.trim() === '') {
        return 'A spawn entry needs a type name.';
    }

    if (type.includes(SEPARATOR)) {
        return `A type name cannot contain '${SEPARATOR}' - it is what separates one entry from the next.`;
    }

    for (const key of KEYS) {
        if (type.includes(`:${key}=`)) {
            return `A type name cannot contain ':${key}=' - the shard would read it as the `
                + `${key} field and ${key === 'MX' ? 'discard the whole entry' : 'misparse the entry'}.`;
        }
    }

    // '<', '>' and '&' are legal and do occur - one Eodon spawner nests a whole equipment
    // expression in angle brackets - so only whitespace that would break the line is refused.
    if (/[\r\n\t]/.test(type)) {
        return 'A type name cannot contain a line break or a tab.';
    }

    return null;
}

/**
 * An entry the shard would not read back, or null when it would.
 *
 * Wider than checkType, and the difference is where the bug hides. A type name is only one way to
 * end up with two `:MX=` in an entry; whatever put them there, LoadSpawnObjectsFromString2 splits
 * on `:MX=`, requires exactly two parts, and **discards the entry entirely** otherwise
 * (XmlSpawner2.cs:12701-12704). The spawner then simply stops spawning that type, with nothing
 * said in any log.
 */
function checkEntry(entry) {
    const reason = checkType(entry.type);

    if (reason) {
        return reason;
    }

    const raw = entry.raw === undefined ? renderEntry(entry) : entry.raw;
    const marks = splitOn(raw, ':MX=').length - 1;

    if (marks > 1) {
        return `'${entry.type}' has ':MX=' ${marks} times, and the shard discards any entry that `
            + 'does not have exactly one.';
    }

    return null;
}

/** Rebuilds one entry in the writer's field order (XmlSpawner2.cs:12466-12476). */
function renderEntry(entry) {
    const reason = checkType(entry.type);

    if (reason) {
        throw new Objects2Error(reason);
    }

    const value = (key, fallback) => {
        const raw = entry[key];

        if (raw === undefined || raw === null || raw === '') {
            return fallback;
        }

        return typeof raw === 'boolean' ? (raw ? '1' : '0') : String(raw);
    };

    return [
        entry.type,
        `MX=${value('MX', '1')}`,
        `SB=${value('SB', '0')}`,
        `RT=${value('RT', '0')}`,
        `TO=${value('TO', '0')}`,
        `KL=${value('KL', '0')}`,
        `RK=${value('RK', '0')}`,
        `CA=${value('CA', '1')}`,
        `DN=${value('DN', '-1')}`,
        `DX=${value('DX', '-1')}`,
        `SP=${value('SP', '1')}`,
        `PR=${value('PR', '-1')}`
    ].join(':');
}

/**
 * Entries back to an <Objects2> string.
 *
 * An entry still carrying its `raw` and marked unedited is emitted verbatim, so opening and saving
 * a file without touching its spawn list cannot reformat one.
 */
function stringify(entries) {
    return entries
        .map((entry) => (entry.edited || entry.raw === undefined ? renderEntry(entry) : entry.raw))
        .join(SEPARATOR);
}

/**
 * Anomalies in a spawn list. None stop it being read; the shard reads all of them without a word.
 *
 * Found by running this over the whole tree: two entries in trammel.xml carry `:PR=` twice, and one
 * in underworld.xml stops after `DN` with three keys missing. Both are harmless - GetParm takes the
 * first occurrence and absent keys default - but they are the kind of thing nobody can see until
 * something looks.
 */
function findings(text) {
    const out = [];

    parse(text).forEach((entry, index) => {
        const where = `entry ${index + 1} (${entry.type || 'no type'})`;

        for (const key of KEYS) {
            const seen = splitOn(entry.raw, `:${key}=`).length - 1;

            if (seen > 1) {
                out.push(`${where} has ':${key}=' ${seen} times; the shard reads the first`);
            }
        }

        const missing = KEYS.filter((key) => entry.raw.indexOf(`:${key}=`) === -1);

        if (missing.length > 0) {
            out.push(`${where} is missing ${missing.map((k) => `'${k}'`).join(', ')}; `
                + 'the shard defaults them');
        }
    });

    return out;
}

/** A fresh entry with the writer's defaults, ready to be edited. */
function makeEntry(type, maxCount = 1) {
    const reason = checkType(type);

    if (reason) {
        throw new Objects2Error(reason);
    }

    return { type, MX: String(maxCount), edited: true };
}

module.exports = {
    parse, stringify, renderEntry, makeEntry, checkType, checkEntry, findings,
    KEYS, SEPARATOR, Objects2Error
};
