// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// PlayerBotWalkAuditProbe.cs — the walk audit's second instrument, which IS a bot.
//
// WHY A SECOND PROBE AND NOT A SWAPPED ONE. [WalkAudit's original probe is a BaseCreature with
// CanOpenDoors => true (NavWalkAudit.WalkAuditProbe), so it has always taken FastAStarAlgorithm's
// `bc != null` branch and has always planned through closed doors. On the day a bot's routes
// stopped planning through them - 12 September 2026, when PlayerBot became a PlayerMobile and
// missed that cast - THIS AUDIT WAS GREEN over 2,510 legs while the fleet's terminal walk failures
// went from about 0.5 to 5.23 per 100. The audit was not wrong: it was a real guarantee about the
// graph and about NavCreatureActor. It was simply never a guarantee about bots, and nobody had
// written that down.
//
// Swapping the one probe would have bought the bot half and sold the other: three of the walker's
// users are daily-life BaseCreatures - DailyLifeTownsfolk, the shop-schedule vendors and the
// behaviour sites that drive them - and NavCreatureActor is measured by nothing else. So there are
// two probes, walked in the same sweep and reported in separate rows. The Navigation README set
// this question up on the day the door gate landed and left it for the rebaseline; this is the
// rebaseline.
//
// IT INHERITS THE RULES RATHER THAN COPYING THEM, and that is the whole design. An instrument that
// used a different rule from the shard has cost more here than any bug: the original probe was
// refused by every occupant class a real bot walks through, because BotShove keyed on PlayerBot and
// the probe was a plain BaseCreature, and the walk audit read SELF-TEST BROKEN until MODIFICATIONS
// entry 5 landed. So this class does not restate one collision or door rule. By deriving from
// PlayerBot it gets, with no code of its own:
//
//   the door gate     - BotPathPolicy.IgnoreDoors asks `p is IBotActor`, so its ROUTES plan
//                       through closed doors exactly as a bot's do (MODIFICATIONS entry 6)
//   the door step     - PlayerBot.Move recovers a refused step through Core/DoorHelper
//   the shove rule    - CheckShove => true, and IBotMover, so BotShove treats it as bot traffic
//   the adapter       - NavActor.For hands a PlayerMobile to NavPlayerActor, which is the step
//                       clock and the PathFollower the fleet actually walks on
//   the diagnostic    - MayBotPass answers its cells as a bot mover's, because it is one
//
// Every one of those is a link in the chain the class swap broke. A probe that implemented the
// interfaces by hand would prove every link except the one that matters.
//
// WHAT IT SUPPRESSES, AND WHY EACH IS WHERE IT IS. PlayerBot has no protected no-op constructor,
// so the full constructor body runs before a single line here does - which means every suppression
// below is a CORRECTION rather than a prevention. That is sound because a subclass constructor runs
// on the same tick, before any timer slice, so nothing has had a chance to observe the bot yet.
//
//   LiveRegistry.Unregister    - ONE LINE FOR SEVEN CONSUMERS. The behaviour tick, the party poll,
//                                the lifecycle roller, the session curve, the crowd counts, the
//                                live map and all three population censuses iterate
//                                LiveRegistry.Snapshot() and nothing else, so leaving the registry
//                                is the whole of staying out of them.
//   NamePool.Release           - the constructor claims a unique name from the pool. Left claimed,
//                                twelve probes would inflate InUseCount above the live bot count,
//                                which is exactly the leak Bots.Population warns about.
//   IsInstrument               - the mount roll and the step census, neither reachable from here:
//                                see PlayerBot.IsInstrument for why each needs a flag.
//   HandlesOnSpeech            - nothing should talk to an instrument, and it should not answer.
//   Blessed, Hidden            - the original probe's list, for the original probe's reasons:
//                                nothing must kill it and nothing must react to it.
//   Role = Fixed               - belt and braces. Already covered by leaving LiveRegistry; kept
//                                because it costs one line and says what this thing is.
//
// WHAT IT DELIBERATELY DOES NOT SUPPRESS:
//
//   ITS GEAR. The constructor rolls six to twenty items and a backpack, and they stay. A real bot
//   walks carrying them, the pace this probe measures is set explicitly through StepDelaySeconds
//   and NavPlayerActor has no SpeedInfo in its path, so gear cannot change the walk - and leaving
//   it is one less way for the instrument to differ from the thing it measures.
//
//   ITS OnMoveOver. The BaseCreature probe answers an unconditional true - it is transparent to
//   every mover, so twelve of them in one town do not refuse each other. THIS ONE KEEPS
//   PlayerBot's, because a real bot is not transparent: a stock uncontrolled vendor jams against
//   it, and that named deviation is a thing the sweep should feel. Probe-on-probe contention does
//   not follow from it: both probe classes are IBotMover, so each passes the other through
//   BotShove's first branch, and a daily-life actor consents through IsSymmetricMover. What can
//   jam it is a stock NPC - which is the point.
//
// NOT [Constructable], on purpose. A spawner refuses a type without it (XmlSpawner2 defaults
// requireconstructable true) and VocabularySnapshot filters the editor's Type dropdown on the same
// attribute, so omitting it is what keeps an instrument out of both. It is only ever built by the
// audit's own factory.

using System;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// A walk-audit probe that is a real <see cref="PlayerBot"/>, so the sweep measures the class
    /// the fleet actually is. See the file header for what it inherits and what it suppresses.
    /// </summary>
    public class PlayerBotWalkAuditProbe : PlayerBot, NavWalkAudit.IWalkAuditProbe
    {
        private int _steps;

        /// <summary>
        /// Tell the audit this class exists.
        ///
        /// REGISTERED FROM HERE, NOT FROM Core, which is the same rule Nav.Doors follows: a check
        /// or an instrument that has to name PlayerBot is registered from Bots, because
        /// Core/Navigation may not know bots exist. NavActorCheck's type-test ledger is the
        /// enforcement of that rule, and it is why NavWalkAudit holds a keyed registry of
        /// factories rather than a switch on a class.
        ///
        /// RegisterProbeClass is idempotent by key, which matters: ScriptCompiler.Invoke calls
        /// every public static void Initialize() it can reflect over, and a second pass would
        /// otherwise walk the whole graph twice for this class.
        /// </summary>
        public static void Initialize()
        {
            NavWalkAudit.RegisterProbeClass(
                "bot", "PlayerBot", () => new PlayerBotWalkAuditProbe());
        }

        public PlayerBotWalkAuditProbe()
            : base(BotClass.Warrior, BotSkillTier.Journeyman)
        {
            // OUT OF THE REGISTRY FIRST. Everything that would tick, roll, count or draw this
            // thing reads LiveRegistry.Snapshot(); the base constructor registered it one line
            // ago, and nothing can have looked yet because a constructor and the timer slice that
            // would look are on the same thread and this one has not returned.
            LiveRegistry.Unregister(this);

            // GIVE THE NAME BACK, then take one nobody could mistake for a person's. The pool is
            // the denominator of Bots.Population's name census, and twelve probes holding claims
            // would read as a leak - which is a warning that exists to catch a real one.
            NamePool.Release(Name);

            Name = "a walk probe (bot)";
            Title = null;

            Role = BotRole.Fixed;

            Blessed = true;
            Hidden = true;

            // THE PACE, matching the BaseCreature probe's exactly so the two classes' rows are
            // comparable. NavPlayerActor reads this through IBotActor.StepDelaySeconds and has no
            // SpeedInfo in its path, so this is the pace the engine steps on - the one number
            // [BotPace exists to tell apart for a creature and, for a PlayerMobile, cannot.
            StepDelaySeconds = Mobile.RunMount / 1000.0;

            IdleTolerance = 0;
            Home = Point3D.Zero;
        }

        public PlayerBotWalkAuditProbe(Serial serial)
            : base(serial)
        {
        }

        /// <summary>
        /// See <see cref="PlayerBot.IsInstrument"/>: no mount roll, no step census.
        /// </summary>
        public override bool IsInstrument
        {
            get { return true; }
        }

        /// <summary>Itself, for <see cref="NavWalkAudit.IWalkAuditProbe"/>.</summary>
        public Mobile Mobile
        {
            get { return this; }
        }

        /// <summary>Tiles moved since the last <see cref="ResetSteps"/>.</summary>
        public int Steps
        {
            get { return _steps; }
        }

        public void ResetSteps()
        {
            _steps = 0;
        }

        /// <summary>
        /// Count the step, then let <see cref="PlayerBot.Move"/> have it.
        ///
        /// base FIRST and the count second, so the door recovery is inside the measurement rather
        /// than beside it: PlayerBot.Move retries through DoorHelper when the first attempt is
        /// refused, and a probe that counted before calling base would miss the retry entirely.
        ///
        /// NOTE WHAT A STEP MEANS HERE, because it is not quite what the name says and the other
        /// probe has the same quirk: Mobile.Move returns true for a TURN as well as for a move
        /// (Mobile.cs:3120, and Nav.Doors was built on that fact). So the count is steps plus
        /// turns. Left as it is deliberately - the BaseCreature probe has counted it this way for
        /// every recorded sweep, and a second probe that counted differently would make the two
        /// classes' stepRatio columns incomparable for no gain. stepRatio is the non-deterministic
        /// column anyway; ratio, which is the engine's planned route over the straight line, is
        /// the one the re-base argument rests on.
        /// </summary>
        public override bool Move(Direction d)
        {
            bool moved = base.Move(d);

            if (moved)
            {
                _steps++;
            }

            return moved;
        }

        /// <summary>
        /// Nothing talks to an instrument, and it answers nobody.
        ///
        /// PlayerBot's override is the one that matters to suppress: it widens hearing to the
        /// bot's own ListenRange so a bot can answer to its name, and a probe crossing a town
        /// would otherwise be handed every line spoken near it - twelve of them, for a few
        /// minutes, into a responder that would try to reply.
        /// </summary>
        public override bool HandlesOnSpeech(Mobile from)
        {
            return false;
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write(0); // version
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);

            int version = reader.ReadInt();
        }
    }
}
