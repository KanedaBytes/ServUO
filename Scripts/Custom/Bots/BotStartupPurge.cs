// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotStartupPurge.cs - nothing about a THROWAWAY bot survives a restart, and this is where that is
// enforced. A NAMED bot (PlayerBot.RosterName, the roster cast) is kept, as upstream keeps a
// guild-bound one (BotStartupManager.cs:106-110), and comes back through NamedBots.
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
        /// How many bots the last purge deleted, for Bots.Named to report beside the kept count.
        /// </summary>
        public static int LastPurged { get; private set; }

        /// <summary>How many NAMED bots the last purge kept. Upstream logs the same number (:119).</summary>
        public static int LastKept { get; private set; }

        /// <summary>Whether a purge has run this boot at all.</summary>
        public static bool HasRun { get; private set; }

        /// <summary>
        /// Early, and now it matters: NamedBots.Initialize (905) finds the named cast in the world
        /// by what this left standing.
        ///
        /// Lower runs first and untagged is 0 (CLAUDE.md section 3). The population fill is at
        /// ServerStarted, later, so the count here is of the bots the last boot left behind and
        /// never of the ones this boot is about to create.
        /// </summary>
        [CallPriority(-1000)]
        public static void Initialize()
        {
            Purge();
        }

        public static int Purge()
        {
            int kept;
            List<PlayerBot> stale = Collect(World.Mobiles.Values, out kept);

            // COUNT WHAT THIS BOOT INHERITED, BEFORE DESTROYING IT (REVIEW.md F5, rule 4).
            //
            // The goods on these bots are real units that existed when the save was taken, and
            // the purge is about to end them. Counted in as an opening balance here and written
            // down again as boot-purge losses by each bot's OnDelete, so the restart reads as
            // "inherited N, destroyed N" in the Bots.Conservation line rather than as a hole in
            // the books. The pass is separate from the delete loop because a bot's cascade would
            // otherwise be counted after part of it had already gone.
            for (int i = 0; i < stale.Count; i++)
            {
                Prepare(stale[i]);
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
            LastKept = kept;
            HasRun = true;

            // One write for the whole sweep. A laden fleet hands the ledger a record per bot and
            // appending them one at a time would be a few hundred opens at the worst moment of
            // the boot.
            BotGoodsLedger.Flush();
            BotGoldLedger.Flush();

            Log.Info(
                "Purged {0} stale bot(s) from the world save; kept {1} named bot(s). Throwaway bots do not survive a restart.",
                stale.Count,
                kept);

            return stale.Count;
        }

        /// <summary>
        /// Which of these mobiles the purge deletes, and how many named bots it keeps.
        ///
        /// SNAPSHOT, THEN DELETE: the caller deletes after this returns, because deleting inside
        /// the enumeration would throw "Collection was modified" the moment the first bot's cascade
        /// touched World.Mobiles. Upstream carries the same two-pass shape for the same reason.
        ///
        /// A kept named bot's goods are counted in as this boot's opening balance and held -
        /// offline, not lost (BotGoodsLedger.NoteNamedCarried). Internal so NamedBotFixtures can
        /// run the real pass over a list of its own; Purge over World.Mobiles would delete every
        /// throwaway on a live shard.
        /// </summary>
        internal static List<PlayerBot> Collect(IEnumerable<Mobile> mobiles, out int kept)
        {
            var stale = new List<PlayerBot>();

            kept = 0;

            foreach (Mobile mobile in mobiles)
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                // UPSTREAM'S EXEMPTION, NOW OURS: `if (bot.IsPermanent) { kept++; continue; }`
                // (uo-offline BotStartupManager.cs:106-110). Theirs is a guild recruit; ours is the
                // named cast (PlayerBot.RosterName, NamedBots). Kept whether or not the roster still
                // lists it - a bot taken out of the roster is RETIRED, and retired means offline for
                // ever, like a player who quit, never destroyed.
                if (!IsStale(bot))
                {
                    BotGoodsLedger.NoteNamedCarried(bot);

                    // And its gold, which it keeps too (Sean, 23 September 2026): counted in as
                    // opening, then held offline - re-read from the bot at every census.
                    BotGoldLedger.NoteNamedCarried(bot);
                    kept++;
                    continue;
                }

                stale.Add(bot);
            }

            return stale;
        }

        /// <summary>Does the boot purge delete this bot? Every throwaway; never a named bot.</summary>
        internal static bool IsStale(PlayerBot bot)
        {
            return bot != null && !bot.IsNamed;
        }

        /// <summary>
        /// What the purge does to one bot before deleting it: count what it was holding in as
        /// this boot's opening balance, and name the reason its loss record will carry.
        ///
        /// Internal so HaulFixtures can prove this wiring on a bot of its own. The alternative -
        /// calling Purge from a fixture - would delete every bot in the world, which is not a
        /// thing [CoreSmoke may do on a live shard.
        /// </summary>
        internal static void Prepare(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            BotGoodsLedger.NoteOpening(bot);

            // A throwaway's purse is inherited and destroyed exactly as its goods are: counted in
            // as opening here, written down as a boot-purge loss by its OnDelete.
            BotGoldLedger.NoteOpening(bot);

            bot.DeletionReason = BotGoodsLedger.ReasonBootPurge;
        }
    }
}
