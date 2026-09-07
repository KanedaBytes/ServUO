'use strict';

// Translating the shard's data into the editor's shape vocabulary, and back.
//
// The editor speaks in {layer, id, kind, map, rect|points, props, fields}; the shard speaks in
// records with string ids and space-separated list fields. Something has to sit between them,
// and it belongs here rather than in the browser: shapes.js does `shape.rect[0] += dx` in around
// thirty places, and a string there silently concatenates instead of adding. Normalising once,
// on the way out, means none of that code has to know anything about our schema.
//
// IDS ARE `<kind>:<our id>`, never an array index or a JSON pointer. Our records already have
// stable ids; an index breaks the moment a record is reordered or deleted, and 5b will be
// writing through these.
//
// UNKNOWN FIELDS PASS THROUGH. Anything on a record that this file does not model is carried into
// props and written back untouched, so adding a field on the shard side cannot be silently
// dropped by an older editor.

const compact = require('./compact.js');

/** Splits one of our space-separated list fields. */
function tokens(value) {
    if (typeof value !== 'string') return [];
    return value.split(' ').filter((part) => part.length > 0);
}

/** Fields this module understands per record type; everything else becomes an opaque prop. */
const MODELLED = {
    waypoint: ['id', 'map', 'x', 'y', 'z'],
    edge: ['from', 'to'],
    destination: ['id', 'map', 'x', 'y', 'z'],
    arrival: ['destination', 'x', 'y', 'z'],
    zone: ['id', 'map', 'x', 'y', 'width', 'height'],

    // 'waypoints' is deliberately NOT modelled here. A route's drawn line is derived from its
    // waypoint list, so the list is the editable content rather than geometry, and it has to reach
    // props or the panel would show an empty field and saving it would blank the route.
    route: ['id', 'map']
};

function extraProps(record, modelled) {
    const props = {};

    for (const key of Object.keys(record)) {
        if (!modelled.includes(key)) {
            props[key] = record[key];
        }
    }

    return props;
}

/**
 * Projects navigation.json into editor shapes.
 *
 * Waypoints, destinations and arrivals are points; zones are rects; edges and routes are
 * polylines resolved through the waypoint table.
 */
function projectNavigation(nav) {
    const shapes = [];
    const byId = new Map();

    for (const wp of nav.waypoints || []) {
        byId.set(wp.id, wp);
    }

    for (const wp of nav.waypoints || []) {
        shapes.push({
            layer: 'nav',
            id: `wp:${wp.id}`,
            kind: 'point',
            map: wp.map,
            label: wp.name || wp.id,
            points: [[wp.x, wp.y, wp.z]],
            props: { id: wp.id, ...extraProps(wp, MODELLED.waypoint) },
            fields: [
                { key: 'name', label: 'Display name', type: 'string' },
                { key: 'tags', label: 'Tags', type: 'string' },
                { key: 'arrivalRange', label: 'Arrival range', type: 'int' }
            ]
        });
    }

    for (const edge of nav.edges || []) {
        const a = byId.get(edge.from);
        const b = byId.get(edge.to);

        // A dangling edge is dropped rather than drawn at the origin. The shard already warns
        // about it through Nav.Data; drawing a line to 0,0 would just be a second, worse report.
        if (!a || !b) continue;

        shapes.push({
            layer: 'nav-edges',
            id: `edge:${edge.from}>${edge.to}`,
            kind: 'polyline',
            map: a.map,
            label: `${edge.from} - ${edge.to}`,
            points: [[a.x, a.y, a.z], [b.x, b.y, b.z]],
            props: { from: edge.from, to: edge.to, ...extraProps(edge, MODELLED.edge) },
            fields: []
        });
    }

    const arrivalsByDestination = new Map();

    for (const arrival of nav.arrivals || []) {
        if (!arrivalsByDestination.has(arrival.destination)) {
            arrivalsByDestination.set(arrival.destination, []);
        }

        arrivalsByDestination.get(arrival.destination).push(arrival);
    }

    for (const dest of nav.destinations || []) {
        shapes.push({
            layer: 'nav-destinations',
            id: `dest:${dest.id}`,
            kind: 'point',
            map: dest.map,
            label: dest.name || dest.id,
            points: [[dest.x, dest.y, dest.z]],
            props: { id: dest.id, ...extraProps(dest, MODELLED.destination) },
            fields: [
                { key: 'name', label: 'Name', type: 'string' },
                { key: 'type', label: 'Type', type: 'string' },
                { key: 'tags', label: 'Tags', type: 'string' },
                { key: 'waypoints', label: 'Approach waypoints', type: 'string' }
            ]
        });

        const arrivals = arrivalsByDestination.get(dest.id) || [];

        arrivals.forEach((arrival, index) => {
            shapes.push({
                layer: 'nav-arrivals',
                // Indexed within the destination because an arrival has no id of its own; the
                // destination id is what makes it addressable.
                id: `arr:${dest.id}#${index}`,
                kind: 'point',
                map: dest.map,
                label: `${dest.id} arrival${arrival.exclusive ? ' (exclusive)' : ''}`,
                points: [[arrival.x, arrival.y, arrival.z]],
                props: { destination: dest.id, ...extraProps(arrival, MODELLED.arrival) },
                fields: [
                    { key: 'exclusive', label: 'Exclusive', type: 'string' },
                    { key: 'waypoints', label: 'Approach waypoints', type: 'string' }
                ]
            });
        });
    }

    for (const zone of nav.zones || []) {
        shapes.push({
            layer: 'nav-zones',
            id: `zone:${zone.id}`,
            kind: 'rect',
            map: zone.map,
            label: zone.id,
            rect: [zone.x, zone.y, zone.width, zone.height],
            props: { id: zone.id, ...extraProps(zone, MODELLED.zone) },
            fields: [{ key: 'tags', label: 'Tags', type: 'string' }]
        });
    }

    for (const route of nav.routes || []) {
        const points = tokens(route.waypoints)
            .map((id) => byId.get(id))
            .filter(Boolean)
            .map((wp) => [wp.x, wp.y, wp.z]);

        if (points.length < 2) continue;

        shapes.push({
            layer: 'nav-routes',
            id: `route:${route.id}`,
            kind: 'polyline',
            map: route.map,
            label: route.id,
            points,
            props: { id: route.id, ...extraProps(route, MODELLED.route) },
            fields: [
                { key: 'mode', label: 'Mode', type: 'string' },
                { key: 'waypoints', label: 'Waypoints', type: 'string' }
            ]
        });
    }

    return shapes;
}

/** Projects restricted-zones.json. Origin plus size, not a corner pair. */
function projectRestrictedZones(config) {
    return (config.zones || []).map((zone) => ({
        layer: 'restricted',
        id: `restricted:${zone.name}`,
        kind: 'rect',
        map: zone.map,
        label: zone.name,
        rect: [zone.x, zone.y, zone.width, zone.height],
        props: { name: zone.name },
        fields: [{ key: 'name', label: 'Name', type: 'string' }]
    }));
}

/**
 * Projects daily life by resolving its nav ids to points.
 *
 * The config deliberately holds no coordinates, so every map shape here is a lookup into the
 * navigation data. An id that does not resolve is skipped: the shard's DailyLife.Config check
 * already reports it, and a shape at the origin would be a worse way to learn the same thing.
 *
 * TWO KINDS OF SHAPE COME OUT OF HERE, and the difference is what you may write to.
 *
 * `marker:...` shapes are DERIVED. Their coordinates belong to navigation.json, not to this file,
 * so dragging one is meaningless and unproject refuses them by name. They exist to show where a
 * shopkeeper stands, not to be edited.
 *
 * The record shapes - `shopkeeper:`, `watchpost:`, `townsperson:` and the `dl:settings` singleton
 * - are the writable ones, and they carry no geometry at all. They are edited as a form in the
 * side panel, which is the only honest way to edit a file whose every location is an id.
 */
function projectDailyLife(daily, nav) {
    const shapes = [];

    const destinations = new Map();
    const waypoints = new Map();

    for (const dest of nav.destinations || []) destinations.set(dest.id, dest);
    for (const wp of nav.waypoints || []) waypoints.set(wp.id, wp);

    const locate = (id) => destinations.get(id) || waypoints.get(id);

    const marker = (id, shape) => {
        const target = locate(id);
        if (!target) return;
        shapes.push({
            ...shape,
            layer: 'dailylife',
            kind: 'point',
            fields: [],
            map: target.map,
            points: [[target.x, target.y, target.z]]
        });
    };

    if (daily.anchor) {
        marker(daily.anchor, {
            id: 'marker:anchor',
            label: 'Clock anchor',
            props: { anchor: daily.anchor }
        });
    }

    if (daily.tavern && daily.tavern.destination) {
        marker(daily.tavern.destination, {
            id: 'marker:tavern',
            label: `Tavern (${daily.tavern.patronCount} patrons)`,
            props: { ...daily.tavern }
        });
    }

    for (const post of daily.watch || []) {
        for (const id of tokens(post.destinations)) {
            marker(id, {
                id: `marker:watch:${post.id}:${id}`,
                label: `Watch post ${post.id}`,
                props: { ...post }
            });
        }
    }

    for (const shop of daily.shopkeepers || []) {
        marker(shop.shop, {
            id: `marker:shop:${shop.id}`,
            label: `${shop.vendor} shop`,
            props: { ...shop }
        });

        marker(shop.home, {
            id: `marker:home:${shop.id}`,
            label: `${shop.vendor} home`,
            props: { ...shop }
        });

        // The walk home, drawn as the route the shard would actually compute.
        const from = locate(shop.shop);
        const to = locate(shop.home);

        if (from && to) {
            shapes.push({
                layer: 'dailylife',
                id: `marker:commute:${shop.id}`,
                kind: 'polyline',
                map: from.map,
                label: `${shop.vendor} commute`,
                points: [[from.x, from.y, from.z], [to.x, to.y, to.z]],
                props: { shopkeeper: shop.id },
                fields: []
            });
        }
    }

    // ---- the writable records ----
    //
    // No geometry, so nothing here is pickable on the canvas; the side panel finds them through
    // the filter and the layer list. Adding a shopkeeper stays a JSON edit - these are for editing
    // the entries that already exist.

    const facet = (nav.waypoints && nav.waypoints[0] && nav.waypoints[0].map) || 'Trammel';

    // anchor and tavern are single values rather than array records, so they share one pseudo-shape
    // instead of inventing an id apiece. Dotted prop keys are permitted here and nowhere else.
    shapes.push({
        layer: 'dailylife',
        id: 'dl:settings',
        kind: 'form',
        map: facet,
        label: 'Daily life settings',
        props: {
            anchor: daily.anchor,
            'tavern.destination': daily.tavern ? daily.tavern.destination : '',
            'tavern.patronCount': daily.tavern ? daily.tavern.patronCount : 0
        },
        fields: [
            { key: 'anchor', label: 'Clock anchor', type: 'navid' },
            { key: 'tavern.destination', label: 'Tavern', type: 'navid' },
            { key: 'tavern.patronCount', label: 'Patrons', type: 'int' }
        ]
    });

    for (const shop of daily.shopkeepers || []) {
        shapes.push({
            layer: 'dailylife',
            id: `shopkeeper:${shop.id}`,
            kind: 'form',
            map: facet,
            label: `${shop.id} (${shop.vendor})`,
            props: { ...shop },
            fields: [
                { key: 'shop', label: 'Shop', type: 'navid' },
                { key: 'home', label: 'Home', type: 'navid' }
            ]
        });
    }

    for (const post of daily.watch || []) {
        shapes.push({
            layer: 'dailylife',
            id: `watchpost:${post.id}`,
            kind: 'form',
            map: facet,
            label: `Watch post ${post.id}`,
            props: { ...post },
            fields: [
                { key: 'route', label: 'Route', type: 'navid' },
                { key: 'destinations', label: 'Destinations', type: 'navid' }
            ]
        });
    }

    for (const person of daily.townsfolk || []) {
        shapes.push({
            layer: 'dailylife',
            id: `townsperson:${person.id}`,
            kind: 'form',
            map: facet,
            label: `${person.name} ${person.title || ''}`.trim(),
            props: { ...person },
            fields: [{ key: 'route', label: 'Route', type: 'navid' }]
        });
    }

    return shapes;
}

function project(files) {
    const nav = files.navigation || {};

    return [
        ...projectNavigation(nav),
        ...projectRestrictedZones(files.restrictedZones || {}),
        ...projectDailyLife(files.dailyLife || {}, nav)
    ];
}

// ---- the write side -------------------------------------------------------------------------
//
// Field order for a NEW record is transcribed from the [JsonProperty] declaration order in the C#
// models (NavRecords.cs, DailyLifeConfig.cs), because that is the order Newtonsoft emits and
// therefore what the file looks like the next time the shard rewrites it. Get it wrong and the
// file churns on the shard's first save, which is exactly the diff 5a's golden test exists to
// prevent.
//
// `omitWhenBlank` mirrors NullValueHandling.Ignore. A created record NEVER contains null: an
// optional field is either written with a real value or its key is absent.

const TEMPLATES = {
    waypoint: {
        keys: ['id', 'name', 'map', 'x', 'y', 'z', 'arrivalRange', 'tags', 'note'],
        geometry: ['map', 'x', 'y', 'z'],
        defaults: { arrivalRange: 0, tags: '' },
        omitWhenBlank: ['name', 'note']
    },
    edge: {
        keys: ['from', 'to', 'kind', 'tags', 'note'],
        geometry: [],
        defaults: { kind: 'walk', tags: '' },
        omitWhenBlank: ['note']
    },
    destination: {
        keys: ['id', 'name', 'type', 'map', 'x', 'y', 'z', 'tags', 'waypoints', 'note'],
        geometry: ['map', 'x', 'y', 'z'],
        defaults: { tags: '', waypoints: '' },
        omitWhenBlank: ['note']
    },
    arrival: {
        // No 'map': an arrival belongs to its destination, which has one.
        keys: ['destination', 'x', 'y', 'z', 'exclusive', 'waypoints', 'note'],
        geometry: ['x', 'y', 'z'],
        defaults: { exclusive: false, waypoints: '' },
        omitWhenBlank: ['note']
    },
    zone: {
        keys: ['id', 'map', 'x', 'y', 'width', 'height', 'tags', 'note'],
        geometry: ['map', 'x', 'y', 'width', 'height'],
        defaults: { tags: '' },
        omitWhenBlank: ['note']
    },
    route: {
        // 'waypoints' is the geometry. The drawn polyline is derived from it, never the reverse.
        keys: ['id', 'map', 'mode', 'waypoints', 'note'],
        geometry: ['map'],
        defaults: { mode: 'cycle' },
        omitWhenBlank: ['note']
    },
    costTag: {
        keys: ['tag', 'multiplier'],
        geometry: [],
        defaults: { multiplier: 1 },
        floats: ['multiplier']
    },
    restrictedZone: {
        keys: ['name', 'map', 'x', 'y', 'width', 'height'],
        geometry: ['map', 'x', 'y', 'width', 'height'],
        defaults: {}
    },
    shopkeeper: {
        keys: ['id', 'spawner', 'stockSpawner', 'vendor', 'shop', 'home', 'closes'],
        geometry: [],
        defaults: { closes: true }
    },
    watchPost: {
        // route XOR destinations, both NullValueHandling.Ignore - so blanking one removes its key
        // rather than writing "", which is what lets a post be switched from one to the other.
        keys: ['id', 'route', 'destinations'],
        geometry: [],
        defaults: {},
        omitWhenBlank: ['route', 'destinations']
    },
    townsfolk: {
        keys: ['id', 'name', 'title', 'body', 'route'],
        geometry: [],
        defaults: { body: 'random' },
        omitWhenBlank: ['title']
    }
};

/**
 * Every writable id prefix: which file it lives in, which section, and how to find one.
 *
 * The file matters. `zone:` and `restricted:` both name a section called "zones", in two different
 * files - 5a's SECTION_FOR_KIND could not tell them apart because it had no idea a file existed.
 */
const KINDS = {
    wp: { file: 'navigation', section: 'waypoints', template: 'waypoint', find: byKey('id') },
    edge: { file: 'navigation', section: 'edges', template: 'edge', find: byFromTo },
    dest: { file: 'navigation', section: 'destinations', template: 'destination', find: byKey('id') },
    arr: { file: 'navigation', section: 'arrivals', template: 'arrival', find: byDestinationIndex },
    zone: { file: 'navigation', section: 'zones', template: 'zone', find: byKey('id') },
    route: { file: 'navigation', section: 'routes', template: 'route', find: byKey('id') },
    costTag: { file: 'navigation', section: 'costTags', template: 'costTag', find: byKey('tag') },

    restricted: {
        file: 'restrictedZones', section: 'zones', template: 'restrictedZone', find: byKey('name')
    },

    shopkeeper: {
        file: 'dailyLife', section: 'shopkeepers', template: 'shopkeeper', find: byKey('id')
    },
    watchpost: { file: 'dailyLife', section: 'watch', template: 'watchPost', find: byKey('id') },
    townsperson: {
        file: 'dailyLife', section: 'townsfolk', template: 'townsfolk', find: byKey('id')
    },
    dl: { file: 'dailyLife', singleton: true },

    marker: { derived: true }
};

function byKey(key) {
    return (section, id) => section.items.find((item) => {
        const field = compact.get(item, key);
        return field && field.value === id;
    }) || null;
}

/** Edges have no id of their own; `<from>><to>` is their natural key, as it is for [NavUnlink. */
function byFromTo(section, id) {
    const at = id.indexOf('>');
    const from = id.slice(0, at);
    const to = id.slice(at + 1);

    return section.items.find((item) => {
        const a = compact.get(item, 'from');
        const b = compact.get(item, 'to');
        return a && b && a.value === from && b.value === to;
    }) || null;
}

/**
 * An arrival has no id, so it is addressed as the nth arrival OF ITS DESTINATION, in file order -
 * which is the order projectNavigation enumerates them in, after grouping by destination. Do not
 * "simplify" this to an index into the whole arrivals array; they are not the same number.
 *
 * An index is normally the wrong identity, but here the save carries a hash of the exact bytes the
 * shapes were projected from, so the array the browser counted is the array being parsed. What the
 * hash does not cover is one batch deleting an earlier arrival and moving a later one, which is why
 * unproject resolves every id to a node before it mutates anything.
 */
function byDestinationIndex(section, id) {
    const at = id.lastIndexOf('#');
    const destination = id.slice(0, at);
    const wanted = Number(id.slice(at + 1));

    let seen = 0;

    for (const item of section.items) {
        const field = compact.get(item, 'destination');

        if (field && field.value === destination) {
            if (seen === wanted) {
                return item;
            }

            seen++;
        }
    }

    return null;
}

function splitId(shapeId) {
    const at = shapeId.indexOf(':');
    return { kind: shapeId.slice(0, at), id: shapeId.slice(at + 1) };
}

/** The file a shape is saved to, or null when it is derived and not writable at all. */
function fileForShape(shape) {
    const spec = KINDS[splitId(shape.id).kind];
    return spec && !spec.derived ? spec.file : null;
}

function specFor(shapeId, file) {
    const { kind } = splitId(shapeId);
    const spec = KINDS[kind];

    if (!spec) {
        throw new Error(`No record for shape id '${shapeId}'`);
    }

    if (spec.derived) {
        throw new Error(
            `'${shapeId}' is derived from the navigation data and cannot be written; `
            + 'edit the record it points at instead');
    }

    if (spec.file !== file) {
        throw new Error(`Shape '${shapeId}' belongs to ${spec.file}, not ${file}`);
    }

    return spec;
}

function sectionOf(root, spec, shapeId) {
    const section = compact.get(root, spec.section);

    if (!section || section.kind !== compact.ARRAY) {
        throw new Error(`No record for shape id '${shapeId}'`);
    }

    return section;
}

/**
 * Writes shape edits back into the original document text.
 *
 * The golden round-trip test asserts against this: SerializeCompact's layout rule exists twice,
 * once in C# and once in compact.js, and this is the half that can corrupt a file.
 *
 * RESOLVE, THEN MUTATE. Every addressed id is turned into a node reference over the untouched tree
 * before anything changes, and deletes are then performed by node identity. Otherwise a delete
 * early in a batch renumbers a move later in it, and the order the editor happened to send its
 * edits in would change what got written.
 *
 * An id that does not exist is an error rather than a silent skip.
 */
function unproject(file, originalText, edits) {
    const { updates = [], creates = [], deletes = [] } = edits || {};
    const root = compact.parse(originalText);

    const resolvedUpdates = updates.map((shape) => ({ shape, ...resolve(root, file, shape.id) }));
    const resolvedDeletes = deletes.map((id) => resolve(root, file, id));

    for (const { spec, section, node } of resolvedDeletes) {
        if (spec.singleton) {
            throw new Error('The daily life settings cannot be deleted');
        }

        compact.removeAt(section, compact.indexOfNode(section, node));
    }

    for (const { shape, spec, node } of resolvedUpdates) {
        if (spec.singleton) {
            applySettings(node, shape);
        } else {
            applyShape(node, shape, spec);
        }
    }

    for (const shape of creates) {
        createShape(root, file, shape);
    }

    return compact.stringify(root);
}

function resolve(root, file, shapeId) {
    const spec = specFor(shapeId, file);

    if (spec.singleton) {
        return { spec, section: null, node: root };
    }

    const section = sectionOf(root, spec, shapeId);
    const node = spec.find(section, splitId(shapeId).id);

    if (!node) {
        throw new Error(`No record for shape id '${shapeId}'`);
    }

    return { spec, section, node };
}

function isBlank(value) {
    return value === undefined || value === null || value === '';
}

/** Where a key belongs when it is not in the record yet: before the next template key that is. */
function anchorFor(node, template, key) {
    const after = template.keys.slice(template.keys.indexOf(key) + 1);
    return after.find((candidate) => compact.has(node, candidate));
}

function writeField(node, template, key, value) {
    if ((template.omitWhenBlank || []).includes(key) && isBlank(value)) {
        compact.remove(node, key);
        return;
    }

    compact.set(node, key, value, {
        float: (template.floats || []).includes(key),
        before: anchorFor(node, template, key)
    });
}

/** Geometry the shape carries, for the keys this record type actually stores. */
function geometryValues(shape, keys) {
    const values = {};

    for (const key of keys) {
        if (key === 'map') {
            if (shape.map !== undefined) {
                values.map = shape.map;
            }

            continue;
        }

        if (shape.rect) {
            const at = { x: 0, y: 1, width: 2, height: 3 }[key];

            if (at !== undefined) {
                values[key] = shape.rect[at];
            }
        } else if (shape.points && shape.points.length === 1) {
            const at = { x: 0, y: 1, z: 2 }[key];

            if (at !== undefined) {
                values[key] = shape.points[0][at];
            }
        }
    }

    return values;
}

/**
 * Applies one shape to the record it addresses.
 *
 * Note what is NOT here: a polyline's points. Every polyline in this schema is derived - an edge's
 * two ends come from its waypoints, a route's line from its waypoint list - so writing points back
 * would be writing a shadow onto the thing casting it. Edges and routes carry an empty geometry
 * list, and a route is edited through its `waypoints` field.
 */
function applyShape(node, shape, spec) {
    const template = TEMPLATES[spec.template];
    const geometry = geometryValues(shape, template.geometry);

    for (const [key, value] of Object.entries(geometry)) {
        writeField(node, template, key, value);
    }

    for (const [key, value] of Object.entries(shape.props || {})) {
        if (key in geometry) {
            continue;
        }

        // A key that is neither modelled nor already in the file would be rejected by the shard
        // anyway - JsonConfig sets MissingMemberHandling.Error - and this says so readably.
        if (!template.keys.includes(key) && !compact.has(node, key)) {
            throw new Error(`${spec.section} has no field '${key}'`);
        }

        writeField(node, template, key, value);
    }
}

function createShape(root, file, shape) {
    const spec = specFor(shape.id, file);

    if (spec.singleton) {
        throw new Error('The daily life settings already exist');
    }

    const section = sectionOf(root, spec, shape.id);

    if (spec.find(section, splitId(shape.id).id)) {
        throw new Error(`${shape.id} already exists`);
    }

    const template = TEMPLATES[spec.template];
    const geometry = geometryValues(shape, template.geometry);
    const pairs = [];

    for (const key of template.keys) {
        let value = geometry[key];

        if (value === undefined) {
            value = (shape.props || {})[key];
        }

        if (value === undefined) {
            value = template.defaults[key];
        }

        if ((template.omitWhenBlank || []).includes(key) && isBlank(value)) {
            continue;
        }

        if (value === undefined) {
            throw new Error(`A new ${spec.section} record needs '${key}'`);
        }

        pairs.push([key, compact.scalar(value, { float: (template.floats || []).includes(key) })]);
    }

    for (const key of Object.keys(shape.props || {})) {
        if (!template.keys.includes(key)) {
            throw new Error(`${spec.section} has no field '${key}'`);
        }
    }

    compact.push(section, compact.object(pairs));
}

/**
 * The daily life singleton: `anchor` at the root and the two `tavern` values under it.
 *
 * Dotted prop keys are permitted here and nowhere else. Two scalars that are not array records do
 * not each deserve an invented id, and one pseudo-shape is less to explain than two.
 */
function applySettings(root, shape) {
    for (const [key, value] of Object.entries(shape.props || {})) {
        if (key === 'anchor') {
            compact.set(root, 'anchor', value);
            continue;
        }

        if (key.startsWith('tavern.')) {
            const tavern = compact.get(root, 'tavern');

            if (!tavern || tavern.kind !== compact.OBJECT) {
                throw new Error("britain-daily-life.json has no 'tavern' section");
            }

            compact.set(tavern, key.slice('tavern.'.length), value);
            continue;
        }

        throw new Error(`dl:settings has no field '${key}'`);
    }
}

module.exports = {
    project, unproject, fileForShape,
    projectNavigation, projectDailyLife, projectRestrictedZones,
    tokens, TEMPLATES, KINDS
};
