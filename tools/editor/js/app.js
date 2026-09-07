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
    READ_ONLY_LAYERS, SPAWNER_LAYERS, setAuditFlags, BEHAVIOR_COLORS, setHopFlags,
    hitTest, pick, geometryOf, applyGeometry, moveShape, resizeRect, moveNode, syncDerived
} from './shapes.js';
import * as coverage from './coverage.js';
import * as worksites from './worksites.js';
import { HOP_CAP, validate, plainFromShapes } from './validate.js';
import { TOOLS, initTools, askFor, fillLists } from './tools.js';
import { nextId } from './ids.js';
import { buildShape } from './build.js';
import { liveStatusText } from './live.js';

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
    draft: null
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
        coords: $('coords'), hint: $('hint'), status: $('status'),
        properties: $('properties'), matches: $('matches'), filter: $('filter'),
        menu: $('context-menu'), toolbar: $('toolbar'),
        banner: $('banner'), bannerText: $('banner-text'),
        bannerDiscard: $('banner-discard'), bannerDismiss: $('banner-dismiss'),
        bannerReapply: $('banner-reapply'),
        createButtons: $('create-buttons'), layers: $('layers'), health: $('health'),
        liveStatus: $('live-status'), bots: $('bots'), botFilter: $('bot-filter'),
        botDetail: $('bot-detail')
    });

    initTools();

    try {
        const status = await api.status();
        const facet = status.facets.find((f) => f.name === DEFAULT_FACET) || status.facets[0];

        state.facet = facet;
        view.setFacet(facet);
        view.goTo(BRITAIN.x, BRITAIN.y, 2);

        setStatus(
            `${status.waypoints} waypoints, ${status.destinations} destinations.`,
            status.navigationLoaded ? 'ok' : 'error');
    } catch (error) {
        setStatus(`Cannot reach the bridge: ${error.message}`, 'error');
        return;
    }

    buildLayerList();
    buildCreateButtons();

    await refreshShapes();

    pollEntities();
    pollHealth();

    // The age has to keep counting up between polls, and especially when the polls stop.
    setInterval(updateLiveStatus, 1000);

    wireInput();

    // Demand-driven from here: every mutation calls requestRender, and these are the three things
    // that change what is on screen without any mutation at all.
    view.onTileLoaded = requestRender;
    new ResizeObserver(requestRender).observe(canvas);
    watchPixelRatio();

    requestRender();
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
    if (!state.visible.has('spawners-stock')) {
        return;
    }

    if (spawnerTimer) {
        clearTimeout(spawnerTimer);
    }

    spawnerTimer = setTimeout(() => {
        spawnerTimer = null;
        refreshSpawners();
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
    for (const element of document.querySelectorAll('.count')) {
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

    try {
        const dropped = await api.request('site-reach', body);
        await api.awaitAck('site-reach', { nonce: dropped.nonce, timeoutMs: 15000 });

        worksites.setReach(await api.reach());
        updateCounts();
        requestRender();
    } catch (error) {
        // A reach answer is an aid, never a gate. The shard being down must not stop anybody
        // placing a point - it only means the dots are not there to help while they do it.
        setStatus(`Could not measure reach: ${error.message}`, 'warn');
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

        row.addEventListener('click', () => {
            state.selectedBot = bot.serial;

            // Centre rather than select: a bot is not a shape, so it cannot enter the normal
            // selection - and centring is what somebody clicking a name in a list wants anyway.
            view.centerOn(bot.x, bot.y);
            renderBots();
            requestRender();
        });

        if (bot.serial === state.selectedBot) {
            row.classList.add('selected');
        }

        dom.bots.append(row);
    }

    renderBotDetail(bots.find((bot) => bot.serial === state.selectedBot) || null);
}

/** The selected bot's detail and its recent events. */
function renderBotDetail(bot) {
    dom.botDetail.innerHTML = '';
    dom.botDetail.hidden = !bot;

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

    dom.botDetail.append(name, kind, status);

    if (bot.dest) {
        const dest = document.createElement('div');

        dest.className = 'muted';
        dest.textContent = `heading for ${bot.dest}`;
        dom.botDetail.append(dest);
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

    dom.botDetail.append(events);
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

    const id = nextId(x, y, state.facet.name, state.shapes);

    const waypoint = buildShape('waypoint', { id, tags: 'road', arrivalRange: '0' },
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

    view.resize();
    view.drawMap();

    const ctx = canvas.getContext('2d');

    // Under everything: it is a background condition, not a thing on the map.
    if (state.coverageVisible) {
        coverage.draw(ctx, view);
    }

    drawShapes(ctx, view, state.shapes, state.visible, state.selected, state.hovered, matchingShapes());

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

    if (shape.rect) {
        dom.properties.append(readonlyRow([
            ['x', shape.rect[0]], ['y', shape.rect[1]],
            ['w', shape.rect[2]], ['h', shape.rect[3]]
        ]));
    } else if (shape.points && shape.points.length === 1) {
        dom.properties.append(readonlyRow([
            ['x', shape.points[0][0]], ['y', shape.points[0][1]], ['z', shape.points[0][2]]
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

    const editable = isWritable(shape);

    shape.entries.forEach((entry, index) => {
        const row = document.createElement('div');
        row.className = 'row';

        const type = document.createElement('input');
        type.value = entry.type;
        type.readOnly = !editable;
        type.setAttribute('list', 'creature-list');

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
    const input = document.createElement('input');

    caption.textContent = field.label;
    input.value = shape.props[field.key] === undefined ? '' : String(shape.props[field.key]);
    input.autocomplete = 'off';

    if (field.type === 'navid') {
        input.setAttribute('list', 'navid-list');
    }

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

function startTool(key, placeAt = null) {
    const tool = TOOLS[key];

    if (!tool) {
        return;
    }

    state.tool = { key, ...tool, phase: 0, owner: null, ids: [], arrivals: [] };
    state.draft = { kind: tool.kind === 'rect' ? 'rect' : 'points', points: [], rect: null };
    state.selected = null;

    // An owner-then-point tool can skip its first phase when the thing it needs is already picked.
    if (tool.kind === 'owner-then-point' && state.hovered && state.hovered.layer === tool.picks) {
        state.tool.owner = state.hovered;
        state.tool.phase = 1;
    }

    setHint(state.tool.phase === 1 ? tool.hint2 : tool.hint);
    showProperties(null);
    updateToolbar();

    if (placeAt && tool.kind === 'point') {
        toolClick(placeAt[0], placeAt[1]);
    }

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
function toolClick(worldX, worldY) {
    const tool = state.tool;

    if (!tool) {
        return false;
    }

    const x = Math.floor(worldX);
    const y = Math.floor(worldY);

    if (tool.kind === 'point') {
        state.draft.points = [[x, y, 0]];
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
        state.draft.points.push([x, y, 0]);

        setHint(state.draft.points.length === 1
            ? tool.hint2
            : `${state.draft.points.length} point(s). Click more, or press Enter to route it.`);

        requestRender();
        return true;
    }

    // A work site is three records collected in one flow: the destination, the zone around it,
    // and the arrival tiles inside that. Separately they are three tools and an author has to
    // remember to reach for all three; together they are the thing being made.
    if (tool.kind === 'site') {
        if (tool.phase === 0) {
            state.draft.points = [[x, y, 0]];

            // The form comes HERE rather than at the end, alone among the tools, because the site
            // type decides which harvest definition the reach probe measures against - and the
            // probe runs while the arrivals are being placed, several steps before a tool would
            // normally ask anything. Asking after the centre click is the first moment there are
            // coordinates to auto-generate the id from.
            askForSite(x, y);
            return true;
        }

        if (tool.phase === 2) {
            tool.arrivals.push([x, y]);
            setHint(`${tool.arrivals.length} arrival(s). Click more, or press Enter to finish.`);

            // Ask the shard what is actually under each tile as it lands. This is the whole
            // reason the tool exists rather than three separate ones: an arrival is a guess until
            // something with the map in front of it says how much is in reach.
            refreshReach(state.tool.siteType || 'mine', tool.arrivals);

            requestRender();
            return true;
        }

        return true;
    }

    const hit = tool.picks ? pick(view, state.shapes, state.visible, worldX, worldY) : null;

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

        state.draft.points = [[x, y, 0]];
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
async function proposeCorridor(points) {
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

    createProposal(hops);
}

/** Turn a verified hop list into unsaved waypoint and edge records. */
function createProposal(hops) {
    const created = [];
    let previous = null;

    for (const [x, y, z] of hops) {
        const id = nextId(x, y, state.facet.name, [...state.shapes, ...created]);

        const waypoint = buildShape('waypoint', { id, tags: 'road', arrivalRange: '0' },
            state.facet.name, { points: [[x, y, z]] }, {});

        // buildShape flattens Z to 0 for a click; a walked road carries the real one, and the
        // authored Z is what makes a raised or sunken hop reachable at all.
        waypoint.points = [[x, y, z]];
        created.push(waypoint);

        if (previous) {
            created.push(buildShape('edge', {}, state.facet.name,
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
    setStatus(`Proposed ${hops.length} waypoint(s). Drag to adjust, then Save.`, 'ok');
    requestRender();
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

        cancelTool();
        proposeCorridor(points);
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

        const [worldX, worldY] = worldAt(event);

        if (state.tool) {
            if (state.tool.kind === 'rect'
                || (state.tool.kind === 'site' && state.tool.phase === 1)) {
                const x = Math.floor(worldX);
                const y = Math.floor(worldY);

                state.drag = { kind: 'draw-rect', startX: x, startY: y };
                state.draft.rect = [x, y, 1, 1];
                requestRender();
                return;
            }

            toolClick(worldX, worldY);
            return;
        }

        const hit = hitTest(view, state.shapes, state.visible, state.selected, worldX, worldY);

        if (!hit) {
            select(null);
            beginPan(event);
            return;
        }

        if (hit.shape !== state.selected) {
            select(hit.shape);
        }

        if (!isWritable(hit.shape)) {
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
            originX: worldX,
            originY: worldY,
            before: geometryOf(hit.shape),
            moved: false
        };

        canvas.classList.add('dragging');
    });

    window.addEventListener('mousemove', (event) => {
        // Bound to the window, not the canvas: a drag that leaves the canvas has to keep tracking,
        // and 5a's canvas-bound listener froze the pan the moment the cursor left.
        const [worldX, worldY] = worldAt(event);

        if (view.facet && dom.coords) {
            dom.coords.textContent = `${Math.floor(worldX)}, ${Math.floor(worldY)}`;
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
            const x = Math.floor(worldX);
            const y = Math.floor(worldY);

            state.draft.rect = [
                Math.min(drag.startX, x),
                Math.min(drag.startY, y),
                Math.max(1, Math.abs(x - drag.startX)),
                Math.max(1, Math.abs(y - drag.startY))
            ];

            requestRender();
            return;
        }

        if (drag.kind === 'resize') {
            resizeRect(drag.shape, drag.index, worldX, worldY);
        } else if (drag.kind === 'node') {
            moveNode(drag.shape, drag.index, worldX, worldY);
        } else {
            const dx = Math.round(worldX - drag.originX);
            const dy = Math.round(worldY - drag.originY);

            if (dx === 0 && dy === 0) {
                return;
            }

            moveShape(drag.shape, dx, dy);

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

        state.drag = null;
        canvas.classList.remove('dragging');

        if (drag && drag.kind === 'draw-rect') {
            if (state.tool && state.tool.kind === 'site') {
                state.tool.phase = 2;
                state.draft.kind = 'points';
                setHint(state.tool.hint3);
                requestRender();
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
        const hit = pick(view, state.shapes, state.visible, worldX, worldY);

        if (hit && hit.layer === 'nav-edges' && isWritable(hit)) {
            event.preventDefault();
            insertOnHop(hit, worldX, worldY);
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
        const hit = pick(view, state.shapes, state.visible, worldX, worldY);
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

        for (const [key, tool] of Object.entries(TOOLS)) {
            items.push({
                label: tool.label,
                run: () => startTool(key, [Math.floor(worldX), Math.floor(worldY)])
            });
        }

        showMenu(event.clientX, event.clientY, items);
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

    const hovered = pick(view, state.shapes, state.visible, worldX, worldY);

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
    return ['navigation', 'restrictedZones', 'dailyLife'].filter((file) => files.has(file));
}

async function save() {
    if (!hasEdits()) {
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

    setStatus('Saving...');

    for (const file of filesWithEdits()) {
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
}

function hideBanner() {
    dom.banner.hidden = true;
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

            setStatus(ack.message, state.audit.problems.length > 0 ? 'error' : 'ok');

            if (state.audit.problems.length > 0) {
                showBanner(
                    `${ack.message}\n\n`
                    + state.audit.problems.slice(0, 12)
                        .map((p) => `• ${p.blocked ? 'BLOCKED' : 'over cap'} ${p.from} -> ${p.to}`
                            + ` (${p.distance} tiles)`)
                        .join('\n')
                    + '\n\nA waypoint at a closed door is a false positive. Verify before editing.',
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

function setHint(message) {
    if (dom.hint) {
        dom.hint.textContent = message || '';
    }
}

boot();
