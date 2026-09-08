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
// What stays the engine's: a real player shoving a bot reaches
// BaseCreature.OnMoveOver's base branch and pays the player rule - full
// stamina, minus ten (Mobile.cs:3516-3550). And a bot cannot push a real
// player or a stock NPC: those two OnMoveOver overrides are upstream files
// and are left alone, so a bot yields to a vendor, a guard and a player, and
// routes around them. Upstream's bots push both. Named deviation; when a
// walker is wedged behind one, NavWalker names it in the rung log so the
// case can be revisited with evidence rather than by feel.

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

            if (bot == null)
            {
                return null;
            }

            if (shoved == null || shoved.Deleted || shoved.Map == null)
            {
                return true;
            }

            return bot.CheckShove(shoved);
        }
    }
}
