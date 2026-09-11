// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotShove.cs — a bot walks through crowds.
//
// uo-offline PlayerBot.cs:504-512: "Bots phase through crowds. The engine's
// CheckShove requires FULL stamina to step onto an occupied tile - so every
// road-weary bot bounced off the permanent bank-plaza crowds forever (the
// Britain stuck cluster: 500+ pacing events per soak at the bank streets).
// Bots ignore bodies when THEY move; a real player shoving a bot still pays
// the normal rules." Their answer is `CheckShove => true`, and so is ours.
//
// THE SEAM. Theirs is a PlayerMobile, so the engine asks the MOVER's
// CheckShove and the line above is the whole change. Ours is a BaseCreature,
// and the engine never asks: BaseCreature.OnMoveOver (BaseCreature.cs:4546-
// 4554) and PlayerMobile.OnMoveOver (PlayerMobile.cs:3487-3494) both refuse an
// uncontrolled creature outright, before CheckShove is consulted. So the
// SHOVED side has to consent. Every mobile a bot may walk through - another
// bot, a daily-life actor - overrides OnMoveOver to route a bot mover here,
// and here it is the free pass.
//
// AND IT RUNS BOTH WAYS, which it did not until now. The rule above says a
// bot walks through a daily-life actor. The reverse was refused - not by any
// decision, but because BaseCreature.OnMoveOver turns away every uncontrolled
// creature and PlayerBot inherits that. So the town jammed in one direction
// only: a GG shopkeeper walking home at dusk stopped dead at a bot standing in
// its doorway, while the same bot would have walked straight through the
// shopkeeper. The pass going the other way is decided by
// NavWalkFailures.ConsentsToBotPass - a bot, the walk probe, or a daily-life
// actor - and is deliberately NARROWER than the one going this way, which is
// every occupant but a real player. Writing the two as one predicate is what
// made the instruments describe a rule the engine had stopped following.
//
// What stays the engine's: a real player shoving a bot reaches
// BaseCreature.OnMoveOver's base branch and pays the player rule - full
// stamina, minus ten (Mobile.cs:3516-3550). And a bot cannot push a real
// player: PlayerMobile.OnMoveOver is an upstream file and is left alone, so a
// bot yields to a player and routes around them.
//
// THE TWO DIRECTIONS ARE NOT THE SAME RULE, and writing them as one predicate
// made the instrument lie about the engine for as long as they were. The
// forward question - may a bot step onto this occupant's tile? - is answered
// by BaseCreature.OnMoveOver, which now routes EVERY stock creature through
// here: the mover is an IBotMover, CheckShove says yes, and the bot walks
// through a vendor, a guard and a dog exactly as it walks through another bot.
// The reverse question - may this mover step onto a BOT's tile? - is narrower
// on purpose and stops at a daily-life actor, because widening it would settle
// a deviation this shard is deliberately holding open (see the Bots README).
//
// MayBotPass answers the first, ConsentsToBotPass the second. NavWalker and the
// walk audit ask the first, because a wedged walker is a forward step that was
// refused, and for a year they asked the second - so a stock vendor on the next
// tile was booked as "WHICH A BOT MAY NOT PUSH" about a step the engine allows,
// and the count of those rows is the evidence for editing upstream files.
// REVIEW.md section 4.

using System;

using Server.Mobiles;

namespace Server.Custom
{
    public static class BotShove
    {
        /// <summary>
        /// The answer a mobile gives when a bot walks onto its tile, or null when the mover is
        /// not a bot and the caller should fall through to its own base rule.
        ///
        /// Mirrors Mobile.OnMoveOver (Mobile.cs:3506-3514): a mobile that is gone or off-map
        /// blocks nothing, and otherwise the mover's CheckShove decides.
        /// </summary>
        public static bool? OnMoveOver(Mobile shoved, Mobile mover)
        {
            // IBotMover, NOT PlayerBot, and the difference is the walk audit's honesty.
            //
            // This test was `mover as PlayerBot` and the probe that walks the whole graph on the
            // fleet's behalf is a plain BaseCreature, so it was refused by every occupant class a
            // real bot walks through - another bot, a GG shopkeeper, a daily-life patron, and a
            // stock NPC through the BaseCreature.OnMoveOver guard, which routes here too. The
            // instrument was strictly more obstructed than the thing it measures, in one direction,
            // and it booked each refusal as a rung: a FRAGILE row about the probe's own class.
            //
            // The interface has exactly two implementers and this is the only place it is tested.
            // A stock vendor in a doorway still jams a bot and a bot still jams a stock vendor;
            // that named deviation is untouched.
            var bot = mover as IBotMover;

            if (bot != null)
            {
                // Mirrors Mobile.OnMoveOver (Mobile.cs:3506-3514): a mobile that is gone or
                // off-map blocks nothing, and otherwise the mover's CheckShove decides.
                if (shoved == null || shoved.Deleted || shoved.Map == null)
                {
                    return true;
                }

                return bot.CheckShove(shoved);
            }

            // THE OTHER DIRECTION, which was missing and was never a decision anybody made.
            //
            // A PlayerBot walks through a daily-life actor, and the actor was refused by the
            // PlayerBot - not because anyone chose that, but because BaseCreature.OnMoveOver
            // refuses any uncontrolled creature and PlayerBot inherits it. So the town jammed in
            // one direction only: a GG shopkeeper walking home at dusk stopped dead at a bot
            // standing in its doorway, while the same bot would have walked straight through the
            // shopkeeper.
            //
            // THE PASS IS NARROWER THAN THE ONE GOING THE OTHER WAY, on purpose.
            // ConsentsToBotPass is the whole of it - a bot, the walk probe, or an
            // IDailyLifeActor - while a bot as the MOVER passes every occupant but a real player,
            // because BaseCreature.OnMoveOver routes all of them through the branch above. A stock
            // vendor still jams against a bot standing in a doorway; that is a named deviation
            // with its own row in the README, and widening it here would quietly settle a question
            // that row is holding open on purpose.
            //
            // Upstream needs none of this: their PlayerBot is a PlayerMobile, so a bot-on-bot
            // shove falls through to CheckShove => true on its own. Ours is a BaseCreature, which
            // is the whole reason this file exists.
            if (IsSymmetricMover(shoved, mover))
            {
                return true;
            }

            return null;
        }

        /// <summary>
        /// Whether a PlayerBot should let this mover onto its tile.
        ///
        /// Only asked when the SHOVED side is a bot.
        /// </summary>
        private static bool IsSymmetricMover(Mobile shoved, Mobile mover)
        {
            // A real player is never handled here. PlayerMobile.OnMoveOver is upstream and is left
            // alone, so a bot yields to a player and a player shoving a bot still pays the
            // engine's full-stamina rule - the half of the deviation that was always right.
            return shoved is PlayerBot && NavWalkFailures.ConsentsToBotPass(mover);
        }

    }
}
