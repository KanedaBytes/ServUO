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
        /// How often walkers are driven. This is a ceiling, not the movement rate: DoMove has
        /// its own NextMove gate derived from the creature's speed, so driving faster than the
        /// creature can walk costs nothing but makes movement smooth.
        /// </summary>
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.25);

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

        /// <summary>Whether the mobile runs rather than walks.</summary>
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

            if (_mobile.InRange(step.Point, range))
            {
                Advance();
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

            if (ai != null)
            {
                ai.MoveTo(_goal, Run, range);
            }
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
        private void HandleStuck(NavStep step)
        {
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
                    Log.Warn(
                        "{0} could not walk {1} after the whole recovery ladder{2}; it was moved. Check that edge.",
                        Who(),
                        DescribeHop(step),
                        _watchedCycles > 0
                            ? String.Format(" and {0} watched cycle(s)", _watchedCycles)
                            : " with nobody watching");

                    Teleport(step);
                    Advance();
                    return;
                }
            }
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
                if (_mobile.Move(directions[i]))
                {
                    taken = directions[i];
                    moved = true;
                    break;
                }
            }

            if (!moved)
            {
                return false;
            }

            for (int step = 1; step < SidestepTiles; step++)
            {
                if (!_mobile.Move(taken))
                {
                    break;
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

        private void Teleport(NavStep step)
        {
            Map map = _mobile.Map;
            Point3D point = new Point3D(step.Point.X, step.Point.Y, ResolveZ(map, step.Point));

            _mobile.MoveToWorld(point, map);
        }

        /// <summary>
        /// A gate hop. The base behaviour is to move the mobile, which is right for a staff
        /// tool and for anything that does not care how it got there; a consumer that should
        /// actually walk onto a teleporter or through a moongate overrides this.
        /// </summary>
        protected virtual void OnTransition(NavStep step)
        {
            Map map = step.Map ?? _mobile.Map;
            Point3D point = new Point3D(step.Point.X, step.Point.Y, ResolveZ(map, step.Point));

            _mobile.MoveToWorld(point, map);
        }

        private int ArrivalRangeFor(NavStep step)
        {
            if (step.Kind == NavStepKind.Arrival)
            {
                return 0;
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

        private void EnsureGoal(NavStep step)
        {
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
        public static int ResolveZ(Map map, Point3D point)
        {
            if (map == null)
            {
                return point.Z;
            }

            if (map.CanFit(point.X, point.Y, point.Z, 16, false, false, true))
            {
                return point.Z;
            }

            return map.GetAverageZ(point.X, point.Y);
        }

        /// <summary>A fresh hop: full deadline, bottom of the ladder, no best distance yet.</summary>
        private void ResetHop()
        {
            _rung = StuckRung.None;
            _watchedCycles = 0;
            _bestDistance = Int32.MaxValue;

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
