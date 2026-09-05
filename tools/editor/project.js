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
    route: ['id', 'map', 'waypoints']
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
            label: wp.id,
            points: [[wp.x, wp.y, wp.z]],
            props: { id: wp.id, ...extraProps(wp, MODELLED.waypoint) },
            fields: [
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
 * The config deliberately holds no coordinates, so every shape here is a lookup into the
 * navigation data. An id that does not resolve is skipped: the shard's DailyLife.Config check
 * already reports it, and a shape at the origin would be a worse way to learn the same thing.
 */
function projectDailyLife(daily, nav) {
    const shapes = [];

    const destinations = new Map();
    const waypoints = new Map();

    for (const dest of nav.destinations || []) destinations.set(dest.id, dest);
    for (const wp of nav.waypoints || []) waypoints.set(wp.id, wp);

    const locate = (id) => destinations.get(id) || waypoints.get(id);

    const point = (id, shape) => {
        const target = locate(id);
        if (!target) return;
        shapes.push({ ...shape, map: target.map, points: [[target.x, target.y, target.z]] });
    };

    if (daily.anchor) {
        point(daily.anchor, {
            layer: 'dailylife',
            id: `anchor:${daily.anchor}`,
            kind: 'point',
            label: 'Clock anchor',
            props: { anchor: daily.anchor },
            fields: []
        });
    }

    if (daily.tavern && daily.tavern.destination) {
        point(daily.tavern.destination, {
            layer: 'dailylife',
            id: `tavern:${daily.tavern.destination}`,
            kind: 'point',
            label: `Tavern (${daily.tavern.patronCount} patrons)`,
            props: { ...daily.tavern },
            fields: [{ key: 'patronCount', label: 'Patrons', type: 'int' }]
        });
    }

    for (const post of daily.watch || []) {
        for (const id of tokens(post.destinations)) {
            point(id, {
                layer: 'dailylife',
                id: `watch:${post.id}:${id}`,
                kind: 'point',
                label: `Watch post ${post.id}`,
                props: { ...post },
                fields: []
            });
        }
    }

    for (const shop of daily.shopkeepers || []) {
        point(shop.shop, {
            layer: 'dailylife',
            id: `shop:${shop.id}`,
            kind: 'point',
            label: `${shop.vendor} shop`,
            props: { ...shop },
            fields: [{ key: 'vendor', label: 'Vendor', type: 'vendor' }]
        });

        point(shop.home, {
            layer: 'dailylife',
            id: `home:${shop.id}`,
            kind: 'point',
            label: `${shop.vendor} home`,
            props: { ...shop },
            fields: []
        });

        // The walk home, drawn as the route the shard would actually compute.
        const from = locate(shop.shop);
        const to = locate(shop.home);

        if (from && to) {
            shapes.push({
                layer: 'dailylife',
                id: `commute:${shop.id}`,
                kind: 'polyline',
                map: from.map,
                label: `${shop.vendor} commute`,
                points: [[from.x, from.y, from.z], [to.x, to.y, to.z]],
                props: { shopkeeper: shop.id },
                fields: []
            });
        }
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

/**
 * Writes shape edits back into the original document text.
 *
 * 5a never calls this - the editor is read-only until 5b - but it is written and tested now,
 * because it is what the golden round-trip test asserts against and it is the half of the
 * translation that can corrupt a file. Building it later, against no test, is how that happens.
 *
 * Edits are matched by id, and an id that no longer exists is an error rather than a silent skip.
 */
function unproject(shapes, originalText) {
    const root = compact.parse(originalText);

    for (const shape of shapes || []) {
        applyShape(root, shape);
    }

    return compact.stringify(root);
}

function applyShape(root, shape) {
    const separator = shape.id.indexOf(':');
    const kind = shape.id.slice(0, separator);
    const id = shape.id.slice(separator + 1);

    const target = findRecord(root, kind, id);

    if (!target) {
        throw new Error(`No record for shape id '${shape.id}'`);
    }

    if (shape.rect) {
        setNumber(target, 'x', shape.rect[0]);
        setNumber(target, 'y', shape.rect[1]);
        setNumber(target, 'width', shape.rect[2]);
        setNumber(target, 'height', shape.rect[3]);
    } else if (shape.points && shape.points.length === 1) {
        setNumber(target, 'x', shape.points[0][0]);
        setNumber(target, 'y', shape.points[0][1]);
        setNumber(target, 'z', shape.points[0][2]);
    }

    for (const [key, value] of Object.entries(shape.props || {})) {
        const field = compact.get(target, key);

        if (field && field.kind === compact.SCALAR && field.value !== value) {
            compact.setScalar(field, value);
        }
    }
}

function setNumber(record, key, value) {
    const field = compact.get(record, key);

    if (field && field.kind === compact.SCALAR && field.value !== value) {
        compact.setScalar(field, value);
    }
}

const SECTION_FOR_KIND = {
    wp: { section: 'waypoints', key: 'id' },
    dest: { section: 'destinations', key: 'id' },
    zone: { section: 'zones', key: 'id' },
    route: { section: 'routes', key: 'id' },
    restricted: { section: 'zones', key: 'name' }
};

function findRecord(root, kind, id) {
    const mapping = SECTION_FOR_KIND[kind];

    if (!mapping) {
        return null;
    }

    const section = compact.get(root, mapping.section);

    if (!section || section.kind !== compact.ARRAY) {
        return null;
    }

    return section.items.find((item) => {
        const field = compact.get(item, mapping.key);
        return field && field.value === id;
    }) || null;
}

module.exports = { project, unproject, projectNavigation, projectDailyLife, projectRestrictedZones, tokens };
