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

    // THE Z IS THE DRAFT'S, not zero. Every one of these used to write `0` flat, and every record
    // the editor has ever created carries `"z": 0` because of it - survivable only because
    // navigation.json's Z is advisory and the shard falls back to map.GetAverageZ when the authored
    // one will not fit. The art view knows the real Z from the renderer's pick map and the radar
    // view asks for it, so there is no longer any reason to throw it away. createProposal at the
    // corridor tool has always overwritten it afterwards with the Z its walker found; that is now
    // an override of a real number rather than a workaround for a missing one.

    switch (key) {
        case 'waypoint':
            return {
                ...common, id: `wp:${props.id}`, kind: 'point', label: props.name || props.id,
                points: [[point[0], point[1], point[2] || 0]],
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
                points: [[point[0], point[1], point[2] || 0]],
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
                points: [[point[0], point[1], point[2] || 0]],
                props: {
                    destination: context.ownerId,
                    exclusive: props.exclusive === true,
                    ...(props.exact === true || props.exact === 'true' ? { exact: true } : {}),
                    // The form has always asked for a range and this has always dropped it, so
                    // every arrival the editor created stood on its exact tile whatever was
                    // typed. `range` is Ignore-on-default in the C# record and omitWhenZero in the
                    // template, so it is written only when it is not 0 - which is also what keeps
                    // the 115 shipped arrivals byte-identical.
                    ...(Number(props.range) > 0 ? { range: Number(props.range) } : {}),
                    waypoints: props.waypoints || ''
                },
                fields: [
                    { key: 'exclusive', label: 'Exclusive', type: 'string' },
                    { key: 'exact', label: 'Exact (no scatter)', type: 'string' },
                    { key: 'range', label: 'Arrival range (0 = the tile)', type: 'int' },
                    { key: 'waypoints', label: 'Approach waypoints', type: 'string' }
                ]
            };

        case 'edge': {
            const [from, to] = context.ids;

            return {
                ...common, id: `edge:${from}>${to}`, kind: 'polyline', label: `${from} - ${to}`,
                points: draft.points.map((p) => [...p]),
                // Tags come from the corridor that minted the edge, when one did. An edge drawn
                // by hand has none, which is what it has always had.
                props: { from, to, kind: 'walk', tags: props.tags || '' }
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
        // The only tool that builds records for three different layers, which is exactly how it
        // came to build them for none. `common` carries LAYER_FOR[key], and there is no single
        // layer a work site belongs to - so every record here names its own. Without that the
        // layer is undefined, and an undefined layer is not a cosmetic fault: `fileOf` returns
        // null for it, `filesWithEdits` drops the file, and Save reports success having written
        // nothing. A whole site was authored and lost that way before this comment existed.
        case 'site': {
            const zoneId = `${props.id}-face`;

            const site = {
                ...common, layer: 'nav-destinations',
                id: `dest:${props.id}`, kind: 'point', label: props.name || props.id,
                points: [[point[0], point[1], point[2] || 0]],
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
                ...common, layer: 'nav-zones',
                id: `zone:${zoneId}`, kind: 'rect', label: zoneId,
                rect: [...draft.rect],
                props: { id: zoneId, tags: `${props.type} wilderness work` },
                fields: [{ key: 'tags', label: 'Tags', type: 'string' }]
            };

            const arrivals = (context.arrivals || []).map(([x, y, z], index) => ({
                ...common, layer: 'nav-arrivals',
                id: `arr:${props.id}#${index}`, kind: 'point',
                label: `${props.id} arrival`,
                points: [[x, y, z || 0]],
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
            const name = ggName(props.Name);

            // `<facet>/GG_Thing.xml`, never the `spawn:` save key. app.js's fileOf reconstructs
            // the key by prefixing `spawn:` back onto this slice, so a key pasted in here would
            // give `spawn:spawn:...`, match no writable file, and make the save a no-op that
            // still said it had succeeded. Tolerated rather than refused because both forms are
            // reasonable things to have in your hand.
            const file = String(props.file || '').replace(/^spawn:/, '');

            return {
                ...common, id: `spawner:${file}#${id}`, kind: 'point', label: name,
                map, points: [[point[0], point[1], point[2] === undefined ? 0 : point[2]]],
                props: {
                    Name: name,
                    UniqueId: id,
                    Map: map,
                    Range: props.Range || '0',
                    MaxCount: props.MaxCount || '1',
                    MinDelay: props.MinDelay || '5',
                    MaxDelay: props.MaxDelay || '10',
                    IsRunning: 'True'
                },
                entries: [{ type: botType(props.kind, props.Objects2), max: props.MaxCount || '1' }],
                fields: SPAWNER_FIELDS
            };
        }

        default:
            return null;
    }
}

/**
 * The spawn string for a bot kind, or the type unchanged for everything else.
 *
 * `kind` is otherwise a pure UI filter and is thrown away here - for the two bot kinds it is the
 * one field that decides what gets written, because a bot spawner does not spawn a "PlayerBotFixed"
 * (there is no such class). It spawns a PlayerBot and then TELLS it what to be, through the
 * property setters XmlSpawner applies after placement:
 *
 *     PlayerBot/Role/Fixed/Seed/BankSitter
 *
 * That indirection is not a flourish. XmlSpawner calls OnAfterSpawn BEFORE it applies the spawn
 * string (XmlSpawner2.cs:9337 then :9344), so a bot cannot learn anything from its spawner the way
 * uo-offline's does - upstream reads `Spawner is FixedRoleBotSpawner` inside OnAfterSpawn, and that
 * hook is simply too early here. The setters are what replace it.
 *
 * The separator is a slash for a reason too: a colon cannot appear in an <Objects2> entry at all -
 * XmlSpawner splits on ":MX=" and its ten siblings and silently DISCARDS an entry containing one -
 * which is why upstream's own "Crafter:Smith" convention could not be carried across.
 *
 * Everything else a spawner can seed - the forced class, the home town, the station - is the
 * population recipe's business and is written by [BotPopulationGen. A hand-placed fixture that
 * needs one can have it typed into Objects2 in the side panel, which round-trips untouched.
 */
export function botType(kind, type) {
    const behaviour = String(type || '').trim();

    if (kind === 'PlayerBotFixed') {
        return `PlayerBot/Role/Fixed/Seed/${behaviour}`;
    }

    if (kind === 'PlayerBotLifecycle') {
        return `PlayerBot/Role/Lifecycle/Seed/${behaviour}`;
    }

    return type;
}

/**
 * The GG_ prefix, applied rather than typed.
 *
 * [XmlLoad and [XmlUnLoad both take an optional prefix filter matched with an ordinal StartsWith,
 * so GG_ is what makes this shard's spawners addressable as a set without touching the ~2,500
 * stock ones. A spawner that is missing it is invisible to [GG_Reimport for ever - it will not be
 * swept and it will not be replaced - and it is the kind of thing nobody notices until a re-import
 * leaves an orphan behind.
 *
 * Idempotent, so a name typed with the prefix out of habit does not become GG_GG_. Case-sensitive
 * on the way in, because the filter that matters is ordinal: `gg_Foo` is NOT prefixed as far as
 * [XmlLoad is concerned, so it gets the real prefix added.
 */
export function ggName(name) {
    const trimmed = String(name || '').trim();

    return trimmed.startsWith('GG_') ? trimmed : `GG_${trimmed}`;
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
