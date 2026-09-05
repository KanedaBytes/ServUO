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
                ...common, id: `wp:${props.id}`, kind: 'point', label: props.id,
                points: [[point[0], point[1], 0]],
                props: {
                    id: props.id,
                    arrivalRange: Number(props.arrivalRange) || 0,
                    tags: props.tags || ''
                },
                fields: [
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
                label: `${context.ownerId} arrival${props.exclusive ? ' (exclusive)' : ''}`,
                points: [[point[0], point[1], 0]],
                props: {
                    destination: context.ownerId,
                    exclusive: props.exclusive === true,
                    waypoints: props.waypoints || ''
                },
                fields: [
                    { key: 'exclusive', label: 'Exclusive', type: 'string' },
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

        default:
            return null;
    }
}

const LAYER_FOR = {
    waypoint: 'nav',
    destination: 'nav-destinations',
    arrival: 'nav-arrivals',
    edge: 'nav-edges',
    route: 'nav-routes',
    navzone: 'nav-zones',
    restricted: 'restricted'
};
