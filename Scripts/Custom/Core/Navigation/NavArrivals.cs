using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Picks where a mobile actually stands when it gets somewhere.
    ///
    /// STOCHASTIC, NOT ALLOCATED. There is no claim/release registry: a random arrival point
    /// plus a small scatter is stateless, survives a restart, cannot leak a claim, and is what
    /// both reference shards converged on. ServUO's CanSpawnMobile already refuses an occupied
    /// tile, so the failure mode of two mobiles choosing the same spot is that the second one
    /// stands beside it.
    ///
    /// The exception is an EXCLUSIVE arrival point - a guard post, where the point is that the
    /// guard stands precisely there. Those are taken exactly, with no scatter, and are skipped
    /// while anyone is within a tile of them. That check is a live world query, not a registry,
    /// so nothing has to be released.
    /// </summary>
    public static class NavArrivals
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>Attempts before a scattered spot falls back to the exact tile.</summary>
        private const int ScatterAttempts = 6;

        public static bool TryPick(NavDestination destination, Mobile forMobile, out Point3D spot)
        {
            spot = Point3D.Zero;

            if (destination == null)
            {
                return false;
            }

            Map map = forMobile != null && forMobile.Map != null && forMobile.Map != Map.Internal
                ? forMobile.Map
                : destination.Map;

            if (map == null || map == Map.Internal)
            {
                return false;
            }

            List<NavArrival> candidates = Eligible(destination, map, forMobile);

            if (candidates.Count > 0)
            {
                NavArrival chosen = candidates[Utility.Random(candidates.Count)];

                if (chosen.Exclusive)
                {
                    spot = chosen.Location;
                    return true;
                }

                spot = Scatter(chosen.Location, map);
                return true;
            }

            // Everything eligible is taken, or the destination has no arrival points at all.
            // Standing still would be worse than standing roughly right, so scatter around the
            // centre - and say so, because it means the data wants another arrival point.
            Log.Debug(
                "No free arrival point at '{0}'; scattering around its centre.",
                destination.Id);

            spot = Scatter(destination.Location, map);
            return true;
        }

        private static List<NavArrival> Eligible(NavDestination destination, Map map, Mobile forMobile)
        {
            var result = new List<NavArrival>();

            if (destination.ArrivalList == null)
            {
                return result;
            }

            foreach (NavArrival arrival in destination.ArrivalList)
            {
                if (arrival.Exclusive && IsOccupied(arrival.Location, map, forMobile))
                {
                    continue;
                }

                result.Add(arrival);
            }

            return result;
        }

        /// <summary>True if anyone other than the asker is standing within a tile.</summary>
        private static bool IsOccupied(Point3D point, Map map, Mobile forMobile)
        {
            IPooledEnumerable<Mobile> eable = map.GetMobilesInRange(point, 1);

            try
            {
                foreach (Mobile mobile in eable)
                {
                    if (mobile != forMobile && !mobile.Deleted)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                eable.Free();
            }

            return false;
        }

        /// <summary>
        /// A random offset within the configured radius, validated with CanSpawnMobile so the
        /// mobile is never sent into a wall. Falls back to the exact tile, which is what the
        /// author asked for anyway.
        /// </summary>
        private static Point3D Scatter(Point3D around, Map map)
        {
            int radius = NavigationSystem.ArrivalScatter;

            if (radius > 0)
            {
                for (int i = 0; i < ScatterAttempts; i++)
                {
                    int x = around.X + Utility.RandomMinMax(-radius, radius);
                    int y = around.Y + Utility.RandomMinMax(-radius, radius);
                    int z = map.GetAverageZ(x, y);

                    if (map.CanSpawnMobile(x, y, z))
                    {
                        return new Point3D(x, y, z);
                    }
                }
            }

            return around;
        }
    }
}
