// Talking to the bridge.
//
// This is the one file of the ModernUO editor that was rewritten rather than adapted. The
// original spoke to an admin API inside the shard, with a bearer token pasted from a config
// file; there is no such API here. The bridge is a separate local process reading files, it
// binds 127.0.0.1 only, and it refuses cross-site writes - so there is no token to paste, and
// nothing for a hostile page to reach.
//
// Everything below the shape vocabulary is unchanged, because the bridge projects our schema
// into exactly the {layer, id, kind, map, rect|points, props, fields} shape the rest of the
// editor already speaks. That translation lives in the bridge, not here, so shapes.js and
// view.js never learn what a space-separated tag list is.

async function request(method, path, body) {
    const response = await fetch(path, {
        method,
        headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
    });

    let payload = null;

    try {
        payload = await response.json();
    } catch {
        // A body that is not JSON is still a response; the status is what matters below.
    }

    if (!response.ok) {
        const error = new Error((payload && payload.error) || `HTTP ${response.status}`);
        error.status = response.status;
        throw error;
    }

    return payload;
}

export const api = {
    status: () => request('GET', '/api/status'),
    shapes: () => request('GET', '/api/shapes'),
    entities: () => request('GET', '/api/entities'),
    health: () => request('GET', '/api/health'),

    /**
     * Asks the shard to do something by dropping a request token.
     *
     * This returns as soon as the token is written, not when the shard has acted - the shard
     * polls once a second and the two processes are deliberately not coupled. Call ack() to find
     * out what happened.
     */
    request: (name, body = '') =>
        fetch(`/api/request/${name}`, { method: 'POST', body }).then((response) => {
            if (!response.ok) {
                return response.json().then((payload) => {
                    throw new Error(payload.error || `HTTP ${response.status}`);
                });
            }

            return response.json();
        }),

    ack: (name) => request('GET', `/api/ack/${name}`),

    /** Polls for a request's ack, giving up rather than hanging if the shard is down. */
    async awaitAck(name, timeoutMs = 8000) {
        const deadline = Date.now() + timeoutMs;

        for (;;) {
            const ack = await this.ack(name);

            if (ack && !ack.pending && ack.utc) {
                return ack;
            }

            if (Date.now() > deadline) {
                throw new Error('The shard did not answer. Is it running?');
            }

            await new Promise((resolve) => setTimeout(resolve, 400));
        }
    }
};
