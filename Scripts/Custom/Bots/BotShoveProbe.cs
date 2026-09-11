// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotShoveProbe.cs — the diagnostic says what the engine does. Reports as Bots.Shove.
//
// WHY THIS EXISTS. The shove rule is written in three places that have to agree: the engine
// (Mobile.Move asks the occupant's OnMoveOver), the policy (BotShove, plus the one delegation in
// BaseCreature.OnMoveOver), and the INSTRUMENTS - NavWalker's rung log and the walk audit's BUSY
// and FRAGILE rows, which are what anybody reads when deciding whether a road is bad or the town
// is merely busy. They did not agree. NavWalkFailures.Shovable answered for three category names,
// while the engine, since the BaseCreature delegation landed, lets a bot walk through EVERY
// uncontrolled creature - so a stock vendor on the next-step tile was logged as "WHICH A BOT MAY
// NOT PUSH" about a step the engine allows, and the count of exactly those rows is the evidence
// for editing further upstream files. REVIEW.md section 4 names it; this is the assertion that
// stops it coming back.
//
// IT ASKS THE ENGINE, and that is the whole design. Every case calls the real OnMoveOver on real
// mobiles and compares the answer with the predicate the instruments use. A test that re-stated
// the predicate in its own words would pass for ever while the engine moved underneath it, which
// is how the original disagreement survived a year of green probes.
//
// FIVE ACTORS, one of each kind the rule distinguishes: a real PlayerBot, the walk audit's own
// WalkAuditProbe (the real class, not a look-alike), a daily-life townsfolk, a stock vendor, and
// an accountless PlayerMobile standing in for a disconnected player - the case whose
// misclassification the review found, and which needs no client to reproduce.
//
// WHAT IT DOES NOT DO: call bot.OnMoveOver(player). A player mover falls through BotShove to the
// engine's own stamina rule, which DEDUCTS TEN STAMINA on the way past (Mobile.cs:3516-3550). The
// contract on that path is "we do not answer, the engine decides", so that is what is asserted -
// BotShove.OnMoveOver returns null - and the engine is left alone rather than measured by poking
// it.

using System;
using System.Collections.Generic;

using Server.Mobiles;

namespace Server.Custom
{
    public static class BotShoveProbe
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

        /// <summary>
        /// Spawn one of each kind, ask the engine every ordered question, and compare.
        ///
        /// Synchronous: nothing here waits for anything. The mobiles exist for the length of this
        /// method and are deleted in the finally, so the probe cannot leave a stock vendor or a
        /// PlayerMobile standing in a plaza - an accountless PlayerMobile would otherwise be
        /// written to the save like any other mobile.
        /// </summary>
        public static HealthResult Run(Map map, Point3D location)
        {
            var problems = new List<string>();
            var checks = 0;

            PlayerBot bot = null;
            NavWalkAudit.WalkAuditProbe probe = null;
            DailyLifeTownsfolk townsfolk = null;
            Baker vendor = null;
            PlayerMobile player = null;

            try
            {
                bot = new PlayerBot(BotClass.Warrior, BotSkillTier.Journeyman);
                probe = new NavWalkAudit.WalkAuditProbe();
                townsfolk = new DailyLifeTownsfolk();
                vendor = new Baker();

                player = new PlayerMobile
                {
                    Name = "Bot Shove Probe Player",
                    Body = 0x190,
                    AccessLevel = AccessLevel.Player,
                    Blessed = true
                };

                // All on one tile on purpose. OnMoveOver is a question about types and states, not
                // about geometry, and stacking them keeps the probe out of anybody's way.
                bot.MoveToWorld(location, map);
                probe.MoveToWorld(location, map);
                townsfolk.MoveToWorld(location, map);
                vendor.MoveToWorld(location, map);
                player.MoveToWorld(location, map);

                // ---- the labels the log lines are written in ----
                //
                // Not the rule any more - MayBotPass and ConsentsToBotPass are - but the word a
                // reader sees beside a wedge, and the player one is the review's finding: a
                // disconnected player has no NetState, and the old classification read the absent
                // NetState as "this is a bot".
                checks += Labelled(problems, bot, "PlayerBot");
                checks += Labelled(problems, probe, "walk-probe");
                checks += Labelled(problems, townsfolk, "daily-life");
                checks += Labelled(problems, vendor, "vendor");
                checks += Labelled(problems, player, "player");

                // ---- forward: may a bot (or the probe) step onto this occupant's tile? ----
                //
                // Both movers, because the instrument stands in for the bot and an instrument that
                // is more obstructed than the thing it measures reports the difference as a fault
                // in the road.
                Mobile[] movers = { bot, probe };
                Mobile[] occupants = { bot, probe, townsfolk, vendor, player };

                for (int m = 0; m < movers.Length; m++)
                {
                    for (int o = 0; o < occupants.Length; o++)
                    {
                        if (ReferenceEquals(movers[m], occupants[o]))
                        {
                            continue;
                        }

                        checks += Forward(problems, movers[m], occupants[o]);
                    }
                }

                // ---- reverse: may this mover step onto a BOT's tile? ----
                checks += Reverse(problems, townsfolk, bot, true);
                checks += Reverse(problems, vendor, bot, false);
                checks += Reverse(problems, probe, bot, true);

                // A player mover is the engine's business, not ours. The assertion is that we say
                // nothing - anything else would be this shard quietly rewriting the player shove
                // rule - and it is made without calling OnMoveOver, which would cost the player
                // ten stamina to find out.
                checks++;

                if (BotShove.OnMoveOver(bot, player) != null)
                {
                    problems.Add(
                        "BotShove answered for a PLAYER mover onto a bot's tile. That path must "
                        + "fall through to the engine's own full-stamina rule.");
                }
            }
            catch (Exception ex)
            {
                problems.Add("the probe faulted: " + ex.Message);
                Log.Error(ex, "The shove contract probe faulted.");
            }
            finally
            {
                Delete(bot);
                Delete(probe);
                Delete(townsfolk);
                Delete(vendor);
                Delete(player);
            }

            string summary = String.Format(
                "{0} shove case(s): the diagnostic matches the engine", checks);

            if (problems.Count > 0)
            {
                _last = HealthResult.Fail(String.Format(
                    "{0} of {1} shove case(s) disagree with the engine: {2}",
                    problems.Count,
                    checks,
                    String.Join("; ", problems.ToArray())));

                Log.Warn("Bot shove contract FAILED - {0}", _last.Detail);
            }
            else
            {
                _last = HealthResult.Ok(summary);

                Log.Info("Bot shove contract PASSED - {0}", summary);
            }

            return _last;
        }

        /// <summary>
        /// The forward case: what the engine answers when <paramref name="mover"/> steps onto
        /// <paramref name="occupant"/>, against what MayBotPass says it will.
        ///
        /// occupant.OnMoveOver is the real call Mobile.Move makes (Mobile.cs:3216). It is
        /// side-effect-free for a bot-like mover in all three of its branches, which is why this
        /// one may be asked directly and the player-mover case may not.
        /// </summary>
        private static int Forward(List<string> problems, Mobile mover, Mobile occupant)
        {
            bool engine = occupant.OnMoveOver(mover);
            bool diagnostic = NavWalkFailures.MayBotPass(occupant);

            if (engine != diagnostic)
            {
                problems.Add(String.Format(
                    "{0} onto {1} ({2}): the engine says {3}, MayBotPass says {4}",
                    Name(mover),
                    Name(occupant),
                    NavWalkFailures.Describe(occupant),
                    engine,
                    diagnostic));
            }

            return 1;
        }

        /// <summary>
        /// The reverse case: what a bot answers when <paramref name="mover"/> steps onto it,
        /// against ConsentsToBotPass - and against what this shard means to allow.
        ///
        /// The expectation is passed in rather than derived, because this direction is a POLICY
        /// and not a mirror of anything: a daily-life actor goes through, a stock vendor does not,
        /// and that asymmetry is a deliberate named deviation. A test that computed its
        /// expectation from the same predicate it is checking would agree with any change to it.
        /// </summary>
        private static int Reverse(List<string> problems, Mobile mover, PlayerBot bot, bool expected)
        {
            bool engine = bot.OnMoveOver(mover);
            bool diagnostic = NavWalkFailures.ConsentsToBotPass(mover);

            if (engine != diagnostic)
            {
                problems.Add(String.Format(
                    "{0} ({1}) onto a bot: the engine says {2}, ConsentsToBotPass says {3}",
                    Name(mover),
                    NavWalkFailures.Describe(mover),
                    engine,
                    diagnostic));
            }

            if (engine != expected)
            {
                problems.Add(String.Format(
                    "{0} ({1}) onto a bot: the engine says {2}, and this shard's rule is {3}",
                    Name(mover),
                    NavWalkFailures.Describe(mover),
                    engine,
                    expected));
            }

            return 2;
        }

        private static int Labelled(List<string> problems, Mobile mobile, string expected)
        {
            string actual = NavWalkFailures.Describe(mobile);

            if (!String.Equals(actual, expected, StringComparison.Ordinal))
            {
                problems.Add(String.Format(
                    "{0} is described as '{1}', expected '{2}'", Name(mobile), actual, expected));
            }

            return 1;
        }

        private static string Name(Mobile mobile)
        {
            return mobile == null ? "?" : (mobile.Name ?? mobile.GetType().Name);
        }

        private static void Delete(Mobile mobile)
        {
            if (mobile != null && !mobile.Deleted)
            {
                mobile.Delete();
            }
        }
    }
}
