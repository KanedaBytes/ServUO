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
// shopkeeper. The pass going the other way is now exactly as wide as this one
// and no wider, and both are decided by the same predicate
// (NavWalkFailures.Shovable), so the rule and the measurement cannot disagree
// about which bucket a mobile is in.
//
// What stays the engine's: a real player shoving a bot reaches
// BaseCreature.OnMoveOver's base branch and pays the player rule - full
// stamina, minus ten (Mobile.cs:3516-3550). And a bot cannot push a real
// player or a stock NPC, nor they it: those two OnMoveOver overrides are
// upstream files and are left alone, so a bot yields to a vendor, a guard and
// a player, and routes around them. Upstream's bots push both. Named
// deviation; when a walker is wedged behind one, NavWalker names it in the
// rung log so the case can be revisited with evidence rather than by feel.

using System;

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
            var bot = mover as PlayerBot;

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
            // THE PASS IS EXACTLY AS WIDE AS THE ONE GOING THE OTHER WAY, and no wider. Shovable
            // is the single line the shove question reduces to - PlayerBot or IDailyLifeActor -
            // and using it here rather than a second list means the rule and the measurement can
            // never disagree about which bucket a mobile is in. A stock vendor in a doorway still
            // jams a bot, and a bot still jams a stock vendor; that is a named deviation with its
            // own row, and widening it here would quietly settle a question that row is holding
            // open on purpose.
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
        /// Only asked when the SHOVED side is a bot, and answered with the same predicate that
        /// decides whether a bot may push the mover - so the two directions are one rule.
        /// </summary>
        private static bool IsSymmetricMover(Mobile shoved, Mobile mover)
        {
            // A real player is never handled here. PlayerMobile.OnMoveOver is upstream and is left
            // alone, so a bot yields to a player and a player shoving a bot still pays the
            // engine's full-stamina rule - the half of the deviation that was always right.
            return shoved is PlayerBot
                && mover != null
                && !mover.Deleted
                && NavWalkFailures.Shovable(mover);
        }
    }
}
