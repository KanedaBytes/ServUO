using System;
using System.Collections.Generic;

using Server.Mobiles;
using Server.Network;

namespace Server.Custom
{
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

        /// <summary>Repaths before the hop is declared genuinely stuck.</summary>
        public const int HopRetries = 2;

        /// <summary>A player this close makes a teleport visible, so we hold instead.</summary>
        public const int PlayerNearTiles = 20;

        /// <summary>Arrival tolerance when a waypoint does not override it.</summary>
        public const int DefaultArrivalRange = 2;

        private static readonly List<NavWalker> _active = new List<NavWalker>();
        private static Timer _timer;

        private readonly BaseCreature _mobile;

        private NavRoute _route;
        private int _index;
        private NavGoal _goal;
        private long _hopDeadline;
        private int _retries;
        private bool _holding;

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

        /// <summary>Whether the mobile runs rather than walks.</summary>
        public bool Run { get; set; }

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
            _retries = 0;
            _holding = false;
            _goal = null;

            ResetHopDeadline();
            Register(this);
        }

        public void Stop()
        {
            _route = null;
            _goal = null;
            _index = 0;
            _retries = 0;
            _holding = false;

            Unregister(this);
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
            _retries = 0;
            _holding = false;

            ResetHopDeadline();

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
        /// The hop failed. Retry it a couple of times, then decide between holding and
        /// cheating - because a mobile frozen against a wall for ever is worse than one that
        /// steps over it while nobody is looking.
        /// </summary>
        private void HandleStuck(NavStep step)
        {
            if (_retries < HopRetries)
            {
                _retries++;

                // Dropping the goal instance makes MoveTo's reference comparison fail, which
                // builds a fresh PathFollower - a repath, without reaching into BaseAI.
                _goal = null;
                ResetHopDeadline();
                return;
            }

            if (PlayerIsWatching())
            {
                if (!_holding)
                {
                    _holding = true;

                    Log.Debug(
                        "{0} is stuck on {1} but a player is nearby; holding.",
                        _mobile.Name ?? _mobile.GetType().Name,
                        DescribeHop(step));
                }

                ResetHopDeadline();
                return;
            }

            // This log line IS the bug report: it names the edge in the data that is wrong.
            Log.Warn(
                "{0} could not walk {1}; nobody was watching, so it was moved. Check that edge.",
                _mobile.Name ?? _mobile.GetType().Name,
                DescribeHop(step));

            Teleport(step);
            Advance();
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

            _goal = new NavGoal(step.Point.X, step.Point.Y, ResolveZ(_mobile.Map, step.Point));
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
