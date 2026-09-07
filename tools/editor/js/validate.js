// The shard's structural checks, reproduced so a save can be judged before it is made.
//
// THE SHARD IS STILL THE AUTHORITY. This is a preview, not a gate: it runs live in the side panel
// so most rejections never reach a round trip, and the bridge runs it only for an explicit dry
// run. A false positive in here must never be able to stop someone writing a file the shard would
// have accepted.
//
// TWO TIERS, and getting them the right way round matters more than it sounds. On this shard a
// dangling edge id is a WARNING: the edge is dropped and the reload succeeds. So are over-cap hops
// and destinations without arrivals. What actually refuses a reload is a duplicate id, an unknown
// facet, a bad kind or mode, a self-edge, a route with fewer than two waypoints - and any unknown
// JSON key, because JsonConfig sets MissingMemberHandling.Error. An editor that called a warning
// fatal would block legal edits; one that called a fatal a warning would report "saved" over data
// it had just broken.
//
// Messages are phrased to match the shard's, so a preview line and a banner line coming back from
// the ack are recognisably the same sentence. What we can add that the shard cannot is `shapeId`:
// the shard reports `waypoints[37]`, an array index that shifts under a delete, and the editor can
// turn our copy of the same finding into a selection.
//
// NOT REPRODUCED, deliberately, and the panel says so rather than implying completeness:
//   - vendor type names, which the shard resolves against its own type table
//   - selfTest pathability and [NavAudit's blocked-edge check, which need real map data
//   - disconnected components across facets - we do report the count, not the fix

/** Must match Custom.NavHopMaxTiles. coverage.test.js asserts it against Config/Custom.cfg. */
export const HOP_CAP = 12;

export const SCHEMA_VERSION = 1;

export const FACETS = ['Felucca', 'Trammel', 'Ilshenar', 'Malas', 'Tokuno', 'TerMur'];

// NavIds.IsValid: letters, digits, '-', '_' and '.'. No spaces, because a list field is a
// space-separated string rather than a JSON array.
const ID = /^[A-Za-z0-9._-]+$/;

/**
 * Every key each record type may carry, which is what stands in for MissingMemberHandling.Error.
 *
 * project.js holds the same lists as TEMPLATES, in the order a new record is written. It cannot be
 * imported here - it is CommonJS and this file is loaded as an ES module by the browser - so
 * validate.test.js asserts the two agree instead.
 */
export const KEYS = {
    costTags: ['tag', 'multiplier'],
    waypoints: ['id', 'name', 'map', 'x', 'y', 'z', 'arrivalRange', 'tags', 'note'],
    edges: ['from', 'to', 'kind', 'tags', 'note'],
    destinations: ['id', 'name', 'type', 'map', 'x', 'y', 'z', 'tags', 'waypoints', 'note'],
    arrivals: ['destination', 'x', 'y', 'z', 'exclusive', 'exact', 'waypoints', 'note'],
    zones: ['id', 'map', 'x', 'y', 'width', 'height', 'tags', 'note'],
    routes: ['id', 'map', 'mode', 'waypoints', 'note'],
    selfTests: ['from', 'to'],
    watch: ['id', 'route', 'destinations'],
    townsfolk: ['id', 'name', 'title', 'body', 'route'],
    shopkeepers: ['id', 'spawner', 'stockSpawner', 'vendor', 'shop', 'home', 'closes'],
    restrictedZones: ['name', 'map', 'x', 'y', 'width', 'height']
};

/** Which record fields the editor keeps as geometry rather than in props, by shape id prefix. */
export const GEOMETRY_KEYS = {
    wp: ['map', 'x', 'y', 'z'],
    edge: [],
    dest: ['map', 'x', 'y', 'z'],
    arr: ['x', 'y', 'z'],
    zone: ['map', 'x', 'y', 'width', 'height'],
    route: ['map'],
    costTag: [],
    restricted: ['map', 'x', 'y', 'width', 'height'],
    shopkeeper: [],
    watchpost: [],
    townsperson: []
};

// Section AND file, because `zone:` and `restricted:` both name a section called "zones" in two
// different files - the same collision unproject has to resolve.
const SECTION_FOR_PREFIX = {
    wp: ['navigation', 'waypoints'],
    edge: ['navigation', 'edges'],
    dest: ['navigation', 'destinations'],
    arr: ['navigation', 'arrivals'],
    zone: ['navigation', 'zones'],
    route: ['navigation', 'routes'],
    costTag: ['navigation', 'costTags'],
    restricted: ['restrictedZones', 'zones'],
    shopkeeper: ['dailyLife', 'shopkeepers'],
    watchpost: ['dailyLife', 'watch'],
    townsperson: ['dailyLife', 'townsfolk']
};

// ---- small helpers ---------------------------------------------------------------------------

function tokens(value) {
    return typeof value === 'string' ? value.split(' ').filter((part) => part.length > 0) : [];
}

function blank(value) {
    return value === undefined || value === null || String(value).trim() === '';
}

/** Chebyshev, because UO movement is eight-directional and so is every other distance we use. */
function chebyshev(a, b) {
    return Math.max(Math.abs(a.x - b.x), Math.abs(a.y - b.y));
}

function lower(value) {
    return typeof value === 'string' ? value.toLowerCase() : value;
}

class Report {
    constructor() {
        this.fatal = [];
        this.warnings = [];
    }

    bad(where, message, shapeId) {
        this.fatal.push({ severity: 'fatal', where, message: `${where} ${message}`, shapeId: shapeId || null });
    }

    warn(message, shapeId) {
        this.warnings.push({ severity: 'warning', where: null, message, shapeId: shapeId || null });
    }
}

/**
 * Shared per-record checks: the null, the id, the facet, the token lists, and the unknown key.
 *
 * `seen` is a per-section map of lowercased id to the index that first used it, because the shard
 * compares ids with OrdinalIgnoreCase and a case-only difference is a duplicate, not a new record.
 */
function checkRecord(report, record, where, section, idKey, seen, shapeId) {
    if (record === null || typeof record !== 'object') {
        report.bad(where, 'is null', shapeId);
        return false;
    }

    for (const key of Object.keys(record)) {
        if (!KEYS[section].includes(key)) {
            report.bad(where, `has the unknown key '${key}'`, shapeId);
        }
    }

    if (idKey) {
        const id = record[idKey];

        if (blank(id) || !ID.test(String(id))) {
            report.bad(where,
                `has an invalid or missing id '${id === undefined ? '' : id}' `
                + "(letters, digits, '-', '_' and '.' only)", shapeId);
        } else if (seen.has(lower(id))) {
            report.bad(where, `duplicates the id '${id}'`, shapeId);
        } else {
            seen.set(lower(id), where);
        }
    }

    if ('map' in record && !FACETS.includes(record.map)) {
        report.bad(where,
            `map '${record.map}' is not a valid facet. Valid: ${FACETS.join(', ')}`, shapeId);
    }

    for (const key of ['tags', 'waypoints', 'destinations']) {
        if (key in record && record[key] !== undefined && record[key] !== null) {
            for (const token of tokens(record[key])) {
                if (!ID.test(token)) {
                    report.bad(where, `contains the invalid token '${token}'`, shapeId);
                }
            }
        }
    }

    return true;
}

function checkSchemaVersion(report, store, where) {
    if (typeof store.schemaVersion === 'number' && store.schemaVersion > SCHEMA_VERSION) {
        report.bad(where,
            `schemaVersion ${store.schemaVersion} is newer than this build understands `
            + `(${SCHEMA_VERSION}).`);
    }
}

// ---- navigation ------------------------------------------------------------------------------

export function validateNavigation(nav, options, report) {
    const out = report || new Report();
    const cap = (options && options.hopCap) || HOP_CAP;

    if (!nav) {
        return out;
    }

    checkSchemaVersion(out, nav, 'navigation.json');

    const byId = new Map();
    const tagSeen = new Map();

    (nav.costTags || []).forEach((tag, i) => {
        const where = `costTags[${i}]`;

        if (!checkRecord(out, tag, where, 'costTags', null, new Map())) return;

        if (blank(tag.tag) || !ID.test(String(tag.tag))) {
            out.bad(where, `has an invalid tag '${tag.tag === undefined ? '' : tag.tag}'`);
        } else if (tagSeen.has(lower(tag.tag))) {
            out.bad(where, `duplicates the tag '${tag.tag}'`);
        } else {
            tagSeen.set(lower(tag.tag), where);
        }

        if (typeof tag.multiplier === 'number' && !(tag.multiplier > 0)) {
            out.bad(where, `multiplier must be greater than zero (found ${tag.multiplier})`);
        }
    });

    const wpSeen = new Map();

    (nav.waypoints || []).forEach((wp, i) => {
        const where = `waypoints[${i}]`;

        if (!checkRecord(out, wp, where, 'waypoints', 'id', wpSeen, `wp:${wp && wp.id}`)) return;

        if (typeof wp.arrivalRange === 'number' && wp.arrivalRange < 0) {
            out.bad(where, `arrivalRange must not be negative (found ${wp.arrivalRange})`,
                `wp:${wp.id}`);
        }

        if (!byId.has(wp.id)) {
            byId.set(wp.id, wp);
        }
    });

    const edgeCount = new Map();

    (nav.edges || []).forEach((edge, i) => {
        const where = `edges[${i}]`;
        const shapeId = edge ? `edge:${edge.from}>${edge.to}` : null;

        if (!checkRecord(out, edge, where, 'edges', null, new Map(), shapeId)) return;

        for (const end of ['from', 'to']) {
            if (blank(edge[end]) || !ID.test(String(edge[end]))) {
                out.bad(where, `has an invalid '${end}' id '${edge[end] === undefined ? '' : edge[end]}'`,
                    shapeId);
            }
        }

        if (lower(edge.from) === lower(edge.to)) {
            out.bad(where, `links '${edge.from}' to itself`, shapeId);
        }

        const kind = edge.kind === undefined ? 'walk' : edge.kind;

        if (kind !== 'walk' && kind !== 'gate') {
            out.bad(where, `kind '${edge.kind}' is not 'walk' or 'gate'`, shapeId);
        }

        const a = byId.get(edge.from);
        const b = byId.get(edge.to);

        // Dropped with a warning by NavGraph.AddEdge, not refused - the rest of the graph loads.
        if (!a) {
            out.warn(`edge '${edge.from}' -> '${edge.to}' dropped: no waypoint '${edge.from}'`, shapeId);
        }

        if (!b) {
            out.warn(`edge '${edge.from}' -> '${edge.to}' dropped: no waypoint '${edge.to}'`, shapeId);
        }

        if (!a || !b) return;

        edgeCount.set(edge.from, (edgeCount.get(edge.from) || 0) + 1);
        edgeCount.set(edge.to, (edgeCount.get(edge.to) || 0) + 1);

        if (kind === 'walk' && a.map !== b.map) {
            out.warn(
                `walk edge '${edge.from}' -> '${edge.to}' dropped: it crosses facets `
                + `(${a.map} to ${b.map}). Use kind "gate".`, shapeId);
            return;
        }

        const length = chebyshev(a, b);

        if (kind === 'walk' && length > cap) {
            out.warn(
                `edge '${edge.from}' -> '${edge.to}' is ${length} tiles, over the ${cap}-tile hop `
                + 'cap - the engine may not be able to path it', shapeId);
        }
    });

    const destSeen = new Map();
    const destById = new Map();

    (nav.destinations || []).forEach((dest, i) => {
        const where = `destinations[${i}]`;
        const shapeId = dest ? `dest:${dest.id}` : null;

        if (!checkRecord(out, dest, where, 'destinations', 'id', destSeen, shapeId)) return;

        if (blank(dest.type) || !ID.test(String(dest.type))) {
            out.bad(where, `has an invalid or missing type '${dest.type === undefined ? '' : dest.type}'`,
                shapeId);
        }

        if (blank(dest.name)) {
            out.bad(where, 'has no display name', shapeId);
        }

        for (const id of tokens(dest.waypoints)) {
            if (!byId.has(id)) {
                out.warn(`destination '${dest.id}' names unknown waypoint '${id}'`, shapeId);
            }
        }

        destById.set(dest.id, dest);
    });

    const arrivalsFor = new Map();

    (nav.arrivals || []).forEach((arrival, i) => {
        const where = `arrivals[${i}]`;

        if (!checkRecord(out, arrival, where, 'arrivals', null, new Map())) return;

        if (blank(arrival.destination) || !ID.test(String(arrival.destination))) {
            out.bad(where,
                `has an invalid destination id '${arrival.destination === undefined ? '' : arrival.destination}'`);
            return;
        }

        if (!destById.has(arrival.destination)) {
            out.warn(
                `arrival at ${arrival.x},${arrival.y} dropped: no destination '${arrival.destination}'`);
            return;
        }

        for (const id of tokens(arrival.waypoints)) {
            if (!byId.has(id)) {
                out.warn(`arrival for '${arrival.destination}' names unknown waypoint '${id}'`);
            }
        }

        if (!arrivalsFor.has(arrival.destination)) {
            arrivalsFor.set(arrival.destination, []);
        }

        arrivalsFor.get(arrival.destination).push(arrival);
    });

    const zoneSeen = new Map();

    (nav.zones || []).forEach((zone, i) => {
        const where = `zones[${i}]`;
        const shapeId = zone ? `zone:${zone.id}` : null;

        if (!checkRecord(out, zone, where, 'zones', 'id', zoneSeen, shapeId)) return;

        if (!(zone.width > 0) || !(zone.height > 0)) {
            out.bad(where,
                `bounds must have a positive width and height (found ${zone.width}x${zone.height})`,
                shapeId);
        }
    });

    const routeSeen = new Map();

    (nav.routes || []).forEach((route, i) => {
        const where = `routes[${i}]`;
        const shapeId = route ? `route:${route.id}` : null;

        if (!checkRecord(out, route, where, 'routes', 'id', routeSeen, shapeId)) return;

        const mode = route.mode === undefined ? 'cycle' : route.mode;

        if (mode !== 'cycle' && mode !== 'oneway' && mode !== 'pingpong') {
            out.bad(where, `mode '${route.mode}' is not 'cycle', 'oneway' or 'pingpong'`, shapeId);
        }

        const legs = tokens(route.waypoints);

        if (legs.length < 2) {
            out.bad(where, 'needs at least two waypoints', shapeId);
            return;
        }

        for (const id of legs) {
            if (!byId.has(id)) {
                out.warn(`route '${route.id}' is unusable: no waypoint '${id}'`, shapeId);
            }
        }

        // A cycle has a closing leg from the last waypoint back to the first, and it is walked like
        // any other. A ping-pong retraces the legs it already walked, so its legs are the forward
        // ones and nothing more. Matches NavigationSystem.CheckRoutes.
        const count = mode === 'cycle' ? legs.length : legs.length - 1;

        for (let leg = 0; leg < count; leg++) {
            const from = legs[leg];
            const to = legs[(leg + 1) % legs.length];
            const a = byId.get(from);
            const b = byId.get(to);

            if (a && b && a.map === b.map && chebyshev(a, b) > cap) {
                out.warn(
                    `route '${route.id}' leg '${from}' -> '${to}' is `
                    + `${chebyshev(a, b)} tiles, over the ${cap}-tile hop cap`, shapeId);
            }
        }
    });

    (nav.selfTests || []).forEach((selfTest, i) => {
        const where = `selfTests[${i}]`;

        if (!checkRecord(out, selfTest, where, 'selfTests', null, new Map())) return;

        for (const end of ['from', 'to']) {
            if (blank(selfTest[end]) || !ID.test(String(selfTest[end]))) {
                out.bad(where, `has an invalid '${end}' id '${selfTest[end]}'`);
            }
        }
    });

    // ---- quality warnings, the tier the shard calls Nav.Data ----

    for (const dest of destById.values()) {
        const arrivals = arrivalsFor.get(dest.id) || [];

        if (arrivals.length === 0) {
            out.warn(`destination '${dest.id}' has no arrival points`, `dest:${dest.id}`);
            continue;
        }

        const exclusive = arrivals.filter((a) => a.exclusive === true).length;
        const shared = arrivals.length - exclusive;

        if (exclusive > 0 && shared < 2) {
            out.warn(
                `destination '${dest.id}' has ${exclusive} exclusive arrival point(s) but only `
                + `${shared} shared one(s)`, `dest:${dest.id}`);
        }

        const reachable = arrivals.some((arrival) =>
            [...byId.values()].some((wp) => wp.map === dest.map && chebyshev(arrival, wp) <= cap));

        if (!reachable) {
            out.warn(
                `destination '${dest.id}' has no arrival point within ${cap} tiles of a waypoint`,
                `dest:${dest.id}`);
        }
    }

    for (const wp of byId.values()) {
        if (!edgeCount.has(wp.id)) {
            out.warn(`waypoint '${wp.id}' has no edges`, `wp:${wp.id}`);
        }
    }

    checkComponents(out, nav, byId, destById);

    return out;
}

/**
 * Walk-connected components per facet, reported when destinations land in more than one.
 *
 * Union-find over walk edges only; a gate is a teleport and does not make two places walkable to
 * one another. This is the closest we get to what [NavAudit does with real map data, and it is not
 * close: it cannot see a river, a wall, or a closed door.
 */
function checkComponents(report, nav, byId, destById) {
    const parent = new Map();

    const find = (id) => {
        while (parent.get(id) !== id) {
            parent.set(id, parent.get(parent.get(id)));
            id = parent.get(id);
        }

        return id;
    };

    for (const id of byId.keys()) parent.set(id, id);

    for (const edge of nav.edges || []) {
        const kind = !edge || edge.kind === undefined ? 'walk' : edge.kind;

        if (kind !== 'walk' || !byId.has(edge.from) || !byId.has(edge.to)) continue;

        parent.set(find(edge.from), find(edge.to));
    }

    const perFacet = new Map();

    for (const dest of destById.values()) {
        const near = [...byId.values()]
            .filter((wp) => wp.map === dest.map)
            .sort((a, b) => chebyshev(a, dest) - chebyshev(b, dest))[0];

        if (!near) continue;

        if (!perFacet.has(dest.map)) perFacet.set(dest.map, new Set());

        perFacet.get(dest.map).add(find(near.id));
    }

    for (const [map, roots] of perFacet) {
        if (roots.size > 1) {
            report.warn(
                `${map} destinations span ${roots.size} disconnected walk components - some places `
                + 'cannot be reached on foot');
        }
    }
}

// ---- daily life ------------------------------------------------------------------------------

export function validateDailyLife(daily, nav, options, report) {
    const out = report || new Report();

    if (!daily) {
        return out;
    }

    checkSchemaVersion(out, daily, 'britain-daily-life.json');

    const navIds = new Set();

    for (const wp of (nav && nav.waypoints) || []) navIds.add(wp.id);
    for (const dest of (nav && nav.destinations) || []) navIds.add(dest.id);

    const routeIds = new Set(((nav && nav.routes) || []).map((route) => route.id));

    const navId = (where, value, label, shapeId) => {
        if (blank(value) || !ID.test(String(value))) {
            out.bad(where, `${label} '${value === undefined ? '' : value}' is not a valid nav id`,
                shapeId);
            return;
        }

        if (!navIds.has(value) && !routeIds.has(value)) {
            out.warn(
                `${where} names '${value}', which is neither a destination, a waypoint nor a route`,
                shapeId);
        }
    };

    if (blank(daily.anchor) || !ID.test(String(daily.anchor))) {
        out.bad('britain-daily-life.json',
            `'anchor' must be a nav id (found '${daily.anchor === undefined ? '' : daily.anchor}')`,
            'dl:settings');
    } else if (!navIds.has(daily.anchor)) {
        out.warn(`the clock anchor names '${daily.anchor}', which is not a nav id`, 'dl:settings');
    }

    if (!daily.tavern) {
        out.bad('britain-daily-life.json', "'tavern' section is missing", 'dl:settings');
    } else {
        if (blank(daily.tavern.destination) || !ID.test(String(daily.tavern.destination))) {
            out.bad('tavern', `destination must be a nav id (found '${daily.tavern.destination}')`,
                'dl:settings');
        } else if (!navIds.has(daily.tavern.destination)) {
            out.warn(`the tavern names '${daily.tavern.destination}', which is not a nav id`,
                'dl:settings');
        }

        if (typeof daily.tavern.patronCount === 'number' && daily.tavern.patronCount < 0) {
            out.bad('tavern', `patronCount must not be negative (found ${daily.tavern.patronCount})`,
                'dl:settings');
        }
    }

    const watchSeen = new Map();

    (daily.watch || []).forEach((post, i) => {
        const where = `watch[${i}]`;
        const shapeId = post ? `watchpost:${post.id}` : null;

        if (!checkRecord(out, post, where, 'watch', 'id', watchSeen, shapeId)) return;

        const hasRoute = !blank(post.route);
        const hasDestinations = !blank(post.destinations);

        if (hasRoute === hasDestinations) {
            out.bad(where,
                "must have exactly one of 'route' or 'destinations' (found "
                + `${hasRoute ? 'both' : 'neither'})`, shapeId);
        }

        if (hasRoute) {
            navId(where, post.route, 'route', shapeId);
        }

        for (const id of tokens(post.destinations)) {
            if (!navIds.has(id)) {
                out.warn(`${where} names '${id}', which is not a nav id`, shapeId);
            }
        }
    });

    const folkSeen = new Map();

    (daily.townsfolk || []).forEach((person, i) => {
        const where = `townsfolk[${i}]`;
        const shapeId = person ? `townsperson:${person.id}` : null;

        if (!checkRecord(out, person, where, 'townsfolk', 'id', folkSeen, shapeId)) return;

        const body = person.body === undefined ? 'random' : person.body;

        if (body !== 'male' && body !== 'female' && body !== 'random') {
            out.bad(where, `body '${person.body}'; expected male, female or random`, shapeId);
        }

        navId(where, person.route, 'route', shapeId);
    });

    const shopSeen = new Map();
    const spawnerSeen = new Map();

    (daily.shopkeepers || []).forEach((shop, i) => {
        const where = `shopkeepers[${i}]`;
        const shapeId = shop ? `shopkeeper:${shop.id}` : null;

        if (!checkRecord(out, shop, where, 'shopkeepers', 'id', shopSeen, shapeId)) return;

        if (blank(shop.spawner)) {
            out.bad(where, 'has no spawner name', shapeId);
        } else if (spawnerSeen.has(lower(shop.spawner))) {
            out.bad(where, `duplicates the spawner '${shop.spawner}'`, shapeId);
        } else {
            spawnerSeen.set(lower(shop.spawner), where);
        }

        if (blank(shop.stockSpawner)) {
            out.bad(where, 'has no stockSpawner name', shapeId);
        }

        if (blank(shop.vendor)) {
            out.bad(where, 'has an entry with no vendor type name', shapeId);
        }

        navId(where, shop.shop, 'shop', shapeId);
        navId(where, shop.home, 'home', shapeId);
    });

    return out;
}

// ---- restricted zones ------------------------------------------------------------------------

export function validateRestrictedZones(config, options, report) {
    const out = report || new Report();

    if (!config) {
        return out;
    }

    if (!Array.isArray(config.zones)) {
        out.bad('restricted-zones.json', "'zones' section is missing");
        return out;
    }

    const seen = new Map();

    config.zones.forEach((zone, i) => {
        const where = `zones[${i}]`;
        const shapeId = zone ? `restricted:${zone.name}` : null;

        if (zone === null || typeof zone !== 'object') {
            out.bad(where, 'is null');
            return;
        }

        for (const key of Object.keys(zone)) {
            if (!KEYS.restrictedZones.includes(key)) {
                out.bad(where, `has the unknown key '${key}'`, shapeId);
            }
        }

        if (blank(zone.name)) {
            out.bad(where, 'has no name', shapeId);
        } else if (seen.has(lower(zone.name))) {
            out.bad(where, `duplicates the name '${zone.name}'`, shapeId);
        } else {
            seen.set(lower(zone.name), where);
        }

        if (!FACETS.includes(zone.map)) {
            out.bad(where, `map '${zone.map}' is not a valid facet. Valid: ${FACETS.join(', ')}`,
                shapeId);
        }

        if (!(zone.width > 0) || !(zone.height > 0)) {
            out.bad(where,
                `bounds must have a positive width and height (found ${zone.width}x${zone.height})`,
                shapeId);
        }
    });

    return out;
}

// ---- entry points ------------------------------------------------------------------------------

/**
 * Validates whichever of the three files were handed over.
 *
 * Navigation is validated first so daily life can resolve its ids against it. Passing only one
 * file is fine and is what a per-file save does; the ids that cannot then be resolved come back as
 * warnings, which is the right tier for them anyway.
 */
export function validate(files, options) {
    const report = new Report();
    const nav = (files && files.navigation) || null;

    validateNavigation(nav, options, report);
    validateRestrictedZones((files && files.restrictedZones) || null, options, report);
    validateDailyLife((files && files.dailyLife) || null, nav, options, report);

    return { fatal: report.fatal, warnings: report.warnings };
}

/**
 * Validates one file, which is what a per-file save asks about.
 *
 * Scoped deliberately: a daily-life dry run that also reported every navigation warning would bury
 * the answer to the question that was asked. `nav` is still needed, because daily life is nothing
 * but nav ids.
 */
export function validateFile(file, doc, nav, options) {
    const report = new Report();

    if (file === 'navigation') {
        validateNavigation(doc, options, report);
    } else if (file === 'restrictedZones') {
        validateRestrictedZones(doc, options, report);
    } else {
        validateDailyLife(doc, nav, options, report);
    }

    return { fatal: report.fatal, warnings: report.warnings };
}

/** Sections project() emits no shapes for, so the live preview is blind to them. */
export const UNPROJECTED = ['costTags', 'selfTests', 'chatter', 'watchChatter'];

/**
 * Rebuilds plain records from the editor's shapes, so the browser can validate what it is holding
 * without shipping the byte-exact writer to it.
 *
 * Structure only. Byte fidelity stays the bridge's job, which is the whole reason unproject reads
 * the original text rather than being handed a document.
 *
 * The UNPROJECTED sections come back empty, because the editor has no shapes for them. That costs
 * nothing: they are exactly the sections the editor cannot edit, so the preview cannot be blind to
 * a problem the editor could have caused. The bridge's dry run parses the real file and does see
 * them.
 */
export function plainFromShapes(file, shapes) {
    const store = file === 'navigation'
        ? { schemaVersion: SCHEMA_VERSION, costTags: [], waypoints: [], edges: [], destinations: [], arrivals: [], zones: [], routes: [], selfTests: [] }
        : file === 'restrictedZones'
            ? { zones: [] }
            : { schemaVersion: SCHEMA_VERSION, anchor: '', tavern: null, watch: [], townsfolk: [], shopkeepers: [] };

    for (const shape of shapes) {
        const at = shape.id.indexOf(':');
        const prefix = shape.id.slice(0, at);

        if (prefix === 'marker') continue;

        if (prefix === 'dl') {
            if (file !== 'dailyLife') continue;

            store.anchor = shape.props.anchor;
            store.tavern = {
                destination: shape.props['tavern.destination'],
                patronCount: shape.props['tavern.patronCount']
            };
            continue;
        }

        const spec = SECTION_FOR_PREFIX[prefix];

        if (!spec || spec[0] !== file) continue;

        const section = spec[1];

        if (!store[section]) continue;

        const record = {};

        for (const key of GEOMETRY_KEYS[prefix] || []) {
            if (key === 'map') {
                record.map = shape.map;
            } else if (shape.rect) {
                const at2 = { x: 0, y: 1, width: 2, height: 3 }[key];
                if (at2 !== undefined) record[key] = shape.rect[at2];
            } else if (shape.points && shape.points.length === 1) {
                const at2 = { x: 0, y: 1, z: 2 }[key];
                if (at2 !== undefined) record[key] = shape.points[0][at2];
            }
        }

        Object.assign(record, shape.props);
        store[section].push(record);
    }

    return store;
}
