// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotStartupPurge.cs - nothing about a bot survives a restart, and this is where that is enforced.
//
// A PORT OF BotStartupManager.PurgeStaleBots (uo-offline BotStartupManager.cs:92-119), adopted in
// the PlayerMobile class swap in place of the Timer.DelayCall(Delete) that used to close
// PlayerBot.Deserialize.
//
// AND UPSTREAM PAYS FOR THE GUARANTEE TOO, which this folder's README used to say they did not.
// Its Ephemerality section had it that ModernUO writes player characters through their account, so
// an accountless bot "was never in the world save at all - upstream got the guarantee for free".
// The first half is true and the conclusion is not: their own file carries this sweep, with the
// comment "any that appear in a fresh load are stale remnants and must be cleared so they don't
// accumulate". Belt and braces on their engine; the only mechanism on ours, because ServUO's
// StandardSaveStrategy.SaveMobiles writes every mobile in World.Mobiles with no account filter.
//
// WHY THIS RATHER THAN THE DESERIALIZE TAIL, which worked - measured at 2,871 bot-owned items in a
// save and not one of them orphaned afterwards. Two reasons, neither of them correctness:
//
//   IT REPORTS A COUNT. The ephemerality rule becomes a line in the boot log you can read, instead
//   of something inferred after the fact from an orphan census that had to join on item serials
//   because the parent was already gone.
//
//   IT IS NOT A SIDE EFFECT OF SERIALIZATION. base.Deserialize is now roughly 440 lines of
//   PlayerMobile housekeeping - an account lookup at PlayerMobile.cs:4902, CheckAtrophies at :4925,
//   a Timer.DelayCall at :4934 - and all of it ran, on this mobile, on the way to a line whose only
//   job was to schedule its destruction.
//
// THE ORDERING THAT MAKES IT SAFE is the same fact the old idiom rested on, read from the other
// end. The Timer thread does not start until after World.Load AND after Initialize (Server/Main.cs,
// CLAUDE.md section 3). Initialize is therefore strictly EARLIER than the first timer slice: a
// loaded bot never gets a tick, and the delete still happens after every item in the save has been
// attached to its parent, so Mobile.Delete cascades through the backpack and bank box exactly as it
// did before (Mobile.cs:3747 -> OnParentDeleted -> Delete, recursively).
//
// A WORLD.MOBILES WALK, AND NOT A VIOLATION OF CLAUDE.md SECTION 15. That rule prefers
// NetState.Instances or LiveRegistry over World.Mobiles, and it is about work that runs on a timer.
// This runs once, at boot, before any timer exists. LiveRegistry could not answer it anyway:
// PlayerBot.Deserialize deliberately does not register a bot it is about to lose, so the registry
// is empty at exactly the moment this has to look.

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public static class BotStartupPurge
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// How many bots the last purge deleted, for Bots.Population to report.
        /// </summary>
        public static int LastPurged { get; private set; }

        /// <summary>Whether a purge has run this boot at all.</summary>
        public static bool HasRun { get; private set; }

        /// <summary>
        /// Early, though nothing yet depends on it being early.
        ///
        /// Lower runs first and untagged is 0 (CLAUDE.md section 3). Nothing else in Initialize
        /// counts bots - the population fill is at ServerStarted, later - so the ordering is
        /// insurance rather than a requirement. It is worth having anyway: the day something does
        /// count them, the count should be of the bots this boot created and not of the ghosts of
        /// the last one.
        /// </summary>
        [CallPriority(-1000)]
        public static void Initialize()
        {
            Purge();
        }

        public static int Purge()
        {
            var stale = new List<PlayerBot>();

            // SNAPSHOT, THEN DELETE. Deleting inside the enumeration would throw
            // "Collection was modified" the moment the first bot's cascade touched World.Mobiles,
            // and upstream's version carries the same two-pass shape for the same reason.
            foreach (Mobile mobile in World.Mobiles.Values)
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                // Upstream keeps guild-bound bots here: a bot a real player has recruited into a
                // real guild is the one kind that is meant to still be standing after a restart
                // (PlayerBot.cs:166-168, and every deletion path on their side checks it). We have
                // no guild layer yet, so nothing is exempt and the test would be a branch nothing
                // can take. The exemption belongs with the guild session that creates the state.

                stale.Add(bot);
            }

            for (int i = 0; i < stale.Count; i++)
            {
                try
                {
                    stale[i].Delete();
                }
                catch (Exception ex)
                {
                    // Per bot, as upstream does. One bot whose cleanup throws must not leave the
                    // other fifty-nine standing - and a bot that survives a purge is exactly the
                    // accumulation this exists to prevent.
                    Log.Error(ex, "A stale bot would not delete.");
                }
            }

            LastPurged = stale.Count;
            HasRun = true;

            if (stale.Count > 0)
            {
                Log.Info(
                    "Purged {0} stale bot(s) from the world save. Bots do not survive a restart.",
                    stale.Count);
            }
            else
            {
                Log.Info("No stale bots in the world save.");
            }

            return stale.Count;
        }
    }
}
