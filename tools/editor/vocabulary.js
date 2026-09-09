'use strict';

// Every list the editor's create forms offer, assembled from things that are true rather than
// from things somebody typed.
//
// THE RULE. A field whose valid values are a finite set gets a control populated from real data.
// Free text survives only where the value is genuinely free - a display name, a corridor's name,
// a restricted zone's name.
//
// WHO ANSWERS WHAT, and why it is split:
//
//   the shard   what it actually LOADED - the concrete creature types, and which of them are
//               vendors. Nothing in this repo can know that: Scripts.dll is what it is, and a
//               type list read off source would include anything that failed to compile.
//               Data/Live/vocabulary.json, written by VocabularySnapshot on the `vocabulary`
//               request.
//
//   this file   what is WRITTEN DOWN - the destination types and tags actually in use, the ones
//               the bot layer weights, the spawn files that exist, and the one thing the shard
//               genuinely cannot answer: which source folder a creature type came from.
//
// THAT LAST ONE IS THE INTERESTING SPLIT. The spawner form wants Monster / NPC / Vendor, and
// ServUO exposes no source folder at runtime - Scripts/Mobiles/Normal holds Alligator next to
// Balron, so no property of a loaded Type separates a townsfolk from a dragon without
// instantiating it, and instantiating 1,400 mobiles to fill a dropdown is not a trade worth
// making. The folder IS the answer, and the folder is a fact about the repo. So the shard says
// what exists and this says where it was written, and neither is a list anybody maintains.
//
// uo-offline solved the same problem with gen_spawn_types.py, which regex-scans the source into a
// checked-in spawn_types.json that a human re-runs. Same scan, but frozen: the file is a snapshot
// of the source as it was on the day somebody last remembered. This does the scan on demand and
// caches it against the tree's own mtimes.
//
// WITH THE SHARD DOWN IT STILL ANSWERS. Everything derived here is present, the shard's lists are
// empty, and `shardSeen` is false so the editor can say which half is missing. The art view
// already works with the shard down; a create form that refused to open without it would be a
// regression.

const fs = require('fs');
const path = require('path');

const whitelist = require('./whitelist.js');

const MOBILE_ROOT = path.join(whitelist.REPO_ROOT, 'Scripts', 'Mobiles');

// The folder that means "a person, not a beast". Everything else under Scripts/Mobiles is a
// monster as far as a spawner is concerned - which is also how uo-offline's own form treats it,
// concatenating its monster and animal buckets into the one Monster option (map.html:1103).
const NPC_FOLDER = 'NPCs';

// `public sealed partial class Foo : Bar` - the name and its immediate base. Only the name is
// used; the base is captured because a match without one is a generic constraint or an interface
// list, not a class declaration we want.
const CLASS_RE = /\bclass\s+([A-Za-z_]\w*)\s*:\s*([A-Za-z_]\w*)/g;

/** The shard's half, or nulls when it has never been asked. */
function readShard(readJson) {
    const data = readJson(path.join(whitelist.REPO_ROOT, 'Data', 'Live', 'vocabulary.json'));

    if (!data) {
        return { seen: false, creatures: [], vendors: [] };
    }

    return {
        seen: true,
        utc: data.utc || null,
        creatures: data.creatures || [],
        vendors: data.vendors || [],
        facets: data.facets || [],
        siteTypes: data.siteTypes || [],
        botClasses: data.botClasses || [],
        botTiers: data.botTiers || [],
        crafterTypes: data.crafterTypes || [],
        botBehaviors: data.botBehaviors || [],
        botRoles: data.botRoles || [],
        edgeKinds: data.edgeKinds || [],
        routeModes: data.routeModes || [],
        legacyBotClasses: data.legacyBotClasses || []
    };
}

/**
 * Which folder each mobile class was declared in, cached until the tree changes.
 *
 * Keyed on the newest mtime and the file count across the tree, the same discipline readStockFile
 * uses for the 4 MB trammel.xml: an edit made outside the editor is picked up on the next request
 * and nothing is served stale, without a timer or a watcher.
 *
 * Reading the file contents rather than trusting the file name, because ServUO does not always
 * match them - AlelleTheAborist.cs declares Alelle, and several files declare more than one
 * class. A name-only index would silently drop those into the wrong bucket, and a silently wrong
 * bucket is the failure this whole arrangement exists to avoid.
 */
let folderCache = null;

function classFolders() {
    const stamp = treeStamp(MOBILE_ROOT);

    if (folderCache && folderCache.stamp === stamp) {
        return folderCache.map;
    }

    const map = new Map();

    for (const file of walk(MOBILE_ROOT)) {
        // The folder directly under Scripts/Mobiles, which is the level the split is made at.
        const relative = path.relative(MOBILE_ROOT, file).split(path.sep);
        const folder = relative.length > 1 ? relative[0] : '';

        let text;

        try {
            text = fs.readFileSync(file, 'utf8');
        } catch {
            continue;
        }

        CLASS_RE.lastIndex = 0;

        let match;

        while ((match = CLASS_RE.exec(text)) !== null) {
            // First declaration wins: a partial class repeats, and the first one is as good an
            // answer as any since they are all in the same folder by definition.
            if (!map.has(match[1])) {
                map.set(match[1], folder);
            }
        }
    }

    folderCache = { stamp, map };

    return map;
}

/** A cheap fingerprint of a directory tree: how many files, and the newest mtime among them. */
function treeStamp(root) {
    let count = 0;
    let newest = 0;

    for (const file of walk(root)) {
        count++;

        try {
            const stat = fs.statSync(file);

            if (stat.mtimeMs > newest) {
                newest = stat.mtimeMs;
            }
        } catch {
            // Vanished between the listing and the stat. It cannot contribute a class either.
        }
    }

    return `${count}:${newest}`;
}

function* walk(root) {
    let entries;

    try {
        entries = fs.readdirSync(root, { withFileTypes: true });
    } catch {
        return;
    }

    for (const entry of entries) {
        const full = path.join(root, entry.name);

        if (entry.isDirectory()) {
            yield* walk(full);
        } else if (entry.name.endsWith('.cs')) {
            yield full;
        }
    }
}

/**
 * The three spawner kinds, and which loaded types belong to each.
 *
 * Vendor comes from the shard's type chain and the folder scan gets no say in it, because
 * BaseVendor is an exact answer and a folder is a guess. `unclassified` is reported rather than
 * hidden: a type the shard loaded that the scan never saw falls into Monster, and there are
 * legitimately a lot of them - creature subclasses live under Scripts/Engines and
 * Scripts/Services too - so the number is information, not an alarm.
 */
function spawnKinds(shard) {
    // With the shard down there is nothing to classify, and the scan is ~900 files of reading to
    // answer a question nobody asked. The kinds still come back, at zero, which is exactly what
    // the form should show.
    // The two bot kinds are uo-offline's, by their names: GenerateCustomSpawnersCommand.cs:167-170
    // switches on exactly "PlayerBotFixed" and "PlayerBotLifecycle". Their "type" list is not a
    // creature list at all - it is the behaviour the spawned bot wakes up in - which is why they
    // are built here rather than falling out of the folder scan with everybody else.
    const botKinds = (behaviours) => [
        { key: 'PlayerBotFixed', label: 'Bot - fixed role', count: behaviours.length },
        { key: 'PlayerBotLifecycle', label: 'Bot - lifecycle seed', count: behaviours.length }
    ];

    if (shard.creatures.length === 0 && shard.vendors.length === 0) {
        return {
            kinds: [
                { key: 'Monster', label: 'Monster', count: 0 },
                { key: 'NPC', label: 'NPC', count: 0 },
                { key: 'Vendor', label: 'Vendor', count: 0 },
                ...botKinds([])
            ],
            types: { Monster: [], NPC: [], Vendor: [], PlayerBotFixed: [], PlayerBotLifecycle: [] },
            unclassified: 0,
            scanned: 0
        };
    }

    const folders = classFolders();
    const monster = [];
    const npc = [];

    let unclassified = 0;

    for (const name of shard.creatures) {
        const folder = folders.get(name);

        if (folder === undefined) {
            unclassified++;
        }

        if (folder === NPC_FOLDER) {
            npc.push(name);
        } else {
            monster.push(name);
        }
    }

    const behaviours = (shard.botBehaviors || []).slice();

    return {
        // Order matters: it is the order of the kind dropdown, and Monster is the common case.
        // The bot kinds go last because a hand-placed bot spawner is the rare one - the population
        // recipe writes GG_BotPop.xml and this form is for the fixture the recipe cannot know
        // about.
        kinds: [
            { key: 'Monster', label: 'Monster', count: monster.length },
            { key: 'NPC', label: 'NPC', count: npc.length },
            { key: 'Vendor', label: 'Vendor', count: shard.vendors.length },
            ...botKinds(behaviours)
        ],
        types: {
            Monster: monster,
            NPC: npc,
            Vendor: shard.vendors.slice(),
            PlayerBotFixed: behaviours,
            PlayerBotLifecycle: behaviours.slice()
        },
        unclassified,
        scanned: folders.size
    };
}

/** Every distinct space-separated token across a field of some records, most-used first. */
function tokensOf(records, field) {
    const counts = new Map();

    for (const record of records || []) {
        for (const token of String(record[field] || '').split(/\s+/)) {
            if (token) {
                counts.set(token, (counts.get(token) || 0) + 1);
            }
        }
    }

    return rank(counts);
}

function valuesOf(records, field) {
    const counts = new Map();

    for (const record of records || []) {
        const value = record[field];

        if (value) {
            counts.set(String(value), (counts.get(String(value)) || 0) + 1);
        }
    }

    return rank(counts);
}

/**
 * Most-used first, then alphabetical.
 *
 * By use rather than alphabetically because these are combos you type into: the value you want is
 * nearly always one already in the file, and `bank` should not be nineteen rows below `alchemist`
 * for no reason other than the letter it starts with. Ties break alphabetically so the order is
 * stable between requests.
 */
function rank(counts) {
    return [...counts.entries()]
        .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
        .map(([value, count]) => ({ value, count }));
}

/** Merges ranked lists, keeping the first occurrence's count and appending the rest at 0. */
function merge(...lists) {
    const seen = new Map();

    for (const list of lists) {
        for (const entry of list) {
            const value = typeof entry === 'string' ? entry : entry.value;
            const count = typeof entry === 'string' ? 0 : entry.count;

            if (!seen.has(value)) {
                seen.set(value, count);
            }
        }
    }

    return [...seen.entries()].map(([value, count]) => ({ value, count }));
}

/**
 * Everything the editor's forms need, in one answer.
 *
 * `readJson` is injected so the tests can point the whole thing at a temp tree without a
 * filesystem stub - the same reason whitelist.js honours GG_EDITOR_ROOT.
 */
function build(readJson) {
    const shard = readShard(readJson);
    const nav = readJson(whitelist.FILES.navigation) || {};
    const bots = readJson(path.join(whitelist.REPO_ROOT, 'Data', 'Custom', 'bots.json')) || {};

    const destinations = bots.destinations || {};
    const byType = Object.keys(destinations.byType || {});
    const byTag = Object.keys(destinations.byTag || {});
    const towns = destinations.towns || [];

    const spawn = spawnKinds(shard);

    return {
        // Whether the shard has ever answered. The editor says which half of a list is missing
        // rather than showing a short one and letting it look complete.
        shardSeen: shard.seen,
        shardUtc: shard.utc || null,

        // In use in navigation.json first, then everything the bot layer has an opinion about.
        // The union rather than either alone: a type nothing weights is still a real type, and a
        // type weighted but not yet used is exactly the one you are about to author.
        destinationTypes: merge(valuesOf(nav.destinations, 'type'), byType),
        destinationTags: merge(tokensOf(nav.destinations, 'tags'), byTag, towns),
        waypointTags: merge(tokensOf(nav.waypoints, 'tags')),
        zoneTags: merge(tokensOf(nav.zones, 'tags'), byTag, towns),
        edgeTags: merge(tokensOf(nav.edges, 'tags'), tokensOf(nav.waypoints, 'tags')),

        // Ids, for the fields that name a record rather than describe one.
        waypointIds: (nav.waypoints || []).map((w) => w.id).filter(Boolean),
        destinationIds: (nav.destinations || []).map((d) => d.id).filter(Boolean),

        // Closed enums, straight from the shard. Empty with the shard down, which is what
        // shardSeen is for.
        siteTypes: shard.siteTypes || [],
        botClasses: shard.botClasses || [],
        botTiers: shard.botTiers || [],
        crafterTypes: shard.crafterTypes || [],
        botBehaviors: shard.botBehaviors || [],
        botRoles: shard.botRoles || [],
        legacyBotClasses: shard.legacyBotClasses || [],
        edgeKinds: shard.edgeKinds || [],
        routeModes: shard.routeModes || [],
        facets: shard.facets || [],

        spawnKinds: spawn.kinds,
        spawnTypes: spawn.types,
        spawnUnclassified: spawn.unclassified,
        spawnScanned: spawn.scanned,

        // The files a new spawner can land in, as `<facet>/GG_Thing.xml` - the RELATIVE form,
        // which is what a spawner shape's id is built from and what app.js's fileOf turns back
        // into a `spawn:` save key. Offering the full key instead produced an id of
        // `spawner:spawn:trammel/...`, fileOf then produced `spawn:spawn:trammel/...`, and the
        // save silently matched no file - so listSpawnFiles' own form is deliberately trimmed
        // here rather than passed through.
        //
        // Free text still works, which is how a brand-new file gets made; whitelist.SPAWN_NAME is
        // what decides whether the name is acceptable, on the way in.
        spawnFiles: whitelist.listSpawnFiles().map((key) => key.replace(/^spawn:/, ''))
    };
}

module.exports = { build, spawnKinds, classFolders, tokensOf, valuesOf, merge, NPC_FOLDER };
