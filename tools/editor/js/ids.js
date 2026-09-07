// Auto-generated ids, the same way [NavMark makes them.
//
// NavigationCommands.NextId: take the smallest nav zone containing the point - the one whose tags
// are most specific - use the FIRST token of its tags as the prefix, fall back to its id, and fall
// back to "wp" outside every zone. Then count up from 1 until the id is free.
//
// Two deliberate differences from the shard, both toward being stricter.
//
// The shard checks NavigationSystem.Graph.Contains, which only holds waypoints that survived
// binding, so it can hand back an id that collides with a record that was dropped with a warning.
// This checks every id in the file.
//
// And it checks across kinds, not just within one. Nav.TryRoute accepts an id naming a waypoint OR
// a destination, so a waypoint and a destination sharing an id is genuinely ambiguous even though
// the schema puts them in different sections and the validator would not complain.

const NAV_KINDS = ['wp', 'dest', 'zone', 'route'];

/** The smallest zone containing the point on that facet, or null. */
export function zoneAt(x, y, mapName, shapes) {
    let best = null;
    let bestArea = Infinity;

    for (const shape of shapes) {
        if (shape.layer !== 'nav-zones' || shape.map !== mapName || !shape.rect) {
            continue;
        }

        const [zx, zy, width, height] = shape.rect;

        if (x < zx || x >= zx + width || y < zy || y >= zy + height) {
            continue;
        }

        const area = width * height;

        // Strictly less than, so the FIRST zone in file order wins a tie - which is what
        // Nav.SmallestZoneAt does, and ties are not hypothetical with nested town rectangles.
        if (area < bestArea) {
            bestArea = area;
            best = shape;
        }
    }

    return best;
}

export function prefixAt(x, y, mapName, shapes) {
    const zone = zoneAt(x, y, mapName, shapes);

    if (!zone) {
        return 'wp';
    }

    const tags = String(zone.props.tags || '').split(' ').filter(Boolean);

    return tags[0] || zone.props.id || 'wp';
}

/**
 * The next free `<prefix>-<n>` for a point.
 *
 * Editable in the create form before the first save: an auto id is a starting point, not a
 * decision, and a hand-written one usually reads better in a route list.
 */
export function nextId(x, y, mapName, shapes) {
    const prefix = prefixAt(x, y, mapName, shapes);
    const taken = new Set();

    for (const shape of shapes) {
        const at = shape.id.indexOf(':');

        if (NAV_KINDS.includes(shape.id.slice(0, at))) {
            taken.add(shape.id.slice(at + 1).toLowerCase());
        }
    }

    for (let n = 1; ; n++) {
        const candidate = `${prefix}-${n}`;

        if (!taken.has(candidate.toLowerCase())) {
            return candidate;
        }
    }
}

/** Every nav id already in use, lowercased. */
function takenIds(shapes) {
    const taken = new Set();

    for (const shape of shapes) {
        const at = shape.id.indexOf(':');

        if (NAV_KINDS.includes(shape.id.slice(0, at))) {
            taken.add(shape.id.slice(at + 1).toLowerCase());
        }
    }

    return taken;
}

/**
 * The id for a point inserted into an existing hop: its left neighbour, plus a letter.
 *
 * A road is read as a sequence, and `nextId` does not know it is looking at one - it names a
 * point after the smallest ZONE containing it, so inserting a point into the road through the
 * bank quarter called it `bank-2`, sitting between `town-9` and `town-10` and belonging to
 * neither. The number in a road id is its position, and a point inserted between 10 and 11 is
 * 10a whatever it happens to be standing inside.
 *
 * Falls back to the zone scheme when neither neighbour is numbered - `brit-gate-w` has no
 * position to be after, and `brit-gate-wa` would be a worse name than the zone would give.
 */
export function insertedId(fromId, toId, mapName, x, y, shapes) {
    const taken = takenIds(shapes);

    for (const neighbour of [fromId, toId]) {
        // Trailing letters are stripped so inserting beside `town-10a` still counts from
        // `town-10` and cannot produce `town-10aa`.
        const match = /^(.*\d)[a-z]*$/i.exec(String(neighbour || ''));

        if (!match) {
            continue;
        }

        const base = match[1];

        for (let i = 0; i < 26; i++) {
            const candidate = `${base}${String.fromCharCode(97 + i)}`;

            if (!taken.has(candidate.toLowerCase())) {
                return candidate;
            }
        }
    }

    return nextId(x, y, mapName, shapes);
}

/**
 * The display name for an inserted point, carried over from the neighbour it is named after.
 *
 * A road whose points are named "Bank road 9", "Bank road 10" reads as a road; one with an
 * unnamed point dropped into the middle of it reads as two roads. Returns an empty string when
 * the neighbour has no name, which is the common case and correctly leaves the field unset.
 */
export function insertedName(fromName, toName, insertedIdValue) {
    const source = fromName || toName;

    if (!source) {
        return '';
    }

    const suffix = /([a-z])$/i.exec(insertedIdValue);

    return suffix ? `${source}${suffix[1]}` : source;
}
