using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// The public navigation API: where things are, and how to get there.
    ///
    /// GAME THREAD ONLY. Routing may be followed by a MovementPath call, and ServUO's
    /// FastAStarAlgorithm keeps its whole working set in shared static fields on a singleton
    /// (Scripts/Services/Pathing/FastAStarAlgorithm.cs:17-24). A background thread must go
    /// through LoopQueue.Post / TryPostAndWait.
    ///
    /// Ids passed to TryRoute may name either a waypoint or a destination; a destination
    /// resolves to its declared approach waypoint, falling back to the nearest one.
    /// </summary>
    public static class Nav
    {
        // ---- lookup ----

        public static NavWaypoint Waypoint(string id)
        {
            return NavigationSystem.Graph.Node(id);
        }

        public static NavDestination Destination(string id)
        {
            return NavigationSystem.Destination(id);
        }

        public static NavZone Zone(string id)
        {
            return NavigationSystem.Zone(id);
        }

        public static NavRouteDef RouteDefinition(string id)
        {
            return NavigationSystem.Route(id);
        }

        // ---- spatial ----

        /// <summary>Nearest waypoint on a facet. maxTiles of 0 or less means no limit.</summary>
        public static NavWaypoint NearestWaypoint(Point3D from, Map map, int maxTiles)
        {
            return NavigationSystem.Graph.Nearest(from, map, maxTiles);
        }

        /// <summary>
        /// Nearest destination on a facet, optionally of one type. maxTiles of 0 or less means
        /// no limit; null type means any.
        /// </summary>
        public static NavDestination NearestDestination(Point3D from, Map map, string type, int maxTiles)
        {
            NavDestination best = null;
            int bestDistance = Int32.MaxValue;

            foreach (NavDestination destination in NavigationSystem.DestinationsOn(map))
            {
                if (type != null && !Insensitive.Equals(destination.Type, type))
                {
                    continue;
                }

                int distance = NavGraph.Chebyshev(from, destination.Location);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = destination;
                }
            }

            if (best != null && maxTiles > 0 && bestDistance > maxTiles)
            {
                return null;
            }

            return best;
        }

        /// <summary>Destinations on a facet, filtered by type and/or tag. Null means "any".</summary>
        public static List<NavDestination> Destinations(Map map, string type, string tag)
        {
            var result = new List<NavDestination>();

            foreach (NavDestination destination in NavigationSystem.DestinationsOn(map))
            {
                if (type != null && !Insensitive.Equals(destination.Type, type))
                {
                    continue;
                }

                if (tag != null && !destination.HasTag(tag))
                {
                    continue;
                }

                result.Add(destination);
            }

            return result;
        }

        public static List<NavZone> ZonesAt(Point3D point, Map map)
        {
            var result = new List<NavZone>();

            foreach (NavZone zone in NavigationSystem.ZonesOn(map))
            {
                if (zone.Contains(point.X, point.Y))
                {
                    result.Add(zone);
                }
            }

            return result;
        }

        public static bool HasZoneTag(Point3D point, Map map, string tag)
        {
            foreach (NavZone zone in NavigationSystem.ZonesOn(map))
            {
                if (zone.Contains(point.X, point.Y) && zone.HasTag(tag))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The smallest zone containing a point, which is the one whose tags are most specific.
        /// Used to name auto-generated waypoint ids.
        /// </summary>
        public static NavZone SmallestZoneAt(Point3D point, Map map)
        {
            NavZone best = null;
            int bestArea = Int32.MaxValue;

            foreach (NavZone zone in NavigationSystem.ZonesOn(map))
            {
                if (!zone.Contains(point.X, point.Y))
                {
                    continue;
                }

                // Area, not bounding-box area: a polygon's box is bigger than the polygon, and
                // using it would let a large diagonal zone beat a small rectangle inside it.
                int area = zone.Area;

                if (area < bestArea)
                {
                    bestArea = area;
                    best = zone;
                }
            }

            return best;
        }

        // ---- routing ----

        /// <summary>
        /// Routes between two ids, each of which may name a waypoint or a destination. When the
        /// target is a destination, the route ends with an Arrival step at the destination's own
        /// centre - use the Mobile overload to end on a picked arrival point instead.
        /// </summary>
        public static bool TryRoute(string fromId, string toId, out NavRoute route, out string error)
        {
            return TryRoute(fromId, toId, null, out route, out error);
        }

        public static bool TryRoute(string fromId, string toId, Mobile forMobile, out NavRoute route, out string error)
        {
            route = null;

            string fromWaypoint;
            NavDestination fromDestination;

            if (!TryResolve(fromId, out fromWaypoint, out fromDestination, out error))
            {
                return false;
            }

            string toWaypoint;
            NavDestination toDestination;

            if (!TryResolve(toId, out toWaypoint, out toDestination, out error))
            {
                return false;
            }

            NavRoute found;

            if (!NavigationSystem.Graph.TryFindPath(
                fromWaypoint, toWaypoint, NavigationSystem.RouteCacheMax,
                NavigationSystem.RouteCacheTtlSeconds, out found, out error))
            {
                return false;
            }

            route = AppendArrival(found, toDestination, forMobile);
            return true;
        }

        /// <summary>
        /// Routes from an arbitrary point - a mobile's current position - by plugging it into
        /// the nearest waypoint. Fails if there is no waypoint within the hop cap, which is the
        /// honest answer: the mobile is somewhere the data does not cover.
        /// </summary>
        public static bool TryRouteFrom(Point3D from, Map map, string toId, Mobile forMobile, out NavRoute route, out string error)
        {
            route = null;

            NavWaypoint start = NavigationSystem.Graph.Nearest(from, map, NavigationSystem.HopMaxTiles);

            if (start == null)
            {
                error = String.Format(
                    "no waypoint within {0} tiles of {1} on {2}",
                    NavigationSystem.HopMaxTiles,
                    from,
                    map);
                return false;
            }

            return TryRoute(start.Id, toId, forMobile, out route, out error);
        }

        /// <summary>
        /// Resolves an id to the waypoint a route should start or end at. A destination prefers
        /// its DECLARED approach waypoints and falls back to the nearest - never the other way
        /// round, so an author can override a bad automatic choice.
        /// </summary>
        private static bool TryResolve(string id, out string waypointId, out NavDestination destination, out string error)
        {
            waypointId = null;
            destination = null;
            error = null;

            if (String.IsNullOrWhiteSpace(id))
            {
                error = "no id given";
                return false;
            }

            if (NavigationSystem.Graph.Contains(id))
            {
                waypointId = id;
                return true;
            }

            destination = NavigationSystem.Destination(id);

            if (destination == null)
            {
                error = String.Format("'{0}' is neither a waypoint nor a destination", id);
                return false;
            }

            if (destination.WaypointList != null)
            {
                for (int i = 0; i < destination.WaypointList.Length; i++)
                {
                    if (NavigationSystem.Graph.Contains(destination.WaypointList[i]))
                    {
                        waypointId = destination.WaypointList[i];
                        return true;
                    }
                }
            }

            NavWaypoint nearest = NavigationSystem.Graph.Nearest(destination.Location, destination.Map, 0);

            if (nearest == null)
            {
                error = String.Format("destination '{0}' has no reachable waypoint", id);
                return false;
            }

            waypointId = nearest.Id;
            return true;
        }

        private static NavRoute AppendArrival(NavRoute route, NavDestination destination, Mobile forMobile)
        {
            if (destination == null)
            {
                return route;
            }

            Point3D spot;
            int range;

            if (forMobile == null || !NavArrivals.TryPick(destination, forMobile, out spot, out range))
            {
                spot = destination.Location;
                range = 0;
            }

            // The arrival's own range rides on the step, so the walker can say "arrived" from a
            // tile short of the spot when the data says a place, not a tile.
            var steps = new List<NavStep>(route.Steps);
            steps.Add(new NavStep(spot, destination.Map, null, NavStepKind.Arrival, range));

            return new NavRoute(steps, route.Cost);
        }

        // ---- laps and patrols ----

        /// <summary>
        /// Builds one full lap of an authored route as a walkable step list.
        ///
        /// NavWalker is deliberately one-shot, so looping lives here: a consumer follows the lap
        /// and re-follows it from the Arrived callback. The lap already contains the return leg,
        /// so "re-follow the same route" is all a patrol ever has to do.
        ///
        /// cycle    - the authored waypoints, then back to the first.
        /// pingpong - out along the waypoints, then back down the same line.
        /// oneway   - exactly as authored; Arrived means finished.
        /// </summary>
        public static bool TryBuildRouteLap(string routeId, out NavRoute route, out string error)
        {
            route = null;

            NavRouteDef definition = NavigationSystem.Route(routeId);

            if (definition == null)
            {
                error = String.Format("no usable route '{0}'", routeId);
                return false;
            }

            string[] ids = definition.WaypointList;

            if (ids == null || ids.Length < 2)
            {
                error = String.Format("route '{0}' has fewer than two waypoints", routeId);
                return false;
            }

            var order = new List<string>(ids);

            if (definition.Mode == NavRouteMode.Cycle)
            {
                order.Add(ids[0]);
            }
            else if (definition.Mode == NavRouteMode.PingPong)
            {
                for (int i = ids.Length - 2; i >= 0; i--)
                {
                    order.Add(ids[i]);
                }
            }

            var steps = new List<NavStep>(order.Count);
            double cost = 0.0;

            for (int i = 0; i < order.Count; i++)
            {
                NavWaypoint waypoint = NavigationSystem.Graph.Node(order[i]);

                if (waypoint == null)
                {
                    error = String.Format("route '{0}' names unknown waypoint '{1}'", routeId, order[i]);
                    return false;
                }

                if (i > 0)
                {
                    cost += NavGraph.Chebyshev(steps[i - 1].Point, waypoint.Location);
                }

                steps.Add(new NavStep(waypoint.Location, waypoint.Map, waypoint.Id, NavStepKind.Walk));
            }

            route = new NavRoute(steps, cost);
            error = null;
            return true;
        }

        /// <summary>
        /// Builds a looping patrol between two or more destinations, routing each leg through
        /// the graph. The lap returns to the first destination, so it can be re-followed.
        /// </summary>
        public static bool TryBuildPatrol(IList<string> destinationIds, out NavRoute route, out string error)
        {
            route = null;

            if (destinationIds == null || destinationIds.Count < 2)
            {
                error = "a patrol needs at least two destinations";
                return false;
            }

            var steps = new List<NavStep>();
            double cost = 0.0;

            for (int i = 0; i < destinationIds.Count; i++)
            {
                string from = destinationIds[i];
                string to = destinationIds[(i + 1) % destinationIds.Count];

                NavRoute leg;

                if (!TryRoute(from, to, out leg, out error))
                {
                    return false;
                }

                cost += leg.Cost;

                // Skip the leg's first step after the first leg: it is the previous leg's last.
                for (int j = steps.Count == 0 ? 0 : 1; j < leg.Steps.Count; j++)
                {
                    steps.Add(leg.Steps[j]);
                }
            }

            if (steps.Count < 2)
            {
                error = "the patrol collapsed to a single point";
                return false;
            }

            route = new NavRoute(steps, cost);
            error = null;
            return true;
        }

        // ---- arrivals ----

        /// <summary>
        /// Picks a standing spot at a destination. See NavArrivals for the policy: random pick
        /// plus scatter, with exclusive spots taken exactly and skipped when occupied.
        /// </summary>
        public static bool TryPickArrival(string destinationId, Mobile forMobile, out Point3D spot)
        {
            int range;

            return TryPickArrival(destinationId, forMobile, out spot, out range);
        }

        /// <summary>As above, with the chosen arrival's range - how close counts as arrived.</summary>
        public static bool TryPickArrival(string destinationId, Mobile forMobile, out Point3D spot, out int range)
        {
            spot = Point3D.Zero;
            range = 0;

            NavDestination destination = NavigationSystem.Destination(destinationId);

            return destination != null && NavArrivals.TryPick(destination, forMobile, out spot, out range);
        }
    }
}
