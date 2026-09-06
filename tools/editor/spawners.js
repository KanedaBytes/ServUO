'use strict';

// Spawners in the editor's shape vocabulary, and back.
//
// The counterpart to project.js, which does the same job for the JSON files. Separate for the same
// reason spawnxml.js is separate from compact.js: this one speaks XmlSpawner's schema, and mixing
// the two would leave one module explaining two file formats.
//
// TWO LAYERS, and the difference is what you may write to.
//
//   `spawners`        - Spawns/Custom/<facet>/GG_*.xml. Editable. Seven of them today.
//   `spawners-stock`  - Spawns/<facet>.xml. Read-only context, 2,572 on Trammel alone.
//
// Stock spawners are never writable, and unproject refuses them by name rather than by omission.
// They are here to answer "what else is already spawning around the thing I am editing", which is
// a question you cannot answer from the GG files alone.
//
// PROP KEYS ARE XML ELEMENT NAMES. Not a translation layer - `props.CentreX` is `<CentreX>`. There
// is nothing to gain from renaming them and one more thing to get wrong, and it means an element
// this file does not model still round-trips through props without a mapping entry.

const xml = require('./spawnxml.js');
const objects2 = require('./objects2.js');

/** Fields the editor models. Everything else on the block passes through untouched. */
const MODELLED = [
    'Name', 'UniqueId', 'Map', 'CentreX', 'CentreY', 'CentreZ',
    'X', 'Y', 'Width', 'Height', 'Range', 'MaxCount',
    'MinDelay', 'MaxDelay', 'DelayInSec', 'IsRunning', 'Objects2'
];

/**
 * The side-panel form.
 *
 * The labels say what the element means rather than what it is called, because three of these are
 * actively misleading: <Range> is HomeRange, <X>/<Y>/<Width>/<Height> are the spawn AREA rather
 * than the spawner's own position, and the delays are minutes unless <DelayInSec> says otherwise.
 * There is no <Z> element at all - <CentreZ> is the spawner's Z.
 */
const FIELDS = [
    { key: 'Name', label: 'Name', type: 'string' },
    { key: 'Objects2', label: 'Spawns', type: 'string' },
    { key: 'MaxCount', label: 'Max at once', type: 'int' },
    { key: 'Range', label: 'Home range', type: 'int' },
    { key: 'Width', label: 'Spawn area width', type: 'int' },
    { key: 'Height', label: 'Spawn area height', type: 'int' },
    { key: 'MinDelay', label: 'Min delay (minutes)', type: 'int' },
    { key: 'MaxDelay', label: 'Max delay (minutes)', type: 'int' },
    { key: 'IsRunning', label: 'Running (True/False)', type: 'string' }
];

/**
 * Projects spawn files into shapes.
 *
 * @param {Array<{key: string, text: string, stock?: boolean}>} files
 * @param {{x, y, width, height}} [bbox]  stock only: what the viewport is looking at
 */
function project(files, bbox) {
    const shapes = [];

    for (const file of files) {
        let doc;

        try {
            doc = xml.parse(file.text);
        } catch {
            // A file the editor cannot read is not a reason to draw nothing at all. The bridge
            // reports it separately; here it is simply absent.
            continue;
        }

        doc.blocks.forEach((block, index) => {
            const shape = file.stock
                ? projectStock(file, block, index, bbox)
                : projectGG(file, block, index);

            if (shape) {
                shapes.push(shape);
            }
        });
    }

    return shapes;
}

function coordinates(block) {
    const x = Number(xml.get(block, 'CentreX'));
    const y = Number(xml.get(block, 'CentreY'));
    const z = Number(xml.get(block, 'CentreZ'));

    return Number.isFinite(x) && Number.isFinite(y) ? [x, y, Number.isFinite(z) ? z : 0] : null;
}

function projectGG(file, block, index) {
    const at = coordinates(block);

    if (!at) {
        return null;
    }

    const uniqueId = xml.get(block, 'UniqueId');
    const name = xml.get(block, 'Name');
    const props = {};

    for (const element of xml.names(block)) {
        props[element] = xml.get(block, element);
    }

    return {
        layer: 'spawners',
        // The file is part of the identity because the family is many files and a save is scoped to
        // one of them. UniqueId is the record identity, and it is what [XmlLoad replaces on.
        id: `spawner:${file.relative}#${uniqueId || `index-${index}`}`,
        kind: 'point',
        map: xml.get(block, 'Map') || 'Trammel',
        label: name || 'Spawner',
        points: [at],
        props,
        fields: FIELDS
    };
}

function projectStock(file, block, index, bbox) {
    const at = coordinates(block);

    if (!at || !inside(at, bbox)) {
        return null;
    }

    const list = objects2.parse(xml.get(block, 'Objects2') || '');
    const name = xml.get(block, 'Name') || 'Spawner';

    return {
        layer: 'spawners-stock',
        // Index, not UniqueId: forty-seven stock rows have theirs commented out, and these are
        // never written, so position in the file is identity enough to select one.
        id: `stock:${file.relative}#${index}`,
        kind: 'point',
        map: xml.get(block, 'Map') || 'Trammel',
        label: list.length > 0
            ? `${name} (${list.map((entry) => entry.type).slice(0, 3).join(', ')}${list.length > 3 ? '…' : ''})`
            : name,
        points: [at],
        props: {
            Name: name,
            MaxCount: xml.get(block, 'MaxCount'),
            Objects2: xml.get(block, 'Objects2')
        },
        fields: []
    };
}

function inside([x, y], bbox) {
    if (!bbox) {
        return true;
    }

    return x >= bbox.x && x < bbox.x + bbox.width && y >= bbox.y && y < bbox.y + bbox.height;
}

/**
 * Writes shape edits back into one spawn file's text.
 *
 * Same discipline as project.js's unproject: resolve every addressed id to a block over the
 * untouched document first, then mutate, and delete by identity - so a delete early in a batch
 * cannot renumber a move later in it.
 */
function unproject(relative, text, edits) {
    const { updates = [], creates = [], deletes = [] } = edits || {};
    const doc = xml.parse(text);

    const resolvedUpdates = updates.map((shape) => ({ shape, block: locate(doc, relative, shape.id) }));
    const resolvedDeletes = deletes.map((id) => locate(doc, relative, id));

    for (const block of resolvedDeletes) {
        xml.removeAt(doc, xml.indexOfBlock(doc, block));
    }

    for (const { shape, block } of resolvedUpdates) {
        applyShape(block, shape);
    }

    for (const shape of creates) {
        createShape(doc, relative, shape);
    }

    return xml.stringify(doc);
}

function splitId(shapeId) {
    const at = shapeId.indexOf(':');
    const kind = shapeId.slice(0, at);
    const rest = shapeId.slice(at + 1);
    const hash = rest.lastIndexOf('#');

    return { kind, relative: rest.slice(0, hash), key: rest.slice(hash + 1) };
}

function locate(doc, relative, shapeId) {
    const { kind, relative: mine, key } = splitId(shapeId);

    if (kind === 'stock') {
        throw new Error(
            `'${shapeId}' is a stock spawner and is read-only; the editor only writes Spawns/Custom`);
    }

    if (kind !== 'spawner') {
        throw new Error(`No spawner for shape id '${shapeId}'`);
    }

    if (mine !== relative) {
        throw new Error(`Shape '${shapeId}' belongs to ${mine}, not ${relative}`);
    }

    const block = doc.blocks.find((candidate) => xml.get(candidate, 'UniqueId') === key);

    if (!block) {
        throw new Error(`No spawner for shape id '${shapeId}'`);
    }

    return block;
}

function applyShape(block, shape) {
    if (shape.points && shape.points.length === 1) {
        const [x, y, z] = shape.points[0];

        xml.set(block, 'CentreX', x);
        xml.set(block, 'CentreY', y);
        xml.set(block, 'CentreZ', z);

        // The spawn AREA follows the spawner when the area is a single tile, which is what every GG
        // spawner uses. A sized area is left where it was, because moving the marker is not the
        // same instruction as moving the region it spawns into.
        if (Number(xml.get(block, 'Width')) === 0 && Number(xml.get(block, 'Height')) === 0) {
            xml.set(block, 'X', x);
            xml.set(block, 'Y', y);
        }
    }

    for (const [key, value] of Object.entries(shape.props || {})) {
        if (['CentreX', 'CentreY', 'CentreZ'].includes(key)) {
            continue;
        }

        if (!xml.COLUMNS.includes(key)) {
            throw new Error(`A spawner has no field '${key}'`);
        }

        if (key === 'Objects2') {
            checkSpawnList(value);
        }

        xml.set(block, key, value);
    }
}

/** Refuses a spawn list the shard would silently drop or misread. See objects2.js. */
function checkSpawnList(value) {
    for (const entry of objects2.parse(value || '')) {
        const reason = objects2.checkEntry(entry);

        if (reason) {
            throw new Error(reason);
        }
    }
}

function createShape(doc, relative, shape) {
    const { kind, relative: mine, key } = splitId(shape.id);

    if (kind !== 'spawner') {
        throw new Error(`'${shape.id}' cannot be created in a spawn file`);
    }

    if (mine !== relative) {
        throw new Error(`Shape '${shape.id}' belongs to ${mine}, not ${relative}`);
    }

    if (doc.blocks.some((block) => xml.get(block, 'UniqueId') === key)) {
        throw new Error(`${shape.id} already exists`);
    }

    const [x, y, z] = shape.points[0];
    const props = shape.props || {};

    checkSpawnList(props.Objects2);

    // Every field the shard's writer always emits, so a created spawner is indistinguishable from
    // one [XmlSave produced. The reader defaults all of them, but a file whose rows disagree about
    // which elements exist is a file nobody can diff.
    const pairs = [
        ['Name', props.Name], ['UniqueId', key], ['Map', shape.map || 'Trammel'],
        ['X', props.X === undefined ? x : props.X],
        ['Y', props.Y === undefined ? y : props.Y],
        ['Width', props.Width === undefined ? 0 : props.Width],
        ['Height', props.Height === undefined ? 0 : props.Height],
        ['CentreX', x], ['CentreY', y], ['CentreZ', z],
        ['Range', props.Range === undefined ? 0 : props.Range],
        ['MaxCount', props.MaxCount === undefined ? 1 : props.MaxCount],
        ['MinDelay', props.MinDelay === undefined ? 5 : props.MinDelay],
        ['MaxDelay', props.MaxDelay === undefined ? 10 : props.MaxDelay],
        ['DelayInSec', false], ['Duration', 0], ['DespawnTime', 0],
        ['ProximityRange', -1], ['ProximityTriggerSound', 500], ['TriggerProbability', 1],
        ['InContainer', false], ['MinRefractory', 0], ['MaxRefractory', 0],
        ['TODStart', 0], ['TODEnd', 0], ['TODMode', 0], ['KillReset', 1],
        ['ExternalTriggering', false], ['SequentialSpawning', -1],
        ['AllowGhostTriggering', false], ['AllowNPCTriggering', false],
        ['SpawnOnTrigger', false], ['SmartSpawning', false], ['TickReset', false],
        ['Team', 0], ['Amount', 1], ['IsGroup', false],
        ['IsRunning', props.IsRunning === undefined ? true : props.IsRunning],
        ['IsHomeRangeRelative', false],
        ['Objects2', props.Objects2 === undefined ? '' : props.Objects2]
    ];

    xml.push(doc, xml.makeBlock(pairs, doc.newline));

    return doc;
}

/** Everything worth telling someone about these files, from both formats. */
function findings(files) {
    const out = [];

    for (const file of files) {
        let doc;

        try {
            doc = xml.parse(file.text);
        } catch (error) {
            out.push(`${file.relative}: ${error.message}`);
            continue;
        }

        for (const line of xml.findings(doc)) {
            out.push(`${file.relative}: ${line}`);
        }

        doc.blocks.forEach((block) => {
            const raw = xml.get(block, 'Objects2');

            if (raw) {
                for (const line of objects2.findings(raw)) {
                    out.push(`${file.relative}: ${xml.get(block, 'Name')} ${line}`);
                }
            }
        });
    }

    return out;
}

module.exports = { project, unproject, findings, splitId, MODELLED, FIELDS };
