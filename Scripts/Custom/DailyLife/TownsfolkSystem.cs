using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// The route walkers - a courier, a farmer - who are not phase-driven at all.
    ///
    /// They exist from startup and walk their lap continuously. They are still ephemeral
    /// (deleted on world load and recreated here), so the JSON stays the single source of truth.
    /// </summary>
    public static class TownsfolkSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        private static readonly List<DailyLifeTownsfolk> _walkers = new List<DailyLifeTownsfolk>();

        public static int WalkerCount
        {
            get { return _walkers.Count; }
        }

        public static void Initialize()
        {
            Spawn();
        }

        /// <summary>
        /// Rebuilds the walkers from the current config. The teardown is essential - without it
        /// a reload would leave the previous courier and farmer walking alongside the new ones.
        /// </summary>
        public static void Reload()
        {
            for (int i = _walkers.Count - 1; i >= 0; i--)
            {
                if (_walkers[i] != null && !_walkers[i].Deleted)
                {
                    _walkers[i].Delete();
                }
            }

            _walkers.Clear();

            Spawn();
        }

        private static void Spawn()
        {
            foreach (TownsfolkConfig entry in DailyLifeSystem.Config.Townsfolk)
            {
                NavRoute lap;
                string error;

                if (!Nav.TryBuildRouteLap(entry.Route, out lap, out error))
                {
                    Log.Warn("Townsfolk '{0}' not spawned: {1}", entry.Id, error);
                    continue;
                }

                Map map = lap.Steps[0].Map;

                if (map == null || map == Map.Internal)
                {
                    Log.Warn("Townsfolk '{0}' not spawned: route '{1}' is not on a facet.",
                        entry.Id, entry.Route);
                    continue;
                }

                var walker = new DailyLifeTownsfolk();

                walker.ApplyBody(entry.Body);

                if (!String.IsNullOrEmpty(entry.Name))
                {
                    walker.Name = entry.Name;
                }

                if (!String.IsNullOrEmpty(entry.Title))
                {
                    walker.Title = entry.Title;
                }

                // No home tether: the route is the plan, and a Home would have WalkRandomInHome
                // pulling backwards whenever the walker paused.
                walker.Home = Point3D.Zero;

                walker.MoveToWorld(lap.Steps[0].Point, map);
                walker.FollowLap(lap);

                _walkers.Add(walker);
            }
        }
    }
}
