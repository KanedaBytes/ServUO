// Creating records: the click-by-click tools, and the form that finishes each one.
//
// A tool collects geometry on the canvas, then a modal asks for the fields the geometry cannot
// supply. Nothing is validated for real here - validate.js previews and the shard decides - but
// the pickers are populated from the shapes already loaded, so a valid choice is the easy one.
//
// WHAT A TOOL IS: a data record, not an object with methods. `kind` names one of six behaviours
// and the state machine for all six lives in app.js. Adding a tool is a table entry; adding a new
// KIND is a change in three places, which is the right amount of friction for the difference.
//
// The ModernUO original's `route` and `point-then-route` kinds are gone rather than ported. They
// collected free polylines, and there is nothing in this schema to write one to: an edge's line is
// its two endpoints and a route's is its waypoint list. Their replacements collect ids instead.

const dom = {};

let resolveForm = null;

/**
 * Binds the modal.
 *
 * Called from boot() rather than run at module scope, which is how the ModernUO original did it -
 * and why importing that file threw until the modal existed. A module that must not be imported
 * before the DOM is ready stays a trap even once the DOM is ready.
 */
export function initTools() {
    dom.modal = document.getElementById('modal');
    dom.form = document.getElementById('modal-form');
    dom.title = document.getElementById('modal-title');
    dom.fields = document.getElementById('modal-fields');
    dom.error = document.getElementById('modal-error');
    dom.cancel = document.getElementById('modal-cancel');
    dom.ok = document.getElementById('modal-ok');

    dom.form.addEventListener('submit', (event) => {
        event.preventDefault();

        const values = {};

        for (const input of dom.fields.querySelectorAll('[name]')) {
            values[input.name] = input.type === 'checkbox' ? input.checked : input.value.trim();
        }

        for (const input of dom.fields.querySelectorAll('[required]')) {
            if (!values[input.name]) {
                dom.error.textContent = `${input.dataset.label} is required.`;
                input.focus();
                return;
            }
        }

        finish(values);
    });

    dom.cancel.addEventListener('click', () => finish(null));

    // Esc inside the modal must close the modal, not fall through to the canvas and deselect.
    dom.modal.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            event.stopPropagation();
            finish(null);
        }
    });
}

export const TOOLS = {
    waypoint: {
        layer: 'nav',
        label: 'Waypoint',
        kind: 'point',
        autoId: true,
        hint: 'Click where the waypoint goes. Esc cancels.',
        title: 'New waypoint',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'tags', label: 'Tags (space separated)', value: 'town road' },
            { key: 'name', label: 'Display name (optional)' },
            { key: 'arrivalRange', label: 'Arrival range (0 = walker default)', value: '0' }
        ]
    },

    /**
     * Ask the shard to walk a road between two points, and propose waypoints along it.
     *
     * Two clicks and no form, because it CREATES NOTHING BY ITSELF. What comes back is a draft
     * the author accepts, edits or throws away - the shard is answering "can a bot walk here and
     * where would the waypoints go", which is a question the browser cannot answer at all: it has
     * no map data, no movement rules, and no way to know that a bot opens a door and a flood does
     * not.
     */
    corridor: {
        layer: 'nav',
        label: 'Corridor',
        kind: 'point-chain',
        hint: 'Click where the road starts. Esc cancels.',
        hint2: 'Click via points to steer it, then the end. Enter or Finish routes it, Esc cancels.',
        title: 'Corridor',
        fields: []
    },

    /**
     * A work site is three records, and authoring them separately is how one gets forgotten.
     *
     * A mine or a wood is a destination (where bots are sent), a zone (the ground they may work
     * and shuffle within) and a set of arrival tiles (where they stand). Miss the zone and
     * GathererBehavior has no work area and walks the bot away; miss the arrivals and it has
     * nowhere to stand. The shard is asked for the harvest reach under each arrival as it is
     * placed, because until something with the map in front of it answers, an arrival tile is a
     * guess - and a guess is what once put a mine on grass.
     */
    site: {
        layer: 'nav-destinations',
        label: 'Work site',
        kind: 'site',
        autoId: true,
        hint: 'Click where the centre of the site goes. Esc cancels.',
        hint2: 'Drag out the zone the bots may work inside. Esc cancels.',
        hint3: 'Asking the shard where a bot could stand...',
        title: 'New work site',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'name', label: 'Display name', required: true },
            { key: 'type', label: 'Type (mine or lumber)', required: true, value: 'mine' },
            { key: 'tags', label: 'Tags', value: 'wilderness' },
            { key: 'waypoints', label: 'Approach waypoints', list: 'waypoint-list' }
        ]
    },

    destination: {
        layer: 'nav-destinations',
        label: 'Destination',
        kind: 'point',
        autoId: true,
        hint: 'Click where the destination is. Esc cancels.',
        title: 'New destination',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'name', label: 'Display name', required: true },
            { key: 'type', label: 'Type', required: true, value: 'shop' },
            { key: 'tags', label: 'Tags' },
            { key: 'waypoints', label: 'Approach waypoints', list: 'waypoint-list' }
        ]
    },

    arrival: {
        layer: 'nav-arrivals',
        label: 'Arrival',
        // Two phases: whose arrival it is, then where. An arrival with no destination is not a
        // thing the schema can express, so the destination is collected first rather than typed.
        kind: 'owner-then-point',
        picks: 'nav-destinations',
        hint: 'Click the destination this arrival belongs to. Esc cancels.',
        hint2: 'Now click a tile someone could stand on. Esc cancels.',
        title: 'New arrival point',
        fields: [
            { key: 'exclusive', label: 'Exclusive (a guard post - nobody else stands here)', checkbox: true },
            { key: 'waypoints', label: 'Approach waypoints', list: 'waypoint-list' }
        ]
    },

    edge: {
        layer: 'nav-edges',
        label: 'Edge',
        // No form at all: an edge is entirely its two ends, and a modal asking nothing would be
        // two clicks of ceremony for no information.
        kind: 'pair',
        picks: 'nav',
        hint: 'Click the first waypoint. Esc cancels.',
        hint2: 'Now click the second. Esc cancels.',
        title: 'New edge',
        fields: []
    },

    route: {
        layer: 'nav-routes',
        label: 'Route',
        kind: 'chain',
        picks: 'nav',
        min: 2,
        hint: 'Click each waypoint in order. Enter finishes, Esc cancels.',
        title: 'New route',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'mode', label: 'Mode', options: ['cycle', 'oneway', 'pingpong'] }
        ]
    },

    navzone: {
        layer: 'nav-zones',
        label: 'Nav zone',
        kind: 'rect',
        autoId: true,
        hint: 'Drag a rectangle for the zone. Esc cancels.',
        title: 'New nav zone',
        fields: [
            { key: 'id', label: 'Id', required: true },
            { key: 'tags', label: 'Tags (the first one names auto-generated ids here)', value: 'town' }
        ]
    },

    /**
     * Adopt a region of the uo-offline reference.
     *
     * `adopt-rect` rather than `rect`: it creates no record of its own. The rectangle is a
     * QUESTION asked of the shard - which of somebody else's roads are in here, and can they be
     * walked - and the answer comes back as a proposal to accept or discard.
     */
    adopt: {
        layer: 'reference',
        label: 'Adopt region',
        kind: 'adopt-rect',
        hint: 'Drag a box over the reference roads to adopt. Esc cancels.',
        title: 'Adopt region',
        fields: []
    },

    restricted: {
        layer: 'restricted',
        label: 'Restricted zone',
        kind: 'rect',
        hint: 'Drag a rectangle for the restricted zone. Esc cancels.',
        title: 'New restricted zone',
        fields: [{ key: 'name', label: 'Name', required: true }]
    },

    spawner: {
        layer: 'spawners',
        label: 'Spawner',
        kind: 'point',
        autoId: true,
        hint: 'Click where the spawner goes. Esc cancels.',
        title: 'New GG spawner',
        fields: [
            // The GG_ prefix is not decoration: [XmlLoad and [XmlUnLoad filter on it with an
            // ordinal StartsWith, and it is what makes this shard's spawners addressable as a set.
            { key: 'Name', label: 'Name (must start with GG_)', required: true },
            { key: 'file', label: 'File', required: true, list: 'spawnfile-list' },
            { key: 'Objects2', label: 'Spawns (type name)', required: true, list: 'creature-list' },
            { key: 'MaxCount', label: 'Max at once', value: '1' },
            { key: 'Range', label: 'Home range', value: '0' },
            { key: 'MinDelay', label: 'Min delay (minutes)', value: '5' },
            { key: 'MaxDelay', label: 'Max delay (minutes)', value: '10' }
        ]
    }
};

function finish(values) {
    dom.modal.hidden = true;

    const resolve = resolveForm;

    resolveForm = null;
    resolve?.(values);
}

/**
 * Opens the modal and resolves with the field values, or null if cancelled.
 *
 * Takes anything with a title and a list of fields, so it serves both the create tools and one-off
 * prompts like "go to coordinate" without a second dialog implementation.
 */
export function askFor(tool, defaults) {
    dom.title.textContent = tool.title;
    dom.ok.textContent = tool.submit || 'Create';
    dom.error.textContent = '';
    dom.fields.innerHTML = '';

    for (const field of tool.fields) {
        const label = document.createElement('label');
        const caption = document.createElement('span');

        caption.textContent = field.label;

        let input;

        if (field.options) {
            input = document.createElement('select');

            for (const option of field.options) {
                const element = document.createElement('option');

                element.value = option;
                element.textContent = option;
                input.append(element);
            }
        } else {
            input = document.createElement('input');
            input.type = field.checkbox ? 'checkbox' : 'text';
            input.autocomplete = 'off';

            if (field.list) {
                input.setAttribute('list', field.list);
            }
        }

        input.name = field.key;
        input.dataset.label = field.label;

        const preset = defaults && field.key in defaults ? defaults[field.key] : field.value;

        if (preset !== undefined && preset !== null) {
            if (field.checkbox) {
                input.checked = preset === true || preset === 'true';
            } else {
                input.value = String(preset);
            }
        }

        if (field.required) {
            input.required = true;
        }

        label.append(caption, input);
        dom.fields.append(label);
    }

    dom.modal.hidden = false;
    dom.fields.querySelector('[name]')?.focus();

    return new Promise((resolve) => {
        resolveForm = resolve;
    });
}

export function showModalError(message) {
    dom.error.textContent = message;
}

/** Fills the id pickers from the shapes already loaded. */
export function fillLists(shapes) {
    const of = (prefix) => shapes
        .filter((shape) => shape.id.startsWith(prefix + ':'))
        .map((shape) => shape.id.slice(prefix.length + 1));

    const waypoints = of('wp');
    const destinations = of('dest');

    fill('waypoint-list', waypoints);
    fill('destination-list', destinations);
    fill('route-list', of('route'));

    // Anything a daily-life field may name: Nav.TryRoute takes either, and so does a shop or home.
    fill('navid-list', [...destinations, ...waypoints]);

    // The spawn files that exist, so a new spawner picks one rather than inventing a path. Free
    // text still works, which is how a brand-new file gets made.
    fill('spawnfile-list', [...new Set(
        shapes
            .filter((shape) => shape.layer === 'spawners')
            .map((shape) => shape.id.slice('spawner:'.length, shape.id.lastIndexOf('#')))
    )]);

    // Every type already spawning anywhere on the map, GG and stock alike. Read off `shape.entries`,
    // which the bridge parsed - the <Objects2> grammar has no escaping and a second copy of it in
    // the browser would be a second thing to get wrong. Not a validated list either: the shard
    // resolves type names and this cannot. It beats typing one from memory.
    fill('creature-list', [...new Set(
        shapes
            .flatMap((shape) => shape.entries || [])
            .map((entry) => entry.type)
            .filter((type) => type && !type.includes('/'))
    )].sort());
}

function fill(id, values) {
    const list = document.getElementById(id);

    if (!list) {
        return;
    }

    list.innerHTML = '';

    for (const value of values) {
        const option = document.createElement('option');

        option.value = value;
        list.append(option);
    }
}
