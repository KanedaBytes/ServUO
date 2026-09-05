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
    hitTest, pick, geometryOf, applyGeometry, moveShape, resizeRect, moveNode
} from './shapes.js';
import * as coverage from './coverage.js';
import { HOP_CAP, validate, plainFromShapes } from './validate.js';
import { TOOLS, initTools, askFor, fillLists } from './tools.js';
import { nextId } from './ids.js';
import { buildShape } from './build.js';
import { liveStatusText } from './live.js';

const ENTITY_POLL_MS = 2000;
const HEALTH_POLL_MS = 15000;

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

    visible: new Set(LAYER_ORDER.filter((layer) => layer !== 'nav-edges')),
    coverageVisible: false,

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
        liveStatus: $('live-status')
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

        if (layer === 'entities') {
            element.textContent = String(state.entities.length);
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

    if (state.visible.has('entities')) {
        drawEntities(ctx, view, state.entities);
    }

    if (state.draft) {
        drawDraft(ctx, view, state.draft);
    }
}

// --- selection and properties ---------------------------------------------------------------------

/** Everything except the derived daily-life markers, whose coordinates live in navigation.json. */
function isWritable(shape) {
    return !shape.id.startsWith('marker:');
}

function fileOf(shape) {
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

    state.tool = { key, ...tool, phase: 0, owner: null, ids: [] };
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

function finishTool() {
    const tool = state.tool;

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
    // how a fast tool becomes a slow one.
    const values = tool.fields.length > 0 ? await askFor(tool, defaults) : {};

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
        arrivalIndex: state.shapes.filter((s) => s.id.startsWith(`arr:${ownerId}#`)).length
    });

    cancelTool();

    if (!shape) {
        return;
    }

    if (state.shapes.some((existing) => existing.id === shape.id)) {
        setStatus(`${shape.id} already exists.`, 'error');
        return;
    }

    createShape(shape);
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
            if (state.tool.kind === 'rect') {
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

        // Moving a waypoint changes the coverage field and every edge length that touches it.
        if (drag.shape.layer === 'nav') {
            coverage.invalidate();

            if (state.coverageVisible) {
                computeCoverage();
            }
        }

        requestRender();
    });

    canvas.addEventListener('wheel', (event) => {
        event.preventDefault();
        view.zoomAt(event.offsetX, event.offsetY, event.deltaY < 0 ? 1.2 : 1 / 1.2);
        requestRender();
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
