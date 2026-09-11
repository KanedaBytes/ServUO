// repoint.js — which waypoint should a destination or an arrival name?
//
// WHY THIS IS A MODULE AND NOT A SCRIPT
//
// `repoint-arrivals.js` has known the answer since the Britain rebase, and it is a CommonJS CLI:
// you have to remember to run it, after the fact, over a whole town or one named record. The
// editor never called it, and a record dragged in the editor kept naming whatever it named before.
// `brit-home-jeweler` is what that cost - moved across town, its destination and both arrivals
// still naming `uo-britain-bank` 223 to 236 tiles back, and the shard booted with ZERO nav warnings
// over a house no bot could reach (commit 83107aa0).
//
// So the rule lives here, as pure functions with no DOM and no `fs`, and both halves call it: the
// browser on a drag, and the script for a town-wide pass. `HOP_CAP` comes from `validate.js`, which
// `coverage.test.js` already pins to `Config/Custom.cfg` - the script used to carry a third,
// untested copy of the number.
//
// WHAT REACHABLE MEANS HERE, AND WHAT IT DOES NOT
//
// A breadth-first flood over authored edges from a home waypoint, and distance is straight-line
// Chebyshev. It is NOT an engine walk-verify: the editor asks the shard for that separately with
// the `nav-hop` token, because only the engine can answer it and the editor has to keep working
// with the shard down. The flood is what stops a repoint trading a long last hop for no route at
// all - a nearer waypoint on an island is worse than the far one already named.

// One-way: this file imports from validate.js and validate.js imports nothing from here. The
// jeweler check itself (`farListed`) lives there for that reason, since validation needs it on
// every edit and a cycle would leave one of the two modules half-initialised in the browser.
import { HOP_CAP } from './validate.js';

export { HOP_CAP };

/** The default flood origin. Britain's bank is the middle of the largest component. */
export const DEFAULT_HOME = 'uo-britain-bank';

/** Tile distance, which is what the hop cap is measured in. */
export function tiles(a, b) {
    return Math.max(Math.abs(a.x - b.x), Math.abs(a.y - b.y));
}

/** Every waypoint id the home waypoint can reach along authored edges, in both directions. */
export function reachableFrom(nav, home = DEFAULT_HOME) {
    const links = new Map();

    for (const edge of nav.edges || []) {
        if (!links.has(edge.from)) links.set(edge.from, []);
        if (!links.has(edge.to)) links.set(edge.to, []);

        links.get(edge.from).push(edge.to);
        links.get(edge.to).push(edge.from);
    }

    const seen = new Set([home]);
    const queue = [home];

    while (queue.length > 0) {
        for (const next of links.get(queue.pop()) || []) {
            if (!seen.has(next)) {
                seen.add(next);
                queue.push(next);
            }
        }
    }

    return seen;
}

/**
 * The nearest reachable waypoint to a record on its own facet.
 *
 * Returns `{nearest: null, tiles: Infinity}` when the facet holds none, which the caller has to
 * treat as stranded rather than as a distance of zero.
 */
export function nearestReachable(nav, record, map, canReach) {
    let nearest = null;
    let best = Infinity;

    for (const waypoint of nav.waypoints || []) {
        if (waypoint.map !== map || !canReach.has(waypoint.id)) {
            continue;
        }

        const distance = tiles(waypoint, record);

        if (distance < best) {
            best = distance;
            nearest = waypoint.id;
        }
    }

    return { nearest, tiles: best };
}

/** The ids in a space-separated list field. */
export function listed(value) {
    return String(value || '').split(/\s+/).filter(Boolean);
}

/**
 * What a record's `waypoints` field should say, given where the record now is.
 *
 * THE NEAREST FIRST, THEN EVERY LISTED ONE STILL INSIDE THE CAP. Keeping the others is not
 * tidiness: a shop fronting two streets is approached from both, and the route takes whichever its
 * search reaches first. Dropping them would quietly halve a destination's approaches.
 *
 * `stranded` is the answer when nothing reachable is within the cap. The field is then left
 * EXACTLY as it was - a repoint to something unreachable is worse than no repoint, and this is a
 * road somebody has to author rather than a field that can be corrected.
 */
export function repointFor(nav, record, options = {}) {
    const cap = options.cap === undefined ? HOP_CAP : options.cap;
    const home = options.home || DEFAULT_HOME;
    const map = options.map || record.map;
    const byId = options.byId || new Map((nav.waypoints || []).map((w) => [w.id, w]));

    // A HOME THAT IS NOT IN THE GRAPH REACHES NOTHING, AND THAT LOOKS EXACTLY LIKE STRANDED.
    // The flood from a missing id returns a set of one, so every record on the facet would answer
    // "nothing reachable within the cap" - a sentence about the data that is really a sentence
    // about the flood. repoint-arrivals.js has always refused outright for this reason; the
    // editor cannot refuse mid-drag, so it says which of the two happened instead.
    if (!options.canReach && !byId.has(home)) {
        return {
            stranded: true,
            homeMissing: home,
            changed: false,
            nearest: null,
            tiles: Infinity,
            waypoints: record.waypoints || ''
        };
    }

    const canReach = options.canReach || reachableFrom(nav, home);

    const { nearest, tiles: distance } = nearestReachable(nav, record, map, canReach);

    if (nearest === null || distance > cap) {
        return {
            stranded: true,
            changed: false,
            nearest,
            tiles: distance,
            waypoints: record.waypoints || ''
        };
    }

    const kept = listed(record.waypoints).filter((id) => {
        const waypoint = byId.get(id);

        return id !== nearest && waypoint && canReach.has(id) && tiles(waypoint, record) <= cap;
    });

    const next = [nearest, ...kept].join(' ');

    return {
        stranded: false,
        changed: next !== (record.waypoints || ''),
        nearest,
        tiles: distance,
        waypoints: next
    };
}

