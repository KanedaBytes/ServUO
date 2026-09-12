// NavWalkFailures.cs — the ledger of walks that ended in a teleport.
//
// WHY THIS EXISTS
// ---------------
// The terminal failure was the one event in the whole walker that recorded nothing usable.
//
// Every rung of the recovery ladder fires LogRung, which raises the RungFired seam, which a bot
// turns into a BotLog entry. The TOP of the ladder - the point where the ladder has been climbed
// twice, the bot has not moved, and NavWalker gives up and teleports it - calls neither. It writes
// one Log.Warn and moves the mobile. So the failure everybody wants to understand is the failure
// with the least evidence attached to it, and the console line it does write says:
//
//     could not walk 'uo-britain-bank' -> '(arrival)' ... it was moved
//
// `(arrival)` because DescribeHop prints waypoint IDS and an arrival step has none. The goal tile's
// coordinates are nowhere. Nor is anything about what was standing on it: DescribeBlocker reports
// mobiles on the goal only when one is exactly there, and otherwise falls back to a list centred on
// the WALKER, which is a different set of mobiles from the one being asked about.
//
// And BotLog could not have held it anyway: fifty events per bot across sixty-four bots, written
// only while the live map is running. A half-hour at a busy bank rolls its own evidence off the end
// before anybody reads it.
//
// So: an in-memory ledger, capped, written out on request as Data/Live/walk-failures.json. The
// same arrangement nav-audit.json has, for the same reason - Core does not write snapshots, the
// Bridge does.
//
// WHAT IT RECORDS, AND WHY EACH FIELD IS THERE
// --------------------------------------------
//   the hop            which edge, in the same words the console uses
//   the goal tile      the coordinates the walk was aiming at, which nothing else captures
//   the near list      every mobile within two tiles OF THE GOAL, typed - a bot, a daily-life
//                      actor, a stock NPC, an animal, a player. The type is the whole question:
//                      a bot yields to a stock NPC and shoves another bot, so who was standing
//                      there decides whether this failure was avoidable.
//   goalUnstandable    whether a mobile fits on the goal tile AT ALL. 21 authored arrivals fail
//                      this, and a failure at one of them is not a crowding problem - it is a walk
//                      that could never have finished, and it has to be separable from the rest or
//                      it poisons the measurement.

using System;
using System.Collections.Generic;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// A mobile that MOVES under the bot shove rule: it may step onto an occupied tile, and the
    /// occupant consents through BotShove rather than refusing it as an uncontrolled creature.
    ///
    /// It lives in Core, beside the two shove predicates, rather than in Bots - because the two
    /// implementers sit on opposite sides of that line and the rule has to be one rule. PlayerBot implements it
    /// (its CheckShove override already satisfies the member), and so does the walk audit's probe,
    /// which is in Core and must not reach up into Bots.
    ///
    /// THE PROBE IS IN HERE BECAUSE THE AUDIT WAS STRICTER THAN THE BOT IT STANDS IN FOR, in every
    /// direction and for every occupant class. BotShove keyed its mover branch on `PlayerBot`
    /// directly, and a probe is a plain BaseCreature - so a probe stepping onto a GG vendor, a
    /// daily-life patron, a stock NPC or another bot was refused where a real bot walks through.
    /// The audit was reporting the instrument's own class as a fault in the road, and the rows it
    /// inflated - BUSY and FRAGILE - are exactly the rows a reader uses to decide a road is fragile.
    ///
    /// Nothing widens for anybody else. The interface is implemented by two types and tested for
    /// in one place, so the rule is still "a bot, and the thing that measures bots".
    /// </summary>
    public interface IBotMover
    {
        /// <summary>Whether this mover may step onto the tile <paramref name="shoved"/> is on.</summary>
        bool CheckShove(Mobile shoved);
    }

    /// <summary>One walk that climbed the whole ladder and was teleported.</summary>
    public sealed class NavWalkFailure
    {
        public DateTime Utc;
        public string Who;
        public string WhoType;
        public string Hop;
        public string MapName;
        public int GoalX;
        public int GoalY;
        public int GoalZ;

        /// <summary>
        /// Where the bot actually was when it gave up.
        ///
        /// Not the same as the hop's authored start, and that difference is the whole reason it is
        /// recorded: a bot that has been sidestepping for eighty seconds is somewhere the route
        /// never named, so "walk this hop with the engine" has to start from HERE to reproduce the
        /// failure. Reconstructing it from the from-waypoint asks a different question and can
        /// answer it "walkable" about a hop the bot could not walk.
        /// </summary>
        public int FromX;
        public int FromY;
        public int FromZ;

        public bool Watched;

        /// <summary>Nothing fits on the goal tile at any height. See the header.</summary>
        public bool GoalUnstandable;

        /// <summary>
        /// WHY this walk ended, in the only two shapes that ask different questions.
        ///
        /// "goal empty" was one bucket and it hid two unrelated faults. A bot standing INSIDE the
        /// arrival's range that still could not finish has a stand-tile problem - the place was
        /// reached and no tile in it would take the mobile. A bot that ran out of ladder ten tiles
        /// short never reached the place at all, and its problem is the road behind it, which is
        /// why that case carries the last hop. Merged, they made 28 of 30 failures look like one
        /// thing; they are not, and the two fixes have nothing in common.
        /// </summary>
        public string Cause;

        /// <summary>
        /// WHICH KIND OF HOP this was - "arrival" or "waypoint" - so the split can be counted
        /// rather than read off the Hop string.
        ///
        /// The two are different failures with different fixes and they were only ever separable
        /// by eye, by noticing that Hop ended in '(arrival)'. An arrival is a picked point plus a
        /// scatter that nothing has asked the pathfinder about; a waypoint was authored against
        /// the engine and audited in both directions. A window where the arrival column is empty
        /// and the waypoint column is not says the graph regressed; the reverse says the arrival
        /// picker did.
        /// </summary>
        public string HopKind;

        /// <summary>
        /// Whether this was the FIRST step of the route, with no waypoint behind it.
        ///
        /// Recorded because it decides which recovery rungs even exist. On a one-step route
        /// TrySkipWaypoint refuses (there is nothing to skip to) and TryReAnchor returns
        /// immediately (PreviousWaypointId is null), so the whole ladder is Repath, Sidestep and
        /// Door - none of which changes the tile being aimed at. Four of one window's seventeen
        /// failures were this, and they are invisible in the cause split.
        /// </summary>
        public bool FromStart;

        /// <summary>The arrival range in force, so "within range" is reproducible from the file.</summary>
        public int ArrivalRange;

        /// <summary>
        /// HOW FAR ALONG THE ROUTE the walker had got, and how much of that it walked.
        ///
        /// The field that answers "where did the 33 come from". A terminal failure prints the
        /// distance from the mobile to `step.Point`, and on a hop authored at ten tiles that number
        /// came back as 33 - which cannot be the edge, so it is either the mobile or the step. Both
        /// are here now:
        ///
        ///   Index / RouteCount   which step of how many. A route whose index has run to the end
        ///                        while the mobile stood still is TrySkipWaypoint marching it:
        ///                        each skip advances the index and calls ResetHop, so the ladder
        ///                        starts again and the reported hop is one the walker never
        ///                        approached.
        ///   Skips                how many of those there were on this route, counted rather than
        ///                        inferred from the index, because Advance moves it too.
        ///   StartX/Y/Z           where the mobile was when Follow was called. If that is far from
        ///                        FromX/Y/Z the mobile travelled; if it is the same tile, it never
        ///                        moved and the index is the whole story.
        /// </summary>
        public int Index;
        public int RouteCount;
        public int Skips;
        public int StartX;
        public int StartY;
        public int StartZ;

        /// <summary>"Name (type) at x,y" for everything within two tiles of the GOAL.</summary>
        public readonly List<string> Near = new List<string>();
    }

    public static class NavWalkFailures
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>
        /// Enough for a long measurement run and small enough to forget about.
        ///
        /// Oldest-first eviction rather than newest-dropped: a run that overflows wants its recent
        /// failures, and a cap that silently kept the first 500 and threw away everything after
        /// would look like the problem stopped.
        /// </summary>
        public const int MaxKept = 500;

        private static readonly List<NavWalkFailure> _failures = new List<NavWalkFailure>();

        private static int _total;

        private static int _started;

        private static int _completed;

        /// <summary>
        /// How many walks were STARTED and how many reached their last step, since the last clear.
        ///
        /// WHY A DENOMINATOR. Windows A, B, C and D are thirty, thirty, fifteen and thirty
        /// minutes, and every table so far has been read per minute - which is a fact about how
        /// busy the shard was, not about how good the roads are. A window with the population
        /// doubled and the visit windows thirded (MeasurementProfile) produces several times the
        /// walks in the same wall time, so per minute would read as a regression while the roads
        /// were unchanged.
        ///
        /// A WALK IS A ROUTE, AND A FAILURE IS AN EVENT INSIDE ONE. `_total` counts hops that
        /// climbed the whole ladder and were rescued; a route of five hops can produce more than
        /// one of those and still reach its last step, because the rescue teleports and carries
        /// on. So "failures per 100 walks" is terminal-failure events per hundred routes BEGUN,
        /// which is well defined and is the number that stops depending on window length. It is
        /// not a percentage of walks that failed, and it can exceed 100 on a graph bad enough.
        ///
        /// Completed is kept beside it as routes that reached their last step. The gap between the
        /// two is the walks still in flight plus the ones that ended some other way - a bot
        /// deleted mid-route by a session ending, a Stop from a hand switch - and a gap that grows
        /// without failures growing is its own finding.
        /// </summary>
        public static int WalksStarted
        {
            get { return _started; }
        }

        public static int WalksCompleted
        {
            get { return _completed; }
        }

        /// <summary>Counted by NavWalker.Follow. Gated on NavWalker.Ledger - see that property.</summary>
        public static void NoteWalkStarted()
        {
            _started++;
        }

        /// <summary>Counted by NavWalker.Finish, the single completion point.</summary>
        public static void NoteWalkCompleted()
        {
            _completed++;
        }

        /// <summary>
        /// Events per hundred walks started, or -1 when nothing has walked yet.
        ///
        /// -1 rather than 0, because "no walks, so no failures" and "many walks and no failures"
        /// are opposite findings and a shared 0 would read as the good one.
        /// </summary>
        public static double PerHundredWalks(int events)
        {
            return _started <= 0 ? -1.0 : (events * 100.0) / _started;
        }

        /// <summary>Every failure since the last clear, oldest first.</summary>
        public static IList<NavWalkFailure> All
        {
            get { return _failures.AsReadOnly(); }
        }

        /// <summary>How many there have been, including any the cap evicted.</summary>
        public static int Total
        {
            get { return _total; }
        }

        public static void Clear()
        {
            _failures.Clear();
            _total = 0;

            // The denominator clears with the numerator or the rate is nonsense: a window opened
            // by `walk-failures clear` would otherwise divide this window's failures by every walk
            // since boot.
            _started = 0;
            _completed = 0;
        }

        /// <summary>
        /// WHY this walk ended, in the ledger's own vocabulary, and whether anything fits on the
        /// goal tile at all.
        ///
        /// EXTRACTED SO THERE IS ONE COPY. Record used to compute this inline, and [WalkAudit asks
        /// exactly the same question about a probe that ran out of ladder. Two copies of a
        /// three-way branch would drift the moment either grew a fourth case, and the whole value
        /// of the walk audit is that its causes read against the live ledger's without a
        /// translation table - so the classification is a function and both callers call it.
        ///
        /// The causes are not degrees of the same thing. `locked-door` is a door the mobile was
        /// never going to get through, standing where the Door rung already tried and failed.
        /// `goal-unstandable` is a tile nothing fits on and a walk that could never have finished.
        /// `arrived-no-stand-tile` is a bot INSIDE the arrival's range that still found nowhere to
        /// stand - the place, not the road. `short-of-goal` never reached the place at all, and its
        /// problem is the road behind it. Merged, they made 28 of 30 failures in one window look
        /// like one thing.
        ///
        /// `locked-door` IS A MEASUREMENT WITH A DECISION WAITING ON IT, and that is why it is
        /// here rather than folded into one of the others. The door gate this shard added to
        /// FastAStarAlgorithm (MODIFICATIONS entry 6) lets a bot's route plan through a closed
        /// door, and ServUO's AlwaysIgnoreDoors has no "unlocked only" notion - so a bot can plan
        /// through a LOCKED door and then fail at it. Upstream carries exactly this limit on their
        /// slow path (INTEGRATION-NOTES.txt:245-250) and closes it on their cache path with a
        /// second flag and a per-cell guard, which here would be a SECOND upstream edit, in
        /// FastMovement.cs:39-52. That edit was deliberately not taken; this count is what decides
        /// whether it should be. House doors need no such guard and never did: FastMovementImpl's
        /// door branch (FastMovement.cs:50-52) runs BaseHouseDoor.CheckAccess already.
        /// </summary>
        public static string CauseFor(
            Mobile mobile, Map map, Point3D goal, int arrivalRange, out bool goalUnstandable)
        {
            goalUnstandable = false;

            if (mobile == null || map == null || map == Map.Internal)
            {
                return "unknown";
            }

            // Mobiles deliberately not counted: the question is whether the TILE can take anybody,
            // not whether somebody is on it right now - that is the near list's job.
            goalUnstandable =
                !map.CanFit(goal.X, goal.Y, goal.Z, 16, false, false, true)
                && !map.CanFit(goal.X, goal.Y, map.GetAverageZ(goal.X, goal.Y), 16, false, false, true);

            // Asked before goalUnstandable, and the ordering is the point: a closed door is an
            // Impassable item, so a goal ON a door tile already reads unstandable - the false
            // positive the navigation README warns about - and that answer hides the one cause
            // anybody can act on. goalUnstandable itself is left computed either way, because the
            // caller stores it as its own fact.
            if (LockedDoorNear(mobile, map))
            {
                return "locked-door";
            }

            if (goalUnstandable)
            {
                return "goal-unstandable";
            }

            // Chebyshev, because that is the metric the walker's own arrival test uses; and at
            // least one, because a range-0 arrival is still "reached" when the bot is on the tile
            // beside it and the single last step is what failed.
            int reach = Math.Max(Math.Abs(mobile.X - goal.X), Math.Abs(mobile.Y - goal.Y));

            return reach <= Math.Max(arrivalRange, 1) ? "arrived-no-stand-tile" : "short-of-goal";
        }

        /// <summary>
        /// A closed, LOCKED door within a tile of where the mobile gave up.
        ///
        /// Range 1 and the vertical window are not chosen here - they are the Door rung's, copied
        /// from Core/DoorHelper.cs (TryOpenAdjacent's GetItemsInRange(location, 1) and
        /// WithinReach's `door.Z + height > from.Z && from.Z + 16 > door.Z`, which is the window
        /// the client's own open-door macro uses). Asking a different question from the rung that
        /// already failed would name a door that was never in the way.
        ///
        /// Only LOCKED doors count. An unlocked one the walker could not open is a different
        /// fault - LOS, reach, or somebody standing in the doorway - and filing it here would bury
        /// the number the second upstream edit is waiting on.
        /// </summary>
        private static bool LockedDoorNear(Mobile mobile, Map map)
        {
            IPooledEnumerable<Item> items = map.GetItemsInRange(mobile.Location, 1);

            try
            {
                foreach (Item item in items)
                {
                    var door = item as BaseDoor;

                    if (door == null || door.Open || !door.Locked)
                    {
                        continue;
                    }

                    if (door.Z + door.ItemData.Height > mobile.Z && mobile.Z + 16 > door.Z)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                items.Free();
            }

            return false;
        }

        /// <summary>
        /// Called by NavWalker at the top of the ladder, immediately before the teleport.
        ///
        /// Everything expensive happens here rather than on every rung, because this is rare - a
        /// few dozen an hour across a sixty-bot population - and the two range queries it makes
        /// would not be worth it on a hot path.
        /// </summary>
        public static void Record(
            Mobile mobile, Map map, Point3D goal, string hop, bool watched, int arrivalRange,
            string hopKind, bool fromStart, int index, int routeCount, int skips, Point3D start)
        {
            if (mobile == null || map == null || map == Map.Internal)
            {
                return;
            }

            var failure = new NavWalkFailure
            {
                Utc = DateTime.UtcNow,
                Who = mobile.Name ?? mobile.GetType().Name,
                WhoType = Describe(mobile),
                Hop = hop,
                MapName = map.Name,
                GoalX = goal.X,
                GoalY = goal.Y,
                GoalZ = goal.Z,
                FromX = mobile.X,
                FromY = mobile.Y,
                FromZ = mobile.Z,
                Watched = watched,
                ArrivalRange = arrivalRange,
                HopKind = hopKind,
                FromStart = fromStart,
                Index = index,
                RouteCount = routeCount,
                Skips = skips,
                StartX = start.X,
                StartY = start.Y,
                StartZ = start.Z
            };

            // Shared with [WalkAudit, so a probe's cause and a live bot's cause are the same
            // words by construction rather than by two people keeping two branches in step.
            bool unstandable;

            failure.Cause = CauseFor(mobile, map, goal, arrivalRange, out unstandable);
            failure.GoalUnstandable = unstandable;

            // Centred on the GOAL, which is the whole point. NavWalker's own DescribeBlocker falls
            // back to a list centred on the walker, and a walker that has been shuffling for two
            // minutes is not necessarily anywhere near the tile it wanted.
            IPooledEnumerable nearby = map.GetMobilesInRange(goal, 2);

            try
            {
                foreach (Mobile other in nearby)
                {
                    if (other == mobile || other.Deleted || !other.Alive)
                    {
                        continue;
                    }

                    failure.Near.Add(String.Format(
                        "{0} ({1}) at {2},{3}",
                        other.Name ?? "?",
                        Describe(other),
                        other.X,
                        other.Y));
                }
            }
            finally
            {
                // A non-generic IPooledEnumerable has to be freed or the pool leaks
                // (CLAUDE.md section 14).
                nearby.Free();
            }

            _total++;
            _failures.Add(failure);

            while (_failures.Count > MaxKept)
            {
                _failures.RemoveAt(0);
            }
        }

        /// <summary>
        /// THE FORWARD QUESTION: may this mover step onto the tile this occupant stands on?
        ///
        /// A faithful mirror of what the engine will actually answer, for the instruments - the
        /// rung log, the walk audit's BUSY and FRAGILE rows - which have to describe the rule
        /// rather than carry one of their own. Mobile.Move asks the OCCUPANT (Mobile.cs:3216,
        /// :3243), so the shape below is the engine's own dispatch: which OnMoveOver override the
        /// occupant carries, and then what that override asks about the mover.
        ///
        ///   WalkAuditProbe   - an unconditional true with no base call (NavWalkAudit.cs:362).
        ///                      Transparent to everything, which is a deliberate divergence from a
        ///                      real bot and is why it is asked about first.
        ///   PlayerBot        - BotShove first (PlayerBot.cs:777), then PlayerMobile's.
        ///   IDailyLifeActor  - BotShove first (eight overrides), then BaseCreature's.
        ///   BaseCreature     - BotShove first (BaseCreature.cs:4557, MODIFICATIONS entry 5), then
        ///                      the uncontrolled-mover refusal (:4564).
        ///   PlayerMobile     - NO BotShove at all. The uncontrolled-mover refusal (:3488), then
        ///                      base. That exclusion is the whole reason entry 5 exists.
        ///   anything else    - Mobile.OnMoveOver defers to the mover's CheckShove (:3506).
        ///
        /// WHICH IS WHY IT TAKES THE MOVER. Two of those five rows turn on the mover's class, so
        /// an answer computed from the occupant alone is wrong for one of the two movers whatever
        /// it says. It was right to omit while both movers were creatures; the class swap of 12
        /// September 2026 made them disagree.
        ///
        /// RE-DERIVED against ConsentsToBotPass, which the previous version's own comment deferred
        /// to "the vocabulary session". What that fixes is an occupant-only early return which
        /// used to stand at the top of this method - "occupant is IBotActor, return true" -
        /// answered BEFORE the mover was looked at. It said a stock uncontrolled vendor may step
        /// onto a bot, where the engine refuses it: PlayerBot.OnMoveOver reaches PlayerMobile's
        /// branch for exactly that mover. The deviation the engine is expressing there is a named
        /// one, held open on purpose - a stock vendor still jams against a bot - and it is now
        /// MODELLED rather than contradicted.
        ///
        /// WHAT IS DELIBERATELY NOT MODELLED, because a pure predicate cannot be:
        /// Mobile.m_Pushing (Mobile.cs:3180, :3529) makes the engine's answer stateful WITHIN one
        /// step - the second occupant of a single move is free whatever the rule says. Every
        /// answer here is the answer for the first occupant of a step, which is the only one a
        /// walker ever asks about.
        ///
        /// Mirrored rather than measured by calling OnMoveOver. That call is side-effect-free for
        /// a bot mover, but for a player mover on a facet without FreeMovement it deducts ten
        /// stamina (Mobile.cs:3544) - and an instrument that cost a player stamina to draw a log
        /// line would be a genuinely nasty bug. BotShoveProbe (reported as Bots.Shove) asserts
        /// this against the real OnMoveOver for every ordered pair on both facets, which is where
        /// the two are held together.
        /// </summary>
        public static bool MayBotPass(Mobile mover, Mobile occupant)
        {
            if (mover == null)
            {
                return true;
            }

            // Mobile.OnMoveOver's own first branch (Mobile.cs:3508): a mobile that is gone or
            // off-map blocks nothing. Every override in the table above reaches it through base.
            if (occupant == null || occupant.Deleted || occupant.Map == null)
            {
                return true;
            }

            // THE PROBE IS TRANSPARENT TO EVERYBODY, and is asked about before anything else
            // because it is an IBotMover as well and a later branch would answer it for the wrong
            // reason. NavWalkAudit.cs:362 is an unconditional true with no base call.
            if (occupant is IBotMover && !(occupant is IBotActor))
            {
                return true;
            }

            // A BOT MOVER IS NEVER REFUSED BY ANY OCCUPANT, and that is one fact rather than five
            // coincidences. Both refusals the engine has - PlayerMobile.cs:3488 and
            // BaseCreature.cs:4564 - are keyed on the MOVER being an uncontrolled BaseCreature,
            // which a PlayerMobile bot is not. So every occupant falls through to the mover's own
            // CheckShove, which PlayerBot answers with an unconditional true (PlayerBot.cs:765).
            //
            // A BOT PASSES A REAL PLAYER. Sean's decision, 12 September 2026, to match upstream,
            // whose PlayerBot.CheckShove is the same unconditional true (uo-offline
            // CustomBots/PlayerBot.cs:595) for the reason their comment gives: the engine's
            // full-stamina rule jammed their bank plazas. The deviation that spends was deliberate
            // and documented; this line is the diagnostic agreeing with the engine rather than a
            // rule of its own.
            //
            // AND NOTE WHAT IT DOES NOT REST ON: the FreeMovement short-circuit. On Trammel
            // Mobile.CheckShove returns true for every mover before reading anything at all
            // (Mobile.cs:3518, Map.cs:128), so the bot override is not what carries this cell
            // there - it is what carries it on Felucca. The answer is the same either way, and
            // Bots.Shove asks both facets so the override cannot be deleted unnoticed.
            if (mover is IBotActor)
            {
                return true;
            }

            // FROM HERE THE MOVER IS NOT A BOT: the walk audit's BaseCreature probe, a real
            // player, a daily-life actor, a stock creature, or a controlled pet.

            // Every occupant that consults BotShove answers an IBotMover mover with that mover's
            // own CheckShove, which both implementers make true (BotShove.cs:100). A real
            // PlayerMobile is the one occupant that does not consult it, so it is excluded here
            // and falls to the refusal below - which is exactly the asymmetry MODIFICATIONS entry
            // 5 was written to close for stock creatures and deliberately did not close for
            // players.
            if (mover is IBotMover && !IsRealPlayer(occupant))
            {
                return true;
            }

            // A BOT OCCUPANT ALSO CONSENTS TO THE TOWN'S OWN TRAFFIC - BotShove's second branch
            // (BotShove.cs:123-126), which is ConsentsToBotPass and is narrower than this question
            // by design. Asked after the branch above, which already covers the overlap between
            // the two.
            if (occupant is IBotActor && ConsentsToBotPass(mover))
            {
                return true;
            }

            // WHAT IS LEFT IS THE UNCONTROLLED-CREATURE REFUSAL, which PlayerMobile.OnMoveOver
            // (:3488-3490) and BaseCreature.OnMoveOver (:4564-4567) carry in identical words, and
            // which every occupant still standing here carries one of.
            //
            // RESTATED WHOLE, including the two MOVER-side clauses the previous version omitted
            // and whose own comment admitted to omitting: a dead mover, or a dead bonded pet,
            // passes a live player. Unreachable for a live walker, and the point of writing it out
            // is that a cell nobody can reach is still a cell that must not be wrong.
            if (IsUncontrolledCreature(mover))
            {
                return (!occupant.Alive || !mover.Alive
                        || occupant.IsDeadBondedPet || mover.IsDeadBondedPet)
                    || (occupant.Hidden && occupant.IsStaff());
            }

            // Mobile.OnMoveOver (:3513) hands the question to the mover's CheckShove.
            return MoverShoves(mover, occupant);
        }

        /// <summary>
        /// A real person's character, connected or not - never a bot, and never the walk probe.
        ///
        /// The one occupant class whose OnMoveOver does not consult BotShove, because
        /// PlayerMobile.cs is an upstream file and is left alone. A LINK-DEAD PLAYER IS A PLAYER:
        /// the test is the type and nothing else, for the reason Describe records - the Player
        /// flag cannot tell a bot from a person, and a null NetState cannot either.
        /// </summary>
        private static bool IsRealPlayer(Mobile mobile)
        {
            return mobile is PlayerMobile && !(mobile is IBotActor);
        }

        /// <summary>
        /// The engine's own uncontrolled-creature test, in one place.
        ///
        /// THE SECOND BaseCreature TYPE TEST IN THIS FILE, and it is deliberate rather than
        /// creeping: Nav.Actor's ledger (NavActorCheck.cs:253-261) allowed this file exactly one,
        /// spent by Describe's animal-and-monster branch, and that ledger's own comment calls
        /// itself "a LEDGER, not a ban - what stops the two becoming three". This is the two, and
        /// the reason is that the condition being mirrored is literally this. Writing it as
        /// anything else would be a paraphrase, and paraphrasing this exact condition is the fault
        /// the whole re-derivation exists to clear up.
        /// </summary>
        private static bool IsUncontrolledCreature(Mobile mobile)
        {
            var creature = mobile as BaseCreature;

            return creature != null && !creature.Controlled;
        }

        /// <summary>
        /// The mover's CheckShove, mirrored: Mobile.cs:3516-3558.
        ///
        /// THE WHOLE GUARDED BODY IS SKIPPED ON A FreeMovement FACET, and that is not a detail.
        /// MapRules.TrammelRules includes FreeMovement (Map.cs:128) and MapDefinitions.cs:27-32
        /// gives TrammelRules to Trammel, Ilshenar, Malas, Tokuno and TerMur - so on this shard's
        /// own facet every mover's CheckShove is an unconditional true and the full-stamina rule
        /// never runs at all. Only Felucca pays it, and Mobile.cs:3518 is the only reader of that
        /// flag in the whole Server tree.
        ///
        /// Which is why the sentence this layer's documentation carried for a year - "a real
        /// player shoving a bot still pays the engine's full-stamina rule, full stamina, minus
        /// ten" - was true of Felucca and false of the facet the bots actually live on.
        ///
        /// NOT MODELLED, deliberately: m_Pushing (:3529), which makes the second occupant of a
        /// single step free; and PlayerMobile.CheckShove's WraithForm branch
        /// (PlayerMobile.cs:3498), which turns on a spell state rather than on a class. Both are
        /// named in the ordered-pair table beside the cells they touch.
        /// </summary>
        private static bool MoverShoves(Mobile mover, Mobile occupant)
        {
            if (mover.Map == null || mover.IgnoreMobiles
                || (mover.Map.Rules & MapRules.FreeMovement) != 0)
            {
                return true;
            }

            if (!occupant.Alive || !mover.Alive
                || occupant.IsDeadBondedPet || mover.IsDeadBondedPet)
            {
                return true;
            }

            if (occupant.Hidden && occupant.IsStaff())
            {
                return true;
            }

            if (mover.IsStaff())
            {
                return true;
            }

            return mover.Stam == mover.StamMax;
        }

        /// <summary>
        /// THE REVERSE QUESTION: may this mover step onto a bot's tile?
        ///
        /// Deliberately narrower than MayBotPass. A bot lets through another bot, the walk audit's
        /// probe, and a daily-life actor - which is the town's own traffic, and is exactly as wide
        /// as it needs to be to stop a shopkeeper freezing in a doorway a bot is standing in. A
        /// stock vendor still jams against a bot, and that asymmetry is a named deviation with its
        /// own row in the Bots README, held open on purpose: widening it here would settle that
        /// question sideways and go further than uo-offline, whose PlayerBot is a PlayerMobile and
        /// blocks uncontrolled creatures outright.
        /// </summary>
        public static bool ConsentsToBotPass(Mobile mover)
        {
            if (mover == null || mover.Deleted)
            {
                return false;
            }

            return mover is IBotMover || mover is IDailyLifeActor;
        }

        /// <summary>
        /// What KIND of thing this is, in the terms the shove rules are written in.
        ///
        /// EVERY TEST HERE IS A TYPE, IN ORDER, and the order is the whole of it. This used to ask
        /// `mobile.Player` first and split it on the NetState - because a PlayerBot sets
        /// Player = true to get past the party gate, so the flag alone cannot tell a bot from a
        /// person. But a person's NetState is null the instant they link-dead, and a disconnected
        /// player was therefore filed as a PlayerBot: as something a bot may walk through, about
        /// the one occupant in the world that refuses (REVIEW.md section 4). A disconnected
        /// PlayerMobile is still a PlayerMobile, and asking the type says so.
        ///
        /// IBotActor first: it is the bot's own interface and PlayerBot is its only implementer,
        /// so it names a bot without Core reaching for the concrete class - the same way this file
        /// has always tested IDailyLifeActor. IBotMover next, which after that leaves only the
        /// walk audit's probe; it is asked before the body test because the probe's body is 0x190,
        /// neither IsAnimal nor IsMonster, so it used to fall through to "npc" - the one bucket
        /// meaning "a bot cannot push this", about the instrument standing in for a bot.
        ///
        /// IDailyLifeActor before the vendor and animal buckets, because it once was not: the test
        /// here was for IBotActor, which no daily-life actor implements, so every townsfolk, patron
        /// and GG vendor came back as "vendor" or "npc". That inflated the very bucket that argues
        /// for editing upstream files - the expensive answer looked better supported than it was.
        ///
        /// The word this returns no longer decides anything: MayBotPass and ConsentsToBotPass are
        /// the rules, and this is a label for a human reading a log line. That separation is the
        /// point of the split - a bucket name and a movement rule drifted apart once already.
        /// </summary>
        public static string Describe(Mobile mobile)
        {
            if (mobile is IBotActor)
            {
                return "PlayerBot";
            }

            if (mobile is IBotMover)
            {
                return "walk-probe";
            }

            if (mobile is IDailyLifeActor)
            {
                return "daily-life";
            }

            if (mobile is PlayerMobile || mobile.Player)
            {
                // Connected or not, staff or not. A link-dead player is a player.
                return "player";
            }

            var creature = mobile as BaseCreature;

            if (creature == null)
            {
                return mobile.GetType().Name;
            }

            if (creature.Body.IsAnimal || creature.Body.IsMonster)
            {
                return "animal";
            }

            return creature is BaseVendor ? "vendor" : "npc";
        }

        /// <summary>The Data/Live snapshot, written on request rather than on a timer.</summary>
        public static string BuildJson()
        {
            var builder = new System.Text.StringBuilder(4096);

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"total\": ").Append(_total).Append(",\n");
            builder.Append("  \"kept\": ").Append(_failures.Count).Append(",\n");

            // The denominator, so a reader of this file can compute the rate without knowing how
            // long the window was or how many bots were on. See WalksStarted.
            builder.Append("  \"walksStarted\": ").Append(_started).Append(",\n");
            builder.Append("  \"walksCompleted\": ").Append(_completed).Append(",\n");
            builder.Append("  \"per100Walks\": ")
                .Append(PerHundredWalks(_total).ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append(",\n");
            builder.Append("  \"failures\": [\n");

            for (int i = 0; i < _failures.Count; i++)
            {
                NavWalkFailure failure = _failures[i];

                builder.Append("    {\"utc\":").Append(Json.Quote(failure.Utc.ToString("o")));
                builder.Append(",\"who\":").Append(Json.Quote(failure.Who));
                builder.Append(",\"whoType\":").Append(Json.Quote(failure.WhoType));
                builder.Append(",\"hop\":").Append(Json.Quote(failure.Hop));
                builder.Append(",\"map\":").Append(Json.Quote(failure.MapName));
                builder.Append(",\"x\":").Append(failure.GoalX);
                builder.Append(",\"y\":").Append(failure.GoalY);
                builder.Append(",\"z\":").Append(failure.GoalZ);
                builder.Append(",\"fromX\":").Append(failure.FromX);
                builder.Append(",\"fromY\":").Append(failure.FromY);
                builder.Append(",\"fromZ\":").Append(failure.FromZ);
                builder.Append(",\"watched\":").Append(failure.Watched ? "true" : "false");
                builder.Append(",\"goalUnstandable\":").Append(failure.GoalUnstandable ? "true" : "false");
                builder.Append(",\"cause\":").Append(Json.Quote(failure.Cause));
                builder.Append(",\"hopKind\":").Append(Json.Quote(failure.HopKind));
                builder.Append(",\"fromStart\":").Append(failure.FromStart ? "true" : "false");
                builder.Append(",\"range\":").Append(failure.ArrivalRange);
                builder.Append(",\"index\":").Append(failure.Index);
                builder.Append(",\"routeCount\":").Append(failure.RouteCount);
                builder.Append(",\"skips\":").Append(failure.Skips);
                builder.Append(",\"startX\":").Append(failure.StartX);
                builder.Append(",\"startY\":").Append(failure.StartY);
                builder.Append(",\"startZ\":").Append(failure.StartZ);
                builder.Append(",\"near\":[");

                for (int n = 0; n < failure.Near.Count; n++)
                {
                    if (n > 0)
                    {
                        builder.Append(",");
                    }

                    builder.Append(Json.Quote(failure.Near[n]));
                }

                builder.Append("]}");

                if (i < _failures.Count - 1)
                {
                    builder.Append(",");
                }

                builder.Append("\n");
            }

            builder.Append("  ]\n}\n");

            return builder.ToString();
        }

        public static bool TryWrite(out string message)
        {
            string error;

            if (!AtomicFile.Write("Data/Live/walk-failures.json", BuildJson(), out error))
            {
                message = error;
                Log.Error("Could not write walk-failures.json: {0}", error);
                return false;
            }

            message = String.Format(
                "{0} walk failure(s) since boot, {1} kept; {2} walk(s) started, {3} completed{4}",
                _total,
                _failures.Count,
                _started,
                _completed,
                _started > 0
                    ? String.Format(" ({0:F2} failures per 100 walks)", PerHundredWalks(_total))
                    : "");

            return true;
        }
    }
}
