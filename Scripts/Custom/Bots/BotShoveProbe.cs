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
// is merely busy. They did not agree. REVIEW.md section 4 names it; this is the assertion that
// stops it coming back.
//
// IT ASKS THE ENGINE, and that is the whole design. Every cell calls the real OnMoveOver on real
// mobiles and compares the answer with the predicate the instruments use. A test that re-stated
// the predicate in its own words would pass for ever while the engine moved underneath it, which
// is how the original disagreement survived a year of green probes.
//
// REWRITTEN 12 September 2026, the collision vocabulary session, and three things changed.
//
// ONE: IT RUNS ON BOTH FACETS, because on Trammel it was measuring nothing.
// Mobile.CheckShove's entire body is guarded by `(m_Map.Rules & MapRules.FreeMovement) == 0`
// (Mobile.cs:3518), MapRules.TrammelRules includes FreeMovement (Map.cs:128), and that one test is
// the only reader of the flag in the whole Server tree. So on the facet this shard's bots actually
// live on, EVERY mover's CheckShove returns an unconditional true before it reads anything - and
// PlayerBot.CheckShove => true, the line this whole file is named after, buys precisely nothing
// there. Delete that override and a Trammel-only grid stays green. Felucca is where it is
// load-bearing, so Felucca is asked too, and FacetRules asserts the premise rather than assuming
// it.
//
// It also means the sentence this layer's documentation carried for a year - "a real player
// shoving a bot still pays the engine's full-stamina rule, full stamina, minus ten" - was true of
// Felucca and false of here.
//
// TWO: THE SUBJECT SET IS THE WHOLE ORDERED-PAIR TABLE, not the movers that happen to occur. It
// gained a CONTROLLED creature - a bot's own mount or pack animal, which is neither an uncontrolled
// creature nor a player and is refused by neither branch - and that is the subject that showed the
// reverse assertion had been the wrong shape all along. See Reverse.
//
// THREE: THE STAND-IN AND THE SUBJECT ARE THE SAME CLASS, and are told apart by type throughout.
// A PlayerBot IS an accountless PlayerMobile with Player = true and no NetState, and so is the
// player stand-in; not one comparison in this file may use the Player flag or the absent NetState
// to tell them apart, because that is exactly the misclassification the review found. Every test
// is a type, and the labels asserted below are what proves it.
//
// WHY THE PLAYER STAND-IN IS ENOUGH FOR BOTH KINDS OF PLAYER. It has no NetState, because nothing
// short of a real client can give it one - so it IS the link-dead case, exactly. A logged-in
// player is the same type with the same overrides, and every predicate in the vocabulary is a type
// test, so the live cells are identical to these by construction. That is a claim this file can
// make honestly only because the predicates contain no NetState test at all; if one ever appears,
// this paragraph is wrong and the probe cannot tell.
//
// WHAT IT DOES NOT ASK, and the rule is derived rather than hand-named now. On a facet WITHOUT
// FreeMovement, Mobile.CheckShove is not a pure function: it deducts ten stamina (:3544) and sets
// m_Pushing (:3529), which is reset only at the top of Mobile.Move - so asking a second time is no
// longer the same question, and an instrument that quietly cost a player stamina and then
// disagreed with itself would be a genuinely nasty bug. So on Felucca only movers whose CheckShove
// is an unconditional override are asked, which is exactly the load-bearing set. The others are
// covered by the Trammel grid, where the guarded body never runs at all.

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
        /// One subject of the ordered-pair table: a mobile, the word Describe must answer for it,
        /// and whether a bot as the SHOVED side is supposed to let it through.
        ///
        /// The consent expectation is carried here rather than derived, for the reason Reverse
        /// gives: that direction is a policy and not a mirror of anything.
        /// </summary>
        private sealed class Subject
        {
            public Mobile Mobile;
            public string Label;
            public bool BotConsents;
        }

        /// <summary>
        /// Assert the diagnostic against the engine for every ordered pair, on both rules
        /// families.
        ///
        /// Synchronous: nothing here waits for anything. The mobiles exist for the length of one
        /// grid and are deleted in its finally, so the probe cannot leave a stock vendor or a
        /// PlayerMobile standing in a plaza - an accountless PlayerMobile would otherwise be
        /// written to the save like any other mobile.
        /// </summary>
        public static HealthResult Run(Map map, Point3D location)
        {
            var problems = new List<string>();
            var checks = 0;

            // The premise the second grid rests on, asserted rather than assumed. If a merge ever
            // changes MapDefinitions, the reader of this probe's result should be told by the
            // probe and not by a silent change of meaning in half its cells.
            checks += FacetRules(problems);

            Map free = map != null && (map.Rules & MapRules.FreeMovement) != 0 ? map : Map.Trammel;
            Map paid = Map.Felucca;

            // The shard's own facet first, and the whole grid on it: with FreeMovement set,
            // CheckShove short-circuits before it can touch stamina or m_Pushing, so every ordered
            // pair is a pure question and all of them may be asked.
            checks += Grid(problems, free, location, true);

            // Then the facet where CheckShove actually runs. Bot-like movers only - see the header.
            checks += Grid(problems, paid, location, false);

            string summary = String.Format(
                "{0} shove case(s) over 2 facets: the diagnostic matches the engine", checks);

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
        /// The facet premise: Trammel waives the shove rule and Felucca charges for it.
        ///
        /// Asserted because the whole two-grid design turns on it, and because the flag is set in
        /// a file this shard does not own (Scripts/Misc/MapDefinitions.cs:27-32). Note the Siege
        /// branch above those lines gives EVERY facet FeluccaRules, so a shard that turned Siege on
        /// would fail this check - correctly, because then the second grid is the only grid and the
        /// first one's short-circuit reasoning no longer holds anywhere.
        /// </summary>
        private static int FacetRules(List<string> problems)
        {
            if ((Map.Trammel.Rules & MapRules.FreeMovement) == 0)
            {
                problems.Add(
                    "Trammel no longer has MapRules.FreeMovement, so Mobile.CheckShove now runs "
                    + "its full-stamina body on the shard's own facet. Every cell of the first "
                    + "grid has changed meaning; re-read MapDefinitions.cs before trusting this.");
            }

            if ((Map.Felucca.Rules & MapRules.FreeMovement) != 0)
            {
                problems.Add(
                    "Felucca now has MapRules.FreeMovement, so nothing on this shard exercises "
                    + "the full-stamina rule and PlayerBot.CheckShove could be deleted without "
                    + "any probe noticing.");
            }

            return 2;
        }

        /// <summary>
        /// Spawn one subject of each kind on one tile of <paramref name="map"/>, ask every ordered
        /// question, compare, delete.
        ///
        /// <paramref name="everyMover"/> is false on a facet without FreeMovement, where only a
        /// mover with an unconditional CheckShove override may be asked - see the header. It is
        /// not a convenience switch: asking the others there would mutate the engine's state
        /// between two questions that are supposed to be the same one.
        /// </summary>
        private static int Grid(List<string> problems, Map map, Point3D location, bool everyMover)
        {
            var checks = 0;
            var subjects = new List<Subject>();

            PlayerBot bot = null;

            try
            {
                bot = new PlayerBot(BotClass.Warrior, BotSkillTier.Journeyman);

                var probe = new NavWalkAudit.WalkAuditProbe();
                var townsfolk = new DailyLifeTownsfolk();
                var vendor = new Baker();

                var player = new PlayerMobile
                {
                    Name = "Bot Shove Probe Player",
                    Body = 0x190,
                    AccessLevel = AccessLevel.Player,
                    Blessed = true
                };

                // A CONTROLLED creature - what a bot's own mount and pack animal are. It is the
                // subject the old five-actor set was missing, and it is not decoration: it is
                // refused by neither uncontrolled-creature branch, so it is the only occupant and
                // the only mover in the table that reaches Mobile.CheckShove through a BaseCreature
                // path. Its master is the bot because that is what one actually is.
                var pet = new Horse
                {
                    Name = "Bot Shove Probe Pet",
                    Blessed = true,
                    Controlled = true,
                    ControlMaster = bot
                };

                subjects.Add(new Subject { Mobile = bot, Label = "PlayerBot", BotConsents = true });
                subjects.Add(new Subject { Mobile = probe, Label = "walk-probe", BotConsents = true });
                subjects.Add(new Subject { Mobile = townsfolk, Label = "daily-life", BotConsents = true });

                // A stock vendor still jams against a bot, both ways. That asymmetry is a named
                // deviation with its own row in the Bots README, held open on purpose: widening it
                // here would settle the question sideways and go further than uo-offline, whose
                // PlayerBot is a PlayerMobile and blocks uncontrolled creatures outright.
                subjects.Add(new Subject { Mobile = vendor, Label = "vendor", BotConsents = false });

                // A pet does not consent-pass, and the engine still lets it through - by a
                // different route. See Reverse for why that is not a contradiction, and why it is
                // the subject that fixed this probe's shape.
                subjects.Add(new Subject { Mobile = pet, Label = "animal", BotConsents = false });

                // A real player is never handled by BotShove at all, in either direction.
                subjects.Add(new Subject { Mobile = player, Label = "player", BotConsents = false });

                // THE BOT-LIKE MOVERS ARE DRAINED, AND WITHOUT THIS THE FELUCCA GRID ASSERTS
                // NOTHING. Mobile.CheckShove allows a shove outright when the mover is at FULL
                // stamina - it just charges ten for it (Mobile.cs:3541-3545) - and refuses only
                // below that (:3550). So a full-stamina bot passes a player on Felucca whether or
                // not PlayerBot.CheckShove => true exists, and the override could be deleted with
                // every cell still green.
                //
                // Empty stamina is also the exact condition the override was written for.
                // Upstream's comment is about "every road-weary bot" bouncing off the permanent
                // bank-plaza crowds, 500+ pacing events per soak (uo-offline PlayerBot.cs:588-594)
                // - a bot at the end of a long walk, not a bot that just spawned. So this is the
                // case being measured rather than a trick to make the test bite.
                //
                // It cannot move the diagnostic: MayBotPass answers an IBotMover mover before
                // MoverShoves is reached, so nothing below reads their stamina at all.
                foreach (Subject subject in subjects)
                {
                    if (subject.Mobile is IBotMover)
                    {
                        subject.Mobile.Stam = 0;
                    }
                }

                // All on one tile on purpose. OnMoveOver is a question about types and states, not
                // about geometry, and stacking them keeps the probe out of anybody's way.
                foreach (Subject subject in subjects)
                {
                    subject.Mobile.MoveToWorld(location, map);
                }

                // ---- the labels a log line is written in ----
                //
                // Not the rule any more - MayBotPass and ConsentsToBotPass are - but the word a
                // reader sees beside a wedge. Asserted on both facets because it costs nothing and
                // because the bot and the player stand-in are the same class: if Describe ever
                // goes back to splitting them on the Player flag or the NetState, these six lines
                // are what says so.
                foreach (Subject subject in subjects)
                {
                    checks += Labelled(problems, subject.Mobile, subject.Label);
                }

                // ---- forward: every ordered pair ----
                foreach (Subject mover in subjects)
                {
                    if (!everyMover && !(mover.Mobile is IBotMover))
                    {
                        continue;
                    }

                    foreach (Subject occupant in subjects)
                    {
                        if (ReferenceEquals(mover.Mobile, occupant.Mobile))
                        {
                            continue;
                        }

                        checks += Forward(problems, map, mover.Mobile, occupant.Mobile);
                    }
                }

                // ---- reverse: the consent policy, on its own terms ----
                foreach (Subject mover in subjects)
                {
                    checks += Reverse(problems, mover);
                }

                // A player mover onto a bot is the engine's business, not ours. The assertion is
                // that we say nothing - anything else would be this shard quietly rewriting the
                // player shove rule - and it is made without calling OnMoveOver, which on a paid
                // facet would cost the player ten stamina to find out.
                checks++;

                if (BotShove.OnMoveOver(bot, player) != null)
                {
                    problems.Add(String.Format(
                        "on {0}: BotShove answered for a PLAYER mover onto a bot's tile. That "
                        + "path must fall through to the engine's own full-stamina rule.",
                        map));
                }
            }
            catch (Exception ex)
            {
                problems.Add(String.Format("the probe faulted on {0}: {1}", map, ex.Message));
                Log.Error(ex, "The shove contract probe faulted on {0}.", map);
            }
            finally
            {
                foreach (Subject subject in subjects)
                {
                    Delete(subject.Mobile);
                }

                // Already in the list above, so this is a no-op on the normal path. It is here
                // for the one path where it is not: a constructor after the bot's throwing, which
                // leaves the bot built and unlisted. A leaked accountless PlayerMobile would be
                // written to the save like any other mobile, which is the thing this finally
                // exists to prevent.
                Delete(bot);
            }

            return checks;
        }

        /// <summary>
        /// The forward case: what the engine answers when <paramref name="mover"/> steps onto
        /// <paramref name="occupant"/>, against what MayBotPass says it will.
        ///
        /// occupant.OnMoveOver is the real call Mobile.Move makes (Mobile.cs:3216, :3243). On a
        /// FreeMovement facet it is side-effect-free for every mover, because CheckShove's whole
        /// body is skipped; off one, it is side-effect-free only for a mover that overrides
        /// CheckShove unconditionally, which is why Grid filters the movers there rather than here.
        /// </summary>
        private static int Forward(List<string> problems, Map map, Mobile mover, Mobile occupant)
        {
            bool engine = occupant.OnMoveOver(mover);
            bool diagnostic = NavWalkFailures.MayBotPass(mover, occupant);

            if (engine != diagnostic)
            {
                problems.Add(String.Format(
                    "on {0}, {1} ({2}) onto {3} ({4}): the engine says {5}, MayBotPass says {6}",
                    map,
                    Name(mover),
                    NavWalkFailures.Describe(mover),
                    Name(occupant),
                    NavWalkFailures.Describe(occupant),
                    engine,
                    diagnostic));
            }

            return 1;
        }

        /// <summary>
        /// The reverse question, as the POLICY it is: does a bot consent to this mover?
        ///
        /// THE SHAPE OF THIS ASSERTION CHANGED WITH THE CONTROLLED PET, and the old shape was
        /// wrong in a way no previous subject could show. It used to compare the engine's
        /// bot.OnMoveOver against ConsentsToBotPass directly, as though consent were a mirror of
        /// the engine. It is not, and a pet is the proof: ConsentsToBotPass says false for it,
        /// correctly - a pet is neither an IBotMover nor an IDailyLifeActor - and yet the engine
        /// lets a pet onto a bot's tile anyway, because BotShove returns null, PlayerMobile's
        /// uncontrolled-creature branch does not match a CONTROLLED creature, and the step falls
        /// through to Mobile.OnMoveOver and the pet's own CheckShove. Two different routes to
        /// true, and only one of them is consent.
        ///
        /// So the two halves are now asserted by the two things that actually own them. The
        /// engine's answer for a bot occupant is one column of the forward grid above, mirrored by
        /// MayBotPass like every other cell. What is left here is consent measured against what
        /// this shard means to allow - and that expectation is carried on the Subject rather than
        /// computed, because a test that derived its expectation from the predicate it is checking
        /// would agree with any change to it.
        /// </summary>
        private static int Reverse(List<string> problems, Subject mover)
        {
            bool consents = NavWalkFailures.ConsentsToBotPass(mover.Mobile);

            if (consents != mover.BotConsents)
            {
                problems.Add(String.Format(
                    "{0} ({1}) onto a bot: ConsentsToBotPass says {2}, and this shard's rule is {3}",
                    Name(mover.Mobile),
                    NavWalkFailures.Describe(mover.Mobile),
                    consents,
                    mover.BotConsents));
            }

            return 1;
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
