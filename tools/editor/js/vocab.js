// The vocabulary, in the browser: one fetch, and the lookup every form field goes through.
//
// A field descriptor in tools.js names a key here rather than carrying values, so a list exists
// in exactly one place - the shard for what it loaded, the bridge for what is written down, and
// this for handing it to a control. Nothing in the editor types a valid value out.
//
// IT IS NEVER EMPTY-BECAUSE-NOT-LOADED-YET. `load()` runs in boot before the first form can be
// opened, and `optionsFor` on a key that has not arrived returns an empty list rather than
// throwing - a combo with no suggestions still takes typing, which is exactly the degradation
// wanted when the shard is down.

let data = { shardSeen: false };

export async function load(api) {
    try {
        data = await api.vocabulary();
    } catch (error) {
        // Not fatal, and not silent either: the forms still open as free text, and the caller
        // puts the reason in the status line.
        data = { shardSeen: false, error: error.message };
        throw error;
    }

    return data;
}

export function all() {
    return data;
}

/** True once the shard has written its half. The forms say so where it matters. */
export function shardSeen() {
    return data.shardSeen === true;
}

/**
 * The values for one key, as plain strings.
 *
 * The bridge returns some lists as `{value, count}` and some as bare strings - the counted ones
 * are the ones ranked by use, which is most of what a combo wants. Both shapes flatten here so
 * no caller has to know which it asked for.
 */
export function optionsFor(key, filter = null) {
    const list = data[key];

    if (!Array.isArray(list)) {
        return [];
    }

    const values = list.map((entry) => (typeof entry === 'string' ? entry : entry.value))
        .filter(Boolean);

    return filter ? values.filter(filter) : values;
}

/** How many records already use a value, for the combo's trailing hint. Zero when unknown. */
export function countFor(key, value) {
    const list = data[key];

    if (!Array.isArray(list)) {
        return 0;
    }

    const hit = list.find((entry) => typeof entry !== 'string' && entry.value === value);

    return hit ? hit.count : 0;
}

/**
 * The spawner kinds, each with the number of types in it.
 *
 * The count is on the label deliberately. A folder scan that found nothing, or a shard that has
 * never reported, both produce an empty type list - and an empty dropdown is indistinguishable
 * from one that has not finished loading. `Monster (0)` is not.
 */
export function spawnKinds() {
    return Array.isArray(data.spawnKinds) ? data.spawnKinds : [];
}

/** The type names for one spawner kind. */
export function spawnTypes(kind) {
    const types = data.spawnTypes || {};

    return Array.isArray(types[kind]) ? types[kind] : [];
}

/**
 * Every spawnable type, whatever kind.
 *
 * What the Selection panel's entry editor offers, because an existing spawner's type is being
 * changed rather than chosen, and there is no kind selected to narrow it by. Sorted, since
 * without a kind to group them the only useful order is alphabetical.
 */
export function allSpawnTypes() {
    const types = data.spawnTypes || {};

    return Object.keys(types)
        .flatMap((kind) => types[kind] || [])
        .sort((a, b) => a.localeCompare(b));
}

/**
 * Which vocabulary a field of an already-saved record draws on, by layer and key.
 *
 * The Selection panel's fields come from `shape.fields`, which project.js and build.js each
 * produce their own copy of - so hanging `optionsFrom` on them would mean writing the same key
 * twice in two files and hoping they stay together. One table here instead, keyed on the pair
 * that identifies the field.
 *
 * IT MATTERS THAT EDIT AND CREATE AGREE. A dropdown when you create a destination and free text
 * when you edit one is exactly the drift this whole arrangement removes: the second form quietly
 * teaches you that the vocabulary is optional.
 *
 * `closed` marks a C# enum, which gets a <select> - a fourth route mode is a file the shard
 * refuses to load, so there is nothing to be gained by letting one be typed.
 */
const FIELD_VOCABULARY = {
    'nav.tags': { key: 'waypointTags' },
    'nav-destinations.type': { key: 'destinationTypes' },
    'nav-destinations.tags': { key: 'destinationTags' },
    'nav-zones.tags': { key: 'zoneTags' },
    'nav-edges.tags': { key: 'edgeTags' },
    'nav-edges.kind': { key: 'edgeKinds', closed: true },
    'nav-routes.mode': { key: 'routeModes', closed: true }
};

export function fieldVocabulary(layer, key) {
    return FIELD_VOCABULARY[`${layer}.${key}`] || null;
}
