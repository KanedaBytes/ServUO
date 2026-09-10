using System;
using System.Collections.Generic;

using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom
{
    /// <summary>
    /// The stuck-recovery ladder, in the order it is climbed. Each rung is tried once per hop,
    /// and the walker drops back to the bottom the moment the hop makes real progress.
    ///
    /// Translated from uo-offline-server's leg recovery, which escalates repath, then
    /// nudge-and-repath, then extract. The middle rungs here are ours: their bots open doors
    /// inside Move itself (every step, for free) because a PlayerBot overrides Move; a
    /// BaseCreature does not, so the door attempt has to be a deliberate rung. And they abandon
    /// the destination where we skip a single waypoint, because a NavWalker does not own the
    /// journey - the caller does.
    /// </summary>
    public enum StuckRung
    {
        /// <summary>Nothing wrong yet.</summary>
        None = 0,

        /// <summary>Throw away the cached path and let A* try again from here.</summary>
        Repath = 1,

        /// <summary>Step off the tile, then repath. Breaks a wedge against scenery or a crowd.</summary>
        Sidestep = 2,

        /// <summary>Open the closed door that is in the way, the way a player's client would.</summary>
        Door = 3,

        /// <summary>Give up on this waypoint and aim at the next one in the route.</summary>
        SkipWaypoint = 4,

        /// <summary>Move the mobile. Only with nobody watching, unless the bound is spent.</summary>
        Teleport = 5,
    }

    /// <summary>
    /// Walks a mobile along a NavRoute, one hop at a time.
    ///
    /// Shared by daily life and, later, the bot layer, because the hop-failure policy is the
    /// part that has to be right and there should be exactly one of it.
    ///
    /// Two ServUO facts shape this class:
    ///
    /// 1. BaseAI.MoveTo compares its cached path's goal by REFERENCE
    ///    (Scripts/Mobiles/AI/BaseAI.cs:2644, "m_Path.Goal == p"). Point3D is a struct, so
    ///    passing one boxes a fresh object every call and rebuilds the PathFollower every tick,
    ///    destroying path persistence. The goal is therefore a NavGoal - a class - held for the
    ///    length of the hop. Replacing that instance is also how a repath is forced.
    ///
    /// 2. BaseCreature.PlayerRangeSensitive stops a creature's AI timer entirely when no player
    ///    is in its sector, so a walker driven from its own OnThink would freeze the moment
    ///    nobody was watching - and would never reach the code that decides what to do about
    ///    being stuck. One shared timer drives every walker instead.
    /// </summary>
    public class NavWalker
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>
        /// How often walkers are driven. A ceiling, not the movement rate: BaseAI.DoMoveImpl has
        /// its own NextMove gate derived from the creature's speed, so driving faster than the
        /// creature can step costs nothing but makes movement smooth.
        ///
        /// It WAS the movement rate, though, and quietly. At 250ms nothing could step faster than
        /// four times a second, so a bot set to a player's run - 200ms on foot, 100ms mounted -
        /// was held to 250ms and ran at less than half the speed it had been told to. A ceiling
        /// only costs nothing while it is above everything underneath it.
        ///
        /// And 100ms was not above everything either. The engine's fastest step is 100ms
        /// (Mobile.RunMount), and a 100ms timer measured 109ms between ticks - the timer thread
        /// re-anchors on the time it observed, so a repeating timer only ever runs late - which
        /// held a mounted run to 109ms a step ([BotPace, walk probe, after the commute fix). Half
        /// the step is the smallest interval that cannot be the limiter; the ticks in between are
        /// turned away at the NextMove gate for the cost of one subtraction.
        /// </summary>
        public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.05);

        /// <summary>
        /// A pace sampler, while [BotPace has one attached. Read from Tick only; null otherwise.
        /// Kept across Stop on purpose: a route that ends mid-sample leaves a report worth reading.
        /// </summary>
        public NavPaceSampler Sampler { get; set; }

        /// <summary>How long one hop may take before it counts as failed.</summary>
        private static readonly TimeSpan HopTimeout = TimeSpan.FromSeconds(20.0);

        /// <summary>A player this close makes a teleport visible, so it is the last resort.</summary>
        public const int PlayerNearTiles = 20;

        /// <summary>
        /// How many times the recoverable rungs are cycled while a player is watching, before
        /// the walker gives up and teleports in view anyway.
        ///
        /// The bound is the point. Holding position until the player leaves is unbounded, and an
        /// NPC frozen against a wall for ten minutes is a worse thing to watch than one that
        /// steps around a corner. At one cycle per HopTimeout this is about two minutes of
        /// genuine attempts, which is long enough that the in-view teleport is rare.
        /// </summary>
        public const int WatchedCycles = 6;

        /// <summary>Tiles a sidestep will try to travel to break a wedge.</summary>
        private const int SidestepTiles = 2;

        /// <summary>Size of a rung-count array: every StuckRung value including None.</summary>
        public const int RungCount = (int)StuckRung.Teleport + 1;

        /// <summary>
        /// Fleet-wide rung counts since the graph was last loaded.
        ///
        /// Reset by NavigationSystem on reload, so the health line always describes the graph
        /// currently in memory rather than accumulating across an edit that was meant to fix the
        /// very edges being counted.
        /// </summary>
        private static readonly int[] _rungTotals = new int[RungCount];

        /// <summary>Arrival tolerance when a waypoint does not override it.</summary>
        public const int DefaultArrivalRange = 2;

        private static readonly List<NavWalker> _active = new List<NavWalker>();
        private static Timer _timer;

        private readonly BaseCreature _mobile;

        private NavRoute _route;
        private int _index;
        private NavGoal _goal;
        private long _hopDeadline;

        private StuckRung _rung;
        private int _watchedCycles;

        /// <summary>
        /// How many times this walker has entered each rung, since it was constructed.
        ///
        /// Per-walker rather than only global, so a probe can attribute recovery to its own bots
        /// instead of counting whatever else happened to be walking at the time. Indexed by
        /// StuckRung, so index 0 (None) is always zero and exists only to keep the arithmetic
        /// obvious.
        /// </summary>
        private readonly int[] _rungsFired = new int[RungCount];

        /// <summary>
        /// The closest this hop has ever got to its goal. Adopted from uo-offline-server, whose
        /// comment is the reason: a mobile pinned against a lightpost jiggles, so "did it move?"
        /// reports progress that is not progress. "Did it get closer than ever?" does not.
        /// </summary>
        private int _bestDistance;

        /// <summary>
        /// Whether this hop has already re-aimed at a free tile inside its arrival's range.
        ///
        /// Once, deliberately. Two bots each shifting onto the tile the other just left would
        /// trade places for ever, and if the second tile is taken as well then the place really is
        /// full and the ladder is the right answer. See TryShiftWithinArrival.
        /// </summary>
        private bool _shifted;

        /// <summary>
        /// How far from the arrival the proactive check last ran, win or lose.
        ///
        /// Separate from _shifted, which records only a SUCCESSFUL retarget: a sweep that
        /// found nothing must not repeat four times a second, because it costs a
        /// MovementPath and the answer will not have changed in 250ms.
        ///
        /// A DISTANCE RATHER THAN A FLAG, because one answer per hop is one answer too few.
        /// The pathfinder is greedy with a 300-expansion budget, so "can I reach that tile"
        /// is not one question with one answer - it is a different question at eight tiles
        /// and at three, and the whole reason MaxApproachDistance exists is that the far
        /// answer is the unreliable one. A single check at the moment the bot came inside
        /// the cap therefore spent itself on the least trustworthy reading available.
        ///
        /// So it re-asks each time the bot has HALVED its distance to the goal: about three
        /// times on a hop that starts at the cap, at 8, 4 and 2, which is cheap and is where
        /// the answers actually differ.
        /// </summary>
        private int _arrivalCheckedAt = Int32.MaxValue;

        /// <summary>
        /// A NAMED PORT of uo-offline's MaxApproachDistance (TravelerBehavior's approach cap,
        /// lowered from 50 to 36 there so a leg stays inside FastAStar's 38-tile box).
        ///
        /// EIGHT, not 36, and the number is measured rather than copied. Their cap is sized to the
        /// pathfinder's BOX; ours is sized to its BUDGET, which is the constraint that actually
        /// binds here. FastAStar is greedy best-first with a squared heuristic against a linear
        /// cost and stops after 300 expansions, so an uphill approach into a constriction fails
        /// well inside the box: walking to the Trinsic alchemist arrival, 9 tiles found a path and
        /// 10 did not, while the same pair reversed worked at 13. See the Navigation README.
        ///
        /// What it is for: the recovery ladder's own nudges walk a bot AWAY from its goal, and past
        /// this range the goal stops being pathable at all - so the ladder that exists to rescue
        /// the hop is what guarantees it fails. Measured over one 30-minute window, every terminal
        /// failure at 9 tiles or more but one had an unreachable goal, 12 of 30 in total.
        /// </summary>
        public const int MaxApproachDistance = 8;

        /// <summary>Where to walk back to before re-aiming at the goal, when one is set.</summary>
        private Point3D _approach;

        private bool _approaching;

        /// <summary>Once per hop, so a bot cannot bounce between the waypoint and the goal.</summary>
        private bool _reAnchored;

        /// <summary>Where the mobile stood when Follow was called. See Follow.</summary>
        private Point3D _startedAt;

        /// <summary>How many waypoints this route has skipped rather than walked. See Follow.</summary>
        private int _skips;

        public NavWalker(BaseCreature mobile)
        {
            _mobile = mobile;
        }

        public BaseCreature Mobile
        {
            get { return _mobile; }
        }

        public NavRoute Route
        {
            get { return _route; }
        }

        public int StepIndex
        {
            get { return _index; }
        }

        public bool Active
        {
            get { return _route != null; }
        }

        /// <summary>Raised on the game thread when the last step is reached.</summary>
        public Action<NavWalker> Arrived;

        /// <summary>
        /// Raised on the game thread each time a stuck-recovery rung is entered, with the reason
        /// this walker just logged.
        ///
        /// An OPTIONAL seam, in the same shape as Arrived, so a consumer can record recovery in
        /// its own diagnostics without this file learning what a consumer is. Navigation is Core
        /// and knows nothing about bots; the bots' event log subscribes to this rather than the
        /// other way round, exactly as NavigationSystem.RegisterAuditor inverts the same
        /// dependency for the data checks.
        /// </summary>
        public Action<NavWalker, StuckRung, string> RungFired;

        /// <summary>
        /// Whether MoveTo is asked to OR the running bit into every direction. Nothing sets it,
        /// and nothing should: BaseAI.DoMoveImpl sets Direction.Running itself whenever the step
        /// delay is under WalkFoot or WalkMount, so the pace decides the animation and the two
        /// cannot disagree. Passing true with a 400ms delay would send a running direction on
        /// walk timing, which is the wrong way round.
        /// </summary>
        public bool Run { get; set; }

        /// <summary>
        /// Moves the mobile's Home to each hop as it is taken.
        ///
        /// For an actor whose AI we do not own, BaseAI.WalkRandomInHome (BaseAI.cs:2509) keeps
        /// stepping it back toward Home with no spawner gate, dragging against the route.
        /// Pointing Home at where we are going anyway makes the wander pull the same way instead
        /// of backwards. Restore Home yourself when the journey ends.
        /// </summary>
        public bool KeepHomeAligned { get; set; }

        // ---- driving ----

        public void Follow(NavRoute route)
        {
            Stop();

            if (route == null || route.Count == 0 || _mobile == null || _mobile.Deleted)
            {
                return;
            }

            // ForceStayHome refuses to path outside Home +/- min(10, RangeHome) 97.5% of the
            // time (BaseAI.cs:2628-2636), which silently pins a walker to its spawn point.
            if (_mobile.ForceStayHome)
            {
                Log.Warn(
                    "{0} has ForceStayHome set; its route will not be walked past its home range.",
                    _mobile.Name ?? _mobile.GetType().Name);
            }

            _route = route;
            _index = 0;
            _goal = null;

            // Where this walk began, and how many waypoints it has skipped rather than walked.
            //
            // PER ROUTE, NOT PER HOP, which is the whole reason they are set here and not in
            // ResetHop: a skip calls ResetHop, so anything reset there cannot count skips. A
            // terminal failure reports the distance from the mobile to the step it was on, and on
            // a ten-tile edge that number has come back as 33 - which is either the mobile having
            // travelled or the index having marched without it. These two say which.
            _startedAt = _mobile.Location;
            _skips = 0;

            ResetHop();
            Register(this);
        }

        public void Stop()
        {
            _route = null;
            _goal = null;
            _index = 0;
            _rung = StuckRung.None;
            _watchedCycles = 0;
            _frozenAnchor = _mobile.Location;
            _frozenAt = Core.TickCount;

            Unregister(this);
        }

        /// <summary>
        /// The rung this walker is currently on, or None when the hop is going fine.
        ///
        /// This IS the walker's own definition of stuck: anything above None means the hop timed
        /// out without getting closer and recovery is in progress. A consumer asking "is this
        /// mobile stuck?" should read this rather than guess from position, which cannot tell a
        /// long route from a wedged one.
        /// </summary>
        public StuckRung CurrentRung
        {
            get { return _rung; }
        }

        /// <summary>How many times this walker has climbed to the given rung.</summary>
        public int RungsFired(StuckRung rung)
        {
            int index = (int)rung;

            return index >= 0 && index < RungCount ? _rungsFired[index] : 0;
        }

        /// <summary>Every rung this walker has climbed, ignoring None.</summary>
        public int TotalRungsFired
        {
            get
            {
                int total = 0;

                for (int i = (int)StuckRung.Repath; i < RungCount; i++)
                {
                    total += _rungsFired[i];
                }

                return total;
            }
        }

        /// <summary>Fleet-wide count for a rung since the graph was last loaded.</summary>
        public static int RungTotal(StuckRung rung)
        {
            int index = (int)rung;

            return index >= 0 && index < RungCount ? _rungTotals[index] : 0;
        }

        /// <summary>
        /// Arrival retargets since the graph was loaded, split by what was wrong with the tile.
        ///
        /// Counted rather than only logged because the retarget is a RECOVERY FROM BAD DATA, not a
        /// routine adjustment, and the split says which bad data. A crowded arrival is the world
        /// being busy; a tile nothing can stand on, or one the engine will not path to, is a
        /// record somebody should move - so a run where the second two columns are non-zero hands
        /// you a list of destinations to look at, and a run where they fall to zero after a data
        /// fix is the proof the fix worked.
        /// </summary>
        private static int _retargetOccupied;

        private static int _retargetUnstandable;

        private static int _retargetUnreachable;

        /// <summary>
        /// "repath 12, sidestep 3, door 0, skip 1, teleport 0" - the fleet-wide recovery line.
        /// </summary>
        public static string DescribeRungTotals()
        {
            return String.Format(
                "repath {0}, sidestep {1}, door {2}, skip {3}, teleport {4}",
                _rungTotals[(int)StuckRung.Repath],
                _rungTotals[(int)StuckRung.Sidestep],
                _rungTotals[(int)StuckRung.Door],
                _rungTotals[(int)StuckRung.SkipWaypoint],
                _rungTotals[(int)StuckRung.Teleport]);
        }

        /// <summary>"taken 4, unstandable 1, unreachable 7" - the fleet-wide arrival-retarget line.</summary>
        public static string DescribeArrivalRetargets()
        {
            return String.Format(
                "taken {0}, unstandable {1}, unreachable {2}",
                _retargetOccupied,
                _retargetUnstandable,
                _retargetUnreachable);
        }

        /// <summary>
        /// Clear the fleet-wide counts. Called when the navigation graph is reloaded: the counts
        /// describe a graph, and after an edit they would otherwise describe two.
        ///
        /// Per-walker counts are deliberately NOT cleared - a walker outlives a reload, and its
        /// own history is still its own.
        /// </summary>
        public static void ResetRungTotals()
        {
            for (int i = 0; i < RungCount; i++)
            {
                _rungTotals[i] = 0;
            }

            // The retarget counts describe a graph too - an arrival that was unreachable
            // before an edit has no business being counted against the graph after it.
            _retargetOccupied = 0;
            _retargetUnstandable = 0;
            _retargetUnreachable = 0;

            _nextStepBlocked = 0;
            _nextStepUnshovable = 0;
        }

        private void Tick()
        {
            if (_mobile == null || _mobile.Deleted || _mobile.Map == null || _mobile.Map == Map.Internal)
            {
                Stop();
                return;
            }

            if (_route == null)
            {
                Unregister(this);
                return;
            }

            NavPaceSampler sampler = Sampler;

            if (sampler != null)
            {
                sampler.TickSeen();
            }

            if (_index >= _route.Count)
            {
                Finish();
                return;
            }

            NavStep step = _route.Steps[_index];

            if (step.Kind == NavStepKind.Transition)
            {
                OnTransition(step);
                Advance();
                return;
            }

            int range = ArrivalRangeFor(step);

            // Every step already satisfied is passed in THIS tick, and the move that follows is
            // asked for in this tick too. Returning after one Advance cost a whole tick with no
            // step at every hop boundary - a hitch every few tiles on a road cut into short hops,
            // for no reason the engine imposed. Bounded by the route: Advance always moves the
            // index on, and Finish clears the route.
            while (_mobile.InRange(step.Point, range))
            {
                Advance();

                if (_route == null || _index >= _route.Count)
                {
                    return;
                }

                step = _route.Steps[_index];

                if (step.Kind == NavStepKind.Transition)
                {
                    OnTransition(step);
                    Advance();
                    return;
                }

                range = ArrivalRangeFor(step);
            }

            // THE LAST HOP AIMS AT A TILE IT CAN REACH, and it settles that BEFORE the ladder
            // rather than after eighty seconds of it.
            //
            // An arrival is not a waypoint. A waypoint was authored against the engine and audited
            // both ways; an arrival is a picked point plus a scatter, validated for standing and
            // never for reachability - so the last hop is the one hop whose goal nobody has ever
            // asked the pathfinder about. In one 30-minute window that was 7 of 17 terminal
            // failures, every one of them a bot three tiles from a range-2 arrival with a clear
            // road behind it and a wall in front.
            //
            // uo-offline does not need this because their final leg never aims at the arrival
            // coord at all: it targets the approach WAYPOINT and completes at
            // FinalLegArrivalRange 8 (TravelerBehavior.cs:70-77, :1942-1963), with the last few
            // tiles a bounded cosmetic drift that is allowed to fail. We cannot take their 8 -
            // our arrival ranges are load-bearing, a forge stand tile has to be within 2 of both
            // anvil and forge or DefBlacksmithy.CanCraft refuses - so we keep the authored range
            // and buy reachability with a sweep instead.
            //
            // ONCE PER HOP, and only from inside the approach cap: see ArrivalTileFault for why
            // the pathfinder's answer is worthless further out than that.
            if (step.Kind == NavStepKind.Arrival)
            {
                int reach = Chebyshev(_mobile.Location, step.Point);

                if (reach <= MaxApproachDistance && reach * 2 <= _arrivalCheckedAt)
                {
                    _arrivalCheckedAt = reach;

                    if (TryShiftWithinArrival(step))
                    {
                        return;
                    }
                }
            }

            // The frozen watchdog sits above the ladder. A mobile that has not moved two tiles
            // in a minute is rooted whatever the rungs say, and the rungs can say quite a lot:
            // a SkipWaypoint that "succeeds" aims at the next waypoint, which resets the ladder,
            // so a bot that cannot take a single step walks the whole route with its index and
            // never reaches the top rung. A miner rooted at the mine face did that for five
            // minutes. This is uo-offline's CheckFrozenWatchdog (TravelerBehavior.cs:2963-3033,
            // FrozenLimit 60 s, FrozenMoveTiles 2), with the same conclusion: the spot itself is
            // the problem, so go straight to the rescue.
            if (CheckFrozen(step))
            {
                return;
            }

            // Real progress resets the ladder, and "closer than ever" is the test rather than
            // "moved at all" - a mobile shuffling around a lightpost does the second all day.
            int distance = Chebyshev(_mobile.Location, step.Point);

            if (distance < _bestDistance)
            {
                _bestDistance = distance;

                if (_rung != StuckRung.None)
                {
                    Log.Debug(
                        "{0} is moving again on {1}; recovery reset.",
                        Who(),
                        DescribeHop(step));
                }

                _rung = StuckRung.None;
                _watchedCycles = 0;

                ResetHopDeadline();
            }

            // Wraparound-safe: compare by subtraction, never a < b.
            if (Core.TickCount - _hopDeadline >= 0)
            {
                HandleStuck(step);
                return;
            }

            EnsureGoal(step);

            BaseAI ai = _mobile.AIObject;

            if (ai == null)
            {
                return;
            }

            // Only ask for a step when the creature is due one. MoveTo runs a MovementPath, which
            // is the expensive half of this tick, and driving at 100ms rather than 250ms would
            // otherwise run it two and a half times as often for every walker on the shard. The
            // creature's own NextMove is exactly the right clock: it is what DoMoveImpl would
            // check anyway, one subtraction earlier.
            //
            // Wraparound-safe: compare by subtraction, never a < b.
            if (Core.TickCount - ai.NextMove < 0)
            {
                if (sampler != null)
                {
                    sampler.Gated();
                }

                return;
            }

            if (sampler == null)
            {
                ai.MoveTo(_goal, Run, range);
                return;
            }

            // Measured, not inferred: did the mobile move, does its direction carry the running
            // bit afterwards, and which delay did the engine step on - CurrentSpeed (the pace it
            // was given) or TransformMoveDelay(CurrentSpeed) (what DoMoveImpl advances NextMove by).
            Point3D before = _mobile.Location;

            ai.MoveTo(_goal, Run, range);

            sampler.Attempted(
                _mobile.Location != before,
                (_mobile.Direction & Direction.Running) != 0,
                ai.NextMove - Core.TickCount,
                (int)Math.Round(ai.TransformMoveDelay(_mobile.CurrentSpeed) * 1000.0),
                (int)Math.Round(_mobile.CurrentSpeed * 1000.0));
        }

        private void Advance()
        {
            _index++;
            _goal = null;

            ResetHop();

            if (_index >= _route.Count)
            {
                Finish();
            }
        }

        private void Finish()
        {
            Action<NavWalker> arrived = Arrived;

            Stop();

            if (arrived != null)
            {
                arrived(this);
            }
        }

        /// <summary>
        /// The hop failed. Climb one rung of the recovery ladder.
        ///
        /// Every rung is a thing a person would actually try, in the order a person would try
        /// them, and each is attempted once before the next. The walker drops back to the bottom
        /// as soon as the hop makes real progress, so an obstruction that clears on its own costs
        /// one rung rather than the whole ladder.
        ///
        /// The teleport at the top is the only rung that looks wrong to a player, so it waits
        /// until nobody is watching. What it does NOT do any more is wait for ever: an NPC frozen
        /// against a wall until the player wanders off is a worse thing to watch than one that
        /// steps around a corner. While watched the recoverable rungs are cycled up to
        /// WatchedCycles times, and only then does it move in view.
        /// </summary>
        /// <summary>
        /// How long a mobile may stay within FrozenMoveTiles of one spot before the watchdog
        /// acts. uo-offline's is 60 s (TravelerBehavior.cs:2943), and 60 s is longer than their
        /// whole ladder, whose rungs are seconds apart. Ours are HopTimeout apart, so one pass
        /// through Repath, Sidestep, Door and Skip is four hop timeouts, and a window shorter than
        /// that pre-empts a Skip that would have worked - it did, three times in one life probe.
        /// Six hop timeouts: one full pass, the Skip's own hop, and one more, so the watchdog only
        /// ever fires on a bot the ladder has already had its whole turn with.
        /// </summary>
        private static readonly TimeSpan FrozenLimit =
            TimeSpan.FromMilliseconds(HopTimeout.TotalMilliseconds * 6.0);

        private const int FrozenMoveTiles = 2;

        private Point3D _frozenAnchor;
        private long _frozenAt;

        /// <summary>
        /// Rooted for FrozenLimit within FrozenMoveTiles of one spot? Then skip the rungs that
        /// have already had their minute and go to the top of the ladder, which keeps its own
        /// rule about players watching. Returns true when it acted.
        /// </summary>
        private bool CheckFrozen(NavStep step)
        {
            int moved = Chebyshev(_mobile.Location, _frozenAnchor);

            if (moved > FrozenMoveTiles)
            {
                _frozenAnchor = _mobile.Location;
                _frozenAt = Core.TickCount;
                return false;
            }

            if (Core.TickCount - (_frozenAt + (long)FrozenLimit.TotalMilliseconds) < 0)
            {
                return false;
            }

            Log.Debug(
                "{0} frozen for {1:0}s within {2} tile(s) of {3},{4} on {5}; going to the top rung{6}.",
                Who(),
                FrozenLimit.TotalSeconds,
                FrozenMoveTiles,
                _frozenAnchor.X,
                _frozenAnchor.Y,
                DescribeHop(step),
                DescribeBlocker(step));

            // A fresh window either way: a rescue that is refused (a player watching) cycles the
            // ladder again, and this must not fire on every tick after it.
            _frozenAnchor = _mobile.Location;
            _frozenAt = Core.TickCount;

            // The rung after SkipWaypoint is the top one.
            _rung = StuckRung.SkipWaypoint;
            HandleStuck(step);

            return true;
        }

        private void HandleStuck(NavStep step)
        {
            // A TAKEN CHAIR IS NOT A BLOCKED ROAD, and the ladder is for blocked roads.
            //
            // If the goal is an arrival that is a PLACE rather than a tile, and the tile itself is
            // the problem, then shuffling, repathing and door-opening are all answers to a question
            // nobody asked - the road was fine, the seat was taken. Shift to a free tile inside the
            // arrival's own range and start again.
            //
            // This has to happen before the rung advances, or the first attempt has already cost a
            // Repath and twenty seconds.
            if (TryShiftWithinArrival(step))
            {
                return;
            }

            // AND A BOT THAT HAS DRIFTED OUT OF APPROACH RANGE IS NOT STUCK, IT IS LOST.
            //
            // Same reasoning as the shift above and for the same reason it goes first: repathing,
            // sidestepping and door-opening are all answers to "something is in the way", and
            // nothing is in the way - the goal has simply stopped being reachable from here, so
            // each of those costs twenty seconds to learn nothing.
            if (TryReAnchor(step))
            {
                return;
            }

            _rung = NextRung(_rung);

            // Counted here rather than in each branch: this is the one place a rung is entered,
            // and it is the same place LogRung reports it from, so the count and the log cannot
            // drift apart.
            int rungIndex = (int)_rung;

            if (rungIndex >= 0 && rungIndex < RungCount)
            {
                _rungsFired[rungIndex]++;
                _rungTotals[rungIndex]++;
            }

            switch (_rung)
            {
                case StuckRung.Repath:
                {
                    LogRung(step, "repathing");

                    // Dropping the goal instance makes MoveTo's reference comparison fail, which
                    // builds a fresh PathFollower - a repath, without reaching into BaseAI.
                    _goal = null;
                    ResetHopDeadline();
                    return;
                }

                case StuckRung.Sidestep:
                {
                    bool moved = Sidestep();

                    LogRung(step, moved ? "sidestepped, repathing" : "could not sidestep");

                    _goal = null;
                    ResetHopDeadline();
                    return;
                }

                case StuckRung.Door:
                {
                    bool opened = TryOpenBlockingDoor(step);

                    LogRung(step, opened ? "opened a door" : "no door to open");

                    _goal = null;
                    ResetHopDeadline();
                    return;
                }

                case StuckRung.SkipWaypoint:
                {
                    if (TrySkipWaypoint(step))
                    {
                        return;
                    }

                    LogRung(step, "nothing to skip to; this is the last step");

                    // Fall through to the top rung on the next timeout rather than stalling here.
                    ResetHopDeadline();
                    return;
                }

                default:
                {
                    if (PlayerIsWatching() && _watchedCycles < WatchedCycles)
                    {
                        _watchedCycles++;

                        LogRung(
                            step,
                            String.Format(
                                "a player is watching, so cycling the ladder again ({0}/{1})",
                                _watchedCycles,
                                WatchedCycles));

                        // Back to the bottom, not frozen: try everything again from here.
                        _rung = StuckRung.None;
                        _goal = null;
                        ResetHopDeadline();
                        return;
                    }

                    // This log line IS the bug report: it names the edge in the data that is
                    // wrong. Read it with the caveats in NavAudit before believing the geometry
                    // is at fault - something standing in the way looks exactly like this.
                    // NAMING THE CAUSE HERE, not only in the ledger, because the console line is
                    // what somebody reads first and "check that edge" was wrong advice for most of
                    // them: a bot that arrived and could not find a stand tile has nothing wrong
                    // with its edge at all.
                    int arrivalRange = ArrivalRangeFor(step);
                    int reach = Math.Max(
                        Math.Abs(_mobile.X - step.Point.X),
                        Math.Abs(_mobile.Y - step.Point.Y));
                    bool arrived = reach <= Math.Max(arrivalRange, 1);

                    // SAYING WHERE THE WALKER IS, not only how far it is. "It stopped 33 tiles
                    // SHORT" on an edge authored at ten tiles is not a statement about the edge,
                    // and "check that edge" was the wrong advice: 33 is the distance from the
                    // MOBILE to the step, so it is either a mobile that travelled or an index that
                    // marched without it. The line now carries the mobile's own tile, where the
                    // route began, which step of how many, and how many of those were skipped
                    // rather than walked - which distinguishes the two on sight.
                    string progress = String.Format(
                        " [at {0},{1},{2}; started {3},{4},{5}; step {6}/{7}, {8} skipped]",
                        _mobile.X,
                        _mobile.Y,
                        _mobile.Z,
                        _startedAt.X,
                        _startedAt.Y,
                        _startedAt.Z,
                        _index + 1,
                        _route == null ? 0 : _route.Count,
                        _skips);

                    Log.Warn(
                        "{0} could not walk {1} after the whole recovery ladder{2}; it was moved. {3}{4}",
                        Who(),
                        DescribeHop(step),
                        _watchedCycles > 0
                            ? String.Format(" and {0} watched cycle(s)", _watchedCycles)
                            : " with nobody watching",
                        arrived
                            ? String.Format(
                                "It was IN RANGE ({0} of {1}) and found no stand tile - the place, not the road.",
                                reach,
                                Math.Max(arrivalRange, 1))
                            : String.Format(
                                "It stopped {0} tile(s) SHORT of the goal{1}.",
                                reach,
                                _skips > 0
                                    ? " - and it SKIPPED its way there, so the edge is not the suspect"
                                    : " - check that edge"),
                        progress);

                    // BEFORE the teleport, because the teleport is what destroys the evidence: it
                    // moves the mobile onto the goal tile, so anything asked afterwards about who
                    // was standing there gets an answer that includes the bot itself.
                    //
                    // This is the only place the terminal failure is recorded with the goal tile
                    // and the mobiles around it. The console line above names the edge and nothing
                    // else - see NavWalkFailures for what that cost.
                    NavWalkFailures.Record(
                        _mobile,
                        _mobile.Map,
                        new Point3D(step.Point.X, step.Point.Y, ResolveZ(_mobile.Map, step.Point)),
                        DescribeHop(step),
                        _watchedCycles > 0,
                        ArrivalRangeFor(step),
                        step.Kind == NavStepKind.Arrival ? "arrival" : "waypoint",
                        _index == 0,
                        _index,
                        _route == null ? 0 : _route.Count,
                        _skips,
                        _startedAt);

                    // AND THE EDGE TAKES A STRIKE, so the next bot routes around it.
                    //
                    // Here and nowhere else: this is the only moment that knows both that the hop
                    // failed completely and which two waypoints it lay between. Striking on a rung
                    // instead would penalise every busy road in Britain within the hour, because
                    // rungs 2 to 4 firing is the ladder working rather than the road failing.
                    //
                    // An arrival step has no waypoint id and is not an edge - the tile is the
                    // problem there, and NavWalkFailures is what records that.
                    NavEdgeHealth.Strike(PreviousWaypointId(), step.WaypointId);

                    Teleport(step);
                    Advance();
                    return;
                }
            }
        }

        /// <summary>
        /// Re-aim at a standable, reachable tile inside the arrival's range, once per hop.
        ///
        /// Two call sites, asking the same question at two moments. Tick asks it PROACTIVELY, the
        /// first time the mobile is inside the approach cap, because an arrival tile that cannot be
        /// reached is not a stuck walker and twenty seconds of ladder learns nothing about it.
        /// HandleStuck asks it again at the top of the ladder, because a tile can become occupied
        /// after the first answer.
        ///
        /// WHY AN OCCUPANT IS A REASON TO WANT A DIFFERENT TILE - CORRECTED, because this comment
        /// argued from the wrong implementation for as long as it existed.
        ///
        /// It used to say that Movement.CheckMovement collects the mobiles on the forward tile and
        /// refuses the step (Movement.cs:345-356, exemption at :411 gated on MoveImpl.Goal), so an
        /// occupied goal was an impossible goal and BotShove was never consulted at all. Every line
        /// of that is true of MovementImpl - and MovementImpl is not what is installed.
        /// FastMovementImpl replaces it in the Initialize pass and never checks mobiles at any
        /// point (FastMovement.cs:23-26, :361-484). See NavMovement.cs, and the [CoreSmoke line
        /// that reports which one is live.
        ///
        /// So the real gate is the occupant's own OnMoveOver, reached at Mobile.cs:3216: a fellow
        /// bot or a daily-life actor consents through BotShove and the step IS legal; a stock NPC
        /// or a real player refuses it and the step is not.
        ///
        /// ANY occupant still counts here, and deliberately, but as a PREFERENCE rather than an
        /// impossibility: this layer is Core and must not learn what a bot is to ask which
        /// occupants consent, and a free tile is the better tile either way. What changes is that
        /// failing to shift is no longer fatal - for a bot-on-bot arrival the ladder that follows
        /// can now genuinely succeed, and two bots on one tile is what a packed forge looks like.
        ///
        /// Once per hop: _shifted stops a crowd of bots trading tiles forever, and if the second
        /// tile is taken too the ladder is the right answer after all. _arrivalChecked is the
        /// separate guard for the proactive call, so a sweep that finds nothing is also only paid
        /// for once.
        /// </summary>
        private bool TryShiftWithinArrival(NavStep step)
        {
            if (_shifted || step == null || step.Kind != NavStepKind.Arrival)
            {
                return false;
            }

            Map map = _mobile.Map;

            if (map == null || map == Map.Internal)
            {
                return false;
            }

            string reason = ArrivalTileFault(map, step);

            if (reason == null)
            {
                return false;
            }

            // A merely CROWDED tile is shifted within the range the author gave and no further:
            // range 0 means "this tile", and a guard post that is standable and reachable and
            // simply busy should wait for it rather than quietly become a different guard post.
            // A tile nothing can stand on, or that cannot be reached from here, is a walk that
            // cannot finish at all - there one tile of latitude beats a certain teleport.
            int radius = reason == OccupiedFault ? step.Range : Math.Max(step.Range, 1);

            if (radius <= 0)
            {
                return false;
            }

            Point3D best;

            if (!TryPickArrivalTile(map, step.Point, radius, out best))
            {
                return false;
            }

            // A CROWDED tile is routine and stays at Debug; the other two are data faults and are
            // said out loud, because each one is a record somebody could fix and neither should be
            // happening often enough to be noise.
            if (reason == OccupiedFault)
            {
                _retargetOccupied++;

                Log.Debug(
                    "{0} shifted its arrival from {1},{2} to {3},{4} - {5}.",
                    Who(), step.Point.X, step.Point.Y, best.X, best.Y, reason);
            }
            else
            {
                if (reason == UnstandableFault)
                {
                    _retargetUnstandable++;
                }
                else
                {
                    _retargetUnreachable++;
                }

                Log.Info(
                    "{0} shifted its arrival from {1},{2} to {3},{4} - {5}.",
                    Who(), step.Point.X, step.Point.Y, best.X, best.Y, reason);
            }

            step.Retarget(best);

            _shifted = true;
            _goal = null;
            _bestDistance = Int32.MaxValue;
            ResetHopDeadline();

            return true;
        }

        private const string OccupiedFault = "the tile was taken";

        private const string UnstandableFault = "nothing can stand on it";

        /// <summary>
        /// What is wrong with the arrival tile, in the words the log line uses, or null when
        /// nothing is.
        ///
        /// Three faults, asked cheapest first. Occupancy and standability are tile lookups.
        ///
        /// REACHABILITY IS ONLY ASKED FROM INSIDE THE APPROACH CAP, and that is not a saving, it is
        /// the difference between a true answer and a false one. FastAStarAlgorithm is greedy
        /// best-first with a 300-expansion budget, so from twelve tiles out it says "no path" about
        /// goals it reaches comfortably from eight - the whole finding behind MaxApproachDistance.
        /// Retargeting an arrival on that answer would move a perfectly good goal because the bot
        /// had not arrived yet.
        /// </summary>
        private string ArrivalTileFault(Map map, NavStep step)
        {
            int z;

            if (!TryResolveZ(map, step.Point, out z)
                || !map.CanFit(step.Point.X, step.Point.Y, z, 16, false, false, true))
            {
                return UnstandableFault;
            }

            if (IsTileOccupied(map, step.Point))
            {
                return OccupiedFault;
            }

            if (NavGraph.Chebyshev(_mobile.Location, step.Point) <= MaxApproachDistance
                && !Reachable(new Point3D(step.Point.X, step.Point.Y, z)))
            {
                return "the engine will not path to it from here";
            }

            return null;
        }

        /// <summary>
        /// The nearest tile to the mobile, inside `radius` of the arrival, that it can stand on
        /// and the engine will path to.
        ///
        /// Nearest to the BOT, not to the arrival: it is standing somewhere already and the point
        /// is to stop walking, not to get as close to the centre as possible. Free tiles are
        /// preferred over taken ones, but a taken one is still taken over nothing - the shove is
        /// consented to by another bot, and two bots on one tile is what a packed forge looks like.
        ///
        /// Candidates are sorted by that distance and path-tested IN ORDER, so the usual cost is
        /// one MovementPath rather than one per tile in the box. It runs once per hop.
        /// </summary>
        private bool TryPickArrivalTile(Map map, Point3D arrival, int radius, out Point3D best)
        {
            best = Point3D.Zero;

            var candidates = new List<Point3D>();

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    int x = arrival.X + dx;
                    int y = arrival.Y + dy;

                    int z;

                    if (!TryResolveZ(map, new Point3D(x, y, arrival.Z), out z))
                    {
                        continue;
                    }

                    if (!map.CanFit(x, y, z, 16, false, false, true))
                    {
                        continue;
                    }

                    candidates.Add(new Point3D(x, y, z));
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            Point3D from = _mobile.Location;

            candidates.Sort((a, b) =>
                NavGraph.Chebyshev(a, from).CompareTo(NavGraph.Chebyshev(b, from)));

            bool haveFallback = false;
            Point3D fallback = Point3D.Zero;

            for (int i = 0; i < candidates.Count; i++)
            {
                Point3D candidate = candidates[i];

                if (!Reachable(candidate))
                {
                    continue;
                }

                if (!IsTileOccupied(map, candidate))
                {
                    best = candidate;
                    return true;
                }

                if (!haveFallback)
                {
                    haveFallback = true;
                    fallback = candidate;
                }
            }

            best = fallback;

            return haveFallback;
        }

        /// <summary>
        /// Will the engine path this mobile from where it stands to here?
        ///
        /// The walker's own question, asked the walker's way - a MovementPath built on the mobile,
        /// not a Point3D probe, because the two get different answers (see the audit's caveats in
        /// the README). Adjacent and same-tile are answered without asking: MovementPath returns no
        /// path for a goal one step away (MovementPath.cs:34), which is a guaranteed false negative
        /// and the one NavAudit already special-cases.
        /// </summary>
        private bool Reachable(Point3D point)
        {
            if (NavGraph.Chebyshev(_mobile.Location, point) <= 1)
            {
                return true;
            }

            return new MovementPath(_mobile, point).Success;
        }

        /// <summary>
        /// Is anybody OTHER THAN THE WALKER standing exactly here?
        ///
        /// The exclusion matters at every call site and used not to be made. A walker asking
        /// whether its own arrival tile is taken, while standing on it, answered yes about
        /// itself; so would a rescue looking for a landing at ring 0 under the frozen watchdog,
        /// which fires precisely when the mobile has been standing in one place.
        /// </summary>
        private bool IsTileOccupied(Map map, Point3D point)
        {
            IPooledEnumerable nearby = map.GetMobilesInRange(point, 0);

            try
            {
                foreach (Mobile other in nearby)
                {
                    if (other != _mobile && !other.Deleted && other.Alive)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                // A non-generic IPooledEnumerable has to be freed or the pool leaks
                // (CLAUDE.md section 14).
                nearby.Free();
            }

            return false;
        }

        private static StuckRung NextRung(StuckRung rung)
        {
            switch (rung)
            {
                case StuckRung.None: return StuckRung.Repath;
                case StuckRung.Repath: return StuckRung.Sidestep;
                case StuckRung.Sidestep: return StuckRung.Door;
                case StuckRung.Door: return StuckRung.SkipWaypoint;
                default: return StuckRung.Teleport;
            }
        }

        /// <summary>One Debug line per rung entered, never per tick.</summary>
        private void LogRung(NavStep step, string what)
        {
            // WHO IS ON THE TILE THE NEXT STEP WANTS - asked first, because it is the only one of
            // the three that answers the question anybody is asking.
            //
            // DescribeBlocker names a mobile on the GOAL tile and otherwise lists whatever is near
            // the walker, and both are the wrong set. A wedge is almost never caused by somebody
            // standing on the goal twelve tiles away; it is caused by somebody standing on the
            // tile the walker is trying to step onto right now. Measured, that gap was total: over
            // a whole run of 133 rung entries, ONE said "blocked by" - a dog - while 75 of the 76
            // that carried a mobile list at all had a stock NPC or an animal within two tiles.
            // "A vendor was near the wedge" is well supported by that and "a vendor caused it" is
            // not, and the difference is what decides whether an upstream file gets edited.
            Mobile blocker = NextStepBlocker(step);

            if (blocker != null)
            {
                bool shovable = NavWalkFailures.Shovable(blocker);

                _nextStepBlocked++;

                if (!shovable)
                {
                    _nextStepUnshovable++;
                }

                what += String.Format(
                    ", next step blocked by {0} ({1}) at {2},{3}{4}",
                    blocker.Name ?? "?",
                    NavWalkFailures.Describe(blocker),
                    blocker.X,
                    blocker.Y,
                    shovable ? "" : " - WHICH A BOT MAY NOT PUSH");
            }

            // The body on the goal tile, when there is one. A bot walks through other bots and
            // the daily-life actors (BotShove), so a rung that fires against a mobile is one it
            // cannot push - a real player, a vendor, a guard, an animal - and that is the case
            // the deviation was named for. Naming the blocker is what turns "wedged at 1450,1683"
            // into evidence.
            what += DescribeBlocker(step);

            Log.Debug(
                "{0} stuck on {1} [{2}] - {3}.",
                Who(),
                DescribeHop(step),
                _rung,
                what);

            // Copied to a local first: a subscriber is free to clear the field, and this is the
            // same null-race guard Advance already applies to Arrived.
            Action<NavWalker, StuckRung, string> fired = RungFired;

            if (fired != null)
            {
                try
                {
                    fired(this, _rung, what);
                }
                catch (Exception ex)
                {
                    // A diagnostic subscriber must never be able to break the recovery ladder it
                    // is watching - that would turn "this bot is stuck" into "this bot is stuck
                    // and can no longer get unstuck".
                    Log.Error(ex, "A RungFired subscriber threw.");
                }
            }
        }

        private string Who()
        {
            return _mobile.Name ?? _mobile.GetType().Name;
        }

        /// <summary>
        /// ", blocked by Perrin (DailyLifeTownsfolk) at 1450,1683" when a live mobile other than
        /// this one stands on the step's tile, and ", near: Bindi (Tinker) at 1421,1652" for every
        /// live mobile within two tiles of the walker otherwise; empty when it stands alone. A
        /// player is named as such rather than by class, because that is the one blocker a bot is
        /// meant to yield to. The near list exists because a bot wedged two tiles from a shop
        /// counter has nothing on the counter tile: what it cannot pass is the vendor in the
        /// doorway, and that is exactly the case the yield-to-stock-NPCs decision wants evidence of.
        /// </summary>
        /// <summary>
        /// Fleet-wide counts of rung entries where the tile the next step wanted was occupied,
        /// and of how many of those occupants a bot may not push.
        ///
        /// The second number is the whole instrument. It is what "the stock-NPC shove costs us N
        /// stalls an hour" has to be measured with, because the terminal ledger cannot see it: a
        /// wedge that the ladder eventually recovers from never reaches the ledger at all, and the
        /// one Sean actually watched - a vendor in the Trinsic alchemist doorway - was exactly
        /// that. Reported on Bots.Population beside the rung totals.
        /// </summary>
        private static int _nextStepBlocked;

        private static int _nextStepUnshovable;

        /// <summary>"blocked 41, unshovable 33" - the fleet-wide next-step line.</summary>
        public static string DescribeNextStepBlocks()
        {
            return String.Format(
                "blocked {0}, unshovable {1}", _nextStepBlocked, _nextStepUnshovable);
        }

        /// <summary>
        /// Who is standing on the tile this hop's next step wants, or null.
        ///
        /// Asked of the ENGINE rather than guessed from the direction to the goal: a walker on a
        /// road bend is not stepping towards its goal, and the tile it is actually about to try is
        /// whatever MovementPath's first direction names. That is the tile Mobile.Move will hand
        /// to the occupant's OnMoveOver, and therefore the only tile whose occupant can refuse
        /// this step.
        ///
        /// Costs one MovementPath, on rung entry only - a few dozen an hour across the fleet,
        /// against the one MoveTo already runs on every step of every walker.
        /// </summary>
        private Mobile NextStepBlocker(NavStep step)
        {
            Map map = _mobile.Map;

            if (map == null || map == Map.Internal || step == null)
            {
                return null;
            }

            var path = new MovementPath(
                _mobile, new Point3D(step.Point.X, step.Point.Y, ResolveZ(map, step.Point)));

            if (!path.Success || path.Directions == null || path.Directions.Length == 0)
            {
                return null;
            }

            int x = _mobile.X;
            int y = _mobile.Y;

            Movement.Movement.Offset(path.Directions[0], ref x, ref y);

            Mobile found = null;

            IPooledEnumerable nearby = map.GetMobilesInRange(new Point3D(x, y, _mobile.Z), 0);

            try
            {
                foreach (Mobile other in nearby)
                {
                    if (other == _mobile || other.Deleted || !other.Alive)
                    {
                        continue;
                    }

                    // The first one that is really in the way vertically. CanMoveOver's own test:
                    // two mobiles more than fifteen apart in Z are on different storeys.
                    if (other.Z + 15 <= _mobile.Z || _mobile.Z + 15 <= other.Z)
                    {
                        continue;
                    }

                    found = other;
                    break;
                }
            }
            finally
            {
                // A non-generic IPooledEnumerable has to be freed or the pool leaks
                // (CLAUDE.md section 14).
                nearby.Free();
            }

            return found;
        }

        private string DescribeBlocker(NavStep step)
        {
            Map map = _mobile.Map;

            if (map == null || map == Map.Internal)
            {
                return String.Empty;
            }

            string onGoal = DescribeMobiles(map, step.Point, 0, ", blocked by ");

            if (onGoal.Length > 0)
            {
                return onGoal;
            }

            return DescribeMobiles(map, _mobile.Location, 2, ", near: ");
        }

        private string DescribeMobiles(Map map, Point3D at, int range, string prefix)
        {
            var parts = new List<string>();

            IPooledEnumerable eable = map.GetMobilesInRange(at, range);

            try
            {
                foreach (Mobile other in eable)
                {
                    if (other == _mobile || other.Deleted || !other.Alive)
                    {
                        continue;
                    }

                    string kind = other.Player && !(other is BaseCreature)
                        ? "player"
                        : other.GetType().Name;

                    parts.Add(String.Format(
                        "{0} ({1}) at {2},{3}",
                        other.Name ?? kind,
                        kind,
                        other.X,
                        other.Y));
                }
            }
            finally
            {
                eable.Free();
            }

            return parts.Count == 0 ? String.Empty : prefix + String.Join("; ", parts.ToArray());
        }

        /// <summary>
        /// Step off the tile the mobile is wedged on, so the next repath starts somewhere else.
        ///
        /// Translated from uo-offline-server's NudgeAway. Directions are shuffled so a walker
        /// wedged against the same corner twice does not make the same escape twice, and it
        /// carries on in whichever direction worked - one tile rarely clears a doorway or a
        /// bank crowd.
        /// </summary>
        private bool Sidestep()
        {
            Direction[] directions =
            {
                Direction.North, Direction.East, Direction.South, Direction.West,
                Direction.Up, Direction.Down, Direction.Left, Direction.Right,
            };

            for (int i = directions.Length - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);

                Direction swap = directions[i];
                directions[i] = directions[j];
                directions[j] = swap;
            }

            Direction taken = Direction.North;
            bool moved = false;

            for (int i = 0; i < directions.Length; i++)
            {
                Point3D before = _mobile.Location;

                if (_mobile.Move(directions[i]))
                {
                    taken = directions[i];
                    moved = true;

                    // Move returns true for a turn as well as a step; only a step is a sidestep.
                    if (Sampler != null && _mobile.Location != before)
                    {
                        Sampler.Sidestepped();
                    }

                    break;
                }
            }

            if (!moved)
            {
                return false;
            }

            for (int step = 1; step < SidestepTiles; step++)
            {
                Point3D before = _mobile.Location;

                if (!_mobile.Move(taken))
                {
                    break;
                }

                if (Sampler != null && _mobile.Location != before)
                {
                    Sampler.Sidestepped();
                }
            }

            return true;
        }

        /// <summary>
        /// Open the closed door in the way, the way a player's client does when they walk into
        /// one.
        ///
        /// Translated from uo-offline-server's DoorHelper, including the two rules its comments
        /// were written to record. ONLY closed doors are touched, because Use() toggles and
        /// calling it on an open door slams it shut on whoever is walking through - and a step
        /// can fail for reasons that have nothing to do with the door, another mobile in the
        /// doorway being the common one. And it goes through Use() rather than setting Open,
        /// because Use() carries the rules with it: a locked door stays shut, and a house door
        /// runs its own access check, so this cannot walk an NPC into someone's locked home.
        /// </summary>
        private bool TryOpenBlockingDoor(NavStep step)
        {
            Map map = _mobile.Map;

            if (map == null || map == Map.Internal || !_mobile.CheckAlive())
            {
                return false;
            }

            // Doors within a tile of the mobile, plus the tile it is trying to reach. Anything
            // further away is not what is blocking this step.
            IPooledEnumerable<Item> items = map.GetItemsInRange(_mobile.Location, 1);

            try
            {
                foreach (Item item in items)
                {
                    BaseDoor door = item as BaseDoor;

                    if (door == null || door.Open)
                    {
                        continue;
                    }

                    // The same vertical window the client's open-door macro uses, so a door on
                    // the floor above is not reachable from down here.
                    if (door.Z + door.ItemData.Height <= _mobile.Z || _mobile.Z + 16 <= door.Z)
                    {
                        continue;
                    }

                    if (!_mobile.CanSee(door) || !_mobile.InLOS(door))
                    {
                        continue;
                    }

                    door.Use(_mobile);

                    // Use() is a no-op on a door this mobile cannot open, so report what actually
                    // happened rather than that it was tried.
                    if (door.Open)
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
        /// Give up on this waypoint and aim at the next one in the route.
        ///
        /// The cheap version of "route via a different waypoint": the rest of the journey is
        /// unchanged, one unreachable node is simply omitted. Two hops is at most twice the hop
        /// cap, which stays inside the engine's 38-tile search box, so the longer leg is usually
        /// still pathable - and if it is not, the ladder simply reaches its top rung on the new
        /// step instead.
        ///
        /// Refuses on the last step, where there is nothing to skip to and skipping would mean
        /// arriving somewhere the caller did not ask for.
        /// </summary>
        private bool TrySkipWaypoint(NavStep step)
        {
            if (_route == null || _index + 1 >= _route.Count)
            {
                return false;
            }

            NavStep next = _route.Steps[_index + 1];

            if (next.Kind == NavStepKind.Transition)
            {
                // A gate is not a tile to walk to; let the top rung place the mobile properly.
                return false;
            }

            LogRung(
                step,
                String.Format("skipping to '{0}'", next.WaypointId ?? "(next)"));

            // Counted BEFORE ResetHop, which is what clears everything else, and outside it,
            // because a skip is a fact about the ROUTE. A walker that cannot take a single step
            // climbs the ladder, skips, resets, and climbs again - marching its index down the
            // route while standing still - and the failure it eventually reports names a hop it
            // never approached. This is the counter that makes that visible rather than inferred.
            _skips++;

            _index++;
            _goal = null;

            ResetHop();

            return true;
        }

        /// <summary>Chebyshev distance, matching how the graph measures a hop.</summary>
        private static int Chebyshev(Point3D a, Point3D b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);

            return dx > dy ? dx : dy;
        }

        /// <summary>
        /// How far out the rescue will look for somewhere to put the mobile.
        ///
        /// uo-offline's PickLanding scans rings 2, 4, 6, 8 around the anchor (MagicTravel.cs:
        /// 266-336). Six rather than eight because their anchor is a destination coordinate and a
        /// recall landing, where a few tiles either way is invisible; ours is a waypoint or an
        /// arrival on a graph whose hops are capped at twelve, and a rescue that lands seven tiles
        /// off the road has moved the problem rather than solved it.
        /// </summary>
        private const int MaxRescueRing = 6;

        /// <summary>
        /// Put the mobile down somewhere it can actually stand.
        ///
        /// WHAT THIS USED TO DO, AND WHAT IT COST. It moved the mobile to step.Point at
        /// ResolveZ(step.Point) - and ResolveZ answers with the land Z when nothing is standable
        /// (see its own summary), so a rescue aimed at a tile nothing fits on put the mobile
        /// exactly there. Measured, in one 30-minute window: three walks failed at trinsic-dock-2's
        /// arrival (2072,2865 z-15, under a pier), each was teleported onto it, and each of the
        /// three NEXT failures in the ledger began at 2072,2865 - eleven tiles from a goal it could
        /// no longer reach. The same shape again at trinsic-shop-smith-2. Eight of seventeen
        /// failures, and all three strikes on 'uo-wp-990' -> 'uo-wp-197-s1', a three-tile hop
        /// between two audited waypoints that has nothing wrong with it.
        ///
        /// THE RULE IS uo-offline's, from MagicTravel.PickLanding (MagicTravel.cs:266-336):
        ///
        ///   - every candidate is validated before the move, never assumed;
        ///   - each is tried at the AUTHORED Z first and the averaged ground Z second, because
        ///     "docks and shop floors sit ABOVE what GetAverageZ reports - averaging under a pier
        ///     returns the water level". TryResolveZ is that rule already: the window around the
        ///     hint, then the window around the land;
        ///   - the search ESCALATES outward, because popular arrival points are permanently
        ///     crowded and the mobile can walk the last tile or two itself;
        ///   - and when nothing near the anchor will take a landing - "a pier ringed by water, a
        ///     shop interior packed wall-to-wall" - it falls back to the approach WAYPOINT, which
        ///     is proven walkable ground a few steps out.
        ///
        /// Two deliberate differences, both about where a rescued bot should end up rather than
        /// whether the tile is real:
        ///
        ///   - OURS PREFERS A TILE INSIDE THE ARRIVAL'S OWN RANGE, which theirs has no notion of
        ///     (no teleport site in their tree reads ArrivalRange, DriftArriveRange or
        ///     FinalLegArrivalRange). It needs no special case: the scan goes outward from the
        ///     arrival tile, so while the ring is inside the range those are the tiles it tries
        ///     first. It matters here because our ranges are load-bearing - a smith rescued outside
        ///     its forge apron has been rescued into a second failure.
        ///   - AND IF NOTHING IS FOUND, THE MOBILE IS NOT MOVED. Theirs falls through to the raw
        ///     authored coordinate as a last resort; that is precisely the behaviour this method
        ///     exists to remove, so the last resort here is to leave the mobile where it is, say
        ///     so at Warn, and let the ladder cycle. Standing in the road is recoverable; standing
        ///     under a pier is not.
        /// </summary>
        private void Teleport(NavStep step)
        {
            Map map = _mobile.Map;

            Point3D landing;

            if (!TryPickLanding(map, step.Point, out landing))
            {
                NavWaypoint previous = Nav.Waypoint(PreviousWaypointId());

                if (previous == null || !TryPickLanding(map, previous.Location, out landing))
                {
                    Log.Warn(
                        "{0} could not be rescued: nothing within {1} tiles of {2},{3} will take a "
                        + "landing, and there is no waypoint behind it that will either. Leaving it "
                        + "where it stands.",
                        Who(),
                        MaxRescueRing,
                        step.Point.X,
                        step.Point.Y);

                    return;
                }

                Log.Warn(
                    "{0} was rescued to '{1}' at {2},{3},{4} - nothing within {5} tiles of "
                    + "{6},{7} would take a landing.",
                    Who(),
                    previous.Id,
                    landing.X,
                    landing.Y,
                    landing.Z,
                    MaxRescueRing,
                    step.Point.X,
                    step.Point.Y);
            }
            else
            {
                // THE ORDINARY RESCUE WAS THE ONE THAT SAID NOTHING. Both failure paths above log;
                // the successful one moved the mobile in silence, so a bot that reappeared on an
                // upper floor twenty tiles from anywhere had no line naming who put it there - and
                // 2026,2832 z 20, the first floor of a Trinsic provisioner, turned up twice in one
                // window as the tile a walk BEGAN from with nothing authored anywhere near it.
                // Every walker-initiated MoveToWorld now says where from, where to, and on whose
                // authority.
                Log.Debug(
                    "{0} was rescued from {1},{2},{3} to {4},{5},{6} - the nearest landing within "
                    + "{7} tiles of {8},{9}.",
                    Who(),
                    _mobile.X,
                    _mobile.Y,
                    _mobile.Z,
                    landing.X,
                    landing.Y,
                    landing.Z,
                    MaxRescueRing,
                    step.Point.X,
                    step.Point.Y);
            }

            _mobile.MoveToWorld(landing, map);
        }

        /// <summary>
        /// The nearest tile to `anchor` a mobile can stand on, searching outward, free first.
        ///
        /// A taken tile at ring 1 is worse than a free one at ring 2 only if you think the point is
        /// to be close; the point is to be somewhere the mobile can move from, and a tile it shares
        /// with somebody is one it may be unable to leave. So an occupied candidate is remembered
        /// and used only when the whole search finds nothing free.
        /// </summary>
        private bool TryPickLanding(Map map, Point3D anchor, out Point3D landing)
        {
            landing = Point3D.Zero;

            if (map == null || map == Map.Internal)
            {
                return false;
            }

            bool haveFallback = false;
            Point3D fallback = Point3D.Zero;

            for (int ring = 0; ring <= MaxRescueRing; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dy = -ring; dy <= ring; dy++)
                    {
                        // The ring only, so the search really does go outward rather than
                        // re-testing the whole box at every radius.
                        if (ring > 0 && Math.Abs(dx) != ring && Math.Abs(dy) != ring)
                        {
                            continue;
                        }

                        int x = anchor.X + dx;
                        int y = anchor.Y + dy;

                        int z;

                        // The authored Z as the hint, the land Z as the fallback - both halves are
                        // inside TryResolveZ, and a false answer here means neither worked.
                        if (!TryResolveZ(map, new Point3D(x, y, anchor.Z), out z))
                        {
                            continue;
                        }

                        if (!map.CanFit(x, y, z, 16, false, false, true))
                        {
                            continue;
                        }

                        var candidate = new Point3D(x, y, z);

                        if (!IsTileOccupied(map, candidate))
                        {
                            landing = candidate;
                            return true;
                        }

                        if (!haveFallback)
                        {
                            haveFallback = true;
                            fallback = candidate;
                        }
                    }
                }
            }

            landing = fallback;

            return haveFallback;
        }

        /// <summary>
        /// A gate hop. The base behaviour is to move the mobile, which is right for a staff
        /// tool and for anything that does not care how it got there; a consumer that should
        /// actually walk onto a teleporter or through a moongate overrides this.
        /// </summary>
        protected virtual void OnTransition(NavStep step)
        {
            Map map = step.Map ?? _mobile.Map;

            Point3D landing;

            // A gate's far side is authored data with the same exposure as an arrival's, so it gets
            // the same validated landing rather than the raw tile at whatever Z resolves. Falling
            // back to the raw tile here rather than refusing: a transition that does not happen
            // leaves the mobile on the wrong facet with a route it cannot walk, which is worse than
            // an awkward arrival - and unlike the rescue, this is the caller's own instruction.
            if (!TryPickLanding(map, step.Point, out landing))
            {
                landing = new Point3D(step.Point.X, step.Point.Y, ResolveZ(map, step.Point));

                Log.Warn(
                    "{0} is taking the gate to {1},{2} unvalidated - nothing within {3} tiles of it "
                    + "will take a landing.",
                    Who(),
                    step.Point.X,
                    step.Point.Y,
                    MaxRescueRing);
            }

            _mobile.MoveToWorld(landing, map);
        }

        private int ArrivalRangeFor(NavStep step)
        {
            if (step.Kind == NavStepKind.Arrival)
            {
                // The picked arrival's own range: 0 for a tile (a bank counter, a guard post),
                // 2 for a place (a forge, where the arriving behaviour picks its stand tile).
                // It was a hard 0 for every arrival, which is why a bot one tile short of an
                // occupied station tile was never "arrived" and never delivered.
                return step.Range;
            }

            NavWaypoint waypoint = Nav.Waypoint(step.WaypointId);

            // Adopted from uo-offline-server: a doorway waypoint at an unusual Z needs the
            // mobile on the exact tile, so its authored tolerance wins.
            if (waypoint != null && waypoint.ArrivalRange > 0)
            {
                return waypoint.ArrivalRange;
            }

            return DefaultArrivalRange;
        }

        /// <summary>
        /// Walk back to the waypoint this hop started from, then try the goal again.
        ///
        /// Only once per hop, and only when there IS a waypoint behind us - a route that began at
        /// the bot's own feet has nothing to go back to, and PreviousWaypointId returns null for
        /// exactly that case. Measured: in 7 of the 12 far failures in one window, the previous
        /// waypoint was reachable from where the bot stood while the goal was not, so this is a
        /// real recovery rather than a hopeful one.
        /// </summary>
        private bool TryReAnchor(NavStep step)
        {
            if (_reAnchored || step == null || _mobile == null)
            {
                return false;
            }

            int distance = Math.Max(
                Math.Abs(_mobile.X - step.Point.X),
                Math.Abs(_mobile.Y - step.Point.Y));

            if (distance <= MaxApproachDistance)
            {
                return false;
            }

            NavWaypoint previous = Nav.Waypoint(PreviousWaypointId());

            if (previous == null)
            {
                return false;
            }

            int back = Math.Max(
                Math.Abs(_mobile.X - previous.Location.X),
                Math.Abs(_mobile.Y - previous.Location.Y));

            // Going back has to be an improvement. If we are already on the waypoint, or it is no
            // closer than the goal, walking to it teaches nothing and burns the one attempt.
            if (back <= 2 || back >= distance)
            {
                return false;
            }

            _reAnchored = true;
            _approaching = true;
            _approach = previous.Location;
            _goal = null;

            LogRung(step, String.Format(
                "{0} tiles from the goal, past the {1}-tile approach cap - walking back to '{2}'",
                distance,
                MaxApproachDistance,
                previous.Id));

            ResetHopDeadline();

            return true;
        }

        private void EnsureGoal(NavStep step)
        {
            // AIM AT THE WAYPOINT BEHIND US FIRST, when the ladder has walked us out of range of
            // the one ahead. Checked before the normal path so it cannot be overwritten: the goal
            // does not match step.Point while re-anchoring, which is exactly the condition the
            // test below would use to rebuild it.
            if (_approaching)
            {
                int back = Math.Max(
                    Math.Abs(_mobile.X - _approach.X),
                    Math.Abs(_mobile.Y - _approach.Y));

                if (back > 2)
                {
                    if (_goal == null || _goal.X != _approach.X || _goal.Y != _approach.Y)
                    {
                        _goal = new NavGoal(_approach.X, _approach.Y, _approach.Z);
                    }

                    return;
                }

                // Back on the road. Fall through and aim at the real goal again.
                _approaching = false;
                _goal = null;
            }

            if (_goal != null && _goal.X == step.Point.X && _goal.Y == step.Point.Y)
            {
                return;
            }

            int z = ResolveZ(_mobile.Map, step.Point);

            _goal = new NavGoal(step.Point.X, step.Point.Y, z);

            if (KeepHomeAligned)
            {
                _mobile.Home = new Point3D(step.Point.X, step.Point.Y, z);
            }
        }

        /// <summary>
        /// The authored Z when a mobile can actually stand there, the land surface otherwise.
        ///
        /// Both halves matter. Preferring the authored Z is what makes an upper floor or a
        /// raised entrance reachable at all, since GetAverageZ only sees land and would aim at
        /// the ground beneath it. Falling back to the surface is what stops a slightly wrong
        /// authored Z from making the hop unreachable.
        ///
        /// NavAudit resolves Z exactly the same way. They must agree, or the audit will pass a
        /// hop the walker then aims at the wrong floor.
        /// </summary>
        /// <summary>
        /// How far a step may climb while looking for the surface to stand on.
        ///
        /// Translated from uo-offline's `CustomBots/Nav/Walkable.cs:27-28`: UO lets a mobile climb
        /// a little and drop a lot, so the window is asymmetric. Theirs is 4. OURS IS 5, and the
        /// difference is a wooden ramp.
        ///
        /// A bridge foot is a `Bridge`-flagged ramp of height 5. A mobile stands on it at half its
        /// height (`ItemData.CalcHeight`, Server/TileData.cs) but the engine's step budget is
        /// measured from its FULL height: `MovementImpl.GetStartZ` takes `tile.Z + Height` as the
        /// top of what you stand on, and `Check` allows a step up to `startTop + StepHeight(2)`
        /// (Scripts/Services/Pathing/Movement.cs). So from a ramp at Z 10 you stand at 12, your
        /// top is 15, and the next ramp at Z 15 (stand 17) is reachable: a climb of 5 measured
        /// stand-to-stand. Every one of Trinsic's canal bridges rises 2 -> 7 -> 10 or 12 -> 17 ->
        /// 22 that way, and at 4 the flood stopped at the foot of all of them. uo-offline's own
        /// `Nav/DistanceField.cs:100-106` admits the same: their window "rejects legs the game
        /// allows (Vesper canal bridges, dock ramps, low arches)", which is why their audit floods
        /// with the real movement engine instead.
        ///
        /// This window is a candidate generator, not a verdict - `MovementPath` is the verdict,
        /// and NavAdopt paths every hop it proposes. Being one Z looser than theirs is the safe
        /// direction for a generator. See `Scripts/Custom/Bots/README.md`, "Z resolution".
        /// </summary>
        private const int MaxClimb = 5;

        /// <summary>And how far it may drop. See MaxClimb.</summary>
        private const int MaxDrop = 20;

        /// <summary>
        /// How far from the land surface a SEED may be found when the window fails.
        ///
        /// uo-offline's `Walkable.SeedScanRange` (`Nav/Walkable.cs:33`), number included. A pier
        /// deck stands 13 above the water it is built over, and the reference stores the water Z
        /// for every generated dock, so a window of 5 around either the hint or the land finds
        /// nothing. The scan is for seeds only - a waypoint, an arrival, a snap - never for a
        /// flood step, because a step that may find any surface at all will step from the ground
        /// straight onto the middle of a ramp, which the engine refuses.
        /// </summary>
        private const int SeedScanRange = 60;

        /// <summary>
        /// How far ABOVE the higher of the hint and the land the seed scan may climb: one storey.
        ///
        /// The scan's job is a floor or a deck the stored Z missed - a shop floor at 10 stored as
        /// 0, a pier at -2 stored as -15. Left uncapped it also found roofs: three Trinsic shop
        /// arrivals sit on counter and display-case tiles, nothing stands on the floor there, and
        /// the nearest surface above was the stone roof at 30 to 35. A roof is never where anything
        /// is authored to go, but roof tiles carry no Roof flag in this tiledata, so the guard is
        /// geometric. Twenty is FastAStarAlgorithm's PlaneHeight: a goal a full plane above the
        /// start is not pathable from it in one search, so nothing at or above that is an answer
        /// a walker could use. Applied only where the land is ground - a deck over water has no
        /// ground storey to measure from. Downward is unlimited, as theirs is; a stale hint above
        /// the ground wants the ground.
        /// </summary>
        private const int SeedClimb = 20;

        /// <summary>
        /// The Z a mobile would actually stand at on this tile, given a Z to start looking from.
        ///
        /// THE HINT IS A HINT, NOT AN ANSWER, and that distinction is the whole of this method.
        /// The first version probed the hint once and fell straight back to `GetAverageZ`:
        ///
        ///     if (map.CanFit(x, y, point.Z, ...)) return point.Z;
        ///     return map.GetAverageZ(x, y);
        ///
        /// which is correct on open ground and wrong on anything built. A bridge deck is a chain
        /// of statics whose Z changes tile by tile, so the tile after a Z 6 deck tile is at 7, the
        /// single probe at 6 fails, and the answer came back as the RIVER BED under the bridge.
        /// The Britain-Trinsic crossing was unwalkable to every flood and audit we have for that
        /// reason alone - and `MovementPath` crosses it perfectly well when handed Z 6 to Z 3,
        /// which is how we know the pathfinder was never the problem.
        ///
        /// So: search a window around the hint, nearest first, climb 5 and drop 20. If nothing in
        /// the window stands, start again from the tile's own land surface before giving up -
        /// a hint can be stale or simply wrong, and the ground is still the right fallback when
        /// it is. Falling back to the ground FIRST was the bug.
        ///
        /// THIS IS THE SEED FORM. When both windows fail it scans outward from the land surface
        /// as far as `SeedScanRange`, which is what resolves a pier stored at the water's Z onto
        /// its deck. It answers "where would a mobile stand on THIS tile" for a waypoint, an
        /// arrival, an audit endpoint or a snap. A flood expanding from one tile to the next must
        /// use `ResolveStepZ` instead, or it will climb anything.
        /// </summary>
        public static int ResolveZ(Map map, Point3D point)
        {
            int z;

            // On a false answer TryResolveZ has already left the land Z (or the hint, with no
            // facet) in z, which is the same fallback the old implementation ended on.
            TryResolveZ(map, point, out z);

            return z;
        }

        /// <summary>
        /// `ResolveZ`, saying whether anything was standable at all rather than answering with
        /// the land Z when nothing was. NavAdopt writes a corrected Z only on a true answer.
        /// </summary>
        public static bool TryResolveZ(Map map, Point3D point, out int z)
        {
            z = point.Z;

            if (map == null)
            {
                return false;
            }

            if (TryStepZ(map, point, out z))
            {
                return true;
            }

            int landZ = map.GetAverageZ(point.X, point.Y);

            // A DOORWAY, before the scan. A closed door is an Impassable item, so at a door tile
            // nothing in either window stands - and the first version of the scan then found the
            // floor above the door, twenty Z up, and aimed every walker at a tavern's upper storey.
            // The walk probe went from zero recoveries to five bots mid-recovery in one run. A door
            // is something a walker opens (TryOpenBlockingDoor), and NavCorridor.CanStand already
            // counts a door tile as standable at the Z beside it; so does this. The hint first,
            // because an upper-floor doorway authored at its own Z is right as authored.
            if (DoorAt(map, point.X, point.Y, point.Z))
            {
                z = point.Z;
                return true;
            }

            if (DoorAt(map, point.X, point.Y, landZ))
            {
                z = landZ;
                return true;
            }

            // uo-offline's `Walkable.TryFindSeedZ` (`Nav/Walkable.cs:38-62`): expand outward from
            // the land surface in both directions, nearest first. The window from the land has
            // already covered +5/-20; this reaches the deck a stale hint put under water.
            //
            // Upward it stops short of one storey above the hint or the land, whichever is higher
            // (SeedClimb) - BUT ONLY WHERE THE LAND IS GROUND. The ceiling exists to stop a blocked
            // shop floor answering with its roof, and a roof is a storey above a ground floor. A
            // deck over water has no ground storey: the Britain-Trinsic bridge stands 21 above the
            // river bed and uo-wp-79 stores the river bed, and a ceiling measured from that bed
            // put the deck out of reach and the engine refused both of the waypoint's edges.
            var landTile = map.Tiles.GetLandTile(point.X, point.Y);
            bool ground = (TileData.LandTable[landTile.ID & TileData.MaxLandValue].Flags & TileFlag.Impassable) == 0;
            int ceiling = ground ? Math.Max(point.Z, landZ) + SeedClimb : Int32.MaxValue;

            for (int d = 1; d <= SeedScanRange; d++)
            {
                if (landZ + d < ceiling && map.CanFit(point.X, point.Y, landZ + d, 16, false, false, true))
                {
                    z = landZ + d;
                    return true;
                }

                if (map.CanFit(point.X, point.Y, landZ - d, 16, false, false, true))
                {
                    z = landZ - d;
                    return true;
                }
            }

            z = landZ;
            return false;
        }

        /// <summary>
        /// THE STEP FORM of ResolveZ: the window around the hint, then the window around the land,
        /// and nothing wider. Used by the floods when expanding from one tile to its neighbour,
        /// where the hint is the Z the flood is standing at and the window is the climb rule.
        ///
        /// The seed scan must not run here. Tried: with it, the flood stepped from flat ground at
        /// Z 10 straight onto a ramp tile whose stand Z is 17 - a step of 7 that `MovementImpl`
        /// refuses (its budget from land is 2) - and proposed a road the engine would not walk.
        /// </summary>
        public static int ResolveStepZ(Map map, Point3D point)
        {
            int z;

            if (map == null)
            {
                return point.Z;
            }

            return TryStepZ(map, point, out z) ? z : map.GetAverageZ(point.X, point.Y);
        }

        /// <summary>
        /// Whether a door stands on this tile within a mobile's height of the given Z. The same
        /// test NavCorridor.HasDoor applies when a flood asks whether a tile can be stood on.
        /// </summary>
        public static bool DoorAt(Map map, int x, int y, int z)
        {
            IPooledEnumerable items = map.GetItemsInRange(new Point3D(x, y, z), 0);

            try
            {
                foreach (Item item in items)
                {
                    if (item is BaseDoor && Math.Abs(item.Z - z) < 20)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                // The non-generic pooled enumerable must be freed or its pool entry leaks.
                items.Free();
            }

            return false;
        }

        private static bool TryStepZ(Map map, Point3D point, out int z)
        {
            if (TryStandZ(map, point.X, point.Y, point.Z, out z))
            {
                return true;
            }

            // The hint was no use here. Try again from the ground this tile actually has, which is
            // what the old implementation did immediately - correct as a fallback, wrong as a rule.
            int landZ = map.GetAverageZ(point.X, point.Y);

            return TryStandZ(map, point.X, point.Y, landZ, out z);
        }

        /// <summary>
        /// A standable Z within the climb/drop window of a reference, nearest to it first.
        ///
        /// uo-offline's `Walkable.TryFindStandZ` (`Nav/Walkable.cs:118-140`), with our CanFit
        /// flags rather than theirs: `requireSurface` true is what keeps this from answering with
        /// a Z in mid-air over a hole.
        /// </summary>
        private static bool TryStandZ(Map map, int x, int y, int reference, out int z)
        {
            z = reference;

            if (map.CanFit(x, y, reference, 16, false, false, true))
            {
                return true;
            }

            for (int d = 1; d <= MaxDrop; d++)
            {
                if (d <= MaxClimb && map.CanFit(x, y, reference + d, 16, false, false, true))
                {
                    z = reference + d;
                    return true;
                }

                if (map.CanFit(x, y, reference - d, 16, false, false, true))
                {
                    z = reference - d;
                    return true;
                }
            }

            return false;
        }

        /// <summary>A fresh hop: full deadline, bottom of the ladder, no best distance yet.</summary>
        private void ResetHop()
        {
            _rung = StuckRung.None;
            _watchedCycles = 0;
            _bestDistance = Int32.MaxValue;

            // Per HOP, so a route with several arrivals gets one shift each rather than one in
            // total - and so a fresh walk to the same crowded bank is not born already spent.
            _shifted = false;
            _arrivalCheckedAt = Int32.MaxValue;
            _reAnchored = false;
            _approaching = false;

            ResetHopDeadline();
        }

        private void ResetHopDeadline()
        {
            _hopDeadline = Core.TickCount + (long)HopTimeout.TotalMilliseconds;
        }

        /// <summary>
        /// Walks NetState.Instances rather than the sector, per the house rule: the online
        /// client list is short and this runs once per stuck hop, not per tick.
        /// </summary>
        private bool PlayerIsWatching()
        {
            Map map = _mobile.Map;
            Point3D location = _mobile.Location;

            foreach (NetState ns in NetState.Instances)
            {
                Mobile player = ns.Mobile;

                if (player == null || player.Deleted || player.Map != map)
                {
                    continue;
                }

                if (player.InRange(location, PlayerNearTiles))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The waypoint this hop started from, or null when the route began where the bot stood.
        ///
        /// Null is the right answer for the first step and NavEdgeHealth.Strike refuses it: a walk
        /// that begins at the bot's own feet has no authored edge behind it to blame.
        /// </summary>
        private string PreviousWaypointId()
        {
            return _index > 0 && _route != null && _index - 1 < _route.Steps.Count
                ? _route.Steps[_index - 1].WaypointId
                : null;
        }

        private string DescribeHop(NavStep step)
        {
            string from = _index > 0 ? _route.Steps[_index - 1].WaypointId : "(start)";

            return String.Format("'{0}' -> '{1}'", from ?? "(point)", step.WaypointId ?? "(arrival)");
        }

        /// <summary>
        /// The walker currently steering a mobile, or null.
        ///
        /// The active list is short - one entry per actor actually on the move - so this is a
        /// scan rather than a dictionary. It exists for the live-map snapshot, which wants to
        /// draw where an NPC is going, not just where it is.
        /// </summary>
        public static NavWalker For(Mobile mobile)
        {
            if (mobile == null)
            {
                return null;
            }

            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i]._mobile == mobile)
                {
                    return _active[i];
                }
            }

            return null;
        }

        // ---- the shared drive timer ----

        private static void Register(NavWalker walker)
        {
            if (!_active.Contains(walker))
            {
                _active.Add(walker);
            }

            if (_timer == null)
            {
                _timer = Timer.DelayCall(TickInterval, TickInterval, DriveAll);
            }
        }

        private static void Unregister(NavWalker walker)
        {
            _active.Remove(walker);

            if (_active.Count == 0 && _timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        private static void DriveAll()
        {
            if (World.Loading || World.Saving)
            {
                return;
            }

            // Iterate a copy: a walker that finishes or faults removes itself from the list.
            NavWalker[] snapshot = _active.ToArray();

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].Tick();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "A navigation walker faulted and was stopped.");

                    try
                    {
                        snapshot[i].Stop();
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>
        /// A mutable, reference-typed point.
        ///
        /// It exists solely so BaseAI.MoveTo's "m_Path.Goal == p" reference comparison can
        /// succeed across ticks. Do not replace it with a Point3D.
        /// </summary>
        private sealed class NavGoal : IPoint3D
        {
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            public NavGoal(int x, int y, int z)
            {
                _x = x;
                _y = y;
                _z = z;
            }

            public int X
            {
                get { return _x; }
            }

            public int Y
            {
                get { return _y; }
            }

            public int Z
            {
                get { return _z; }
            }
        }
    }
}
