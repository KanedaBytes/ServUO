// The editor: state, input, selection, the tools, undo, and the save cycle.
//
// This is the file 5a rewrote and 5b puts back. The ModernUO original's spine is the edit cycle -
// dirty tracking, per-shape baselines, an undo stack, a save that then reloads, and a persistent
// banner for the case where the write succeeded but the reload was rejected - and none of it had
// anything to hold onto while the editor was read-only.
//
// Three things are ours rather than ported, because the save is a different shape here.
//
// IDENTITY IS `<kind>:<id>`, a string, and every edit map is keyed by it rather than by the shape
// object. refreshShapes replaces every object it holds, so an object key would silently drop the
// lot on the first refresh.
//
// SAVING IS PER FILE, not per record. The original PATCHed one record at a time against an API
// inside the shard; ours sends the shapes that changed for one file and lets the bridge rewrite
// it, carrying the hash it started from so a stale editor cannot flatten a walk [NavRecord just
// wrote.
//
// AND A REJECTED RELOAD IS NOT A FAILED SAVE. The file is on disk and the shard is still running
// the previous config. Refreshing over that would show the OLD values and look exactly like the
// save being undone - which is the bug the banner exists for. So the edits stay, the banner says
// what the shard said, and discard puts the file back too.

import { api } from './api.js';
import { View, DEFAULT_FACET, BRITAIN } from './view.js';
import {
    LAYERS, LAYER_ORDER, draw as drawShapes, drawEntities, drawDraft, hasGeometry,
    READ_ONLY_LAYERS, SPAWNER_LAYERS, REFERENCE_LAYERS, setAuditFlags, BEHAVIOR_COLORS, setHopFlags,
    hitTest, pick, entityAt, grabsOverEntity, nearSegment, geometryOf, applyGeometry, moveShape,
    resizeRect, moveNode, syncDerived, isStaleZ
} from './shapes.js';
import * as coverage from './coverage.js';
import * as worksites from './worksites.js';
import { auditLine, hasBlocked } from './audit.js';
import { HOP_CAP, validate, plainFromShapes } from './validate.js';
import { TOOLS, initTools, askFor, fillLists } from './tools.js';
import { nextId, insertedId, insertedName } from './ids.js';
import { buildShape } from './build.js';
import { liveStatusText } from './live.js';
import * as adopt from './adopt.js';
import * as iso from './iso.js';
import * as pickmap from './pickmap.js';
import * as landz from './landz.js';
import { initSections, initResize } from './panels.js';
import * as vocab from './vocab.js';

const ENTITY_POLL_MS = 2000;
const HEALTH_POLL_MS = 15000;

// Stock spawners: how zoomed in you must be before they are worth fetching, how long a pan settles
// before asking, and the grid the request box snaps to so revisits hit the same box.
const STOCK_MIN_SCALE = 0.5;
const STOCK_DEBOUNCE_MS = 250;
const STOCK_GRID = 256;

const $ = (id) => document.getElementById(id);

const dom = {};

const state = {
    shapes: [],
    entities: [],
    health: null,
    live: null,
    facet: null,

    // The hash each file was loaded at, handed back on save so the bridge can refuse a stale write.
    hashes: {},

    // Which stock box is on screen, and how much of the facet it is. The count says "N in view /
    // 2,572 total" because a bare N would read as the whole world.
    spawnerBox: null,
    stockTotal: null,
    stockInView: 0,
    spawnFindings: [],

    // The last [NavAudit result, drawn over the edges.
    audit: null,

    // The ids of the current unsaved corridor proposal, so it can be replaced or edited as a
    // unit. Null once it has been saved or discarded.
    //
    // The walked path itself is deliberately NOT kept. It was drawn as a polyline once, and the
    // moment a proposed waypoint was dragged the road stayed where the probe had walked while the
    // point moved away from it - two pictures of the same road disagreeing. The hops are edges
    // now, derived from the waypoints like every other edge, so there is only one picture.
    proposal: null,

    // What the shard last said about individual hops, by edge id.
    hopFlags: null,

    // The Bots panel: which bot is open, and the last log the shard wrote.
    botLog: null,
    selectedBot: null,

    visible: new Set(LAYER_ORDER.filter((layer) => layer !== 'nav-edges')),
    coverageVisible: false,
    worksitesVisible: false,

    // Which reference regions are on. Overworld only by default: dungeon is 1988 waypoints of
    // somewhere nothing can be adopted into yet, and drawing it over Britain is noise.
    referenceRegions: new Set(),
    referenceBox: null,
    referenceCounts: null,

    /** Edges the last adopt could not walk. Drawn red; never proposed. */
    adoptFailures: [],

    /** Why Save is refused, or null. Set by an adopt that would leave an island. */
    adoptBlocked: null,

    selected: null,
    hovered: null,
    filter: '',

    // Edits since the last save, all keyed by shape id. A created shape is only ever in `created`,
    // never also in `dirty`: it is written as a whole record either way.
    dirty: new Map(),
    created: new Map(),
    deleted: new Map(),

    undo: [],
    redo: [],

    // Files written this session, so discard knows whether there is a .bak worth restoring.
    written: new Set(),

    drag: null,
    spaceDown: false,
    tool: null,
    draft: null,

    /**
     * The cursor, in canvas pixels, so a pick map that arrives after the cursor has stopped moving
     * can correct the readout. Without it, stopping dead on a tile nobody has hovered before leaves
     * the readout showing the ground-plane estimate until the mouse twitches.
     */
    cursor: null,

    /**
     * Whether the left button is still held.
     *
     * A press whose pick map has to be fetched is answered a frame or two late, and by then the
     * click may be over. Starting a drag or a pan at that point would leave one stuck until the
     * next mouseup, which is the sort of thing that reads as the editor having frozen.
     */
    buttonDown: false
};

const canvas = $('map');
const view = new View(canvas);

// Exposed deliberately. This is a localhost-only admin tool with nothing on the page a same-origin
// script could not already reach, and having the real state reachable is what lets the editor be
// driven and asserted on from a browser automation script rather than by squinting at screenshots.
window.__editor = { state, view, coverage };

// --- startup ------------------------------------------------------------------------------------

async function boot() {
    Object.assign(dom, {
        coords: $('coords'), status: $('status'),
        toolGuide: $('tool-guide'), toolStep: $('tool-step'), toolHint: $('tool-hint'),
        toolLegend: $('tool-legend'),
        properties: $('properties'), matches: $('matches'), filter: $('filter'),
        menu: $('context-menu'), toolbar: $('toolbar'),
        banner: $('banner'), bannerText: $('banner-text'),
        bannerDiscard: $('banner-discard'), bannerDismiss: $('banner-dismiss'),
        bannerReapply: $('banner-reapply'),
        createButtons: $('create-buttons'), layers: $('layers'), health: $('health'),
        liveStatus: $('live-status'), bots: $('bots'), botFilter: $('bot-filter'),
        botDetail: $('bot-detail'),
        basemap: $('basemap'), basemapArt: $('basemap-art'),
        floorRow: $('floor-row'), floor: $('floor'), floorLabel: $('floor-label'),
        artStatus: $('art-status'),
        sidebar: $('sidebar'), sidebarResize: $('sidebar-resize'),
        botCard: $('bot-card'), botCardHead: $('bot-card-head'),
        botCardTitle: $('bot-card-title'), botCardBody: $('bot-card-body'),
        botCardClose: $('bot-card-close')
    });

    initTools();

    // Before anything else draws: a section restored closed must never flash open, and the column
    // width has to be in place before the canvas is measured for the first time.
    initSections(dom.sidebar);
    initResize(dom.sidebarResize);
    wireBotCard();

    try {
        const status = await api.status();
        const facet = status.facets.find((f) => f.name === DEFAULT_FACET) || status.facets[0];

        state.facet = facet;
        view.setFacet(facet);
        view.goTo(BRITAIN.x, BRITAIN.y, 2);

        // A zone corner's ground Z arrives after the frame that asked for it, so the answer has to
        // be able to ask for another one - otherwise a zone stays on the ground plane until
        // something else happens to redraw.
        landz.configure(requestRender);

        setStatus(
            `${status.waypoints} waypoints, ${status.destinations} destinations.`,
            status.navigationLoaded ? 'ok' : 'error');
    } catch (error) {
        setStatus(`Cannot reach the bridge: ${error.message}`, 'error');
        return;
    }

    // Before any form can be opened, so a field's list is never empty merely because the fetch
    // had not finished. A failure here is not fatal: the forms fall back to free text, which is
    // what every one of them was before this existed.
    try {
        await vocab.load(api);

        if (!vocab.shardSeen()) {
            setStatus(
                'The shard has not reported its types yet - the creature and class lists will be'
                + ' empty until it has. Everything else is from the files.', 'warn');
        }
    } catch (error) {
        setStatus(`Could not read the vocabulary: ${error.message}. Forms stay free text.`, 'warn');
    }

    buildLayerList();
    buildCreateButtons();

    await refreshShapes();

    pollEntities();
    pollHealth();

    // The age has to keep counting up between polls, and especially when the polls stop.
    setInterval(updateLiveStatus, 1000);

    wireInput();
    wireBaseMap();

    // Demand-driven from here: every mutation calls requestRender, and these are the three things
    // that change what is on screen without any mutation at all.
    view.onTileLoaded = requestRender;
    new ResizeObserver(requestRender).observe(canvas);
    watchPixelRatio();

    requestRender();
}

// --- the base map -------------------------------------------------------------------------------
//
// Two controls and one asynchronous fact. The fact is whether there is an art renderer at all:
// MapExport.exe may simply not have been built, in which case the Art option is disabled with the
// reason in its tooltip rather than left as a control that does nothing.
//
// FLOORS ONLY EXIST AT THE TWO CLOSEST ZOOM LEVELS. Coarser than that the renderer draws one
// composite, because at 1:4 a storey is five pixels tall and peeling it off shows nothing. The
// slider stays visible and goes inactive there, saying why, rather than disappearing - a control
// that vanishes when you zoom out reads as a bug.

const FLOOR_LABELS = { ground: 'Ground', first: 'First floor', all: 'All' };

async function wireBaseMap() {
    if (dom.floor) {
        dom.floor.addEventListener('input', () => {
            view.setFloor(iso.FLOORS[Number(dom.floor.value)] || 'all');
            updateFloorRow();
            requestRender();
        });
    }

    if (dom.basemap) {
        dom.basemap.addEventListener('change', (event) => {
            if (event.target.name !== 'basemap') {
                return;
            }

            view.setProjection(event.target.value);
            updateFloorRow();
            setStatus(view.isArt
                ? 'Art view: selection works, placing and dragging need radar.'
                : 'Radar view.', 'ok');
            requestRender();
        });
    }

    let info;

    try {
        info = await api.artInfo();
    } catch (error) {
        info = { available: false, reason: error.message };
    }

    if (!info.available) {
        if (dom.basemapArt) {
            dom.basemapArt.disabled = true;
            dom.basemapArt.title = info.reason || 'No art renderer.';
        }

        return;
    }

    view.setArtInfo(info);
    updateArtStatus(info);

    // The snapshot can appear or change while the editor is open - somebody runs [WorldItems, or
    // decorates and runs it again - and the layer id is part of every item tile's URL, so noticing
    // is what makes the furniture refresh. Polled rather than watched: the bridge is the only thing
    // that can see the file, and this is the same cadence the rest of the panel runs at.
    pollArt();

    // /api/artinfo starts the renderer, which takes a second or so on a cold TileMatrix, and the
    // radio is live the whole time. Somebody who clicked Art while it was in flight got radar and
    // a checked Art button, which reads as the art view being broken rather than not ready yet.
    if (dom.basemapArt && dom.basemapArt.checked) {
        view.setProjection('art');
        updateFloorRow();
        requestRender();
    }
}

/**
 * The art line: what the renderer is, how long its last tile took, and what the item snapshot holds.
 *
 * `/api/artstats` shipped in session 1 with nothing calling it - the endpoint was real and the line
 * that was supposed to read it never got wired, so the README described a status nobody could see.
 * The item snapshot needs the poll anyway, so it is wired here.
 */
async function pollArt() {
    try {
        const stats = await api.artStats();

        if (stats.items && (!view.items || view.items.id !== stats.items.id)) {
            view.setItems(stats.items);
            // A new snapshot is a whole new set of pick URLs, and the old entries are 256 KB each.
            pickmap.reset();
            requestRender();
        } else if (!stats.items && view.items) {
            view.setItems(null);
            pickmap.reset();
            requestRender();
        }

        updateArtStatus(stats);
    } catch (error) {
        // The bridge answering nothing is the Live panel's problem to report, not this line's.
    }

    setTimeout(pollArt, ART_POLL_MS);
}

const ART_POLL_MS = 5000;

function updateArtStatus(stats) {
    if (!dom.artStatus) {
        return;
    }

    if (!stats || stats.available === false) {
        dom.artStatus.textContent = stats && stats.reason ? stats.reason : 'no art renderer';
        return;
    }

    const parts = [];

    if (typeof stats.rendered === 'number') {
        parts.push(`${stats.rendered} tile(s), last ${stats.lastMs}ms`);
    }

    // "no world items" is a real state worth naming rather than an absence worth hiding: the art
    // view without a snapshot draws empty paving where the forges are, and somebody looking at
    // that deserves to know why rather than conclude the map is wrong.
    parts.push(stats.items
        ? `${stats.items.count} world item(s)`
        : 'no world items - run [WorldItems');

    dom.artStatus.textContent = parts.join(', ');
}

/** Keeps the floor row's visibility, label and active state in step with the view. */
function updateFloorRow() {
    if (!dom.floorRow) {
        return;
    }

    dom.floorRow.hidden = !view.isArt;

    if (!view.isArt) {
        return;
    }

    const composite = view.effectiveFloor !== view.floor;

    dom.floorRow.classList.toggle('inactive', composite);
    dom.floorLabel.textContent = composite
        ? `${FLOOR_LABELS[view.floor]} (all, at this zoom)`
        : FLOOR_LABELS[view.floor];
}

/**
 * A device-pixel-ratio change is not a resize, and matchMedia listeners for it fire exactly once -
 * the query itself is stale after the ratio moves - so it has to be re-registered each time.
 */
function watchPixelRatio() {
    const query = matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);

    query.addEventListener('change', () => {
        requestRender();
        watchPixelRatio();
    }, { once: true });
}

/**
 * Replaces the local shapes with the bridge's.
 *
 * Refuses to run while there are unsaved edits unless forced. This is the guard the silent-revert
 * bug needed: a refresh over a dirty shape throws the work away with no trace, and from the
 * outside that is indistinguishable from a save that did not apply.
 */
async function refreshShapes({ force = false } = {}) {
    if (hasEdits() && !force) {
        return false;
    }

    try {
        const response = await api.shapes();

        state.shapes = response.shapes;
        state.hashes = {};

        for (const [file, info] of Object.entries(response.files || {})) {
            state.hashes[file] = info.hash;
        }
    } catch (error) {
        setStatus(`Could not load shapes: ${error.message}`, 'error');
        return false;
    }

    // The spawners come from their own endpoint, because the stock ones are fetched by viewport
    // rather than in full. Awaited here so a refresh leaves one complete world rather than a nav
    // map that grows spawners a moment later.
    await refreshSpawners({ force: true });

    state.dirty.clear();
    state.created.clear();
    state.deleted.clear();
    state.undo.length = 0;
    state.redo.length = 0;
    state.selected = null;
    state.hovered = null;

    // Derived from the waypoints, so a stale grid is worse than none: it looks authoritative.
    coverage.invalidate();

    if (state.coverageVisible) {
        computeCoverage();
    }

    fillLists(state.shapes);
    showProperties(null);
    applyFilter();
    updateCounts();
    updateToolbar();
    requestRender();

    return true;
}

/**
 * Loads the spawner layers, replacing what is on screen without disturbing an edit in progress.
 *
 * A pan refetches, so a spawner being dragged or renamed must survive it. Dirty and created shapes
 * are kept and the incoming copy of them dropped - the same rule refreshShapes follows for the
 * whole world, applied here per shape because a pan is not a refresh.
 */
async function refreshSpawners({ force = false } = {}) {
    const bbox = stockBox();
    const key = bbox ? `${bbox.x},${bbox.y},${bbox.width},${bbox.height}` : 'none';

    if (!force && key === state.spawnerBox) {
        return;
    }

    let response;

    try {
        response = await api.spawners(bbox);
    } catch (error) {
        setStatus(`Could not load spawners: ${error.message}`, 'error');
        return;
    }

    state.spawnerBox = key;
    state.stockTotal = response.total;
    state.stockInView = response.shapes.filter((s) => s.layer === 'spawners-stock').length;

    for (const [file, info] of Object.entries(response.files || {})) {
        state.hashes[file] = info.hash;
    }

    const keep = new Set([...state.dirty.keys(), ...state.created.keys()]);

    state.shapes = state.shapes
        .filter((shape) => !SPAWNER_LAYERS.has(shape.layer) || keep.has(shape.id))
        .concat(response.shapes.filter((shape) => !keep.has(shape.id)));

    if ((response.findings || []).length > 0) {
        state.spawnFindings = response.findings;
    }

    fillLists(state.shapes);
    updateCounts();
    requestRender();
}

/**
 * Loads the uo-offline reference for what is on screen.
 *
 * Same shape as refreshSpawners and for the same reasons: bbox-loaded because 3952 waypoints and
 * 4291 edges are not drawable at facet scale, keyed on the box so a small pan refetches nothing.
 *
 * Nothing here can be edited, so unlike the spawner refresh there is no dirty-shape rule to
 * observe - the incoming set simply replaces the old one.
 */
async function refreshReference({ force = false } = {}) {
    const regions = [...state.referenceRegions];

    if (regions.length === 0) {
        const had = state.shapes.length;

        state.shapes = state.shapes.filter((shape) => !REFERENCE_LAYERS.has(shape.layer));
        state.referenceBox = null;

        if (state.shapes.length !== had) {
            updateCounts();
            requestRender();
        }

        return;
    }

    const bbox = stockBox();

    // No box means we are zoomed out past the floor, and for THIS layer that is the opposite of
    // the spawner case: a null bbox there omits the 2,572 stock spawners, but here it means "the
    // whole overworld", which is 4,364 shapes of grey smear. Drop what is drawn and ask for
    // nothing until the view is worth drawing them in.
    if (!bbox) {
        const had = state.shapes.length;

        state.shapes = state.shapes.filter((shape) => !REFERENCE_LAYERS.has(shape.layer));
        state.referenceBox = null;

        if (state.shapes.length !== had) {
            updateCounts();
            requestRender();
        }

        return;
    }

    const key = `${regions.sort().join(',')}|${bbox.x},${bbox.y},${bbox.width},${bbox.height}`;

    if (!force && key === state.referenceBox) {
        return;
    }

    let response;

    try {
        response = await api.reference(bbox, regions);
    } catch (error) {
        setStatus(`Could not load the reference: ${error.message}`, 'error');
        return;
    }

    state.referenceBox = key;
    state.referenceCounts = response;

    if (!response.available) {
        setStatus('No reference data - run node tools/nav-import/uo-offline.js first.', 'warn');
        return;
    }

    state.shapes = state.shapes
        .filter((shape) => !REFERENCE_LAYERS.has(shape.layer))
        .concat(response.shapes);

    updateCounts();
    requestRender();
}

/**
 * The box to ask stock spawners for, or null when we are too far out to want them.
 *
 * PADDED BY A SCREEN in each direction, so a small pan lands inside what was already fetched and
 * refetches nothing. Tile-ALIGNED, so panning back and forth across a boundary asks the same
 * question twice rather than two nearly-identical ones - which is what makes the cache work at all.
 *
 * Below the zoom floor there is no box: 2,572 markers on a whole-facet view is a grey smear that
 * costs a 4 MB parse to draw.
 */
function stockBox() {
    if (!view.facet || view.scale < STOCK_MIN_SCALE) {
        return null;
    }

    const wide = canvas.clientWidth / view.scale;
    const high = canvas.clientHeight / view.scale;

    const left = view.centerX - wide * 1.5;
    const top = view.centerY - high * 1.5;

    const snap = (n) => Math.floor(n / STOCK_GRID) * STOCK_GRID;

    return {
        x: snap(left),
        y: snap(top),
        width: snap(wide * 3) + STOCK_GRID,
        height: snap(high * 3) + STOCK_GRID
    };
}

let spawnerTimer = null;

/** Debounced, because a pan is a hundred mousemove events and none of them is the one that matters. */
function requestSpawners() {
    // Two viewport-loaded layers now, and the guard used to be one condition for one of them - so
    // panning with the reference on and the stock spawners off refetched nothing at all.
    const wantsSpawners = state.visible.has('spawners-stock');
    const wantsReference = state.referenceRegions.size > 0;

    if (!wantsSpawners && !wantsReference) {
        return;
    }

    if (spawnerTimer) {
        clearTimeout(spawnerTimer);
    }

    spawnerTimer = setTimeout(() => {
        spawnerTimer = null;

        if (wantsSpawners) {
            refreshSpawners();
        }

        if (wantsReference) {
            refreshReference();
        }
    }, STOCK_DEBOUNCE_MS);
}

function computeCoverage() {
    const waypoints = state.shapes
        .filter((s) => s.layer === 'nav' && s.map === state.facet.name)
        .map((s) => ({ x: s.points[0][0], y: s.points[0][1] }));

    coverage.compute(waypoints, HOP_CAP);
    updateCounts();
}

// A setTimeout chain rather than setInterval, so a slow response cannot stack polls on each other.
async function pollEntities() {
    if (state.visible.has('entities')) {
        try {
            const response = await api.entities();

            state.entities = response.entities || [];
            state.live = response;

            // The log is fetched alongside, not on its own timer: it is written by the same
            // snapshot pass, so a second cadence could only ever show a bot's history from a
            // different moment than its position.
            try {
                state.botLog = await api.botlog();
            } catch {
                state.botLog = state.botLog || null;
            }

            renderBots();
            updateCounts();
            requestRender();
        } catch {
            // A bridge that is briefly unreachable is not worth a banner; the live count going
            // stale says it well enough.
        }
    }

    setTimeout(pollEntities, ENTITY_POLL_MS);
}

async function pollHealth() {
    try {
        state.health = await api.health();
        renderHealth();
    } catch {
        // Same reasoning as entities.
    }

    setTimeout(pollHealth, HEALTH_POLL_MS);
}

// --- the sidebar --------------------------------------------------------------------------------

function buildLayerList() {
    dom.layers.innerHTML = '';

    for (const layer of LAYER_ORDER) {
        // The reference layers get region rows of their own further down. A row per shape layer
        // would be three checkboxes that must always agree, which is three chances to disagree.
        if (REFERENCE_LAYERS.has(layer)) {
            continue;
        }

        dom.layers.appendChild(
            layerRow(layer, LAYERS[layer].label, LAYERS[layer].color, state.visible.has(layer), (on) => {
                if (on) {
                    state.visible.add(layer);
                } else {
                    state.visible.delete(layer);
                }

                updateCounts();
                requestRender();
            }));
    }

    // Turning the stock layer on is the first time anyone wants it, so that is when it is fetched.
    const stockRow = dom.layers.querySelector('#layer-spawners-stock');

    if (stockRow) {
        stockRow.addEventListener('change', () => {
            if (stockRow.checked) {
                refreshSpawners({ force: true });
            }
        });
    }

    dom.layers.appendChild(layerRow('worksites', 'Work-site reach', '#7bd88f', false, (on) => {
        state.worksitesVisible = on;

        // Fetched on first show, then refreshed by the Site tool as points are placed. The shard
        // rewrites the file on every navigation reload, so it is never staler than the data.
        if (on && !worksites.isComputed()) {
            refreshReach();
        }

        requestRender();
    }));

    // The reference, as three rows: what to draw, and which halves of the world to ask for.
    //
    // Separate toggles rather than one, because they answer different questions. The first is
    // "show me somebody else's roads at all"; the other two are "and include the 1988 dungeon
    // waypoints", which is a different amount of map and a region Adopt refuses anyway.
    dom.layers.appendChild(layerRow('reference', 'uo-offline reference', '#6b7a8f', false, (on) => {
        setReferenceRegion('overworld', on);
    }));

    dom.layers.appendChild(layerRow('reference-dungeon', '  ...dungeons', '#5a6675', false, (on) => {
        setReferenceRegion('dungeon', on);
    }));

    dom.layers.appendChild(layerRow('reference-lostlands', '  ...Lost Lands', '#5a6675', false, (on) => {
        setReferenceRegion('lostlands', on);
    }));

    dom.layers.appendChild(layerRow('coverage', 'Coverage gaps', '#ff2800', false, (on) => {
        state.coverageVisible = on;

        // Computed on first show rather than on load: it is O(cells x waypoints) and most sessions
        // never ask for it.
        if (on && !coverage.isComputed()) {
            computeCoverage();
        }

        updateCounts();
        requestRender();
    }));
}

/**
 * Turn one reference region on or off and refetch.
 *
 * The three shape layers are shown together whenever any region is on: the split that matters to
 * the author is by REGION, not by whether a road is drawn without its waypoints.
 */
function setReferenceRegion(region, on) {
    if (on) {
        state.referenceRegions.add(region);
    } else {
        state.referenceRegions.delete(region);
    }

    const any = state.referenceRegions.size > 0;

    for (const layer of REFERENCE_LAYERS) {
        if (any) {
            state.visible.add(layer);
        } else {
            state.visible.delete(layer);
        }
    }

    refreshReference({ force: true });
}

/** Turn the work-site overlay on, and tick its row so the map and the panel agree. */
function showWorksiteOverlay() {
    state.worksitesVisible = true;

    const row = dom.layers && dom.layers.querySelector('#layer-worksites');

    if (row) {
        row.checked = true;
    }
}

function layerRow(key, label, color, checked, onChange) {
    const row = document.createElement('li');

    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = checked;
    box.id = `layer-${key}`;
    box.addEventListener('change', () => onChange(box.checked));

    const swatch = document.createElement('span');
    swatch.className = 'swatch';
    swatch.style.background = color;

    const text = document.createElement('label');
    text.htmlFor = box.id;
    text.textContent = label;

    const count = document.createElement('span');
    count.className = 'count';
    count.dataset.layer = key;

    row.append(box, swatch, text, count);

    return row;
}

function buildCreateButtons() {
    dom.createButtons.innerHTML = '';

    for (const [key, tool] of Object.entries(TOOLS)) {
        const button = document.createElement('button');

        button.type = 'button';
        button.dataset.tool = key;
        button.textContent = tool.label;
        button.addEventListener('click', () => startTool(key));

        dom.createButtons.appendChild(button);
    }
}

function updateCounts() {
    // Scoped to the layer list, not the document. `.count` is also the class on the Bots panel's
    // trailing slot, and a document-wide sweep found those too: they carry no data-layer, so they
    // fell through to the last branch and were set to the number of shapes in layer `undefined`,
    // which is 0. Every bot row has read "0" where its behaviour or its stuck rung should be,
    // for as long as the panel has existed - so the one thing the README says that slot is for,
    // "a wedged bot is the thing somebody opened this panel to find", has never once worked.
    for (const element of dom.layers.querySelectorAll('.count')) {
        const layer = element.dataset.layer;

        if (layer === 'coverage') {
            element.textContent = coverage.isComputed() ? String(coverage.gapCount()) : '-';
            continue;
        }

        // Sites, and how many of their arrival tiles fall under their type's floor. The second
        // number is the one worth reading: a site can be fine on average and still have arrivals
        // that put a bot somewhere it can dig nothing.
        if (layer === 'worksites') {
            if (!worksites.isComputed()) {
                element.textContent = '-';
            } else {
                const thin = worksites.thinCount();
                element.textContent = thin > 0
                    ? `${worksites.siteCount()} / ${thin} thin`
                    : String(worksites.siteCount());
            }

            continue;
        }

        if (layer === 'entities') {
            element.textContent = String(state.entities.length);
            continue;
        }

        // Never a bare number: 68 markers out of 2,572 looks like the whole world otherwise, and
        // the whole point of the viewport fetch is that it is not.
        if (layer === 'spawners-stock') {
            element.textContent = state.stockTotal === null
                ? (view.scale < STOCK_MIN_SCALE ? 'zoom in' : '-')
                : `${state.stockInView} / ${state.stockTotal}`;
            continue;
        }

        element.textContent = String(
            state.shapes.filter((s) => s.layer === layer && s.map === state.facet.name).length);
    }

    updateLiveStatus();
}

/**
 * The live line, including how old the snapshot is.
 *
 * On its own timer as well as on every poll, because when the shard stops answering pollEntities
 * swallows the error and stops updating - so an age computed only on a successful poll would
 * freeze at the last good value, which is the one number that must not.
 */
function updateLiveStatus() {
    if (!dom.liveStatus) {
        return;
    }

    const { text, stale } = liveStatusText(state.live, state.entities.length);

    dom.liveStatus.textContent = text;
    dom.liveStatus.classList.toggle('stale', stale);
}


function renderHealth() {
    if (!dom.health || !state.health) {
        return;
    }

    dom.health.innerHTML = '';

    for (const check of state.health.checks || []) {
        const row = document.createElement('li');

        row.className = `health ${check.status.toLowerCase()}`;
        row.title = check.detail;
        row.textContent = `${check.status.toUpperCase()} ${check.name}`;
        dom.health.appendChild(row);
    }
}

function updateToolbar() {
    const has = hasEdits();

    for (const button of dom.toolbar.querySelectorAll('[data-act]')) {
        const act = button.dataset.act;

        if (act === 'save' || act === 'discard') {
            button.disabled = !has;
        } else if (act === 'delete') {
            button.disabled = !state.selected || !isWritable(state.selected);
        } else if (act === 'finish-tool') {
            // Enter has always finished a site and a road, and nothing on screen said so. A
            // button is the affordance; the key stays the fast path.
            button.disabled = !state.tool;
        }
    }

    for (const button of dom.createButtons.querySelectorAll('[data-tool]')) {
        button.classList.toggle('active', !!state.tool && state.tool.key === button.dataset.tool);
    }
}

// --- the filter ---------------------------------------------------------------------------------

function applyFilter() {
    const term = state.filter.toLowerCase();

    dom.matches.innerHTML = '';

    if (!term) {
        return;
    }

    const found = state.shapes.filter(
        (s) => (s.label || '').toLowerCase().includes(term) || s.id.toLowerCase().includes(term));

    for (const shape of found.slice(0, 60)) {
        const row = document.createElement('li');
        const layer = document.createElement('span');

        layer.className = 'layer';
        layer.textContent = ` ${LAYERS[shape.layer] ? LAYERS[shape.layer].label : shape.layer}`;

        row.textContent = shape.label || shape.id;
        row.append(layer);
        row.addEventListener('click', () => {
            select(shape);
            centerOnShape(shape);
        });

        dom.matches.appendChild(row);
    }

    if (found.length === 0) {
        const empty = document.createElement('li');

        empty.className = 'muted';
        empty.textContent = 'Nothing matches.';
        dom.matches.appendChild(empty);
    }
}

function matchingShapes() {
    if (!state.filter) {
        return null;
    }

    const needle = state.filter.toLowerCase();

    return new Set(state.shapes.filter(
        (s) => (s.label || '').toLowerCase().includes(needle) || s.id.toLowerCase().includes(needle)));
}

function centerOnShape(shape) {
    if (shape.rect) {
        view.goTo(shape.rect[0] + shape.rect[2] / 2, shape.rect[1] + shape.rect[3] / 2);
    } else if (shape.points && shape.points.length > 0) {
        view.goTo(shape.points[0][0], shape.points[0][1]);
    }

    requestRender();
}

// --- rendering ----------------------------------------------------------------------------------

let pending = false;

/**
 * Demand-driven, unlike 5a's unconditional 60fps loop.
 *
 * That loop called view.resize() and rebuilt a Set over every shape on every frame, which was
 * tolerable when nothing could change and is not now: this file calls requestRender from around
 * forty places.
 */
/**
 * Pull per-arrival reach from the shard.
 *
 * `probeType` and `probePoints` ask about tiles that are not in navigation.json yet - the Site
 * tool uses them so an arrival can be judged as it is placed. Without them this just refreshes
 * the authored set.
 *
 * Never computed here. The count comes from BotWorkSites.ReachFrom running the same sweep the bot
 * runs, against real map data the browser does not have.
 */
async function refreshReach(probeType, probePoints) {
    const body = probeType && probePoints && probePoints.length > 0
        ? `${probeType} ${probePoints.map(([x, y]) => `${x},${y}`).join(' ')}`
        : '';

    return sendReach(body);
}

/**
 * Ask the shard for the best places to stand inside a zone.
 *
 * One rect, not a point list, because the token is capped at 4096 bytes and a 25x20 lumber zone
 * would not fit as points. The shard sweeps it with the same ReachFrom the bot runs and hands
 * back the best two dozen standable tiles, already ranked.
 */
async function sweepZone(probeType, [x, y, w, h]) {
    return sendReach(`${probeType} ${x},${y},${w},${h}`);
}

/**
 * Never computed here. The count comes from BotWorkSites.ReachFrom running the same sweep the bot
 * runs, against real map data the browser does not have.
 *
 * Returns the ack so a caller can show what the shard warned about - an over-large zone, or a
 * zone with nothing standable in it, which is the one case where an empty answer needs a reason.
 */
async function sendReach(body) {
    try {
        const dropped = await api.request('site-reach', body);
        const ack = await api.awaitAck('site-reach', { nonce: dropped.nonce, timeoutMs: 30000 });

        worksites.setReach(await api.reach());
        updateCounts();
        requestRender();

        return ack;
    } catch (error) {
        // A reach answer is an aid, never a gate. The shard being down must not stop anybody
        // placing a point - it only means the dots are not there to help while they do it.
        setStatus(`Could not measure reach: ${error.message}`, 'warn');
        return null;
    }
}

/**
 * The Bots panel: every live bot, what it is doing, and why.
 *
 * Fed by the same entity snapshot the map draws, so a row and a dot can never disagree - they are
 * the same record. The event log behind a selected bot comes from Data/Live/botlog.json, which the
 * shard writes on the same timer for the same reason.
 *
 * This is the panel the Gatherer walk-in fault needed and did not have. Three separate faults
 * shared one symptom - a bot standing still - and telling them apart meant reading a console that
 * logged almost none of it. A list that says "walking in to The Northern Outcrop" next to one that
 * says "standing outside a work site" separates two of them at a glance.
 */
/**
 * Opens a bot's inspector, from the Bots panel or from a click on the map.
 *
 * `centre` is the difference between the two. A name clicked in a list has to bring the map to the
 * bot, because the list gives no idea where it is; a bot clicked ON the map is already in front of
 * you, and moving the view out from under the cursor is exactly the thing that makes a canvas feel
 * like it is fighting you.
 *
 * `state.selectedBot` is a serial and is deliberately parallel to `state.selected`, which is a
 * shape: a bot is not a record, cannot be edited, dragged or saved, and giving it the shape
 * selection would mean every writer path having to ask what kind of thing it was holding.
 */
function selectBot(bot, centre) {
    state.selectedBot = bot.serial;

    if (centre) {
        view.centerOn(bot.x, bot.y);
    }

    openBotCard(bot);
    renderBots();
    requestRender();
}

// --- the bot card -------------------------------------------------------------------------------
//
// The selected bot, floating over the map, with everything the Bots panel shows and the same
// event log. It exists because the panel's detail block can be an entire column's scroll away
// from the dot that was clicked - and on the art view that dot is usually the only reason
// anybody is looking at that part of the map.
//
// It does NOT replace the panel selection. selectBot still calls renderBots, the row still
// highlights and still scrolls itself into view, and clicking a row still opens the card. Two
// views of one selection, never two selections.

const CARD_KEY = 'gg-editor:bot-card';

function wireBotCard() {
    dom.botCardClose.addEventListener('click', closeBotCard);

    let drag = null;

    dom.botCardHead.addEventListener('pointerdown', (event) => {
        // Not the close button: a drag started on it would swallow the click that closes.
        if (event.button !== 0 || event.target === dom.botCardClose) {
            return;
        }

        const box = dom.botCard.getBoundingClientRect();
        const stage = dom.botCard.parentElement.getBoundingClientRect();

        drag = {
            id: event.pointerId,
            dx: event.clientX - box.left,
            dy: event.clientY - box.top,
            stage
        };

        dom.botCardHead.setPointerCapture(event.pointerId);
        dom.botCard.classList.add('dragging');
        event.preventDefault();
    });

    dom.botCardHead.addEventListener('pointermove', (event) => {
        if (!drag) {
            return;
        }

        placeCard(
            event.clientX - drag.stage.left - drag.dx,
            event.clientY - drag.stage.top - drag.dy);
    });

    const end = (event) => {
        if (!drag) {
            return;
        }

        drag = null;
        dom.botCard.classList.remove('dragging');

        try {
            dom.botCardHead.releasePointerCapture(event.pointerId);
        } catch {
            // Already released; the pointer left the window mid-drag.
        }

        try {
            localStorage.setItem(CARD_KEY, JSON.stringify({
                left: parseFloat(dom.botCard.style.left) || 0,
                top: parseFloat(dom.botCard.style.top) || 0
            }));
        } catch {
            // Storage unavailable. The card still moves; it just starts at the default next time.
        }
    };

    dom.botCardHead.addEventListener('pointerup', end);
    dom.botCardHead.addEventListener('pointercancel', end);
}

/**
 * Puts the card at a stage-relative position, clamped so its header stays grabbable.
 *
 * Clamped on every placement rather than only on drag, because a remembered position is against
 * a window that may since have been made smaller - and a card whose header is off the bottom of
 * the stage cannot be moved or closed with the mouse at all.
 */
function placeCard(left, top) {
    const stage = dom.botCard.parentElement;
    const width = dom.botCard.offsetWidth;
    const headHeight = dom.botCardHead.offsetHeight;

    const x = Math.min(Math.max(0, left), Math.max(0, stage.clientWidth - width));
    const y = Math.min(Math.max(0, top), Math.max(0, stage.clientHeight - headHeight));

    dom.botCard.style.left = `${Math.round(x)}px`;
    dom.botCard.style.top = `${Math.round(y)}px`;
    dom.botCard.style.right = 'auto';
    dom.botCard.style.bottom = 'auto';
}

function openBotCard(bot) {
    const first = dom.botCard.hidden;

    dom.botCard.hidden = false;
    dom.botCardTitle.textContent = bot.name;
    fillBotDetail(dom.botCardBody, bot);

    if (!first) {
        return;
    }

    // Only on the first open of a session: re-placing it every time would undo a drag the moment
    // the next bot is picked.
    let stored = null;

    try {
        stored = JSON.parse(localStorage.getItem(CARD_KEY) || 'null');
    } catch {
        stored = null;
    }

    if (stored && Number.isFinite(stored.left) && Number.isFinite(stored.top)) {
        placeCard(stored.left, stored.top);
    }
}

function closeBotCard() {
    dom.botCard.hidden = true;
    dom.botCardBody.innerHTML = '';
}

function renderBots() {
    const filter = (dom.botFilter.value || '').trim().toLowerCase();

    const bots = state.entities
        .filter((entity) => entity.kind === 'bot')
        .filter((entity) => !filter
            || `${entity.name} ${entity.class || ''} ${entity.behavior || ''}`
                .toLowerCase().includes(filter))
        .sort((a, b) => (a.name || '').localeCompare(b.name || ''));

    dom.bots.innerHTML = '';

    if (bots.length === 0) {
        const empty = document.createElement('li');

        empty.className = 'muted';
        empty.textContent = state.entities.length === 0
            ? 'live is off - turn it on under Shard'
            : (filter ? 'no bot matches' : 'no bots');

        dom.bots.append(empty);
        renderBotDetail(null);
        return;
    }

    for (const bot of bots) {
        const row = document.createElement('li');
        const swatch = document.createElement('span');

        swatch.className = 'swatch';
        swatch.style.background = BEHAVIOR_COLORS[bot.behavior] || '#c98bdb';

        const label = document.createElement('label');

        label.textContent = bot.name;
        label.title = bot.status || '';

        const count = document.createElement('span');

        count.className = 'count';

        // The stuck rung, when there is one, beats the behaviour name for the trailing slot: a
        // wedged bot is the thing somebody opened this panel to find.
        count.textContent = bot.stuck ? `stuck: ${bot.stuck}` : (bot.behavior || '');

        if (bot.stuck) {
            count.classList.add('warn');
        }

        row.append(swatch, label, count);

        row.addEventListener('click', () => selectBot(bot, true));

        if (bot.serial === state.selectedBot) {
            row.classList.add('selected');

            // A bot selected by clicking its dot on the map may be a hundred rows down a list
            // nobody has scrolled. Bringing the row into view is what makes the two halves of the
            // panel feel like one thing rather than two that happen to agree.
            row.scrollIntoView({ block: 'nearest' });
        }

        dom.bots.append(row);
    }

    renderBotDetail(bots.find((bot) => bot.serial === state.selectedBot) || null);
}

/**
 * The selected bot's detail and its recent events, in the panel and in the card.
 *
 * One function filling both, because they are the same answer to the same question and a second
 * copy of it would be free to disagree - which is exactly the fault the Bots panel exists to
 * prevent between a row and a dot.
 */
function renderBotDetail(bot) {
    dom.botDetail.hidden = !bot;
    fillBotDetail(dom.botDetail, bot);

    // The card is closable independently: filling it does not reopen it. Only selectBot does.
    if (!dom.botCard.hidden) {
        dom.botCardTitle.textContent = bot ? bot.name : '';
        fillBotDetail(dom.botCardBody, bot);

        if (!bot) {
            closeBotCard();
        }
    }
}

function fillBotDetail(host, bot) {
    host.innerHTML = '';

    if (!bot) {
        return;
    }

    const name = document.createElement('div');

    name.className = 'name';
    name.textContent = bot.name;

    const kind = document.createElement('div');

    kind.className = 'kind';
    kind.textContent = [bot.class, bot.tier, bot.behavior].filter(Boolean).join(' / ');

    const status = document.createElement('div');

    status.className = 'muted';
    status.textContent = bot.status || '';

    host.append(name, kind, status);

    // How it is moving, named rather than left as a number. The step delay is what
    // BaseAI.DoMoveImpl derives the run flag from, so 200 on foot IS running - but nobody reads
    // it that way at a glance, and "is that one running or walking?" is the question this panel
    // exists to answer without timing a bot across two snapshots.
    if (typeof bot.stepMs === 'number') {
        const pace = document.createElement('div');
        const running = bot.stepMs <= (bot.mounted ? 100 : 200);

        pace.className = 'muted';
        pace.textContent = `${running ? 'running' : 'walking'}`
            + `${bot.mounted ? ', mounted' : ', on foot'} (${bot.stepMs}ms/step)`;

        host.append(pace);
    }

    if (bot.dest) {
        const dest = document.createElement('div');

        dest.className = 'muted';
        dest.textContent = `heading for ${bot.dest}`;
        host.append(dest);
    }

    const entry = (state.botLog && state.botLog.bots || [])
        .find((row) => row.serial === bot.serial);

    const events = document.createElement('ul');

    events.className = 'events';

    if (!entry || entry.events.length === 0) {
        const none = document.createElement('li');

        none.className = 'muted';
        none.textContent = 'no events recorded';
        events.append(none);
    } else {
        // Newest first here, though the shard writes them oldest first: the file is a log to read
        // forwards, the panel is a question about what just happened.
        for (const event of entry.events.slice().reverse()) {
            const item = document.createElement('li');

            item.textContent = `${event.kind}: ${event.text}`;
            item.title = event.utc;
            events.append(item);
        }
    }

    host.append(events);
}

/**
 * Everything a corridor proposal can be edited with, without reaching for another tool.
 *
 * A proposal is ordinary waypoint and edge records, so dragging already works; what these add is
 * the two operations that would otherwise mean deleting an edge, deleting a point, and drawing two
 * more by hand - and the re-verification that makes an edit trustworthy rather than hopeful.
 */

/** The edges of the proposal that touch this waypoint. */
function hopsTouching(waypointId) {
    return state.shapes.filter((shape) =>
        shape.layer === 'nav-edges'
        && (shape.props.from === waypointId || shape.props.to === waypointId));
}

/**
 * Remove a waypoint and join what it was between.
 *
 * Deleting a point out of a road otherwise leaves a gap: its two hops go with it and the road is
 * in two pieces. Relinking is what makes "this waypoint is unnecessary" a single action rather
 * than three, and it is the common edit - the search proposes a point every ten tiles whether or
 * not the road bends there.
 */
function deleteAndRelink(shape) {
    const id = shape.props.id;
    const touching = hopsTouching(id);

    const neighbours = [];

    for (const hop of touching) {
        const other = hop.props.from === id ? hop.props.to : hop.props.from;

        if (!neighbours.includes(other)) {
            neighbours.push(other);
        }
    }

    pushOp({ op: 'delete', shapeId: shape.id, shape });

    for (const hop of touching) {
        removeShape(hop);
    }

    removeShape(shape);

    // Only an interior point relinks. An end has one neighbour and nothing to join it to, which
    // is correct: deleting the end of a road shortens the road.
    if (neighbours.length === 2) {
        const a = state.shapes.find((s) => s.layer === 'nav' && s.props.id === neighbours[0]);
        const b = state.shapes.find((s) => s.layer === 'nav' && s.props.id === neighbours[1]);

        if (a && b) {
            const edge = buildShape('edge', {}, state.facet.name,
                { points: [a.points[0], b.points[0]] },
                { ids: [neighbours[0], neighbours[1]] });

            createShape(edge);

            if (state.proposal) {
                state.proposal.add(edge.id);
            }

            verifyHops([edge]);
        }
    }

    state.selected = null;
    showProperties(null);
    fillLists(state.shapes);
    syncDerived(state.shapes);
    updateToolbar();
    requestRender();

    setStatus(neighbours.length === 2
        ? `Removed ${id} and joined its neighbours.`
        : `Removed ${id}.`, 'ok');
}

/**
 * Put a waypoint in the middle of a hop, splitting it in two.
 *
 * The other half of the same idea: the search puts points at a fixed spacing, so a road that has
 * to bend around something usually needs one MORE point exactly where the bend is. Double-clicking
 * the hop is where somebody is already looking when they notice.
 */
function insertOnHop(edge, worldX, worldY) {
    const from = edge.props.from;
    const to = edge.props.to;

    const x = Math.floor(worldX);
    const y = Math.floor(worldY);

    // Z is taken from the hop's own end rather than assumed flat: a road on a slope has a real Z
    // at every point, and a new point at 0 would be underground.
    const z = edge.points[0][2] || 0;

    // Named after its neighbours, not after the zone it landed in. A point inserted between
    // 'town-9' and 'town-10' while standing in the bank quarter used to be called 'bank-2',
    // which reads as part of a different road and sorts nowhere near the one it belongs to.
    const id = insertedId(from, to, state.facet.name, x, y, state.shapes);

    const named = (waypointId) => {
        const shape = state.shapes.find((s) => s.id === `wp:${waypointId}`);

        return shape ? shape.props.name || '' : '';
    };

    const name = insertedName(named(from), named(to), id);

    const props = { id, tags: 'road', arrivalRange: '0' };

    if (name) {
        props.name = name;
    }

    const waypoint = buildShape('waypoint', props,
        state.facet.name, { points: [[x, y, z]] }, {});

    waypoint.points = [[x, y, z]];

    pushOp({ op: 'delete', shapeId: edge.id, shape: edge });
    removeShape(edge);

    createShape(waypoint);

    const left = buildShape('edge', {}, state.facet.name,
        { points: [[0, 0, 0], [0, 0, 0]] }, { ids: [from, id] });
    const right = buildShape('edge', {}, state.facet.name,
        { points: [[0, 0, 0], [0, 0, 0]] }, { ids: [id, to] });

    createShape(left);
    createShape(right);

    if (state.proposal) {
        state.proposal.add(waypoint.id);
        state.proposal.add(left.id);
        state.proposal.add(right.id);
    }

    syncDerived(state.shapes);
    verifyHops([left, right]);

    setStatus(`Inserted ${id} into that hop.`, 'ok');
    requestRender();
}

/**
 * Ask the shard whether these hops are walkable, and colour them by the answer.
 *
 * The browser cannot answer this - it has no map data and no movement rules - and an edit that
 * merely looked plausible is exactly what the corridor tool exists to stop somebody saving. The
 * probe drives the same real BaseCreature the proposal was built with, so a hop that passes here
 * passes for the same reason the original ones did.
 */
async function verifyHops(edges) {
    const pairs = [];

    for (const edge of edges) {
        if (!edge.points || edge.points.length < 2) {
            continue;
        }

        pairs.push(`${edge.points[0][0]},${edge.points[0][1]}`);
        pairs.push(`${edge.points[1][0]},${edge.points[1][1]}`);
    }

    if (pairs.length === 0) {
        return;
    }

    try {
        const dropped = await api.request('nav-hop', `verify ${pairs.join(' ')}`);

        await api.awaitAck('nav-hop', { nonce: dropped.nonce, timeoutMs: 30000 });

        const answer = await api.hops();
        const flags = new Map(state.hopFlags || []);

        answer.hops.forEach((hop, index) => {
            if (edges[index]) {
                flags.set(edges[index].id, hop.ok);
            }
        });

        state.hopFlags = flags;
        setHopFlags(flags);

        const bad = answer.hops.filter((hop) => !hop.ok).length;

        if (bad > 0) {
            setStatus(`${bad} hop(s) the engine will not walk - shown in red.`, 'error');
        }

        requestRender();
    } catch (error) {
        setStatus(`Could not verify that hop: ${error.message}`, 'warn');
    }
}

/**
 * Pull a point onto the nearest made road.
 *
 * Shift-drag, because it is the same gesture as a drag and the modifier says "and tidy it up".
 * Which tiles are road is a fact about the client's tiledata, so the shard answers it - the same
 * classification the search weights by, rather than a second opinion that could disagree.
 */
async function snapToRoad(shape) {
    const [x, y] = shape.points[0];

    try {
        const dropped = await api.request('nav-hop', `snap ${x},${y}`);

        await api.awaitAck('nav-hop', { nonce: dropped.nonce, timeoutMs: 30000 });

        const answer = await api.hops();

        if (!answer.snap) {
            setStatus('No road within reach of that point.', 'warn');
            return;
        }

        shape.points = [answer.snap.slice()];
        markDirty(shape);
        syncDerived(state.shapes);

        verifyHops(hopsTouching(shape.props.id));

        setStatus(`Snapped ${shape.props.id} to the road at ${answer.snap[0]},${answer.snap[1]}.`, 'ok');
        requestRender();
    } catch (error) {
        setStatus(`Could not snap: ${error.message}`, 'warn');
    }
}

function requestRender() {
    if (pending) {
        return;
    }

    pending = true;
    requestAnimationFrame(render);
}

function render() {
    pending = false;

    // The floor row says whether the stop it is on is the one being drawn, and that depends on
    // the zoom - so it is updated from the render rather than only from the slider.
    updateFloorRow();

    view.resize();
    view.drawMap();

    const ctx = canvas.getContext('2d');

    // Under everything: it is a background condition, not a thing on the map.
    if (state.coverageVisible) {
        coverage.draw(ctx, view);
    }

    drawShapes(ctx, view, state.shapes, state.visible, state.selected, state.hovered, matchingShapes());

    // Edges the adopt could not walk. Drawn over everything because they are the thing the author
    // has to decide about, and they are NOT records - nothing will be written for them.
    if (state.adoptFailures.length > 0) {
        drawAdoptFailures(ctx, view);
    }

    if (state.worksitesVisible) {
        worksites.draw(ctx, view);
    }

    if (state.visible.has('entities')) {
        drawEntities(ctx, view, state.entities);
    }

    if (state.draft) {
        drawDraft(ctx, view, state.draft);
    }
}

// --- selection and properties ---------------------------------------------------------------------

/**
 * Whether a shape can be edited at all.
 *
 * Two kinds cannot. The derived daily-life markers have their coordinates in navigation.json rather
 * than their own file, and the stock spawners are somebody else's content - the editor writes
 * Spawns/Custom and nothing else.
 */
function isWritable(shape) {
    return !shape.id.startsWith('marker:') && !READ_ONLY_LAYERS.has(shape.layer);
}

/**
 * Which file a shape is saved to.
 *
 * A table lookup for everything with one file, and read off the id for spawners, whose family is
 * many files. `spawner:trammel/GG_DailyLife.xml#<guid>` carries its own file, which is the same
 * reason the id carries it at all: a save is scoped to one file and the shape has to say which.
 */
function fileOf(shape) {
    if (shape.layer === 'spawners') {
        return `spawn:${shape.id.slice('spawner:'.length, shape.id.lastIndexOf('#'))}`;
    }

    return LAYERS[shape.layer] ? LAYERS[shape.layer].file : null;
}

function select(shape) {
    state.selected = shape;
    showProperties(shape);
    updateToolbar();
    requestRender();
}

function showProperties(shape) {
    dom.properties.innerHTML = '';

    if (!shape) {
        dom.properties.innerHTML = '<p class="muted">Nothing selected.</p>';
        return;
    }

    const name = document.createElement('p');
    name.className = 'name';
    name.textContent = shape.label || shape.id;

    const kind = document.createElement('p');
    kind.className = 'kind';
    // The id rather than a file and a pointer, which is what the original printed: this IS our
    // address, and it is what a bug report needs.
    kind.textContent = shape.id;

    dom.properties.append(name, kind);

    if (!isWritable(shape)) {
        const note = document.createElement('p');

        note.className = 'muted';
        note.textContent =
            'Derived from the navigation data. Edit the record it points at instead.';
        dom.properties.append(note);
        return;
    }

    const stale = isStaleZ(shape);

    if (shape.rect) {
        dom.properties.append(readonlyRow([
            ['x', shape.rect[0]], ['y', shape.rect[1]],
            ['w', shape.rect[2]], ['h', shape.rect[3]]
        ]));
    } else if (shape.points && shape.points.length === 1) {
        // `z?` spelled the same way the cursor readout spells it, and meaning the same thing: the
        // number is not a measurement of the ground it sits on. On the marker it is a glyph you
        // notice; here it is the one place that says HOW far out, which is what decides whether
        // the record wants a drag or a second look.
        const ground = stale ? landz.peek(shape.points[0][0], shape.points[0][1]) : null;

        dom.properties.append(readonlyRow([
            ['x', shape.points[0][0]],
            ['y', shape.points[0][1]],
            [stale ? 'z?' : 'z', stale
                ? `${shape.points[0][2]} (ground ${ground})`
                : shape.points[0][2]]
        ]));
    }

    for (const field of shape.fields || []) {
        dom.properties.append(editableField(shape, field));
    }

    if (shape.entries) {
        dom.properties.append(entryEditor(shape));
    }
}

/**
 * What a spawner spawns: a list of type and count.
 *
 * The panel never sees the <Objects2> micro-format. It has no escaping at all and two of its rules
 * fail silently on the shard - a type name containing `:MX=` makes the reader discard the whole
 * entry - so the grammar lives in one place, on the bridge, and this edits a plain list.
 */
function entryEditor(shape) {
    const box = document.createElement('div');
    const heading = document.createElement('span');

    heading.className = 'muted';
    heading.textContent = 'Spawns';
    box.append(heading);

    // Every spawnable type, whatever kind: there is no kind selected here to narrow by, because
    // an existing spawner's type is being CHANGED rather than chosen. Minted with the panel
    // rather than declared in index.html, so it cannot be stale relative to the vocabulary.
    const types = document.createElement('datalist');

    types.id = 'entry-type-list';

    for (const name of vocab.allSpawnTypes()) {
        const option = document.createElement('option');

        option.value = name;
        types.append(option);
    }

    box.append(types);

    const editable = isWritable(shape);

    shape.entries.forEach((entry, index) => {
        const row = document.createElement('div');
        row.className = 'row';

        const type = document.createElement('input');
        type.value = entry.type;
        type.readOnly = !editable;
        type.setAttribute('list', types.id);

        const max = document.createElement('input');
        max.value = entry.max;
        max.readOnly = !editable;
        max.style.maxWidth = '4em';

        if (editable) {
            const commit = () => {
                const before = shape.entries.map((e) => ({ ...e }));

                shape.entries[index] = { type: type.value.trim(), max: max.value.trim() || '1' };
                pushOp({ op: 'entries', shapeId: shape.id, before, after: shape.entries.map((e) => ({ ...e })) });
                markDirty(shape);
                showProperties(shape);
            };

            type.addEventListener('change', commit);
            max.addEventListener('change', commit);
        }

        row.append(type, max);

        if (editable) {
            const remove = document.createElement('button');

            remove.type = 'button';
            remove.textContent = '−';
            remove.title = 'Remove this entry';
            remove.addEventListener('click', () => {
                const before = shape.entries.map((e) => ({ ...e }));

                shape.entries.splice(index, 1);
                pushOp({ op: 'entries', shapeId: shape.id, before, after: shape.entries.map((e) => ({ ...e })) });
                markDirty(shape);
                showProperties(shape);
            });

            row.append(remove);
        }

        box.append(row);
    });

    if (editable) {
        const add = document.createElement('button');

        add.type = 'button';
        add.textContent = 'Add a spawn entry';
        add.addEventListener('click', () => {
            const before = shape.entries.map((e) => ({ ...e }));

            shape.entries.push({ type: '', max: '1' });
            pushOp({ op: 'entries', shapeId: shape.id, before, after: shape.entries.map((e) => ({ ...e })) });
            markDirty(shape);
            showProperties(shape);
        });

        box.append(add);
    }

    return box;
}

function readonlyRow(pairs) {
    const row = document.createElement('div');

    row.className = 'row';

    for (const [key, value] of pairs) {
        const label = document.createElement('label');
        const caption = document.createElement('span');
        const input = document.createElement('input');

        caption.textContent = key;
        input.value = String(value);
        input.readOnly = true;

        label.append(caption, input);
        row.append(label);
    }

    return row;
}

function editableField(shape, field) {
    const label = document.createElement('label');
    const caption = document.createElement('span');

    // The same vocabulary the create form for this record used. A dropdown on create and free
    // text on edit would teach, one form at a time, that the vocabulary is optional.
    const source = vocab.fieldVocabulary(shape.layer, field.key);
    const values = source ? vocab.optionsFor(source.key) : [];

    const input = source && source.closed && values.length > 0
        ? document.createElement('select')
        : document.createElement('input');

    caption.textContent = field.label;

    if (input.tagName === 'SELECT') {
        for (const value of values) {
            const option = document.createElement('option');

            option.value = value;
            option.textContent = value;
            input.append(option);
        }
    } else {
        input.autocomplete = 'off';

        if (field.type === 'navid') {
            input.setAttribute('list', 'navid-list');
        } else if (values.length > 0) {
            const datalist = document.createElement('datalist');

            datalist.id = `prop-list-${shape.layer}-${field.key}`;
            input.setAttribute('list', datalist.id);

            for (const value of values) {
                const option = document.createElement('option');

                option.value = value;
                datalist.append(option);
            }

            label.append(datalist);
        }
    }

    input.value = shape.props[field.key] === undefined ? '' : String(shape.props[field.key]);

    input.addEventListener('change', () => {
        const before = { ...shape.props };
        const raw = input.value.trim();

        shape.props[field.key] = field.type === 'int' ? Number(raw) || 0 : raw;

        // The label is derived from props for most kinds, so it goes stale otherwise.
        if (field.key === 'id' || field.key === 'name') {
            shape.label = shape.props.name || shape.props.id || shape.label;
        }

        pushOp({ op: 'props', shapeId: shape.id, before, after: { ...shape.props } });
        markDirty(shape);
        showProperties(shape);
        requestRender();
    });

    label.append(caption, input);

    return label;
}

// --- dirty tracking, undo and redo ------------------------------------------------------------------

function hasEdits() {
    return state.dirty.size > 0 || state.created.size > 0 || state.deleted.size > 0;
}

/**
 * Marks a shape as changed since the last save.
 *
 * The ModernUO original also snapshotted the server's value here, so its discard could PATCH the
 * old values back. Ours does not need to: discard restores the .bak, which is the pre-save file
 * byte for byte rather than a reconstruction of it. Keeping a baseline nothing read would be dead
 * state that looks live.
 */
function markDirty(shape) {
    // A shape created this session is written as a whole record, so it stays a create and never
    // also becomes an update.
    if (!state.created.has(shape.id)) {
        state.dirty.set(shape.id, shape);
    }

    updateToolbar();
    validatePreview();
}

/**
 * Pushes an undoable step and drops the redo stack.
 *
 * A step is `{op, shapeId, before, after}` rather than the original's geometry-only snapshot, so
 * that creates and deletes are undoable too. Redo is the popped steps, cleared by any new edit -
 * a redo stack that survives a divergent edit describes a history that never happened.
 */
function pushOp(step) {
    state.undo.push(step);
    state.redo.length = 0;
}

function shapeById(id) {
    return state.shapes.find((shape) => shape.id === id);
}

function applyStep(step, direction) {
    const value = direction === 'undo' ? step.before : step.after;

    if (step.op === 'geometry') {
        applyGeometry(shapeById(step.shapeId), value);
        return;
    }

    if (step.op === 'props') {
        Object.assign(shapeById(step.shapeId), { props: { ...value } });
        return;
    }

    if (step.op === 'entries') {
        shapeById(step.shapeId).entries = value.map((entry) => ({ ...entry }));
        return;
    }

    // create and delete are each other's inverse, so one branch does both.
    const adding = (step.op === 'create') === (direction === 'redo');

    if (adding) {
        state.shapes.push(step.shape);
        state.deleted.delete(step.shapeId);

        if (step.op === 'create') {
            state.created.set(step.shapeId, step.shape);
        }
    } else {
        state.shapes = state.shapes.filter((shape) => shape.id !== step.shapeId);
        state.created.delete(step.shapeId);

        if (step.op === 'delete') {
            state.deleted.set(step.shapeId, step.shape);
        }
    }
}

function undo() {
    const step = state.undo.pop();

    if (!step) {
        return;
    }

    applyStep(step, 'undo');
    state.redo.push(step);

    // The shape stays dirty: undoing back to the on-disk value still needs a save to be sure, and
    // guessing wrong in the other direction would silently drop an edit.
    afterHistory('Undone.');
}

function redo() {
    const step = state.redo.pop();

    if (!step) {
        return;
    }

    applyStep(step, 'redo');
    state.undo.push(step);

    afterHistory('Redone.');
}

function afterHistory(message) {
    if (state.selected && !shapeById(state.selected.id)) {
        state.selected = null;
    }

    showProperties(state.selected);
    fillLists(state.shapes);
    updateToolbar();
    validatePreview();
    requestRender();
    setStatus(message);
}

// --- creating and deleting --------------------------------------------------------------------------

function createShape(shape) {
    state.shapes.push(shape);
    state.created.set(shape.id, shape);

    pushOp({ op: 'create', shapeId: shape.id, shape });

    fillLists(state.shapes);
    select(shape);
    updateToolbar();
    validatePreview();
    requestRender();
    setStatus(`Created ${shape.id}. Save to write it.`, 'ok');
}

/**
 * Take a shape out without the ceremony deleteSelected performs.
 *
 * Same bookkeeping - unsaved records simply stop existing, saved ones become a pending delete -
 * but no selection change, no status line and no undo entry, because the callers are batch
 * operations that push one undo entry of their own. Splitting this out is what lets a proposal be
 * replaced, or a waypoint relinked, without four status lines flashing past.
 */
function removeShape(shape) {
    state.shapes = state.shapes.filter((s) => s.id !== shape.id);

    if (state.created.has(shape.id)) {
        state.created.delete(shape.id);
    } else {
        state.deleted.set(shape.id, shape);
    }

    state.dirty.delete(shape.id);

    if (state.proposal) {
        state.proposal.delete(shape.id);
    }
}

function deleteSelected() {
    const shape = state.selected;

    if (!shape || !isWritable(shape)) {
        return;
    }

    state.shapes = state.shapes.filter((s) => s.id !== shape.id);

    if (state.created.has(shape.id)) {
        // Never written, so there is nothing on disk to delete - it simply stops existing.
        state.created.delete(shape.id);
    } else {
        state.deleted.set(shape.id, shape);
    }

    state.dirty.delete(shape.id);
    pushOp({ op: 'delete', shapeId: shape.id, shape });

    state.selected = null;
    showProperties(null);
    fillLists(state.shapes);
    updateToolbar();
    validatePreview();
    requestRender();
    setStatus(`Deleted ${shape.id}. Save to write it.`, 'ok');
}

// --- the tools ----------------------------------------------------------------------------------

/**
 * Starts a create tool, optionally with the tile it should place at already decided.
 *
 * `placeAt` is the context menu's "Add here", and it is a PICKED TILE - `{x, y, z}` - rather than a
 * pair. It used to be `[floor(worldX), floor(worldY)]` from the ground-plane inverse, and it called
 * toolClick directly, past canPlace: right-clicking Waypoint in the art view created one about 2.7
 * tiles from where the menu had been opened, silently, while every other route into the same tool
 * was refusing to guess.
 */
function startTool(key, placeAt = null) {
    const tool = TOOLS[key];

    if (!tool) {
        return;
    }

    state.tool = { key, ...tool, phase: 0, owner: null, ids: [], arrivals: [] };
    state.draft = {
        kind: tool.kind === 'rect' || tool.kind === 'adopt-rect' ? 'rect' : 'points',
        points: [], rect: null
    };
    state.selected = null;

    // An owner-then-point tool can skip its first phase when the thing it needs is already picked.
    if (tool.kind === 'owner-then-point' && state.hovered && state.hovered.layer === tool.picks) {
        state.tool.owner = state.hovered;
        state.tool.phase = 1;
    }

    setHint(state.tool.phase === 1 ? tool.hint2 : tool.hint);
    setToolStep();
    showProperties(null);
    updateToolbar();

    // The corridor asks its form before the first click: its name is what every waypoint the road
    // mints is called, so it has to exist before there is anything to name.
    if (tool.formAtStart) {
        askAtStart(state.tool);
        return;
    }

    if (placeAt && tool.kind === 'point') {
        toolClick(placeAt, placeAt.x, placeAt.y);
    }

    requestRender();
}

/** The form a formAtStart tool opens before collecting anything. */
async function askAtStart(tool) {
    const values = await askFor(tool, {});

    // Esc closed the modal, or another tool was started while it was open.
    if (!state.tool || state.tool !== tool) {
        return;
    }

    if (!values) {
        cancelTool();
        return;
    }

    tool.props = values;
    setHint(tool.hint);
    setToolStep();
    requestRender();
}

function cancelTool() {
    state.tool = null;
    state.draft = null;
    setHint('');
    updateToolbar();
    requestRender();
}

/**
 * A click while a tool is running. Returns true when it consumed the click.
 *
 * `pair`, `chain` and `owner-then-point` all collect EXISTING shapes rather than bare points,
 * because everything they build is made of ids: an edge is two waypoint ids, a route is a list of
 * them, an arrival belongs to a destination. A click that hits nothing collectable says so rather
 * than doing nothing, which is the classic way click-to-collect feels broken.
 */
function toolClick(tile, worldX, worldY, screenX = null, screenY = null) {
    const tool = state.tool;

    if (!tool) {
        return false;
    }

    // The tile is the pick - which tile the cursor is ON, and the Z a mobile would stand at there.
    // The world pair beside it is the fractional cursor, and is only for hit testing the shapes a
    // collecting tool picks: sub-tile in radar, ignored in art in favour of the screen pair.
    const x = tile.x;
    const y = tile.y;
    const z = tile.z === null || tile.z === undefined ? 0 : tile.z;

    if (tool.kind === 'point') {
        state.draft.points = [[x, y, z]];
        completeTool();
        return true;
    }

    // Bare points, unlike 'pair' and 'chain', which collect existing shapes. A road may start
    // and end anywhere - the whole reason for wanting one is usually that there are no waypoints
    // out there yet - and the points between the ends are VIA points, steering it by hand.
    //
    // Steering matters because the search optimises for cost, and cheapest is not always the road
    // somebody has in mind: two ways round a building can differ by a handful of tiles and by a
    // great deal of sense.
    if (tool.kind === 'point-chain') {
        state.draft.points.push([x, y, z]);

        // Steering, from the first click on. The step number has to move with it or the guidance
        // reads "Step 1 of 2" for the whole of a road being drawn.
        tool.phase = 1;
        setToolStep();

        // The running count beats the static wording once there is a count to give: how many
        // points are down is the thing you cannot see from the map, because the via points look
        // exactly like the ends.
        if (state.draft.points.length > 1) {
            setHint(`${state.draft.points.length} point(s). Click more, or press Enter to route it.`);
        }

        requestRender();
        return true;
    }

    // A work site is three records collected in one flow: the destination, the zone around it,
    // and the arrival tiles inside that. Separately they are three tools and an author has to
    // remember to reach for all three; together they are the thing being made.
    if (tool.kind === 'site') {
        if (tool.phase === 0) {
            state.draft.points = [[x, y, z]];

            // The form comes HERE rather than at the end, alone among the tools, because the site
            // type decides which harvest definition the reach probe measures against - and the
            // probe runs while the arrivals are being placed, several steps before a tool would
            // normally ask anything. Asking after the centre click is the first moment there are
            // coordinates to auto-generate the id from.
            askForSite(x, y);
            return true;
        }

        if (tool.phase === 2) {
            takeArrival(x, y);
            return true;
        }

        return true;
    }

    // The screen pair matters here: in art view hitTest compares projected anchors, and without it
    // an edge, a route or an arrival could not collect the shape it was clicked on at all.
    const hit = tool.picks
        ? pick(view, state.shapes, state.visible, worldX, worldY, screenX, screenY)
        : null;

    if (tool.picks && (!hit || hit.layer !== tool.picks)) {
        setStatus(`Click a ${LAYERS[tool.picks].label.toLowerCase().replace(/s$/, '')}.`, 'error');
        return true;
    }

    if (tool.kind === 'owner-then-point') {
        if (tool.phase === 0) {
            tool.owner = hit;
            tool.phase = 1;
            setHint(tool.hint2);
            return true;
        }

        state.draft.points = [[x, y, z]];
        completeTool();
        return true;
    }

    // pair and chain both collect ids; only the finish condition differs.
    tool.ids.push(hit.props.id);
    state.draft.points.push([hit.points[0][0], hit.points[0][1], hit.points[0][2]]);

    if (tool.kind === 'pair' && tool.ids.length === 2) {
        completeTool();
        return true;
    }

    if (tool.kind === 'chain') {
        setHint(`${tool.hint} (${tool.ids.length} so far)`);
    }

    requestRender();

    return true;
}

/**
 * Yes or no, on the same modal every other prompt uses.
 *
 * A field-less askFor: submitting resolves to an empty object and cancelling resolves to null, so
 * the truthiness is the answer. Reusing the modal rather than adding a second one keeps Esc, focus
 * and the overlay behaving identically - a confirm that closed differently from every other dialog
 * would be its own small bug.
 */
async function confirmModal(title, detail) {
    const answer = await askFor({
        title,
        submit: 'Propose',
        fields: [{ key: 'note', label: detail, readonly: true }]
    }, { note: '' });

    return !!answer;
}

/**
 * Ask the shard to walk a road through every point given, and offer it as waypoints and edges.
 *
 * NOTHING IS SAVED HERE, and nothing is created until the author says so. Each consecutive pair is
 * routed as its own leg and the legs are joined, so a via point is a hard constraint the road must
 * pass through rather than a hint the search may ignore.
 *
 * A road the shard could not finish still draws. "It reaches the gate and stops" is a different
 * problem from "it never leaves town", and seeing where it stopped is most of the diagnosis.
 */
async function proposeCorridor(points, corridor = {}) {
    if (!(await confirmReplaceProposal())) {
        return;
    }

    const legs = [];
    let hops = [];
    let tiles = 0;
    let complete = true;
    let failure = null;

    for (let i = 1; i < points.length; i++) {
        const a = points[i - 1];
        const b = points[i];

        setStatus(`Walking leg ${i} of ${points.length - 1}...`, 'ok');

        let answer;

        try {
            const body = `${a[0]},${a[1]} ${b[0]},${b[1]} ${state.facet.name}`;
            const dropped = await api.request('nav-route', body);

            await api.awaitAck('nav-route', { nonce: dropped.nonce, timeoutMs: 120000 });
            answer = await api.route();
        } catch (error) {
            setStatus(`The shard could not walk leg ${i}: ${error.message}`, 'error');
            return;
        }

        legs.push({ tiles: (answer.points || []).length, ok: answer.ok, error: answer.error });
        tiles += (answer.points || []).length;

        if (!answer.ok) {
            complete = false;
            failure = answer.error;
        }

        // The joint is dropped rather than kept twice: the end of one leg and the start of the
        // next are the same tile, and two waypoints on it would be a zero-length hop.
        const legHops = answer.hops || [];

        hops = hops.length === 0 ? legHops.slice() : hops.concat(legHops.slice(1));
    }

    if (hops.length < 2) {
        showBanner(`No road: ${failure || 'the shard walked nothing'}`);
        return;
    }

    const summary = legs.length === 1
        ? `${tiles} tiles, ${hops.length} waypoints proposed`
        : `${legs.length} legs, ${tiles} tiles in total, ${hops.length} waypoints proposed`;

    const proceed = await confirmModal(
        'Propose this road?',
        `${summary}${complete ? ', every hop verified' : `. NOT complete: ${failure}`}`
        + '. Created unsaved - drag, edit or delete them, then Save to write them.');

    if (!proceed) {
        return;
    }

    createProposal(hops, corridor);
}

/**
 * An authored waypoint already standing on this tile, or within `slack` of it.
 *
 * Only the ends of a road are asked about. A road that starts where a waypoint already is has to
 * JOIN it, and the first version did not: it made a second waypoint at the identical coordinates,
 * which meant 43 waypoints and a mine hung off the graph as an island reachable by nothing. The
 * duplicate paths perfectly, audits clean, and goes nowhere.
 */
function waypointNear(x, y, slack) {
    return state.shapes.find((shape) =>
        shape.layer === 'nav'
        && shape.map === state.facet.name
        && shape.points
        && shape.points.length > 0
        && Math.max(Math.abs(shape.points[0][0] - x), Math.abs(shape.points[0][1] - y)) <= slack)
        || null;
}

/**
 * Turn a verified hop list into unsaved waypoint and edge records.
 *
 * THE IDS ARE THE CORRIDOR'S, not the enclosing zone's. `<name>-WP-0001` upward in walk order,
 * which is the one place this tool departs from js/ids.js - and it departs deliberately, because
 * a road is a thing rather than a scattering of points. Named that way it reads as one set in the
 * filter, in a navigation.json diff and in an audit finding, exactly as a work site's three
 * records already do. Letters of any case, digits, `-`, `_` and `.` are all legal in an id
 * (NavIds.IsValid), so the shard takes it as written.
 *
 * Waypoints JOINED at the ends keep their own ids. Renaming a record the author did not ask to
 * touch would be worse than a mixed-looking road, and the join is what stops the road becoming an
 * island in the first place.
 */
function createProposal(hops, corridor = {}) {
    const created = [];
    const reused = [];
    const tags = corridor.tags || 'road';
    let previous = null;
    let minted = 0;

    for (let i = 0; i < hops.length; i++) {
        const [x, y, z] = hops[i];
        const atEnd = i === 0 || i === hops.length - 1;
        const existing = atEnd ? waypointNear(x, y, 1) : null;

        let waypoint;

        if (existing) {
            // Joined, not duplicated. The existing point keeps its own id, name and position -
            // moving it to match the walked hop would edit a record the author did not ask to
            // touch, and one tile is inside the slack the walker already works to.
            waypoint = existing;
            reused.push(existing.props.id);
        } else {
            const id = corridor.name
                ? corridorId(corridor.name, ++minted, [...state.shapes, ...created])
                : nextId(x, y, state.facet.name, [...state.shapes, ...created]);

            waypoint = buildShape('waypoint', { id, tags, arrivalRange: '0' },
                state.facet.name, { points: [[x, y, z]] }, {});

            // buildShape flattens Z to 0 for a click; a walked road carries the real one, and the
            // authored Z is what makes a raised or sunken hop reachable at all.
            waypoint.points = [[x, y, z]];
            created.push(waypoint);
        }

        if (previous) {
            created.push(buildShape('edge', { tags }, state.facet.name,
                { points: [previous.points[0], waypoint.points[0]] },
                { ids: [previous.props.id, waypoint.props.id] }));
        }

        previous = waypoint;
    }

    for (const shape of created) {
        createShape(shape);
    }

    // Remembered so the tool can offer to replace it, and so editing knows which records are a
    // proposal rather than part of the authored graph.
    state.proposal = new Set(created.map((shape) => shape.id));

    syncDerived(state.shapes);

    // Said out loud, because joining is the difference between a road and an island and the
    // author has no other way to see which happened.
    setStatus(
        reused.length > 0
            ? `Proposed ${hops.length} waypoint(s), joining ${reused.join(' and ')}.`
                + ' Drag to adjust, then Save.'
            : `Proposed ${hops.length} waypoint(s). Drag to adjust, then Save.`,
        'ok');

    requestRender();
}

/**
 * `<corridor>-WP-0001`, skipping any number already taken.
 *
 * Zero-padded to four so the ids sort in walk order as text - which is how they appear in the
 * filter, in the file and in every message that lists them, and `WP-10` sorting between `WP-1`
 * and `WP-2` is the sort of thing that makes a road look shuffled.
 *
 * The collision skip counts across kinds, the way js/ids.js does, because Nav.TryRoute accepts an
 * id naming a waypoint OR a destination and a clash between the two is genuinely ambiguous. It
 * matters here for a second corridor authored under a name already used: the numbering continues
 * past the existing ones rather than colliding with them.
 */
function corridorId(name, ordinal, shapes) {
    const taken = new Set(shapes.map((shape) => (shape.props && shape.props.id) || '')
        .filter(Boolean)
        .map((id) => id.toLowerCase()));

    let n = ordinal;

    for (;;) {
        const id = `${name}-WP-${String(n).padStart(4, '0')}`;

        if (!taken.has(id.toLowerCase())) {
            return id;
        }

        n++;
    }
}

/**
 * Ask before throwing away a proposal that has not been saved.
 *
 * A road is several minutes of walking and adjusting, and the second corridor somebody draws is
 * usually the one meant to REPLACE the first - but not always, and losing the first silently is
 * the kind of thing that is only noticed after the save.
 */
async function confirmReplaceProposal() {
    const live = [...(state.proposal || [])].filter((id) => state.created.has(id));

    if (live.length === 0) {
        state.proposal = null;
    state.adoptFailures = [];
    state.adoptBlocked = null;
        return true;
    }

    const replace = await confirmModal(
        'Replace the current proposal?',
        `${live.length} unsaved record(s) from the last road will be discarded.`);

    if (!replace) {
        return false;
    }

    for (const id of live) {
        const shape = state.shapes.find((candidate) => candidate.id === id);

        if (shape) {
            removeShape(shape);
        }
    }

    state.proposal = null;
    state.adoptFailures = [];
    state.adoptBlocked = null;
    syncDerived(state.shapes);

    return true;
}

/** The Site tool's one modal, taken early. See the comment in toolClick. */
async function askForSite(x, y) {
    const tool = state.tool;
    const defaults = tool.autoId
        ? { id: nextId(x, y, state.facet.name, state.shapes) }
        : {};

    const values = await askFor(tool, defaults);

    // The tool may have been cancelled with Esc while the modal was open.
    if (!state.tool || state.tool !== tool) {
        return;
    }

    if (!values) {
        cancelTool();
        return;
    }

    tool.props = { ...defaults, ...values };
    tool.siteType = (values.type || 'mine').toLowerCase();
    tool.phase = 1;
    state.draft.kind = 'rect';

    setHint(tool.hint2);
    setToolStep();
    requestRender();
}

/**
 * The edges an adopt could not walk, in red.
 *
 * Not shapes, deliberately: a shape is something the editor could be asked to save, and these must
 * never be. They are a drawing of a decision - this road is broken, here - and they vanish with the
 * next adopt or a discard.
 */
function drawAdoptFailures(ctx, view) {
    ctx.save();
    ctx.strokeStyle = '#ff2d2d';
    ctx.lineWidth = 3;
    ctx.setLineDash([6, 4]);

    for (const failure of state.adoptFailures) {
        const [ax, ay] = view.toScreen(failure.fromX + 0.5, failure.fromY + 0.5);
        const [bx, by] = view.toScreen(failure.toX + 0.5, failure.toY + 0.5);

        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(bx, by);
        ctx.stroke();

        // A cross at each end, so a failure whose ends are off screen still reads as an endpoint
        // rather than a line running out of the view.
        for (const [px, py] of [[ax, ay], [bx, by]]) {
            ctx.beginPath();
            ctx.setLineDash([]);
            ctx.moveTo(px - 4, py - 4);
            ctx.lineTo(px + 4, py + 4);
            ctx.moveTo(px + 4, py - 4);
            ctx.lineTo(px - 4, py + 4);
            ctx.stroke();
            ctx.setLineDash([6, 4]);
        }
    }

    ctx.restore();
}

/** The failed edge under the cursor, for the hover readout. */
function adoptFailureAt(worldX, worldY) {
    const slack = 3 / Math.max(view.scale, 0.001);

    for (const failure of state.adoptFailures) {
        if (nearSegment(
            [failure.fromX, failure.fromY], [failure.toX, failure.toY], worldX, worldY, slack)) {
            return failure;
        }
    }

    return null;
}

/**
 * Adopt a region of the uo-offline reference.
 *
 * The shard does all of it: only the shard can walk an edge. This drops the token, polls the
 * proposal while it is built, and turns the answer into unsaved records - which is the same review
 * a hand-drawn corridor gets, and the reason nothing here can damage the nav data.
 *
 * The poll is what makes a long run bearable. Every edge is a flood-fill and a large region is
 * minutes of them; without `done`/`total` the editor is indistinguishable from a hang.
 */
async function startAdopt(rect) {
    if (!rect) {
        return;
    }

    const [x, y, width, height] = rect;

    setStatus(`Asking the shard to walk ${width}x${height} at ${x},${y}...`, 'ok');

    try {
        const dropped = await api.request('nav-adopt', `${x},${y},${width},${height}`);
        const ack = await api.awaitAck('nav-adopt', { nonce: dropped.nonce, timeoutMs: 20000 });

        if (!ack.ok) {
            setStatus(ack.message, 'error');
            return;
        }
    } catch (error) {
        setStatus(`Adopt failed: ${error.message}`, 'error');
        return;
    }

    await pollAdopt();
}

/** Follow a running adopt to its end, showing how far it has got. */
async function pollAdopt() {
    for (let i = 0; i < 900; i++) {
        let proposal;

        try {
            proposal = await api.adopt();
        } catch (error) {
            setStatus(`Adopt: ${error.message}`, 'error');
            return;
        }

        if (proposal.status === 'done') {
            showAdoptProposal(proposal);
            return;
        }

        setStatus(`Adopting: walked ${proposal.done} of ${proposal.total} edge(s)...`, 'ok');

        await new Promise((resolve) => setTimeout(resolve, 500));
    }

    setStatus('The adopt did not finish. Check the shard console.', 'error');
}

/**
 * Turn a finished proposal into unsaved records, and say what it refused.
 *
 * The rule for which records survive - the reachable component is written, the stranded and the
 * unreachable are dropped - lives in adopt.js, where it is tested. This is the wiring.
 */
function showAdoptProposal(proposal) {
    const created = adopt.survivors(proposal);

    for (const shape of created) {
        createShape(shape);
    }

    state.proposal = new Set(created.map((shape) => shape.id));
    state.adoptFailures = proposal.failures || [];

    // Save is BLOCKED only when NOTHING proposed reaches the graph we already have. A proposal that
    // reaches us in part is written in part: the unreachable records are dropped above, listed in
    // the banner, and stay in the reference layer for a later box. The shard decides `blocked`
    // from what its flood reached; the editor takes that flag and nothing else, because every
    // record in an island is individually valid and nothing here could tell.
    state.adoptBlocked = adopt.blocked(proposal) ? (proposal.islands || []) : null;

    syncDerived(state.shapes);
    updateCounts();
    requestRender();

    showBanner(adopt.summary(proposal, created.length).join('\n'), 'warn');
    setStatus(`Adopted ${created.length} record(s). Save to accept.`, 'ok');
}

/**
 * Ask the shard where a bot could stand inside the zone just drawn, and take the best few.
 *
 * The tool used to arrive at this step with nothing on the map and nothing to say, and the
 * author was left clicking at a cliff face to find out - one round trip per click - which tiles
 * were any good. The shard holds the map; it should answer first and be corrected, rather than
 * be asked the same question fifteen times.
 */
async function proposeArrivals() {
    const tool = state.tool;
    const type = tool.siteType || 'mine';

    // The overlay these circles belong to is off by default, and the tool that exists to produce
    // them never turned it on - so the shard answered, the editor drew, and the author saw an
    // empty map.
    showWorksiteOverlay();

    setStatus('Asking the shard where a bot could stand...', 'ok');

    const ack = await sweepZone(type, state.draft.rect);

    if (!state.tool || state.tool !== tool) {
        return;
    }

    tool.arrivals = worksites.bestCandidates();
    worksites.setTaken(tool.arrivals);

    const offered = worksites.candidates().length;

    if (offered === 0) {
        // The one case where an empty answer needs a reason. The shard says which floor it was
        // holding the zone to, because the fix is usually to move the zone rather than the floor.
        const why = (ack && ack.warnings && ack.warnings[0])
            || `nothing in this zone can be stood on with enough ${type} in reach`;

        setStatus(`No arrival tiles: ${why}`, 'error');
    } else if (ack && ack.warnings && ack.warnings.length > 0) {
        setStatus(ack.warnings[0], 'warn');
    } else {
        setStatus(`${offered} tile(s) offered, best ${tool.arrivals.length} taken.`, 'ok');
    }

    setToolStep();
    requestRender();
}

/**
 * A click in the arrival step: take a proposed tile, drop one already taken, or test a new one.
 *
 * Clicking the same tile twice used to append a second arrival at the same coordinates, and the
 * writer committed both - so the way to fix a misplaced arrival was to throw the whole site away
 * with Esc and start again.
 */
async function takeArrival(x, y) {
    const tool = state.tool;
    const key = worksites.tileKey(x, y);
    const already = tool.arrivals.findIndex(([ax, ay]) => worksites.tileKey(ax, ay) === key);

    if (already >= 0) {
        tool.arrivals.splice(already, 1);
        worksites.setTaken(tool.arrivals);
        setToolStep();
        requestRender();
        return;
    }

    const offered = worksites.candidateAt(x, y);

    if (offered) {
        // The shard's own Z for the tile, not the pick's. It measured canFit AT that Z, so it is
        // the one number here that has already been checked against a bot actually standing there.
        tool.arrivals.push([x, y, offered.z]);
        worksites.setTaken(tool.arrivals);
        setToolStep();
        requestRender();
        return;
    }

    // A tile the sweep did not offer. It is still measured rather than refused - the sweep hands
    // back the best two dozen, not every acceptable tile, and the author may want one it capped
    // out. But it has to pass the same test, and if it does not, say which test it failed:
    // silently ignoring the click reads as a broken tool.
    setStatus(`Measuring ${x},${y}...`, 'ok');

    await refreshReach(tool.siteType || 'mine', [...tool.arrivals, [x, y]]);

    if (!state.tool || state.tool !== tool) {
        return;
    }

    const measured = worksites.candidateAt(x, y);

    if (!measured) {
        setStatus(`${x},${y} was not measured; is the shard up?`, 'warn');
        return;
    }

    if (measured.canFit === false) {
        setStatus(`${x},${y} cannot be stood on.`, 'error');
    } else if (measured.reach < measured.min) {
        setStatus(`${x},${y} reaches ${measured.reach}, needs ${measured.min}.`, 'error');
    } else {
        tool.arrivals.push([x, y, measured.z]);
        setStatus(`${x},${y} taken, reach ${measured.reach}.`, 'ok');
    }

    worksites.setTaken(tool.arrivals);
    setToolStep();
    requestRender();
}

function finishTool() {
    const tool = state.tool;

    if (tool && tool.kind === 'point-chain') {
        if (tool !== state.tool || state.draft.points.length < 2) {
            setStatus('A road needs at least a start and an end.', 'error');
            return;
        }

        const points = state.draft.points.map(([x, y]) => [x, y]);
        const corridor = tool.props || {};

        cancelTool();
        proposeCorridor(points, corridor);
        return;
    }

    if (tool && tool.kind === 'site' && tool.phase === 2) {
        if (tool.arrivals.length === 0) {
            setStatus('A work site needs at least one arrival point.', 'error');
            return;
        }

        completeTool();
        return;
    }

    if (!tool || tool.kind !== 'chain') {
        return;
    }

    if (tool.ids.length < (tool.min || 2)) {
        setStatus(`A route needs at least ${tool.min || 2} waypoints.`, 'error');
        return;
    }

    completeTool();
}

async function completeTool() {
    const tool = state.tool;
    const draft = state.draft;

    if (!tool) {
        return;
    }

    const map = state.facet.name;
    const point = draft.points[0];
    const defaults = {};

    if (tool.autoId) {
        const at = draft.rect
            ? [draft.rect[0], draft.rect[1]]
            : [point[0], point[1]];

        defaults.id = nextId(at[0], at[1], map, state.shapes);
    }

    // A tool with nothing to ask never opens a modal. Two clicks of ceremony for no information is
    // how a fast tool becomes a slow one. The Site tool asked at the start - it needed the answer
    // to measure reach while the arrivals were going down - so it is not asked twice.
    const values = tool.kind === 'site'
        ? tool.props
        : (tool.fields.length > 0 ? await askFor(tool, defaults) : {});

    if (!values) {
        cancelTool();
        return;
    }

    const props = { ...defaults, ...values };
    const owner = state.tool.owner;
    const ownerId = owner ? owner.props.id : null;

    const shape = buildShape(tool.key, props, map, draft, {
        ownerId,
        ids: state.tool.ids,
        arrivals: state.tool.arrivals,
        arrivalIndex: state.shapes.filter((s) => s.id.startsWith(`arr:${ownerId}#`)).length,
        // A spawner without a <UniqueId> is ADDED with a fresh GUID on every import rather than
        // replaced, which is how 47 stock spawners came to exist twice in this world. Every
        // spawner this editor creates gets one at birth.
        uniqueId: crypto.randomUUID()
    });

    cancelTool();

    if (!shape) {
        return;
    }

    // A tool may build more than one record. The Site tool makes a destination, a zone and its
    // arrivals in one go, and they are created together or not at all: half a site is a
    // destination a bot can be sent to with nowhere to stand when it gets there.
    const built = Array.isArray(shape) ? shape : [shape];

    for (const one of built) {
        if (state.shapes.some((existing) => existing.id === one.id)) {
            setStatus(`${one.id} already exists.`, 'error');
            return;
        }
    }

    for (const one of built) {
        createShape(one);
    }

    if (built.length > 1) {
        setStatus(`Created ${built.length} records. Save to write them.`, 'ok');
    }
}

// --- the context menu -----------------------------------------------------------------------------

function showMenu(x, y, items) {
    dom.menu.innerHTML = '';

    for (const item of items) {
        const row = document.createElement('li');

        if (item.heading) {
            row.className = 'heading';
            row.textContent = item.heading;
        } else {
            row.textContent = item.label;
            row.addEventListener('click', () => {
                hideMenu();
                item.run();
            });
        }

        dom.menu.appendChild(row);
    }

    dom.menu.hidden = false;

    // Clamp inside the window rather than letting the menu run off the edge.
    const box = dom.menu.getBoundingClientRect();

    dom.menu.style.left = `${Math.min(x, window.innerWidth - box.width - 8)}px`;
    dom.menu.style.top = `${Math.min(y, window.innerHeight - box.height - 8)}px`;
}

function hideMenu() {
    dom.menu.hidden = true;
}

// --- input --------------------------------------------------------------------------------------

function worldAt(event) {
    const rect = canvas.getBoundingClientRect();

    return view.toWorld(event.clientX - rect.left, event.clientY - rect.top);
}

/**
 * The cursor in canvas pixels. hitTest wants this in art view, where the world position is a
 * ground-plane guess and the screen position is exact - see the note at the top of hitTest.
 */
function screenAt(event) {
    const rect = canvas.getBoundingClientRect();

    return [event.clientX - rect.left, event.clientY - rect.top];
}

/**
 * The tile under the cursor, as the editor is willing to state it: {x, y, z, exact}.
 *
 * `exact` is the whole of it. In art view the answer comes from the renderer's own pick map and
 * names the tile the eye is on, at the Z a mobile would stand there; while that tile is still in
 * flight - or where nothing was drawn at all - it falls back to the ground-plane inverse, which is
 * off by z*4/44 tiles, about 2.7 where Britain stands. The readout marks that with a ~ and nothing
 * is placed from it. In radar there is nothing to be inexact about, and Z is not a question the
 * cursor can answer at all.
 *
 * Asking also ASKS FOR the pick map, so hovering a tile is what fetches it - which means that by
 * the time a button goes down, the answer a click needs is almost always already in memory.
 */
function tileAt(event) {
    const [worldX, worldY] = worldAt(event);
    const estimate = { x: Math.floor(worldX), y: Math.floor(worldY), z: null, exact: !view.isArt };

    // The facet check is not defensive padding: `canvasAt` needs the facet's height to find the
    // pyramid's origin, and the renderer's handshake can land before /api/status does.
    if (!view.isArt || !view.facet) {
        // Radar has no surface under the cursor to read, but the land under a tile is still a
        // question with an answer, and it is the same one the pick map records. PEEKED, not asked:
        // this runs on every mousemove, and asking here would put a query per frame in front of the
        // art tiles on the renderer's one serial channel. A placement asks (see pickedTile), so the
        // readout fills in for the tiles that have actually been worth an answer.
        return { ...estimate, z: landz.peek(estimate.x, estimate.y) };
    }

    const [screenX, screenY] = screenAt(event);
    const [canvasX, canvasY] = view.canvasAt(screenX, screenY);

    state.cursor = [canvasX, canvasY];
    pickmap.request(view, canvasX, canvasY, refreshReadout);

    const found = pickmap.at(view, canvasX, canvasY);

    return found ? { x: found.x, y: found.y, z: found.z, what: found.what, exact: true } : estimate;
}

/**
 * The same answer, waiting for the pick map if it has to. What every gesture that WRITES uses.
 *
 * A click is rare and a pick map is about 13 ms, so waiting is imperceptible and is the only answer
 * that is never wrong. In art view a pick that cannot be had at all resolves to null, and the
 * caller refuses rather than placing from the estimate: a waypoint three tiles from where it was
 * clicked would look right and be wrong, which is the whole reason this exists.
 */
async function pickedTile(event) {
    if (!view.isArt || !view.facet) {
        const tile = tileAt(event);

        // THE ONE STEP OUTSIDE THIS SESSION'S SCOPE, and taken deliberately: every record the
        // editor has ever created was written `z: 0`, and the query that fixes the art view fixes
        // this too for ten lines. It resolves null rather than failing when there is no renderer -
        // a placement must never depend on MapExport having been built - and the readout says the Z
        // was not sampled rather than letting a silent 0 look like a measurement.
        return { ...tile, z: tile.z === null ? await landz.resolve(tile.x, tile.y) : tile.z };
    }

    const [screenX, screenY] = screenAt(event);
    const [canvasX, canvasY] = view.canvasAt(screenX, screenY);
    const found = await pickmap.resolve(view, canvasX, canvasY);

    return found ? { x: found.x, y: found.y, z: found.z, what: found.what, exact: true } : null;
}

/** The coordinate strip, from wherever the cursor was last seen. */
function refreshReadout() {
    if (!view.facet || !dom.coords || !state.cursor || !view.isArt) {
        return;
    }

    const found = pickmap.at(view, state.cursor[0], state.cursor[1]);

    if (found) {
        dom.coords.textContent = readoutFor({ ...found, exact: true });
    }
}

/**
 * The coordinate strip's text for a tile.
 *
 * A `~` means the number is the ground-plane guess - the pick map for this tile has not arrived -
 * and is off by about 2.7 tiles where Britain stands. `z?` means the land Z was not sampled, which
 * is what a radar placement writes as 0. Both used to be shown as if they were measurements.
 */
/**
 * A point dragged over ground takes that ground's Z with it.
 *
 * ART VIEW ONLY, and that is not a limitation to work around: radar has no Z to offer. A radar drag
 * therefore leaves the stored Z alone rather than guessing, which is right - guessing is what wrote
 * `z: 0` across half the file in the first place (js/build.js:22-28) - and the `z?` badge is what
 * makes the resulting staleness visible instead of silent.
 *
 * Only `point` shapes: a rect has no Z in its schema, and a polyline's is derived from the
 * waypoints it names.
 */
function carryTheHill(shape, index, tile) {
    if (!view.isArt || shape.kind !== 'point' || !tile || tile.z === null || tile.z === undefined) {
        return;
    }

    if (shape.points[index]) {
        shape.points[index][2] = tile.z;
    }
}

function readoutFor(tile) {
    const where = tile.exact ? `${tile.x}, ${tile.y}` : `~${tile.x}, ${tile.y}`;

    if (tile.z !== null && tile.z !== undefined) {
        return `${where}  z${tile.z}`;
    }

    return tile.exact && landz.problem() ? `${where}  z?` : where;
}

/**
 * Whether a gesture that needs to turn a screen point into a WORLD point can be answered.
 *
 * It used to be `!view.isArt` flat, because the isometric inverse is not a function: a screen pixel
 * names a world tile only once a Z is assumed, and a waypoint placed 2.7 tiles from where it was
 * clicked would look right and be wrong. The pick map is that Z, so the art view places and drags
 * like radar - and what is refused now is not a projection but a MISSING ANSWER: no renderer built,
 * or a render that failed. There is no zoom gate, because one 1:1 sidecar answers every art level.
 */
function canPlace() {
    return !view.isArt || Boolean(view.art);
}

function refusePlacing() {
    const reason = pickmap.problem();

    setStatus(
        reason
            ? `The renderer could not say what is under the cursor: ${reason}`
            : 'The art view needs the renderer to say what is under the cursor. Build MapExport, or '
                + 'switch to the radar view.',
        'error');
}

function wireInput() {
    canvas.addEventListener('mousedown', (event) => {
        hideMenu();

        // Middle button, or space held: always a pan, whatever is under the cursor.
        if (event.button === 1 || state.spaceDown) {
            event.preventDefault();
            beginPan(event);
            return;
        }

        if (event.button !== 0) {
            return;
        }

        state.buttonDown = true;

        const tile = tileAt(event);

        // The common case by a long way: the mousemove that put the cursor here already asked for
        // this tile's pick map, so the answer is in memory and the gesture starts synchronously.
        if (tile.exact) {
            press(event, tile, true);
            return;
        }

        // It is not, so it is WAITED FOR rather than answered from the ground-plane guess - about
        // 13 ms for a tile nobody has hovered before, against a placement 2.7 tiles from where it
        // was clicked. `live` says whether there is still a button held by the time it lands: a
        // click that was over before the answer arrived is still a click, but there is no gesture
        // left to start a drag or a pan with, and starting one would leave it stuck.
        pickedTile(event).then((picked) => {
            if (picked) {
                press(event, picked, state.buttonDown);
                return;
            }

            // No pick, and the two reasons are not alike. NOTHING WAS DRAWN under the cursor - open
            // sea, or off the edge of the facet - is not a failure and there is nothing to say
            // about it; the click deselects and pans like a click on any empty ground. A renderer
            // that could not answer is worth a sentence.
            if (pickmap.problem()) {
                refusePlacing();
            } else {
                select(null);
            }

            if (state.buttonDown) {
                beginPan(event);
            }
        });
    });

    /**
     * A left press, once the tile under it is known.
     *
     * Split out of the listener because the answer can arrive a frame late - see above - and the
     * two paths have to do the same thing rather than nearly the same thing.
     */
    function press(event, tile, live) {
        const [screenX, screenY] = screenAt(event);

        // Two different points, deliberately. hitTest wants the FRACTIONAL world position, because
        // its slack is sub-tile in radar - and in art it ignores this pair entirely and compares
        // screen pixels. The gesture wants the PICKED TILE, which is the answer that is right in
        // both projections and is what ends up in a record.
        const [worldX, worldY] = worldAt(event);
        const originX = view.isArt ? tile.x : worldX;
        const originY = view.isArt ? tile.y : worldY;

        if (state.tool) {
            if (!canPlace()) {
                refusePlacing();

                if (live) {
                    beginPan(event);
                }

                return;
            }

            if (state.tool.kind === 'rect'
                || state.tool.kind === 'adopt-rect'
                || (state.tool.kind === 'site' && state.tool.phase === 1)) {
                state.drag = { kind: 'draw-rect', startX: tile.x, startY: tile.y };
                state.draft.rect = [tile.x, tile.y, 1, 1];
                requestRender();
                return;
            }

            toolClick(tile, worldX, worldY, screenX, screenY);
            return;
        }

        const hit = hitTest(
            view, state.shapes, state.visible, state.selected, worldX, worldY, screenX, screenY);

        // A BOT BEATS A LINE OR AN AREA, AND LOSES TO A POINT OR A DRAG HANDLE.
        //
        // It was checked only when nothing else was hit, which sounded conservative and made bots
        // unclickable in the one place there are any: `brit-town` is a 324x279 zone covering the
        // whole of Britain, so every click inside it hits the zone's body first. Measured over 120
        // positions along real roads, 114 were shadowed - 63 by a zone rect, 50 by an edge - and six
        // were reachable.
        //
        // Entities are drawn last, over everything, so picking what is visually on top is the least
        // surprising rule. The exception is the things you can grab and drag: a waypoint under a
        // wandering bot has to stay draggable, or a bot makes the map read-only wherever it goes.
        //
        // Bots only, not every live entity: the inspector this opens is the Bots panel's, and an NPC
        // or a player has no row in it - selecting one would set a serial that resolves to nothing
        // and look exactly like the click having missed.
        const bot = !grabsOverEntity(hit) && state.visible.has('entities')
            ? entityAt(
                view,
                state.entities.filter((entity) => entity.kind === 'bot'),
                worldX, worldY, screenX, screenY)
            : null;

        if (bot) {
            selectBot(bot, false);
            return;
        }

        if (!hit) {
            select(null);

            if (live) {
                beginPan(event);
            }

            return;
        }

        if (hit.shape !== state.selected) {
            select(hit.shape);
        }

        if (!isWritable(hit.shape) || !live) {
            if (live) {
                beginPan(event);
            }

            return;
        }

        // Selected above, so the inspector opens either way; only the DRAG is refused.
        if (!canPlace()) {
            beginPan(event);
            return;
        }

        // A route is edited through its waypoint list and an edge through its ends, so their lines
        // are not draggable: the geometry is derived, and moving it would write nothing.
        if (hit.mode !== 'move' && isDerivedGeometry(hit.shape)) {
            setStatus(
                hit.shape.layer === 'nav-routes'
                    ? 'A route is edited by its waypoint list, not by dragging its line.'
                    : 'An edge follows its two waypoints; move those instead.',
                'error');
            beginPan(event);
            return;
        }

        state.drag = {
            kind: hit.mode === 'resize' ? 'resize' : hit.mode === 'node' ? 'node' : 'move',
            shape: hit.shape,
            index: hit.index,
            originX,
            originY,
            before: geometryOf(hit.shape),
            moved: false
        };

        canvas.classList.add('dragging');
    }

    window.addEventListener('mousemove', (event) => {
        // Bound to the window, not the canvas: a drag that leaves the canvas has to keep tracking,
        // and 5a's canvas-bound listener froze the pan the moment the cursor left.
        const [worldX, worldY] = worldAt(event);

        // The tile under the cursor, and in art view the Z of the surface there. A `~` says the
        // pick map for this tile has not arrived and the number is the ground-plane guess, which is
        // off by about 2.7 tiles where Britain stands - a distinction the readout used to hide.
        const tile = tileAt(event);

        if (view.facet && dom.coords) {
            dom.coords.textContent = readoutFor(tile);
        }

        // Why an adopted road broke, where it broke. The reason comes from the shard's own walker,
        // so it says what the pathfinder actually refused rather than a guess about it.
        if (state.adoptFailures.length > 0) {
            const failure = adoptFailureAt(worldX, worldY);

            if (failure) {
                setStatus(`${failure.from} -> ${failure.to}: ${failure.reason}`, 'error');
            }
        }

        const drag = state.drag;

        if (!drag) {
            updateHover(event, worldX, worldY);
            return;
        }

        if (drag.kind === 'pan') {
            view.panBy(event.clientX - drag.lastX, event.clientY - drag.lastY);
            drag.lastX = event.clientX;
            drag.lastY = event.clientY;
            requestRender();
            requestSpawners();
            return;
        }

        if (drag.kind === 'draw-rect') {
            // The picked tile, so a zone dragged across the art covers the tiles it was dragged
            // over rather than the ones the ground plane put under the cursor. In radar the pick
            // IS the floored world position, so this is the line it always was.
            const x = tile.x;
            const y = tile.y;

            state.draft.rect = [
                Math.min(drag.startX, x),
                Math.min(drag.startY, y),
                Math.max(1, Math.abs(x - drag.startX)),
                Math.max(1, Math.abs(y - drag.startY))
            ];

            requestRender();
            return;
        }

        // A drag follows the GROUND IT IS DRAGGED OVER in art view, and the fractional cursor in
        // radar. `tile` is the pick in art and the floored cursor in radar; `dragX/dragY` keeps the
        // radar path sub-tile, which is what makes a one-tile nudge land on the tile you aimed at
        // rather than one early. A tile whose pick has not arrived reports inexact and the drag
        // holds where it was for that frame rather than jumping to the ground-plane guess.
        const dragX = view.isArt ? tile.x : worldX;
        const dragY = view.isArt ? tile.y : worldY;

        if (view.isArt && !tile.exact) {
            return;
        }

        if (drag.kind === 'resize') {
            resizeRect(drag.shape, drag.index, dragX, dragY);
        } else if (drag.kind === 'node') {
            moveNode(drag.shape, drag.index, dragX, dragY);

            // A node dragged up a hill carries the hill too, and for two sessions it did not.
            //
            // The Z write lived only on the whole-shape branch below, and which branch a drag takes
            // is decided by hitTest: a SELECTED non-rect shape offers node handles first
            // (shapes.js:824-831), and a one-point waypoint is non-rect. So dragging a marker you
            // had not selected wrote the Z and dragging one you had - click, then drag, which is
            // the normal gesture - did not. The same action, two answers, depending on a state
            // nobody thinks about while placing a record.
            //
            // It cost real data: brit-home-baker and its two arrivals were moved 200 tiles and
            // kept the Z of where they had been, which the resample then had to correct.
            carryTheHill(drag.shape, drag.index, tile);
        } else {
            const dx = Math.round(dragX - drag.originX);
            const dy = Math.round(dragY - drag.originY);

            if (dx === 0 && dy === 0) {
                return;
            }

            moveShape(drag.shape, dx, dy);

            carryTheHill(drag.shape, 0, tile);

            // Re-derived on every frame of the drag, not just at the end: a hop that only caught
            // up on mouse-up would make the road look broken for the length of the gesture.
            if (drag.shape.layer === 'nav') {
                syncDerived(state.shapes);
            }
            drag.originX += dx;
            drag.originY += dy;
        }

        drag.moved = true;
        showProperties(drag.shape);
        requestRender();
    });

    window.addEventListener('mouseup', () => {
        const drag = state.drag;

        state.buttonDown = false;
        state.drag = null;
        canvas.classList.remove('dragging');

        if (drag && drag.kind === 'draw-rect') {
            // Adopt creates nothing here: the rect is a question, and startAdopt asks it.
            if (state.tool && state.tool.kind === 'adopt-rect') {
                const rect = state.draft.rect;

                cancelTool();
                startAdopt(rect);
                return;
            }

            if (state.tool && state.tool.kind === 'site') {
                state.tool.phase = 2;
                state.draft.kind = 'points';
                setHint(state.tool.hint3);
                setToolStep();
                requestRender();
                proposeArrivals();
                return;
            }

            completeTool();
            return;
        }

        if (!drag || drag.kind === 'pan' || !drag.moved) {
            return;
        }

        pushOp({
            op: 'geometry', shapeId: drag.shape.id,
            before: drag.before, after: geometryOf(drag.shape)
        });

        markDirty(drag.shape);

        // Shift means "and put it on the road". Checked at the END of the drag, not the start, so
        // the modifier can be decided after seeing where the point landed.
        if (drag.shape.layer === 'nav' && event.shiftKey) {
            snapToRoad(drag.shape);
        } else if (drag.shape.layer === 'nav') {
            verifyHops(hopsTouching(drag.shape.props.id));
        }

        // Moving a waypoint changes the coverage field and every edge length that touches it.
        if (drag.shape.layer === 'nav') {
            // The edges are pictures of ids, so they have to be redrawn from where the waypoint
            // is NOW. Without this the dot moves and its hops stay pointing at where it was.
            syncDerived(state.shapes);
            coverage.invalidate();

            if (state.coverageVisible) {
                computeCoverage();
            }
        }

        requestRender();
    });

    // Double-clicking a hop puts a point in it. The gesture is on the hop rather than in a menu
    // because it needs a POSITION - where in the hop the new point goes - and a menu would have
    // thrown that away by the time it was chosen.
    canvas.addEventListener('dblclick', (event) => {
        const [worldX, worldY] = worldAt(event);
        const [screenX, screenY] = screenAt(event);
        const hit = pick(view, state.shapes, state.visible, worldX, worldY, screenX, screenY);

        if (hit && hit.layer === 'nav-edges' && isWritable(hit)) {
            event.preventDefault();

            // Inserting needs a position ON the hop, which is a world point from the cursor - so
            // it is the pick in art view and the cursor in radar, awaited either way because a
            // double click is the rarest gesture there is.
            if (!canPlace()) {
                refusePlacing();
                return;
            }

            pickedTile(event).then((tile) => {
                if (tile) {
                    insertOnHop(hit, tile.x, tile.y);
                    return;
                }

                refusePlacing();
            });
        }
    });

    canvas.addEventListener('wheel', (event) => {
        event.preventDefault();
        view.zoomAt(event.offsetX, event.offsetY, event.deltaY < 0 ? 1.2 : 1 / 1.2);
        requestRender();
        requestSpawners();
    }, { passive: false });

    canvas.addEventListener('contextmenu', (event) => {
        event.preventDefault();

        const [worldX, worldY] = worldAt(event);
        const [screenX, screenY] = screenAt(event);
        const hit = pick(view, state.shapes, state.visible, worldX, worldY, screenX, screenY);
        const items = [];

        if (hit) {
            items.push({ heading: hit.label || hit.id });
            items.push({ label: 'Centre on this', run: () => { select(hit); centerOnShape(hit); } });

            if (isWritable(hit)) {
                // Relinking is offered for any waypoint with two hops, proposal or not: leaving a
                // road in two pieces is never what somebody deleting a middle point meant.
                if (hit.layer === 'nav' && hopsTouching(hit.props.id).length === 2) {
                    items.push({
                        label: 'Delete and relink',
                        run: () => deleteAndRelink(hit)
                    });
                }

                items.push({ label: 'Delete', run: () => { select(hit); deleteSelected(); } });
            }

            items.push({ heading: 'Add here' });
        }

        // "Add here" needs a tile, and in art view that means the pick. It is fetched before the
        // menu opens rather than when an entry is chosen, so an entry that cannot be honoured is
        // not offered - the alternative is a menu that looks the same and quietly does nothing.
        pickedTile(event).then((tile) => {
            if (tile) {
                for (const [key, tool] of Object.entries(TOOLS)) {
                    items.push({ label: tool.label, run: () => startTool(key, tile) });
                }
            } else if (hit) {
                // The heading is already in; leave it saying why nothing follows it.
                items[items.length - 1] = { heading: 'Add here - no pick map' };
            }

            showMenu(event.clientX, event.clientY, items);
        });
    });

    window.addEventListener('keydown', (event) => {
        if (event.target.tagName === 'INPUT' || event.target.tagName === 'SELECT') {
            if (event.key === 'Escape') {
                event.target.blur();
            }

            return;
        }

        const key = event.key.toLowerCase();

        if (key === ' ') {
            state.spaceDown = true;
            return;
        }

        if (event.ctrlKey && key === 's') {
            event.preventDefault();
            save();
            return;
        }

        if (event.ctrlKey && key === 'z') {
            event.preventDefault();

            if (event.shiftKey) {
                redo();
            } else {
                undo();
            }

            return;
        }

        if (event.ctrlKey && key === 'y') {
            event.preventDefault();
            redo();
            return;
        }

        if (key === 'escape') {
            hideMenu();

            if (state.tool) {
                cancelTool();
            } else {
                select(null);
            }

            return;
        }

        if (key === 'enter') {
            finishTool();
            return;
        }

        if (key === 'delete' || key === 'backspace') {
            event.preventDefault();
            deleteSelected();
            return;
        }

        if (key === 'f') {
            event.preventDefault();
            dom.filter.focus();
            return;
        }

        const nudge = {
            arrowleft: [-1, 0], arrowright: [1, 0], arrowup: [0, -1], arrowdown: [0, 1]
        }[key];

        if (nudge) {
            event.preventDefault();
            nudgeSelected(nudge[0], nudge[1], event.shiftKey ? 10 : 1);
        }
    });

    window.addEventListener('keyup', (event) => {
        if (event.key === ' ') {
            state.spaceDown = false;
        }
    });

    dom.filter.addEventListener('input', () => {
        state.filter = dom.filter.value.trim();
        applyFilter();
        requestRender();
    });

    // Its own box rather than the shape filter: a bot is not a shape, and searching one should
    // never dim the other.
    dom.botFilter.addEventListener('input', renderBots);

    $('go-britain').addEventListener('click', () => {
        view.goTo(BRITAIN.x, BRITAIN.y, 2);
        requestRender();
    });

    dom.toolbar.addEventListener('click', (event) => {
        const act = event.target.dataset?.act;

        if (!act) {
            return;
        }

        const midX = canvas.clientWidth / 2;
        const midY = canvas.clientHeight / 2;

        const actions = {
            'zoom-in': () => view.zoomAt(midX, midY, 1.4),
            'zoom-out': () => view.zoomAt(midX, midY, 1 / 1.4),
            'whole-map': () => view.fitAll(),
            goto: gotoCoordinate,
            'finish-tool': finishTool,
            delete: deleteSelected,
            save,
            discard
        };

        actions[act]?.();
        requestRender();
    });

    dom.bannerDismiss.addEventListener('click', hideBanner);
    dom.bannerDiscard.addEventListener('click', discard);
    dom.bannerReapply.addEventListener('click', reloadAndReapply);

    window.addEventListener('click', (event) => {
        if (!dom.menu.contains(event.target)) {
            hideMenu();
        }
    });

    wireAudit();
    wireResync();

    wireRequest('reload-nav', 'nav-reload', '', 'Navigation reloaded');
    wireRequest('reload-dailylife', 'dailylife-reload', '', 'Daily life reloaded');
    wireRequest('live-on', 'livemap-on', '2', 'Live map on');
    wireRequest('live-off', 'livemap-off', '', 'Live map off');
}

function isDerivedGeometry(shape) {
    return shape.layer === 'nav-edges' || shape.layer === 'nav-routes';
}

function beginPan(event) {
    state.drag = { kind: 'pan', lastX: event.clientX, lastY: event.clientY };
    canvas.classList.add('dragging');
}

function updateHover(event, worldX, worldY) {
    if (event.target !== canvas || state.tool) {
        return;
    }

    const [screenX, screenY] = screenAt(event);
    const hovered = pick(view, state.shapes, state.visible, worldX, worldY, screenX, screenY);

    if (hovered !== state.hovered) {
        state.hovered = hovered;
        requestRender();
    }
}

function nudgeSelected(dx, dy, step) {
    const shape = state.selected;

    // A daily-life record has no geometry to nudge. hitTest cannot return one, but the filter's
    // match list can select one, and an arrow key would then reach geometryOf with no points.
    if (!shape || !isWritable(shape) || isDerivedGeometry(shape) || !hasGeometry(shape)) {
        return;
    }

    const before = geometryOf(shape);

    moveShape(shape, dx * step, dy * step);

    pushOp({ op: 'geometry', shapeId: shape.id, before, after: geometryOf(shape) });
    markDirty(shape);
    showProperties(shape);
    requestRender();
}

async function gotoCoordinate() {
    const values = await askFor({
        title: 'Go to a coordinate',
        submit: 'Go',
        fields: [
            { key: 'x', label: 'X', required: true },
            { key: 'y', label: 'Y', required: true }
        ]
    });

    if (!values) {
        return;
    }

    view.goTo(Number(values.x), Number(values.y), Math.max(view.scale, 2));
    requestRender();
}

// --- validation preview ----------------------------------------------------------------------------

/**
 * Runs the shard's structural checks over what is on screen, before anything is sent.
 *
 * A preview, never a gate. This is a replica of rules that live in C#, and a false positive in it
 * must not be able to stop a write the shard would have accepted - so save() reports fatals and
 * refuses only because that refusal is cheap to undo, and the bridge writes whatever it is given.
 */
function validatePreview() {
    const files = {
        navigation: plainFromShapes('navigation', state.shapes),
        dailyLife: plainFromShapes('dailyLife', state.shapes),
        restrictedZones: plainFromShapes('restrictedZones', state.shapes)
    };

    const result = validate(files, { hopCap: HOP_CAP });

    if (result.fatal.length > 0) {
        setStatus(`${result.fatal.length} problem(s) would be refused: ${result.fatal[0].message}`,
            'error');
    }

    return result;
}

// --- saving ----------------------------------------------------------------------------------------

/** The edits for one file, in the shape POST /api/save expects. */
function editsFor(file) {
    const mine = (shape) => fileOf(shape) === file;

    return {
        baseHash: state.hashes[file],
        updates: [...state.dirty.values()].filter(mine),
        creates: [...state.created.values()].filter(mine),
        deletes: [...state.deleted.values()].filter(mine).map((shape) => shape.id)
    };
}

function filesWithEdits() {
    const files = new Set();

    for (const shape of [...state.dirty.values(), ...state.created.values(), ...state.deleted.values()]) {
        const file = fileOf(shape);

        if (file) {
            files.add(file);
        }
    }

    // Navigation first: a daily-life record naming a nav id that does not exist yet is only a
    // warning, but it makes the editor look wrong for the length of one refresh.
    const known = ['navigation', 'restrictedZones', 'dailyLife'].filter((file) => files.has(file));

    // AND THE SPAWN FILES, which this used to drop on the floor. The three names above are a
    // fixed list and a spawn key is `spawn:<facet>/GG_Thing.xml` - dynamic by construction - so
    // no spawner edit has ever survived this filter. Editing a GG spawner in the panel, or
    // creating one with the Spawner tool, reported "Saved and reloaded" and wrote nothing at all:
    // save() iterates what this returns, and an empty list is indistinguishable from a clean run.
    // The bridge's half worked the whole time and is tested; this was the half nobody called.
    const spawn = [...files].filter((file) => file.startsWith('spawn:')).sort();

    return [...known, ...spawn];
}

async function save() {
    if (!hasEdits()) {
        return;
    }

    // An adopt that would leave an island is refused outright, before validation, and there is no
    // override. Every record in it is individually valid - that is what makes an island the one
    // fault the editor cannot show you. It looks like a road right up until a bot stands on it
    // for ever, and Britain shipped exactly that once already.
    if (state.adoptBlocked) {
        showBanner(
            'This adopt cannot be saved.\n\n'
            + state.adoptBlocked.join('\n\n')
            + '\n\nDiscard it and adopt a region that overlaps ground you have already saved,'
            + ' so its edges have something to join onto.',
            'warn');
        setStatus('Save refused: the proposal would leave records nothing can reach.', 'error');
        return;
    }

    const preview = validatePreview();

    if (preview.fatal.length > 0) {
        showBanner(
            'The shard would refuse this, so it has not been sent:\n\n'
            + preview.fatal.slice(0, 10).map((p) => `• ${p.message}`).join('\n')
            + '\n\nFix these and save again.',
            'warn');

        const first = preview.fatal.find((p) => p.shapeId && shapeById(p.shapeId));

        if (first) {
            select(shapeById(first.shapeId));
        }

        return;
    }

    const files = filesWithEdits();

    // There are edits - hasEdits() said so at the top - so an empty file list means at least one
    // of them belongs to no file the editor can name, and the loop below would then say "Saved
    // and reloaded" having sent nothing. That is the failure this whole function just had, in two
    // independent forms at once, and it was invisible both times because the success message did
    // not depend on anything having been written.
    if (files.length === 0) {
        const orphans = [...state.dirty.values(), ...state.created.values(), ...state.deleted.values()]
            .filter((shape) => !fileOf(shape))
            .map((shape) => shape.id);

        showBanner(
            'Nothing was written: these edits do not belong to any file the editor can save.\n\n'
            + (orphans.length > 0 ? orphans.slice(0, 5).join('\n') : '(no shape could be named)')
            + '\n\nThis is a bug - please say what you were creating when it happened.');
        setStatus('Save wrote nothing: no file could be resolved for these edits.', 'error');
        return;
    }

    setStatus('Saving...');

    for (const file of files) {
        let result;

        try {
            result = await api.save(file, editsFor(file));
        } catch (error) {
            if (error.status === 409) {
                offerReapply(file);
                return;
            }

            // Nothing was written. Keep the edits so they can be corrected and saved again.
            showBanner(`Save failed, nothing was written: ${error.message}`);
            return;
        }

        state.written.add(file);
        state.hashes[file] = result.hash;

        if (!result.reloaded) {
            updateToolbar();

            showBanner(
                'Written to disk, but the shard refused to load it, so it is still running the '
                + `previous ${file} config:\n\n${result.message}\n`
                + (result.errors || []).map((line) => `\n• ${line}`).join('')
                + '\n\nYour edits are still here - fix them and save again, or discard them to go '
                + 'back to what the shard has.',
                'warn');

            return;
        }

        if ((result.warnings || []).length > 0) {
            showBanner(
                `Saved and reloaded, but the shard reports ${result.warnings.length} warning(s):\n\n`
                + result.warnings.slice(0, 10).map((line) => `• ${line}`).join('\n'),
                'warn');
        }
    }

    hideBannerIfClean();
    await refreshShapes({ force: true });
    setStatus('Saved and reloaded.', 'ok');
}

/**
 * The file changed underneath the editor - another tab, or [NavMark in game.
 *
 * Nothing was written, so the choice is real: take what is on disk and put these edits back on top
 * of it, or throw them away. Reapply is offered rather than merging silently, because the two sets
 * of edits can genuinely conflict and only the person looking at them can say which wins.
 */
function offerReapply(file) {
    showBanner(
        `${file} changed on disk since you loaded it - something else wrote to it, most likely `
        + '[NavMark, [NavRecord, or another editor tab.\n\n'
        + 'Nothing has been written. Reload and reapply takes what is on disk now and puts your '
        + 'edits back on top of it, reporting anything that no longer exists; discard throws your '
        + 'edits away and shows what the shard has.',
        'warn', true);

    dom.bannerReapply.hidden = false;

    setStatus('Save refused: the file changed on disk.', 'error');
}

/**
 * Reloads the file and replays this session's edits onto it.
 *
 * Replayed by id, which is exactly why identity is `<kind>:<id>` and not an array index: the
 * records have moved. Anything the other writer removed cannot be replayed and is named rather
 * than dropped quietly - losing an edit silently is the failure this whole banner exists to
 * prevent, and it would be perverse to reintroduce it here.
 */
async function reloadAndReapply() {
    const updates = [...state.dirty.values()];
    const creates = [...state.created.values()];
    const deletes = [...state.deleted.values()];

    dom.bannerReapply.hidden = true;

    if (!await refreshShapes({ force: true })) {
        return;
    }

    const lost = [];

    for (const shape of updates) {
        const fresh = shapeById(shape.id);

        if (!fresh) {
            lost.push(`${shape.id} (edited, but it is no longer in the file)`);
            continue;
        }

        applyGeometry(fresh, geometryOf(shape));
        fresh.props = { ...shape.props };
        fresh.label = shape.label;
        markDirty(fresh);
    }

    for (const shape of creates) {
        if (shapeById(shape.id)) {
            lost.push(`${shape.id} (created here, but that id now exists on disk)`);
            continue;
        }

        state.shapes.push(shape);
        state.created.set(shape.id, shape);
    }

    for (const shape of deletes) {
        if (!shapeById(shape.id)) {
            lost.push(`${shape.id} (deleted here, and already gone from the file)`);
            continue;
        }

        state.shapes = state.shapes.filter((s) => s.id !== shape.id);
        state.deleted.set(shape.id, shape);
    }

    fillLists(state.shapes);
    updateToolbar();
    validatePreview();
    requestRender();

    if (lost.length > 0) {
        showBanner(
            `Reapplied onto the file as it is now, except for ${lost.length} edit(s) that no `
            + `longer make sense:\n\n${lost.map((line) => `• ${line}`).join('\n')}`,
            'warn');
        return;
    }

    hideBanner();
    setStatus('Reloaded and reapplied. Save again to write it.', 'ok');
}

/**
 * Puts everything back the way the shard has it - including the file.
 *
 * Re-reading the shapes would not be enough. A save whose reload was rejected has already written
 * to disk, so the file and the running config disagree; dropping the local edits alone would leave
 * that bad file in place to fail on the next restart. Restoring the .bak is what makes discard
 * mean discard.
 */
async function discard() {
    hideBanner();

    for (const file of state.written) {
        try {
            await api.restore(file);
        } catch (error) {
            showBanner(`Could not restore ${file} to the shard's version: ${error.message}`);
            return;
        }
    }

    state.written.clear();
    state.dirty.clear();
    state.created.clear();
    state.deleted.clear();
    state.undo.length = 0;
    state.redo.length = 0;

    await refreshShapes({ force: true });
    setStatus('Edits discarded and the file restored.', 'ok');
}

// --- the banner --------------------------------------------------------------------------------

function showBanner(message, kind, offerReload) {
    if (!offerReload) {
        dom.bannerReapply.hidden = true;
    }

    dom.bannerText.textContent = message;
    dom.banner.className = kind === 'warn' ? 'warn' : '';
    dom.banner.hidden = false;
    dom.bannerDiscard.hidden = !hasEdits() && state.written.size === 0;

    stackGuideUnderBanner();
}

function hideBanner() {
    dom.banner.hidden = true;
    stackGuideUnderBanner();
}

/**
 * Keeps the tool guide clear of the banner.
 *
 * Both live on the shelf below the toolbar, and the banner's height is whatever its message
 * needs - an audit banner is twelve findings tall - so the offset is measured rather than
 * guessed at. A tool being driven while a banner is up is not rare: the banner is how a refused
 * save reports itself, and the first thing anybody does about it is reach for a tool.
 */
function stackGuideUnderBanner() {
    if (!dom.toolGuide) {
        return;
    }

    if (dom.banner.hidden) {
        dom.toolGuide.style.top = '';
        return;
    }

    dom.toolGuide.style.top = `${dom.banner.offsetTop + dom.banner.offsetHeight + 8}px`;
}

function hideBannerIfClean() {
    if (!hasEdits()) {
        hideBanner();
    }
}

// --- the shard buttons ---------------------------------------------------------------------------

/**
 * Runs [NavAudit and draws what it found.
 *
 * The audit pathfinds every walk edge with the engine's own MovementPath against real map data, so
 * it is the only thing here that knows whether a hop is actually walkable rather than merely short.
 * The ack carries the summary; the structured findings come from nav-audit.json, because the ack's
 * arrays are capped and a formatted line cannot be drawn as a layer.
 */
function wireAudit() {
    const button = $('run-audit');

    if (!button) {
        return;
    }

    button.addEventListener('click', async () => {
        button.disabled = true;
        setStatus('Auditing every walk edge against the map...', 'ok');

        try {
            const dropped = await api.request('nav-audit', '');
            const ack = await api.awaitAck('nav-audit', { nonce: dropped.nonce, timeoutMs: 60000 });

            state.audit = await api.audit();
            setAuditFlags(state.audit.problems);

            // Occupied findings are a pass. NavAudit.TryRun returns `blocked == 0`, so an audit
            // that found nothing but mobiles standing on edges has not failed, and must not paint
            // the status bar red - the shard's own contract says occupancy is a warning.
            const blocked = hasBlocked(state.audit.problems);

            setStatus(ack.message, blocked ? 'error' : 'ok');

            if (state.audit.problems.length > 0) {
                showBanner(
                    `${ack.message}\n\n`
                    + state.audit.problems.slice(0, 12).map(auditLine).join('\n')
                    + (blocked
                        ? '\n\nA waypoint at a closed door is a false positive.'
                            + ' Verify before editing.'
                        : ''),
                    'warn');
            } else {
                hideBanner();
            }

            updateCounts();
            requestRender();
        } catch (error) {
            setStatus(`Audit failed: ${error.message}`, 'error');
        } finally {
            button.disabled = false;
        }
    });
}

/**
 * Re-imports every GG spawn file.
 *
 * Behind a confirmation because it is the disruptive one: it deletes every GG_ spawner in the world
 * and re-imports the tree, and deleting an XmlSpawner deletes its spawned mobiles with it. A save
 * does not do this - it reloads one file - so this is for when the world and the files have drifted
 * apart, not for ordinary editing.
 */
function wireResync() {
    const button = $('resync-spawns');

    if (!button) {
        return;
    }

    button.addEventListener('click', async () => {
        const values = await askFor({
            title: 'Resync all spawns?',
            submit: 'Resync',
            fields: [{
                key: 'confirm',
                label: 'This deletes every GG_ spawner in the world and re-imports every file, so '
                    + 'the shopkeepers and Old Marta all vanish and come back. Type RESYNC.',
                required: true
            }]
        });

        if (!values || values.confirm.trim().toUpperCase() !== 'RESYNC') {
            setStatus('Resync cancelled.', 'ok');
            return;
        }

        button.disabled = true;
        setStatus('Resyncing every spawn file...', 'ok');

        try {
            const dropped = await api.request('gg-reimport', '');
            const ack = await api.awaitAck('gg-reimport', { nonce: dropped.nonce, timeoutMs: 30000 });

            setStatus(ack.ok ? ack.message : `Resync failed: ${ack.message}`, ack.ok ? 'ok' : 'error');

            if (!ack.ok) {
                showBanner(`The shard refused the resync and changed nothing:

${ack.message}`, 'warn');
            }

            await refreshSpawners({ force: true });
        } catch (error) {
            setStatus(`Resync failed: ${error.message}`, 'error');
        } finally {
            button.disabled = false;
        }
    });
}

/**
 * Wires a button to a request token.
 *
 * The shard polls once a second, so this waits for the ack rather than reporting success the
 * moment the file is written - "reloaded" appearing before anything reloaded would be a lie, and
 * a failing reload would be invisible.
 */
function wireRequest(buttonId, requestName, body, successText) {
    const button = $(buttonId);

    if (!button) {
        return;
    }

    button.addEventListener('click', async () => {
        button.disabled = true;
        setStatus(`Asking the shard: ${requestName}...`, 'ok');

        try {
            const dropped = await api.request(requestName, body);

            // The nonce is what tells this run's ack from the last one's; the shard overwrites the
            // ack file in place rather than deleting it.
            const ack = await api.awaitAck(requestName, { nonce: dropped.nonce });

            if (ack.ok) {
                setStatus(`${successText}: ${ack.message}`, 'ok');
                await refreshShapes();
            } else {
                setStatus(`${requestName} failed: ${ack.message}`, 'error');
            }
        } catch (error) {
            setStatus(`${requestName} failed: ${error.message}`, 'error');
        } finally {
            button.disabled = false;
        }
    });
}

// --- the readout ----------------------------------------------------------------------------------

let statusTimer = null;

function setStatus(message, kind) {
    if (!dom.status) {
        return;
    }

    dom.status.textContent = message;
    dom.status.className = kind || '';

    if (statusTimer) {
        clearTimeout(statusTimer);
    }

    statusTimer = setTimeout(() => {
        dom.status.textContent = '';
        dom.status.className = '';
    }, 8000);
}

/**
 * The active tool's instruction, in the panel above the map.
 *
 * The text was always written - `tool.hint`, `hint2`, `hint3` have said "Enter finishes" since
 * the tools were built. It went to a 12px span in the bottom-left corner of the canvas, and an
 * author looking at fifteen circles in the middle of the map never saw a word of it. Same
 * strings, somewhere they land.
 */
function setHint(message) {
    if (!dom.toolGuide) {
        return;
    }

    if (!message) {
        dom.toolGuide.hidden = true;
        dom.toolHint.textContent = '';
        dom.toolStep.textContent = '';
        dom.toolLegend.textContent = '';
        return;
    }

    dom.toolGuide.hidden = false;
    dom.toolHint.textContent = message;

    // A tool started while a banner is already up has to clear it too, not only the other way
    // round.
    stackGuideUnderBanner();
}

/**
 * Where the active tool is, and what the circles on the map mean.
 *
 * Split from setHint because the step number and the legend change on their own - taking an
 * arrival changes the count without changing the instruction.
 */
function setToolStep() {
    const tool = state.tool;

    if (!tool || !dom.toolGuide) {
        return;
    }

    // Every tool declares its steps in tools.js now, rather than the site tool being the only one
    // with a count hardcoded here. The wording tracks the README's own walkthroughs, so a tool
    // that changes changes in one place and the two cannot end up saying different things.
    const steps = Array.isArray(tool.steps) ? tool.steps : [];

    dom.toolGuide.hidden = false;
    dom.toolStep.textContent = steps.length > 1
        ? `Step ${Math.min(tool.phase, steps.length - 1) + 1} of ${steps.length}`
        : '';

    // The step's own wording, where the tool gave one. The site tool's third step is computed
    // below instead, because it counts tiles that are still being taken.
    const step = steps[Math.min(tool.phase, steps.length - 1)];

    if (step && !(tool.kind === 'site' && tool.phase === 2)) {
        dom.toolHint.textContent = `${step}. Esc cancels.`;
    }

    if (tool.kind === 'site' && tool.phase === 2) {
        const offered = worksites.candidates().length;

        dom.toolHint.textContent = `${tool.arrivals.length} of ${offered} tile(s) taken.`
            + ' Click a circle to take or drop it, or click bare ground to test a tile.'
            + ' Enter finishes, then Save.';
        dom.toolLegend.textContent = 'The number on a circle is how many harvestable tiles are'
            + ' in reach from it. Filled means taken.';
        return;
    }

    dom.toolLegend.textContent = '';
}

boot();
