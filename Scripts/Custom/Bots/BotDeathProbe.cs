// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotDeathProbe.cs — a reportable bot death, on the record. Reports as Bots.Death.
//
// THIS FIXES NOTHING, DELIBERATELY. It is REVIEW.md's F2 turned into a reproduction that runs on
// every [BotSmoke, so the defect is a measured fact rather than a paragraph somebody has to believe.
//
// THE DEFECT. PlayerBot is a BaseCreature that sets Player = true, for the party gate among other
// things. Mobile.OnDeath takes the PLAYER branch for anything with that flag and raises PlayerDeath
// (Mobile.cs:4235). ReportMurdererGump handles that event, and when Core.SE is on and the victim
// has a reportable player aggressor it evaluates ((PlayerMobile)m).RecentlyReported
// (ReportMurderer.cs:43) - a cast a bot cannot satisfy. Our own bots are Player-flagged too, so a
// bot killing a bot is a reportable player aggressor: no real player, no NetState, no client needed
// to reach it.
//
// It is not "every bot death crashes". It needs SE rules, a Player-flagged aggressor, and
// CanReportMurder still true on that aggression - which is why nothing has tripped over it yet, and
// exactly why it needs a probe rather than a note: the conditions are ordinary enough to arrive the
// day bots fight each other, and the failure lands INSIDE PlayerBot.OnDeath's base call, before the
// mount is released and before the delete is scheduled, so a bot dying that way also skips its own
// cleanup.
//
// WHY THE CHAIN STAYS GREEN. A probe that failed on a known, scheduled defect would make [BotSmoke
// permanently red, and a permanently red check is one nobody reads - which is how a real regression
// hides. So the EXPECTED outcome reports Ok and says so in its own words. What fails here is the
// probe being unable to establish its own preconditions: that is the probe going wrong, and it is
// worth waking somebody for. If it ever stops reproducing, that reports Ok too, with a different
// sentence asking to be read - because on the day the class decision lands, this is the check that
// tells you it worked.
//
// The permanent repair is the PlayerMobile identity decision (REVIEW.md section 3), not a patch to
// ReportMurderer: that file is upstream, and the cast is correct about every mobile ServUO ships.

using System;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    public static class BotDeathProbe
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        private static HealthResult _last;

        public static HealthResult BuildHealthResult()
        {
            if (_last == null)
            {
                return HealthResult.Ok("not run this boot (runs with [BotSmoke)");
            }

            return _last;
        }

        public static HealthResult Run(Map map, Point3D location)
        {
            // The cast is behind Core.SE. On a pre-SE shard the branch is unreachable and saying
            // "reproduced" or "fixed" would both be wrong.
            if (!Core.SE)
            {
                _last = HealthResult.Ok(String.Format(
                    "not applicable: ReportMurderer's PlayerMobile cast is behind Core.SE, and this "
                    + "build is {0}",
                    Core.Expansion));

                return _last;
            }

            PlayerBot victim = null;
            PlayerBot killer = null;
            Container corpse = null;

            try
            {
                victim = new PlayerBot(BotClass.Warrior, BotSkillTier.Journeyman);
                killer = new PlayerBot(BotClass.Warrior, BotSkillTier.Journeyman);

                victim.MoveToWorld(location, map);
                killer.MoveToWorld(location, map);

                // The real registration path, not a hand-built AggressorInfo: AggressiveAction is
                // what combat calls, and `criminal: true` is what sets CanReportMurder
                // (Mobile.cs:2333). Anything else would be testing a shape rather than the code.
                victim.AggressiveAction(killer, true);

                if (!Reportable(victim, killer))
                {
                    return Broken(victim, killer,
                        "the aggression did not register as reportable, so the death would not "
                        + "have reached the murder-report branch at all. AggressiveAction or "
                        + "AggressorInfo has changed - this probe is no longer asking the question "
                        + "it thinks it is asking.");
                }

                Exception thrown = null;

                try
                {
                    victim.Kill();
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                corpse = victim.Corpse;

                if (thrown is InvalidCastException)
                {
                    _last = HealthResult.Ok(String.Format(
                        "reproduced as expected: a reportable bot death throws {0} in the murder "
                        + "report ({1}). EXPECTED FAILURE until the class decision - PlayerBot is a "
                        + "BaseCreature with Player = true, and ReportMurderer casts the victim to "
                        + "PlayerMobile. REVIEW.md F2; the repair is the identity migration, not a "
                        + "patch to an upstream file.",
                        thrown.GetType().Name,
                        FirstFrame(thrown)));

                    Log.Warn("Bot death probe reproduced F2 as expected - {0}", thrown.Message);

                    return Finish(victim, killer, corpse);
                }

                if (thrown != null)
                {
                    return Broken(victim, killer, String.Format(
                        "the death threw {0}, which is not the cast this probe reproduces: {1}",
                        thrown.GetType().Name,
                        thrown.Message));
                }

                _last = HealthResult.Ok(
                    "NO LONGER REPRODUCES: a reportable bot death completed without the "
                    + "PlayerMobile cast failing. That is news - either the class decision has "
                    + "landed (REVIEW.md F2, section 3) or the conditions moved. Read this line "
                    + "rather than skimming it, and retire the probe deliberately.");

                Log.Info("Bot death probe - {0}", _last.Detail);

                return Finish(victim, killer, corpse);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The death probe faulted.");

                return Broken(victim, killer, "the probe faulted: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether the aggression this probe just registered is the kind ReportMurderer acts on:
        /// a Player-flagged attacker, still reportable, not yet reported.
        ///
        /// The same three conditions ReportMurderer.EventSink_PlayerDeath tests (line 43), asked
        /// before the kill - so a probe that reports "did not reproduce" cannot be one that never
        /// set the trap.
        /// </summary>
        private static bool Reportable(Mobile victim, Mobile killer)
        {
            foreach (AggressorInfo info in victim.Aggressors)
            {
                if (info.Attacker == killer && info.Attacker.Player
                    && info.CanReportMurder && !info.Reported)
                {
                    return true;
                }
            }

            return false;
        }

        private static string FirstFrame(Exception ex)
        {
            string stack = ex.StackTrace;

            if (String.IsNullOrEmpty(stack))
            {
                return "no stack";
            }

            int end = stack.IndexOf('\n');
            string first = end >= 0 ? stack.Substring(0, end) : stack;

            return first.Trim();
        }

        private static HealthResult Broken(PlayerBot victim, PlayerBot killer, string why)
        {
            _last = HealthResult.Fail(why);

            Log.Error("Bot death probe FAILED - {0}", why);

            return Finish(victim, killer, null);
        }

        /// <summary>
        /// Clear up, whatever happened.
        ///
        /// The exception lands INSIDE base.OnDeath, so PlayerBot.OnDeath never reaches its mount
        /// release or its delete timer: nothing else is going to tidy this up. The corpse goes too,
        /// which is a deviation from the ordinary death path on purpose - a real bot's corpse is
        /// left for a player to loot, and a probe's is litter in a plaza.
        /// </summary>
        private static HealthResult Finish(PlayerBot victim, PlayerBot killer, Container corpse)
        {
            if (victim != null && !victim.Deleted)
            {
                BotMovement.ReleaseMount(victim);
                victim.Delete();
            }

            if (killer != null && !killer.Deleted)
            {
                BotMovement.ReleaseMount(killer);
                killer.Delete();
            }

            if (corpse != null && !corpse.Deleted)
            {
                corpse.Delete();
            }

            return _last;
        }
    }
}
