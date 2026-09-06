// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// IdleBehavior.cs — stands there, and occasionally says something.
//
// The default brain and the universal fallback. Session 7d gave it the muttering
// upstream always had, which its own header promised: a Tick body and a single
// chat category.
//
// The rest of it is still deliberately empty. An idle bot is meant to read as a
// player who stepped away from the keyboard, or one standing about deciding what
// to do next - so it does not wander, does not turn, and does not gesture. It
// stands there. The only thing that makes it a person rather than a statue is
// that now and then it says something, and rarely: this is by some way the
// quietest brain in the layer.

using System;

namespace Server.Custom
{
    public class IdleBehavior : PlayerBotBehavior
    {
        public IdleBehavior()
        {
            ChatCategories = new[] { ChatLibrary.SmallTalk };

            // Half the Traveler's chance and a longer cooldown. Somebody standing still with
            // nothing to do is the last person in the room who should be talking the most.
            ChatChance = 0.05;
            MinChatCooldown = TimeSpan.FromSeconds(60.0);
            MaxChatCooldown = TimeSpan.FromSeconds(180.0);
        }

        public override string SerializableName
        {
            get { return "Idle"; }
        }

        public override string GetStatusLine(PlayerBot bot)
        {
            return "standing idle";
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            if (CheckVisitExpired(bot))
            {
                return;
            }

            TrySpeak(bot);
        }
    }
}
