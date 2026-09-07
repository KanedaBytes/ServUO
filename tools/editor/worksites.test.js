/**
 * The work-site overlay's arithmetic.
 *
 * Nothing tested this module at all, and two of the three things it got wrong were visible on
 * screen: a tile nothing can stand on drawn green because canFit was never read, and fifteen
 * distinct tiles drawn from thirty-three duplicate rows.
 *
 * The fixture is real. It is Data/Live/site-reach.json as the shard wrote it while the west
 * cliff was being authored - 33 probe rows, 15 distinct tiles, 5 of them actually usable.
 */

const test = require('node:test');
const assert = require('node:assert');

let worksites;

test.before(async () => {
    worksites = await import('./js/worksites.js');
});

function row(x, y, z, reach, canFit, min = 5) {
    return { type: 'mine', x, y, z, reach, min, canFit };
}

/** The west cliff as the shard answered it, duplicates and all. */
const WEST_CLIFF = [
    row(1190, 1748, 11, 19, false),
    row(1192, 1750, 2, 15, true),
    row(1192, 1750, 2, 15, true),
    row(1192, 1750, 2, 15, true),
    row(1195, 1752, 0, 7, true),
    row(1192, 1750, 2, 15, true),
    row(1191, 1750, 13, 19, false),
    row(1193, 1750, 0, 10, true),
    row(1192, 1751, 11, 18, false),
    row(1192, 1752, 21, 21, false),
    row(1194, 1750, 0, 5, true),
    row(1192, 1756, 41, 25, false),
    row(1188, 1748, 32, 25, false),
    row(1192, 1744, 0, 5, true),
    row(1198, 1741, 0, 0, true)
];

test('a tile nothing can stand on is never green, however much ore surrounds it', () => {
    // This is the whole bug in one assertion. The tiles up on the cliff carry the HIGHEST reach
    // on the map because they are buried in rock, and the overlay recommended them.
    assert.strictEqual(worksites.reachColor(25, 5, false), '#ff7a6b');
    assert.strictEqual(worksites.reachColor(21, 5, false), '#ff7a6b');

    // Standable and over the floor is green; standable and under it is amber; nothing is red.
    assert.strictEqual(worksites.reachColor(15, 5, true), '#7bd88f');
    assert.strictEqual(worksites.reachColor(4, 5, true), '#ffd479');
    assert.strictEqual(worksites.reachColor(0, 5, true), '#ff7a6b');

    // Authored arrivals carry no canFit, so an absent one must not turn every arrival red.
    assert.strictEqual(worksites.reachColor(14, 5), '#7bd88f');
});

test('duplicate probe rows collapse to one circle per tile', () => {
    worksites.setReach({ probe: WEST_CLIFF, arrivals: [] });

    const candidates = worksites.candidates();
    const keys = new Set(candidates.map((c) => worksites.tileKey(c.x, c.y)));

    assert.strictEqual(keys.size, candidates.length, 'the same tile was offered twice');

    // Four rows in the fixture are 1192,1750. One circle.
    assert.strictEqual(
        candidates.filter((c) => c.x === 1192 && c.y === 1750).length, 1);
});

test('the best few are taken in the order the shard ranked them', () => {
    // The shard sorts by reach and hands back the best; the editor must not re-sort or reverse
    // it. Feeding an already-ranked list, the first four out are the first four in.
    const ranked = [
        row(1192, 1750, 2, 15, true),
        row(1193, 1750, 0, 10, true),
        row(1195, 1752, 0, 7, true),
        row(1194, 1750, 0, 5, true),
        row(1192, 1744, 0, 5, true)
    ];

    worksites.setReach({ probe: ranked, arrivals: [] });

    assert.deepStrictEqual(worksites.bestCandidates(), [
        [1192, 1750], [1193, 1750], [1195, 1752], [1194, 1750]
    ]);

    assert.strictEqual(worksites.bestCandidates(2).length, 2);

    // Fewer offered than asked for is not an error - a thin face is a real answer.
    worksites.setReach({ probe: ranked.slice(0, 1), arrivals: [] });
    assert.strictEqual(worksites.bestCandidates().length, 1);
});

test('a click finds the tile under it, and nothing when there is none', () => {
    worksites.setReach({ probe: WEST_CLIFF, arrivals: [] });

    // A click anywhere inside the tile counts as that tile.
    assert.strictEqual(worksites.candidateAt(1193.4, 1750.9).x, 1193);
    assert.strictEqual(worksites.candidateAt(1193, 1750).reach, 10);

    assert.strictEqual(worksites.candidateAt(1300, 1800), null);
});

test('an empty answer is empty, not a stale one', () => {
    // The overlay is refreshed wholesale on every answer. A zone with nothing standable in it
    // must clear what the last zone offered, or the author takes tiles from the wrong site.
    worksites.setReach({ probe: WEST_CLIFF, arrivals: [] });
    assert.ok(worksites.candidates().length > 0);

    worksites.setReach({ probe: [], arrivals: [] });

    assert.strictEqual(worksites.candidates().length, 0);
    assert.deepStrictEqual(worksites.bestCandidates(), []);
    assert.strictEqual(worksites.candidateAt(1192, 1750), null);
});

test('thin arrivals are counted against their own type floor', () => {
    worksites.setReach({
        probe: [],
        arrivals: [
            { destination: 'brit-mine-north', type: 'mine', index: 0, x: 1, y: 1, z: 0, reach: 14, min: 5 },
            { destination: 'brit-mine-north', type: 'mine', index: 1, x: 2, y: 2, z: 0, reach: 3, min: 5 },
            { destination: 'brit-lumber-south', type: 'lumber', index: 0, x: 3, y: 3, z: 0, reach: 4, min: 3 }
        ]
    });

    // The lumber arrival reaches 4 against a floor of 3 and is fine; only the mine one is thin.
    assert.strictEqual(worksites.thinCount(), 1);
    assert.strictEqual(worksites.siteCount(), 2);
});
