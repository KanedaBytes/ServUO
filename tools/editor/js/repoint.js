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
//
// ROUTABILITY IS THE GATE, AND DISTANCE IS ONLY THE TIEBREAK
//
// It used to be the other way round, and `brit-inn-central` is what that cost. A destination was
// judged by the straight line from its OWN CENTRE TILE: 7 tiles to `uo-central-britain-road-2`,
// comfortably inside the cap, so the tool kept it. But that centre sits inside a sealed building
// nothing stands on, and the route that waypoint actually hands the walker - to the ARRIVAL - is
// 15 tiles and plans one way only. Distance to a centre is not routability to the arrivals.
//
// So an approach now has to reach every one of the destination's arrivals, both ways, and the
// centre distance only orders the survivors. Two consequences worth stating:
//
//   - `options.targets` is what an approach must reach. For an arrival that is the arrival itself;
//     for a destination it is EVERY arrival it has. A destination with no arrivals falls back to
//     its own tile, or nothing would be gated at all.
//   - `options.routes` is INJECTED, exactly as `options.canReach` already is, because only the
//     engine can answer it and this module may not reach for one. The CLI hands in a real oracle
//     built from the `nav-hop-probe` token on the bot class; the browser hands in one when the
//     shard is up and nothing when it is down. Absent or undecided, the rule falls back to the old
//     distance ordering and says so through `verified: false` - the editor keeps working with the
//     shard down, which is the promise directly above this paragraph.
//
// NOT `nav-hop`, WHICH CANNOT ANSWER THIS. It goes through MovementPath, and MovementPath fails
// any goal within one tile (MovementPath.cs:34-35) - so the best approach a record could possibly
// have reads as no route. `nav-hop-probe` calls the algorithm directly and takes the probe class,
// which is why the oracle is built on that one.

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
 * What an approach has to reach: every arrival of a destination, or an arrival itself.
 *
 * A destination with no arrivals answers with its own tile rather than an empty list, because an
 * empty list would make every candidate pass vacuously - the exact shape of the bug this replaced.
 */
export function targetsFor(nav, record) {
    if (!record || record.destination !== undefined || record.id === undefined) {
        return [record];
    }

    const arrivals = (nav.arrivals || []).filter((a) => a.destination === record.id);

    return arrivals.length > 0 ? arrivals : [record];
}

/**
 * Is this waypoint inside the hop cap of every target?
 *
 * Exported because the CLI has to know which pairs are worth asking the shard about BEFORE it
 * asks - a route query per waypoint per arrival over 1003 waypoints is not a thing to send.
 */
export function withinCap(waypoint, targets, cap = HOP_CAP) {
    return targets.every((target) => tiles(waypoint, target) <= cap);
}

/**
 * Does this waypoint serve every target - inside the cap, and routable both ways?
 *
 * Returns `{ok, decided}`. `decided` is false when the oracle was absent or would not say, which
 * is not the same as a refusal: the caller downgrades the whole answer to unverified rather than
 * dropping a candidate on a question nobody could ask.
 */
function serves(waypoint, targets, cap, routes) {
    let decided = true;

    // The cap is measured to the TARGET, not to the record. This alone is the brit-inn-central
    // fix: 7 tiles from the centre, 15 from the arrival.
    if (!withinCap(waypoint, targets, cap)) {
        return { ok: false, decided: true };
    }

    for (const target of targets) {
        if (!routes) {
            decided = false;
            continue;
        }

        // Both sides are records with x/y/z, in both orders, so an oracle can key on the tiles it
        // actually asked the engine about rather than on an id the engine never sees.
        const there = routes(waypoint, target);
        const back = routes(target, waypoint);

        if (there === null || there === undefined || back === null || back === undefined) {
            decided = false;
            continue;
        }

        if (!there || !back) {
            return { ok: false, decided: true };
        }
    }

    return { ok: true, decided };
}

/**
 * What a record's `waypoints` field should say, given where the record now is.
 *
 * THE NEAREST FIRST, THEN EVERY LISTED ONE STILL INSIDE THE CAP. Keeping the others is not
 * tidiness: a shop fronting two streets is approached from both, and the route takes whichever its
 * search reaches first. Dropping them would quietly halve a destination's approaches.
 *
 * `stranded` is the answer when nothing reachable SERVES - inside the cap of every target and
 * routable both ways to each. The field is then left EXACTLY as it was: a repoint to something
 * unreachable is worse than no repoint, and this is a road somebody has to author rather than a
 * field that can be corrected.
 *
 * `verified` is false when the oracle was absent or would not answer for some candidate. The
 * answer is still returned - it is the old distance ordering - but a caller that writes files
 * should refuse to write on it. Unverified is not the same as refused.
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
    const targets = options.targets || targetsFor(nav, record);
    const routes = options.routes || null;

    let verified = true;

    // Distance orders the candidates; it no longer decides between them. The sort is stable, so
    // equal distances keep navigation.json's own order - the same tiebreak the old strict `<`
    // scan gave, and the reason two runs over one file agree.
    const ranked = (nav.waypoints || [])
        .filter((waypoint) => waypoint.map === map && canReach.has(waypoint.id))
        .map((waypoint) => ({ waypoint, distance: tiles(waypoint, record) }))
        .sort((a, b) => a.distance - b.distance);

    let chosen = null;
    let chosenDistance = Infinity;

    for (const { waypoint, distance } of ranked) {
        const verdict = serves(waypoint, targets, cap, routes);

        if (!verdict.decided) {
            verified = false;
        }

        if (verdict.ok) {
            chosen = waypoint.id;
            chosenDistance = distance;
            break;
        }
    }

    // Reported rather than chosen: the nearest reachable waypoint, whether or not it serves. It is
    // what the stranded message names, and it is often the one that just failed the gate - which
    // is the sentence somebody needs to read.
    const { nearest, tiles: distance } = nearestReachable(nav, record, map, canReach);

    if (chosen === null) {
        return {
            stranded: true,
            changed: false,
            nearest,
            tiles: distance,
            waypoints: record.waypoints || '',
            verified
        };
    }

    const kept = listed(record.waypoints).filter((id) => {
        const waypoint = byId.get(id);

        if (id === chosen || !waypoint || !canReach.has(id)) {
            return false;
        }

        const verdict = serves(waypoint, targets, cap, routes);

        if (!verdict.decided) {
            verified = false;
        }

        return verdict.ok;
    });

    const next = [chosen, ...kept].join(' ');

    return {
        stranded: false,
        changed: next !== (record.waypoints || ''),
        nearest: chosen,
        tiles: chosenDistance,
        waypoints: next,
        verified
    };
}

