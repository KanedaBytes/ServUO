// Turning a finished tool into a shape.
//
// Its own module, and pure, for one reason: this is the seam between the browser and the writer.
// A shape built here goes straight to unproject, and if the two disagree about a field name the
// symptom is a save that throws - or worse, one that quietly writes a record missing a field. Kept
// here it can be tested against unproject with no DOM at all, which is what editor.test.js does.
//
// The vocabulary is exactly what project.js emits, because the editor cannot tell a shape it made
// from one the bridge sent, and nothing downstream should have to.

/**
 * @param {string} key      which tool finished
 * @param {object} props    the form values, plus any auto id
 * @param {string} map      the facet
 * @param {object} draft    {points, rect} collected on the canvas
 * @param {object} context  {ownerId, ids, arrivalIndex} - what the collecting kinds gathered
 */
export function buildShape(key, props, map, draft, context = {}) {
    const common = { layer: LAYER_FOR[key], map, fields: [] };
    const point = draft.points && draft.points[0];

    switch (key) {
        case 'waypoint':
            return {
                ...common, id: `wp:${props.id}`, kind: 'point', label: props.name || props.id,
                points: [[point[0], point[1], 0]],
                props: {
                    id: props.id,
                    // Omitted when blank rather than written empty: the schema marks name
                    // NullValueHandling.Ignore, and a created record never contains a blank
                    // optional field.
                    ...(props.name ? { name: props.name } : {}),
                    arrivalRange: Number(props.arrivalRange) || 0,
                    tags: props.tags || ''
                },
                fields: [
                    { key: 'name', label: 'Display name', type: 'string' },
                    { key: 'tags', label: 'Tags', type: 'string' },
                    { key: 'arrivalRange', label: 'Arrival range', type: 'int' }
                ]
            };

        case 'destination':
            return {
                ...common, id: `dest:${props.id}`, kind: 'point', label: props.name || props.id,
                points: [[point[0], point[1], 0]],
                props: {
                    id: props.id, name: props.name, type: props.type,
                    tags: props.tags || '', waypoints: props.waypoints || ''
                },
                fields: [
                    { key: 'name', label: 'Name', type: 'string' },
                    { key: 'type', label: 'Type', type: 'string' },
                    { key: 'tags', label: 'Tags', type: 'string' },
                    { key: 'waypoints', label: 'Approach waypoints', type: 'string' }
                ]
            };

        case 'arrival':
            // Indexed within its destination and appended, which is where unproject puts it too.
            return {
                ...common, id: `arr:${context.ownerId}#${context.arrivalIndex}`, kind: 'point',
                label: `${context.ownerId} arrival${props.exclusive ? ' (exclusive)' : props.exact ? ' (exact)' : ''}`,
                points: [[point[0], point[1], 0]],
                props: {
                    destination: context.ownerId,
                    exclusive: props.exclusive === true,
                    ...(props.exact === true || props.exact === 'true' ? { exact: true } : {}),
                    waypoints: props.waypoints || ''
                },
                fields: [
                    { key: 'exclusive', label: 'Exclusive', type: 'string' },
                    { key: 'exact', label: 'Exact (no scatter)', type: 'string' },
                    { key: 'waypoints', label: 'Approach waypoints', type: 'string' }
                ]
            };

        case 'edge': {
            const [from, to] = context.ids;

            return {
                ...common, id: `edge:${from}>${to}`, kind: 'polyline', label: `${from} - ${to}`,
                points: draft.points.map((p) => [...p]),
                props: { from, to, kind: 'walk', tags: '' }
            };
        }

        case 'route':
            return {
                ...common, id: `route:${props.id}`, kind: 'polyline', label: props.id,
                points: draft.points.map((p) => [...p]),
                props: {
                    id: props.id,
                    mode: props.mode || 'cycle',
                    waypoints: context.ids.join(' ')
                },
                fields: [
                    { key: 'mode', label: 'Mode', type: 'string' },
                    { key: 'waypoints', label: 'Waypoints', type: 'string' }
                ]
            };

        // Three records, built together because they are one thing. Their ids are derived from
        // the site's own id rather than auto-generated separately, so a site reads as a set in the
        // filter and in a diff: brit-mine-west, brit-mine-west-face, and its arrivals.
        case 'site': {
            const zoneId = `${props.id}-face`;

            const site = {
                ...common, id: `dest:${props.id}`, kind: 'point', label: props.name || props.id,
                points: [[point[0], point[1], 0]],
                props: {
                    id: props.id,
                    name: props.name,
                    type: props.type,
                    tags: props.tags || '',
                    waypoints: props.waypoints || ''
                },
                fields: [
                    { key: 'name', label: 'Display name', type: 'string' },
                    { key: 'type', label: 'Type', type: 'string' },
                    { key: 'tags', label: 'Tags', type: 'string' },
                    { key: 'waypoints', label: 'Approach waypoints', type: 'string' }
                ]
            };

            // The zone carries the site's type as a tag as well as 'work', because
            // GathererBehavior.ResolveSite looks for a zone tagged 'mine' or 'lumber' at the
            // bot's feet - a zone without it is invisible to the behaviour that needs it.
            const zone = {
                ...common, id: `zone:${zoneId}`, kind: 'rect', label: zoneId,
                rect: [...draft.rect],
                props: { id: zoneId, tags: `${props.type} wilderness work` },
                fields: [{ key: 'tags', label: 'Tags', type: 'string' }]
            };

            const arrivals = (context.arrivals || []).map(([x, y], index) => ({
                ...common, id: `arr:${props.id}#${index}`, kind: 'point',
                label: `${props.id} arrival`,
                points: [[x, y, 0]],
                props: {
                    destination: props.id,
                    exclusive: false,
                    waypoints: props.waypoints || ''
                },
                fields: [
                    { key: 'exclusive', label: 'Exclusive', type: 'string' },
                    { key: 'exact', label: 'Exact (no scatter)', type: 'string' },
                    { key: 'waypoints', label: 'Approach waypoints', type: 'string' }
                ]
            }));

            return [site, zone, ...arrivals];
        }

        case 'navzone':
            return {
                ...common, id: `zone:${props.id}`, kind: 'rect', label: props.id,
                rect: [...draft.rect],
                props: { id: props.id, tags: props.tags || '' },
                fields: [{ key: 'tags', label: 'Tags', type: 'string' }]
            };

        case 'restricted':
            return {
                ...common, id: `restricted:${props.name}`, kind: 'rect', label: props.name,
                rect: [...draft.rect],
                props: { name: props.name },
                fields: [{ key: 'name', label: 'Name', type: 'string' }]
            };

        case 'spawner': {
            // The file is part of the identity, because the spawner family is many files and a save
            // is scoped to one of them. The GUID is minted here rather than by the shard: <UniqueId>
            // is what [XmlLoad replaces on, and a row without one is ADDED with a fresh GUID on
            // every load instead - which is exactly how 47 stock spawners came to exist twice.
            const id = context.uniqueId;

            return {
                ...common, id: `spawner:${props.file}#${id}`, kind: 'point', label: props.Name,
                map, points: [[point[0], point[1], point[2] === undefined ? 0 : point[2]]],
                props: {
                    Name: props.Name,
                    UniqueId: id,
                    Map: map,
                    Range: props.Range || '0',
                    MaxCount: props.MaxCount || '1',
                    MinDelay: props.MinDelay || '5',
                    MaxDelay: props.MaxDelay || '10',
                    IsRunning: 'True'
                },
                entries: [{ type: props.Objects2, max: props.MaxCount || '1' }],
                fields: SPAWNER_FIELDS
            };
        }

        default:
            return null;
    }
}

/** Mirrors spawners.js FIELDS; editor.test.js asserts the two agree. */
const SPAWNER_FIELDS = [
    { key: 'Name', label: 'Name', type: 'string' },
    { key: 'MaxCount', label: 'Max at once', type: 'int' },
    { key: 'Range', label: 'Home range', type: 'int' },
    { key: 'MinDelay', label: 'Min delay (minutes)', type: 'int' },
    { key: 'MaxDelay', label: 'Max delay (minutes)', type: 'int' },
    { key: 'IsRunning', label: 'Running (True/False)', type: 'string' }
];

const LAYER_FOR = {
    waypoint: 'nav',
    destination: 'nav-destinations',
    arrival: 'nav-arrivals',
    edge: 'nav-edges',
    route: 'nav-routes',
    navzone: 'nav-zones',
    restricted: 'restricted',
    spawner: 'spawners'
};
