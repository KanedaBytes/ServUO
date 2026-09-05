using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Deploys the night watch at dusk and stands it down at dawn.
    ///
    /// A post is described one of three ways, and all three come from the same config field pair:
    ///   route          - an authored patrol line, walked as a lap for ever
    ///   one destination - a stationary post, standing on one of its arrival points
    ///   two or more     - a patrol routed between them through the graph, looping
    ///
    /// The exclusive flag on an arrival point is what makes the stationary case work: a guard
    /// post is a specific tile, and a second watchman must not take it.
    /// </summary>
    public static class NightWatchSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        private static readonly List<DailyLifeWatchman> _watch = new List<DailyLifeWatchman>();

        public static int WatchCount
        {
            get { return _watch.Count; }
        }

        public static void Initialize()
        {
            DayCycleSystem.PhaseChanged += OnPhaseChanged;
            ApplyPhase(DayCycleSystem.Current);
        }

        private static void OnPhaseChanged(DayPhase oldPhase, DayPhase newPhase)
        {
            ApplyPhase(newPhase);
        }

        public static void ApplyPhase(DayPhase phase)
        {
            if (phase.IsAfterDark())
            {
                Deploy();
            }
            else
            {
                StandDown();
            }
        }

        /// <summary>
        /// Rebuilds the watch from the current config. Stands down first because Deploy
        /// early-returns outright when the watch is already out, so changed posts would
        /// otherwise be ignored.
        /// </summary>
        public static void Reload()
        {
            StandDown();
            ApplyPhase(DayCycleSystem.Current);
        }

        private static void Deploy()
        {
            Prune();

            if (_watch.Count > 0)
            {
                return;
            }

            foreach (WatchPostConfig post in DailyLifeSystem.Config.Watch)
            {
                if (post.IsPatrolRoute)
                {
                    DeployPatrolRoute(post);
                }
                else if (post.DestinationList.Length == 1)
                {
                    DeployStationary(post);
                }
                else
                {
                    DeployPatrolDestinations(post);
                }
            }
        }

        private static void DeployPatrolRoute(WatchPostConfig post)
        {
            NavRoute lap;
            string error;

            if (!Nav.TryBuildRouteLap(post.Route, out lap, out error))
            {
                Log.Warn("Watch post '{0}' not deployed: {1}", post.Id, error);
                return;
            }

            Deploy(post, lap, lap.Steps[0].Point, lap.Steps[0].Map, 4);
        }

        private static void DeployPatrolDestinations(WatchPostConfig post)
        {
            NavRoute lap;
            string error;

            if (!Nav.TryBuildPatrol(post.DestinationList, out lap, out error))
            {
                Log.Warn("Watch post '{0}' not deployed: {1}", post.Id, error);
                return;
            }

            Deploy(post, lap, lap.Steps[0].Point, lap.Steps[0].Map, 4);
        }

        private static void DeployStationary(WatchPostConfig post)
        {
            NavDestination destination = Nav.Destination(post.DestinationList[0]);

            if (destination == null || destination.Map == null || destination.Map == Map.Internal)
            {
                Log.Warn("Watch post '{0}' not deployed: no destination '{1}'",
                    post.Id, post.DestinationList[0]);
                return;
            }

            var watchman = new DailyLifeWatchman();

            Point3D spot;

            if (!NavArrivals.TryPick(destination, watchman, out spot))
            {
                watchman.Delete();
                Log.Warn("Watch post '{0}' not deployed: no free arrival point at '{1}'",
                    post.Id, destination.Id);
                return;
            }

            // RangeHome 0 on a stationary post: WalkRandomInHome walks the mobile onto Home and
            // then stops, which is exactly what standing a post means.
            Place(watchman, spot, destination.Map, 0);
            _watch.Add(watchman);
        }

        private static void Deploy(WatchPostConfig post, NavRoute lap, Point3D start, Map map, int rangeHome)
        {
            if (map == null || map == Map.Internal)
            {
                Log.Warn("Watch post '{0}' not deployed: its route is not on a facet.", post.Id);
                return;
            }

            var watchman = new DailyLifeWatchman();

            Place(watchman, start, map, rangeHome);

            watchman.FollowLap(lap);

            _watch.Add(watchman);
        }

        private static void Place(DailyLifeWatchman watchman, Point3D spot, Map map, int rangeHome)
        {
            watchman.Home = spot;
            watchman.RangeHome = rangeHome;
            watchman.MoveToWorld(spot, map);
        }

        private static void StandDown()
        {
            for (int i = _watch.Count - 1; i >= 0; i--)
            {
                DailyLifeWatchman watchman = _watch[i];

                if (watchman != null && !watchman.Deleted)
                {
                    watchman.Delete();
                }
            }

            _watch.Clear();
        }

        private static void Prune()
        {
            for (int i = _watch.Count - 1; i >= 0; i--)
            {
                if (_watch[i] == null || _watch[i].Deleted)
                {
                    _watch.RemoveAt(i);
                }
            }
        }
    }
}
