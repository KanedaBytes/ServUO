// liveview.js — the pure parts of the two panels that redraw on a timer.
//
// WHY THESE TWO THINGS SHARE A FILE
//
// The Bots card and the Admin console feed had the same fault from opposite ends: a timer redraw
// destroying what the reader was in the middle of doing. The card rebuilt its whole body every two
// seconds, which collapsed an expanded `[BotInfo` block about a second after it was opened; the
// console feed forced itself to the bottom on every new line, so scrolling up to read was undone
// by the next poll.
//
// Both fixes need one decision made correctly - "has anything actually changed?" and "was the
// reader at the bottom?" - and both decisions were previously inline in a handler where nothing
// could test them. That is the fault js/problems.js records in its own header: the line it replaced
// lived inside a click handler, was wrong for a year, and nothing could have caught it. So they
// live here, as pure functions over plain values, and `liveview.test.js` tests them directly.

/**
 * Was the reader already at the bottom of a scrolling box?
 *
 * `slack` exists because none of these three numbers is reliably an integer: a fractional device
 * pixel ratio, a zoom level, or a sub-pixel line height all leave `scrollTop + clientHeight` a
 * fraction short of `scrollHeight` when the box is *visibly* scrolled to the end. Comparing for
 * equality therefore reads "the user has scrolled up" about somebody who has not moved at all,
 * which is the exact bug this is here to avoid, in the other direction.
 *
 * A box with nothing to scroll is at the bottom by definition, which is what a fresh feed is.
 */
export function atBottom(scrollTop, scrollHeight, clientHeight, slack = 4) {
    if (!Number.isFinite(scrollTop) || !Number.isFinite(scrollHeight)
        || !Number.isFinite(clientHeight)) {
        return true;
    }

    if (scrollHeight <= clientHeight) {
        return true;
    }

    return scrollHeight - (scrollTop + clientHeight) <= slack;
}

/** `atBottom` against a live element, for the caller that has one. */
export function elementAtBottom(element, slack = 4) {
    return !element
        || atBottom(element.scrollTop, element.scrollHeight, element.clientHeight, slack);
}

/**
 * Everything the bot detail block DRAWS, as one string, so a redraw can be skipped when none of it
 * moved.
 *
 * THE HAZARD IS OMISSION, not the comparison: a field that is rendered but not in here is a card
 * that silently stops updating, which is worse than the flicker this removes. `liveview.test.js`
 * asserts the list against the fields `fillBotDetail` actually reads.
 *
 * `visitLeft` is DELIBERATELY ABSENT. It counts down every two seconds, so including it would make
 * the signature differ on every poll for exactly the bots somebody is most likely to be reading
 * about - a sitter or a shopper - and the guard would never fire for them. The caller updates that
 * one number in place instead.
 */
export function botDetailSignature(bot, logEntry) {
    if (!bot) {
        return '';
    }

    const events = logEntry && logEntry.events ? logEntry.events : [];

    return [
        bot.serial,
        bot.name,
        bot.class,
        bot.tier,
        bot.behavior,
        bot.role,
        bot.status,
        bot.stepMs,
        bot.mounted,
        bot.dest,
        bot.stuck,
        events.length,
        events.length > 0 ? events[events.length - 1].utc : ''
    ].join('');
}

/** The fields `botDetailSignature` covers, so a test can compare it with what the panel renders. */
export const BOT_DETAIL_FIELDS = [
    'serial', 'name', 'class', 'tier', 'behavior', 'role',
    'status', 'stepMs', 'mounted', 'dest', 'stuck'
];
