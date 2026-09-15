// NavConnectivity.cs - which islands a bot can reach, counting moongates.
//
// NavGraph labels components over WALK edges, and that is still the right label for everything
// that asks "can a mobile walk from here to there" - the cliff scan, the audit, the corridor
// search. It is the wrong question for "can a bot get there at all", because a public moongate
// joins Moonglow to Britain as surely as a road would, and a player crosses it every day.
//
// So an island is judged here, once, and every consumer asks this rather than re-deriving it:
//
//   Nav.Data      FAILS on an island that holds somewhere to go and has no gate edge into it.
//   NavAdopt      seeds its reach check only with saved waypoints the mainland can reach.
//   [CoreSmoke    runs SelfTest, the three-case fixture below.
//
// The mainland is the anchor's component (Custom.NavHomeWaypoint), with the largest component as
// the fallback for a graph that names no anchor - the same rule NavigationSystem has used since an
// adopt of 481 waypoints inverted "largest".

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>One walk component that is not the mainland and holds a destination or an arrival.</summary>
    public sealed class NavIsland
    {
        public int Component;
        public readonly List<NavWaypoint> Waypoints = new List<NavWaypoint>();
        public readonly List<string> Destinations = new List<string>();
        public int Arrivals;

        /// <summary>True when the mainland reaches it through at least one gate edge.</summary>
        public bool ReachedByGate;
    }

    public static class NavConnectivity
    {
        /// <summary>The mainland: the anchor's component, else the largest. -1 for an empty graph.</summary>
        public static int Mainland(NavGraph graph, string homeId)
        {
            if (graph == null || graph.NodeCount == 0)
            {
                return -1;
            }

            int anchored = graph.ComponentOf(homeId);

            if (anchored >= 0)
            {
                return anchored;
            }

            var sizes = new Dictionary<int, int>();
            int best = -1;
            int bestSize = -1;

            foreach (NavWaypoint waypoint in graph.Nodes)
            {
                int component = graph.ComponentOf(waypoint.Id);
                int size;

                sizes.TryGetValue(component, out size);
                sizes[component] = ++size;

                if (size > bestSize)
                {
                    bestSize = size;
                    best = component;
                }
            }

            return best;
        }

        /// <summary>
        /// Every component the mainland reaches over walk and gate edges, the mainland included.
        /// Empty for an empty graph.
        /// </summary>
        public static HashSet<int> Reachable(NavGraph graph, string homeId)
        {
            int mainland = Mainland(graph, homeId);

            if (mainland < 0)
            {
                return new HashSet<int>();
            }

            foreach (NavWaypoint waypoint in graph.Nodes)
            {
                if (graph.ComponentOf(waypoint.Id) == mainland)
                {
                    return graph.ComponentsReachableFrom(waypoint.Id);
                }
            }

            return new HashSet<int>();
        }

        /// <summary>
        /// Every non-mainland component holding a destination or an arrival, each marked with
        /// whether a gate reaches it. A destination or arrival belongs to the component of its
        /// nearest waypoint, as it always has (NavigationSystem.ComponentNear).
        /// </summary>
        /// <param name="arrivals">Each arrival with the facet of the destination that owns it.</param>
        public static List<NavIsland> Islands(
            NavGraph graph,
            IEnumerable<NavDestination> destinations,
            IEnumerable<KeyValuePair<NavArrival, Map>> arrivals,
            string homeId)
        {
            var result = new List<NavIsland>();

            if (graph == null || graph.ComponentCount <= 1)
            {
                return result;
            }

            int mainland = Mainland(graph, homeId);
            HashSet<int> reachable = Reachable(graph, homeId);

            var byComponent = new Dictionary<int, NavIsland>();

            Func<int, NavIsland> island = component =>
            {
                NavIsland found;

                if (!byComponent.TryGetValue(component, out found))
                {
                    found = new NavIsland { Component = component, ReachedByGate = reachable.Contains(component) };
                    byComponent[component] = found;
                }

                return found;
            };

            if (destinations != null)
            {
                foreach (NavDestination destination in destinations)
                {
                    int component = ComponentNear(graph, destination.Location, destination.Map);

                    if (component >= 0 && component != mainland)
                    {
                        island(component).Destinations.Add(destination.Id);
                    }
                }
            }

            if (arrivals != null)
            {
                foreach (KeyValuePair<NavArrival, Map> pair in arrivals)
                {
                    int component = ComponentNear(graph, pair.Key.Location, pair.Value);

                    if (component >= 0 && component != mainland)
                    {
                        island(component).Arrivals++;
                    }
                }
            }

            foreach (NavWaypoint waypoint in graph.Nodes)
            {
                NavIsland found;

                if (byComponent.TryGetValue(graph.ComponentOf(waypoint.Id), out found))
                {
                    found.Waypoints.Add(waypoint);
                }
            }

            result.AddRange(byComponent.Values);
            result.Sort((a, b) => a.Component.CompareTo(b.Component));

            return result;
        }

        private static int ComponentNear(NavGraph graph, Point3D at, Map map)
        {
            NavWaypoint nearest = graph.Nearest(at, map, 0);

            return nearest == null ? -1 : graph.ComponentOf(nearest.Id);
        }

        // ---- the fixture --------------------------------------------------------------------------

        /// <summary>
        /// Three cases on a hand-built graph, no map data needed:
        ///
        ///   1. an island holding a destination with NO gate into it   -> found, not reached
        ///   2. the same island with a gate edge from the mainland       -> found, reached
        ///   3. an island holding nothing                                 -> not reported at all
        ///
        /// Case 1 is the one Nav.Data fails on. If this check ever passes case 1 as reached, the
        /// Fail can never fire and a gateless island ships silently.
        /// </summary>
        public static bool SelfTest(List<string> report)
        {
            bool ok = true;

            List<NavIsland> without = FixtureIslands(false);
            List<NavIsland> with = FixtureIslands(true);

            NavIsland a1 = Find(without, "conn-dest-a");
            NavIsland a2 = Find(with, "conn-dest-a");

            if (a1 == null || a1.ReachedByGate)
            {
                ok = false;
                report.Add("FAIL gate islands: an island with a destination and no gate was "
                    + (a1 == null ? "not reported" : "reported as reached"));
            }
            else
            {
                report.Add("ok   gate islands: no gate -> unreached (" + a1.Waypoints.Count + " waypoint(s))");
            }

            if (a2 == null || !a2.ReachedByGate)
            {
                ok = false;
                report.Add("FAIL gate islands: an island with a gate edge was "
                    + (a2 == null ? "not reported" : "reported as unreached"));
            }
            else
            {
                report.Add("ok   gate islands: gate edge -> reached");
            }

            foreach (NavIsland island in with)
            {
                if (island.Destinations.Count == 0 && island.Arrivals == 0)
                {
                    ok = false;
                    report.Add("FAIL gate islands: an island holding nothing was reported");
                }
            }

            if (with.Count != 1)
            {
                ok = false;
                report.Add(String.Format("FAIL gate islands: expected 1 island reported, got {0}", with.Count));
            }

            return ok;
        }

        private static NavIsland Find(List<NavIsland> islands, string destinationId)
        {
            foreach (NavIsland island in islands)
            {
                if (island.Destinations.Contains(destinationId))
                {
                    return island;
                }
            }

            return null;
        }

        private static List<NavIsland> FixtureIslands(bool gate)
        {
            var store = new NavigationStore
            {
                Waypoints =
                {
                    new NavWaypoint("conn-home", Map.Trammel, new Point3D(1000, 1000, 0)),
                    new NavWaypoint("conn-pad", Map.Trammel, new Point3D(1010, 1000, 0)),
                    new NavWaypoint("conn-a1", Map.Trammel, new Point3D(3000, 1000, 0)),
                    new NavWaypoint("conn-a2", Map.Trammel, new Point3D(3010, 1000, 0)),
                    new NavWaypoint("conn-b1", Map.Trammel, new Point3D(1000, 3000, 0)),
                    new NavWaypoint("conn-b2", Map.Trammel, new Point3D(1010, 3000, 0))
                },
                Edges =
                {
                    new NavEdge("conn-home", "conn-pad"),
                    new NavEdge("conn-a1", "conn-a2"),
                    new NavEdge("conn-b1", "conn-b2")
                }
            };

            if (gate)
            {
                store.Edges.Add(new NavEdge("conn-pad", "conn-a1") { KindName = "gate" });
            }

            foreach (NavWaypoint w in store.Waypoints) { w.Bind(); }
            foreach (NavEdge e in store.Edges) { e.Bind(); }

            var destination = new NavDestination { Id = "conn-dest-a", X = 3005, Y = 1002, Z = 0 };
            destination.Bind();

            var graph = new NavGraph();
            graph.Build(store, new Dictionary<string, double>(), 12);

            return Islands(graph, new[] { destination }, null, "conn-home");
        }
    }
}
