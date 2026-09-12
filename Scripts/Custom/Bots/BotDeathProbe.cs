// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotDeathProbe.cs — a reportable bot death, on the record. Reports as Bots.Death.
//
// THIS USED TO FIX NOTHING, DELIBERATELY, AND NOW IT ASSERTS. It was REVIEW.md's F2 turned into a
// reproduction: a known defect, reported green on purpose so that a permanently red check did not
// become one nobody read. The class swap closed the defect, so the probe turned round - a clean
// reportable death is now the PASS, and the cast coming back is a FAIL naming F2 as regressed.
//
// WHAT THE DEFECT WAS. A PlayerBot was a BaseCreature that set Player = true, for the party gate
// among other things. Mobile.OnDeath takes the PLAYER branch for anything with that flag and raises
// PlayerDeath (Mobile.cs:4235). ReportMurdererGump handles that event, and when Core.SE is on and
// the victim has a reportable player aggressor it evaluates ((PlayerMobile)m).RecentlyReported
// (ReportMurderer.cs:43) - a cast a BaseCreature cannot satisfy. Our own bots are Player-flagged
// too, so a bot killing a bot was a reportable player aggressor: no real player, no NetState and no
// client were needed to reach it.
//
// WHAT CLOSED IT. PlayerBot is a PlayerMobile (CLASS-DECISION.md, option d), so the cast succeeds
// and the handler runs to completion. That is the whole repair, and it is why the class decision
// was taken rather than ReportMurderer patched: that file is upstream and its cast is correct about
// every mobile ServUO ships.
//
// WHAT IT DOES NOT CLOSE, AND MUST NOT BE READ AS CLOSING. The gump is delivered to a client
// (ReportMurderer.cs:96) and the count is awarded inside OnResponse (:112, killer.Kills++ at :122).
// A clientless victim answers nothing, so a player can still cut down the whole of Britain and stay
// blue. Upstream answers that with an adapter their bot runs before base.OnDeath
// (BotMurderReport.OnBotDeath), and it is not ported here - it belongs with the PK session, and it
// carries a shard policy question with it about whether bot-on-bot kills should award counts at
// all. THIS PROBE ASSERTS THAT THE DEATH COMPLETES, NOT THAT ANYBODY WAS REPORTED.
//
// WHY IT STILL FAILS LOUDLY WHEN IT CANNOT SET ITS OWN TRAP. An exception other than the cast, or
// an aggression that never registers as reportable, is the probe going wrong rather than the engine,
// and a probe that cannot ask its question must never answer it green.

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
                    // F2, BACK FROM THE DEAD. This was the expected outcome until the class swap
                    // and reported Ok; it is now a failure, because the only way to reach it again
                    // is for a PlayerBot to have stopped being a PlayerMobile.
                    _last = HealthResult.Fail(String.Format(
                        "REGRESSED: a reportable bot death threw {0} in the murder report ({1}). "
                        + "That is REVIEW.md F2, which the PlayerMobile class swap closed - "
                        + "ReportMurderer casts the victim to PlayerMobile (ReportMurderer.cs:43) "
                        + "and a PlayerBot must satisfy it. Check what PlayerBot now inherits from.",
                        thrown.GetType().Name,
                        FirstFrame(thrown)));

                    Log.Error("Bot death probe FAILED - F2 has regressed: {0}", thrown.Message);

                    return Finish(victim, killer, corpse);
                }

                if (thrown != null)
                {
                    return Broken(victim, killer, String.Format(
                        "the death threw {0}: {1}",
                        thrown.GetType().Name,
                        thrown.Message));
                }

                _last = HealthResult.Ok(
                    "a reportable bot death completed: the aggression registered as reportable, "
                    + "the victim died, and the murder report ran through without the PlayerMobile "
                    + "cast failing. REVIEW.md F2 is closed by the class swap. Note what this does "
                    + "NOT assert: the report gump is delivered to a client and the count is "
                    + "awarded in its OnResponse, so a clientless victim still awards nobody a "
                    + "murder count. That adapter is the PK session.");

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
        /// Unconditional, and it was not always safe to assume it needed to be: while F2 stood,
        /// the exception landed INSIDE base.OnDeath, so PlayerBot.OnDeath never reached its mount
        /// release or its delete timer and nothing else was going to tidy up. That path is closed,
        /// and PlayerBot.OnDeath now runs to the end - but this stays unconditional, because a
        /// probe that only cleans up after the outcomes it predicted is a probe that leaks on the
        /// one it did not.
        ///
        /// The corpse goes too, which is a deviation from the ordinary death path on purpose - a
        /// real bot's corpse is left for a player to loot, and a probe's is litter in a plaza.
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
