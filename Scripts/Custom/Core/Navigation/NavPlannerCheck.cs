// NavPlannerCheck.cs - is the ROUTE PLANNER's heuristic admissible? Two hand-built graphs say.
//
// WHAT THIS IS ABOUT, AND WHAT IT IS NOT
// --------------------------------------
// This is NavGraph's A* over the waypoint graph - the search that picks which waypoints a bot
// walks through. It is NOT the tile pathfinder; that is FastAStarAlgorithm and NavPathfinder.cs.
// The two get confused because both are called "the pathfinder", and they fail differently.
//
// REVIEW.md:91 asked for this test by name:
//
//   "the graph heuristic uses the cheapest individual tag multiplier, while edge costs multiply
//    all their tags. Multiple discounted tags can therefore make the heuristic too large.
//    Fixed-cost gate edges also permit shortcuts for which geographic distance is not a lower
//    bound. ... before activating gate routing, use a provably admissible lower bound or zero
//    heuristic, and test against Dijkstra on tiny graphs with stacked discounts and gate
//    shortcuts."
//
// NavGraph.cs:568-576 already concedes both cases in prose. This turns the concession into a
// measurement, because a claim in a comment is not a test and the Navigation README's *Current
// contract* says the two cases "must be settled before" gate routing is activated.
//
// HOW IT ANSWERS
// --------------
// Two graphs, each a handful of waypoints built in memory - NavigationStore is a plain POCO with
// settable Waypoints and Edges (NavRecords.cs:143-185), so NavGraph.Build takes one with no file,
// no fixture and no reload of the live graph.
//
//   STACKED DISCOUNTS. A short edge carrying two tags at 0.5 each costs 0.25x its length, because
//   AddEdge multiplies ALL of an edge's tags (NavGraph.cs:273-286). The heuristic scales Chebyshev
//   by _minMultiplier, the cheapest SINGLE tag - 0.5 (NavGraph.cs:214-221, :742-751). So the
//   heuristic can exceed the true remaining cost, which is what inadmissible means, and A* may
//   close a node before its cheapest route is found.
//
//   A GATE SHORTCUT. A gate edge costs a flat GateBaseCost of 30 (NavGraph.cs:120, :288-292)
//   however far it reaches. Geographic distance is no lower bound on a cost that ignores
//   geography, so a long gate can be cheap and the heuristic can promise more than the road costs.
//
// Each is compared against a plain Dijkstra over the SAME costs, written here rather than reused,
// because the whole question is whether the heuristic changes the answer and a comparison against
// something that shared the heuristic would answer nothing. Dijkstra has no heuristic at all, so
// its answer is the true optimum by construction.
//
// WHAT A FAILURE MEANS, AND WHAT IT DOES NOT
// -------------------------------------------
// Reported, not fixed. Neither case arises in the live data - gate routing is not active and no
// gate edge has ever been authored (Nav.Data reports 0) - so a disagreement here is a fact about
// what the planner WOULD do, not about what it does. That is the whole reason to have it before
// somebody authors the first gate.
//
// The check reports Ok when the two agree, and Warn - never Fail - when they do not: a warning on
// a hypothetical is honest, and a red check for a case the shard cannot reach is one people learn
// to ignore.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Server.Commands;

namespace Server.Custom
{
    public static class NavPlannerCheck
    {
        private static string _lastDetail = "not run yet";
        private static bool _lastAgreed = true;

        public static void Initialize()
        {
            CommandSystem.Register("NavPlannerCheck", AccessLevel.Administrator, OnCommand);
            HealthCheck.Register("Nav.Planner", BuildHealthResult);
        }

        [Usage("NavPlannerCheck")]
        [Description(
            "Compare NavGraph's A* against Dijkstra on hand-built graphs with stacked cost-tag "
            + "discounts and a gate edge - the admissibility test REVIEW.md:91 asked for. Reports "
            + "only; nothing in the live graph is touched.")]
        private static void OnCommand(CommandEventArgs e)
        {
            IList<string> report;

            Run(out report);

            foreach (string line in report)
            {
                e.Mobile.SendMessage(line);
            }
        }

        // ---- the two graphs -----------------------------------------------------------------------

        /// <summary>
        /// Every case, run. Returns true when A* matched Dijkstra everywhere.
        ///
        /// The graphs are laid out on a line at y = 0 so Chebyshev distance is just the X gap and
        /// the arithmetic in a failure message can be checked by hand.
        /// </summary>
        public static bool Run(out IList<string> report)
        {
            var lines = new List<string>();
            report = lines;

            int cases = 0;
            int disagreed = 0;
            int surprises = 0;

            foreach (Case c in Cases())
            {
                cases++;

                var graph = new NavGraph();
                graph.Build(c.Store, c.CostTags, c.HopCap);

                NavRoute astar;
                string error;

                bool found = graph.TryFindPath(c.From, c.To, 0, 0, out astar, out error);

                double truth;
                List<string> truthPath;
                bool reachable = Dijkstra(c, out truth, out truthPath);

                if (!reachable || Math.Abs(truth - c.ExpectedTruth) > 0.0001)
                {
                    surprises++;
                    lines.Add(String.Format(
                        CultureInfo.InvariantCulture,
                        "  {0}: DID NOT CONSTRUCT - the oracle costs {1} but the case was built to "
                        + "cost {2:F2}. The graph is not the graph this case is about.",
                        c.Name,
                        reachable ? truth.ToString("F2", CultureInfo.InvariantCulture) : "nothing (unreachable)",
                        c.ExpectedTruth));
                    continue;
                }

                if (!found || astar == null)
                {
                    surprises++;
                    lines.Add(String.Format(
                        "  {0}: A* found no route ({1}); Dijkstra {2}",
                        c.Name, error ?? "no reason given",
                        reachable ? String.Format("costs {0:F2}", truth) : "agrees there is none"));
                    continue;
                }

                bool same = Math.Abs(astar.Cost - truth) < 0.0001;

                if (!same)
                {
                    disagreed++;
                }

                string verdict;

                if (same != c.ExpectDisagreement)
                {
                    verdict = same
                        ? "AGREE, as expected"
                        : "DISAGREE, as expected - the heuristic overshot";
                }
                else
                {
                    surprises++;
                    verdict = same
                        ? "AGREES AND WAS NOT EXPECTED TO - was the planner heuristic fixed?"
                        : "DISAGREES AND WAS NOT EXPECTED TO - the heuristic overshot somewhere new";
                }

                lines.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "  {0}: A* {1:F2} via {2} | Dijkstra {3:F2} via {4} | {5}",
                    c.Name, astar.Cost, Describe(astar), truth, String.Join("->", truthPath.ToArray()),
                    verdict));
            }

            string liveWarning = InspectLiveData();

            _lastAgreed = liveWarning == null && surprises == 0;

            _lastDetail = String.Format(
                "{0} constructed case(s), {1} showing the overshoot REVIEW.md:91 names, {2} "
                + "surprise(s). Live graph: {3}",
                cases, disagreed, surprises, liveWarning ?? _liveSummary);

            lines.Add("  live data: " + (liveWarning ?? _liveSummary));
            lines.Insert(0, "Nav.Planner: " + _lastDetail);

            return _lastAgreed;
        }

        private static string _liveSummary = "not inspected";

        /// <summary>
        /// Whether the constructed failure above can reach the LIVE graph, which is the only
        /// question that decides whether anybody has to do anything.
        ///
        /// The heuristic is Chebyshev scaled by the single cheapest cost tag, and an edge is
        /// scaled by the PRODUCT of all of its. So the heuristic is a valid lower bound exactly
        /// while no edge's product falls below that cheapest single tag. Two conditions are
        /// sufficient and both are checkable in a line:
        ///
        ///   - at most one cost tag is below 1.0, so no two discounts exist to stack;
        ///   - no edge's tag product is below the cheapest single multiplier.
        ///
        /// The second is the real test and the first is why it holds. Measured on the shipped
        /// graph, 12 September 2026: five cost tags of which exactly ONE discounts (road, 0.9),
        /// and exactly one edge carries two tags at all - trinsic-alchemist-slope-1 ->
        /// trinsic-alchemist-slope-2, "road stairs", 0.9 x 1.3 = 1.17, a net PENALTY. So the
        /// heuristic is admissible on the data as authored, and this turns that from a fact
        /// somebody checked once into one the shard rechecks every sixty seconds.
        ///
        /// Null means the live graph is safe.
        /// </summary>
        private static string InspectLiveData()
        {
            NavigationStore store = NavigationSystem.Store;

            if (store == null || store.CostTags == null || store.Edges == null)
            {
                _liveSummary = "no graph loaded, so the live check did not run";
                return null;
            }

            var multipliers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double cheapest = 1.0;
            int discounting = 0;

            foreach (NavCostTag tag in store.CostTags)
            {
                multipliers[tag.Tag] = tag.Multiplier;

                if (tag.Multiplier < 1.0)
                {
                    discounting++;
                }

                if (tag.Multiplier < cheapest)
                {
                    cheapest = tag.Multiplier;
                }
            }

            // Positive infinity, not 1.0: seeded at 1.0 the line would print "worst product
            // 1.00" for a graph whose only stacked edge multiplies out to 1.17, which is a number
            // that appears in a health report and is not true of anything.
            string worstEdge = null;
            double worstProduct = Double.PositiveInfinity;
            int stacked = 0;

            foreach (NavEdge edge in store.Edges)
            {
                if (edge.TagList == null || edge.TagList.Length < 2)
                {
                    continue;
                }

                stacked++;

                double product = 1.0;

                foreach (string tag in edge.TagList)
                {
                    double m;

                    if (multipliers.TryGetValue(tag, out m))
                    {
                        product *= m;
                    }
                }

                if (product < worstProduct)
                {
                    worstProduct = product;
                    worstEdge = edge.From + " -> " + edge.To;
                }
            }

            _liveSummary = String.Format(
                CultureInfo.InvariantCulture,
                "{0} cost tag(s), {1} of them discounting (cheapest {2:F2}); {3} edge(s) carry two "
                + "or more tags{4} - the heuristic is a valid lower bound here",
                multipliers.Count, discounting, cheapest, stacked,
                worstEdge == null
                    ? ""
                    : String.Format(
                        CultureInfo.InvariantCulture,
                        ", cheapest product {0:F2} ({1})", worstProduct, worstEdge));

            if (worstProduct < cheapest - 0.0001)
            {
                return String.Format(
                    CultureInfo.InvariantCulture,
                    "THE LIVE GRAPH CAN NOW HIT THIS: edge {0} multiplies out to {1:F2}, below the "
                    + "cheapest single tag {2:F2} that the heuristic assumes. The route planner may "
                    + "return a more expensive route than exists. See NavGraph.cs:568-576.",
                    worstEdge, worstProduct, cheapest);
            }

            if (discounting > 1)
            {
                return String.Format(
                    "{0} cost tags now discount. Nothing stacks two of them yet, but the moment an "
                    + "edge does, the heuristic stops being a lower bound. See NavGraph.cs:568-576.",
                    discounting);
            }

            return null;
        }

        private static string Describe(NavRoute route)
        {
            var ids = new List<string>();

            foreach (NavStep step in route.Steps)
            {
                ids.Add(step.WaypointId);
            }

            return String.Join("->", ids.ToArray());
        }

        private sealed class Case
        {
            public string Name;
            public NavigationStore Store;
            public Dictionary<string, double> CostTags;
            public int HopCap;
            public string From;
            public string To;

            /// <summary>
            /// What Dijkstra must find, worked out by hand from the edge list.
            ///
            /// THIS FIELD IS THE POINT OF THE WHOLE FILE BEING TRUSTWORTHY. The first version of
            /// this check reported "0 disagreements" while testing nothing: NavIds.Split separates
            /// tags on a SPACE (NavRecords.cs:20), the cases wrote "road,paved", so every tag
            /// lookup missed, every multiplier was 1, and the two searches agreed on a graph with
            /// no discounts in it. A vacuous green is worse than a red. So each case now declares
            /// the answer it was built to have, and a case whose oracle disagrees with its own
            /// declaration reports DID NOT CONSTRUCT instead of quietly passing.
            /// </summary>
            public double ExpectedTruth;

            /// <summary>
            /// Whether this case is built to make A* get the WRONG answer.
            ///
            /// Two of these exist to demonstrate a mechanism, not to police it, and a check that
            /// went red because a demonstration demonstrated would be one nobody reads. So a case
            /// says up front what it expects, and only a MISMATCH is a finding: a case built to
            /// fail that now passes means either the planner was fixed - worth saying out loud -
            /// or the case stopped constructing, which ExpectedTruth catches first.
            ///
            /// What decides this check's colour is the live graph, further down, because that is
            /// the only part anybody can act on.
            /// </summary>
            public bool ExpectDisagreement;
        }

        private static IEnumerable<Case> Cases()
        {
            yield return StackedDiscounts();
            yield return GateShortcut();
            yield return GateShortcutDeep();
            yield return StackedDiscountsLong();
        }

        /// <summary>
        /// The stacked-discount case, sized so the heuristic actually bites.
        ///
        /// A -> B -> C along the line, and a detour A -> D -> C. The direct leg B->C carries TWO
        /// 0.5 tags, so it costs 0.25 per tile; the heuristic at B assumes 0.5 per tile and
        /// therefore promises MORE than the road costs. If that overshoot is enough to make A*
        /// close C by the detour first, the two searches disagree.
        /// </summary>
        private static Case StackedDiscounts()
        {
            var store = new NavigationStore();

            store.Waypoints.Add(Wp("a", 0));
            store.Waypoints.Add(Wp("b", 10));
            store.Waypoints.Add(Wp("c", 30));
            store.Waypoints.Add(Wp("d", 12));

            store.Edges.Add(Edge("a", "b", null));
            store.Edges.Add(Edge("b", "c", "road paved"));
            store.Edges.Add(Edge("a", "d", null));
            store.Edges.Add(Edge("d", "c", null));

            foreach (NavWaypoint w in store.Waypoints) { w.Bind(); }
            foreach (NavEdge e in store.Edges) { e.Bind(); }

            return new Case
            {
                // a->b 10, b->c 20 tiles x 0.5 x 0.5 = 5. Total 15, against 12 + 18 = 30 the
                // other way.
                // Agrees, and is kept BECAUSE it agrees: the overshoot is only 5 here, not
                // enough to reorder the pops. It is what shows the fault is a function of
                // distance rather than of stacking as such.
                ExpectedTruth = 15.0,
                ExpectDisagreement = false,
                Name = "stacked discounts (two 0.5 tags on one edge)",
                Store = store,
                CostTags = new Dictionary<string, double> { { "road", 0.5 }, { "paved", 0.5 } },
                HopCap = 64,
                From = "a",
                To = "c"
            };
        }

        /// <summary>
        /// The same shape stretched, because admissibility failures scale with distance: the
        /// heuristic's overshoot is proportional to the remaining Chebyshev gap, so a longer
        /// discounted leg is a harder test than a short one.
        /// </summary>
        private static Case StackedDiscountsLong()
        {
            var store = new NavigationStore();

            store.Waypoints.Add(Wp("a", 0));
            store.Waypoints.Add(Wp("b", 5));
            store.Waypoints.Add(Wp("c", 200));
            store.Waypoints.Add(Wp("d", 60));
            store.Waypoints.Add(Wp("e", 130));

            store.Edges.Add(Edge("a", "b", null));
            store.Edges.Add(Edge("b", "c", "road paved"));
            store.Edges.Add(Edge("a", "d", "road"));
            store.Edges.Add(Edge("d", "e", "road"));
            store.Edges.Add(Edge("e", "c", "road"));

            foreach (NavWaypoint w in store.Waypoints) { w.Bind(); }
            foreach (NavEdge e in store.Edges) { e.Bind(); }

            return new Case
            {
                // a->b 5, b->c 195 x 0.25 = 48.75. Total 53.75, against
                // 60x0.5 + 70x0.5 + 70x0.5 = 100 round the discounted road.
                // Disagrees, and this is the demonstration: A* returns 100.00 by road while
                // the true optimum is 53.75 through the discounted leg - 86% worse. The overshoot
                // scales with the remaining distance, so the shape that is harmless at 20 tiles
                // is not at 200.
                ExpectedTruth = 53.75,
                ExpectDisagreement = true,
                Name = "stacked discounts over 200 tiles",
                Store = store,
                CostTags = new Dictionary<string, double> { { "road", 0.5 }, { "paved", 0.5 } },
                HopCap = 256,
                From = "a",
                To = "c"
            };
        }

        /// <summary>
        /// The gate case. a -> gate -> far is a flat 30 whatever the distance; the walked road
        /// a -> m -> far costs its length. Geographic distance is no lower bound on the gate, so
        /// the heuristic at `a` can promise more than the cheapest route costs.
        /// </summary>
        private static Case GateShortcut()
        {
            var store = new NavigationStore();

            store.Waypoints.Add(Wp("a", 0));
            store.Waypoints.Add(Wp("m", 20));
            store.Waypoints.Add(Wp("far", 500));

            store.Edges.Add(Edge("a", "m", null));
            store.Edges.Add(Edge("m", "far", null));

            NavEdge gate = Edge("a", "far", null);
            gate.KindName = "gate";
            store.Edges.Add(gate);

            foreach (NavWaypoint w in store.Waypoints) { w.Bind(); }
            foreach (NavEdge e in store.Edges) { e.Bind(); }

            return new Case
            {
                // The gate is GateBaseCost 30 flat; the road is 20 + 480 = 500.
                ExpectedTruth = 30.0,
                ExpectDisagreement = false,
                Name = "gate shortcut (flat 30 against a 500-tile road)",
                Store = store,
                // A cost tag is present but unused by any edge, so _minMultiplier is below 1 and
                // the heuristic is scaled exactly as it would be on the live graph.
                CostTags = new Dictionary<string, double> { { "road", 0.9 } },
                HopCap = 1024,
                From = "a",
                To = "far"
            };
        }

        /// <summary>
        /// The gate one step further in, because the shallow case above proves less than it looks.
        /// There the gate leaves the START node, so it is relaxed on the very first expansion and
        /// no heuristic could get in the way. Here the gate hangs off a node the search has to
        /// reach first, which is the arrangement a real moongate would have: town -> gate pad ->
        /// somewhere far. If a flat-cost edge can mislead the search at all, it is here.
        /// </summary>
        private static Case GateShortcutDeep()
        {
            var store = new NavigationStore();

            store.Waypoints.Add(Wp("a", 0));
            store.Waypoints.Add(Wp("pad", 40));
            store.Waypoints.Add(Wp("m", 200));
            store.Waypoints.Add(Wp("far", 900));

            store.Edges.Add(Edge("a", "pad", null));
            store.Edges.Add(Edge("a", "m", null));
            store.Edges.Add(Edge("m", "far", null));

            NavEdge gate = Edge("pad", "far", null);
            gate.KindName = "gate";
            store.Edges.Add(gate);

            foreach (NavWaypoint w in store.Waypoints) { w.Bind(); }
            foreach (NavEdge e in store.Edges) { e.Bind(); }

            return new Case
            {
                // a->pad 40, then the gate's flat 30. Total 70, against 200 + 700 = 900 by road.
                ExpectedTruth = 70.0,
                ExpectDisagreement = false,
                Name = "gate shortcut two hops in",
                Store = store,
                CostTags = new Dictionary<string, double> { { "road", 0.9 } },
                HopCap = 1024,
                From = "a",
                To = "far"
            };
        }

        private static NavWaypoint Wp(string id, int x)
        {
            return new NavWaypoint { Id = id, MapName = "Trammel", X = x, Y = 0, Z = 0 };
        }

        private static NavEdge Edge(string from, string to, string tags)
        {
            return new NavEdge { From = from, To = to, Tags = tags, KindName = "walk" };
        }

        // ---- the oracle -----------------------------------------------------------------------

        /// <summary>
        /// Plain Dijkstra over the same cost model NavGraph.AddEdge applies: Chebyshev distance
        /// times the product of every tag multiplier for a walk edge, GateBaseCost times the same
        /// product for a gate. No heuristic, so the answer is optimal by construction.
        ///
        /// Written here rather than reused from NavGraph on purpose. The question is whether the
        /// heuristic changes the answer; an oracle that shared any part of the search would be
        /// unable to tell.
        /// </summary>
        private static bool Dijkstra(Case c, out double cost, out List<string> path)
        {
            cost = Double.PositiveInfinity;
            path = new List<string>();

            var location = new Dictionary<string, NavWaypoint>(StringComparer.OrdinalIgnoreCase);

            foreach (NavWaypoint w in c.Store.Waypoints)
            {
                location[w.Id] = w;
            }

            var adjacency = new Dictionary<string, List<KeyValuePair<string, double>>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (NavWaypoint w in c.Store.Waypoints)
            {
                adjacency[w.Id] = new List<KeyValuePair<string, double>>();
            }

            foreach (NavEdge e in c.Store.Edges)
            {
                NavWaypoint a, b;

                if (!location.TryGetValue(e.From, out a) || !location.TryGetValue(e.To, out b))
                {
                    continue;
                }

                double multiplier = 1.0;

                if (e.TagList != null)
                {
                    foreach (string tag in e.TagList)
                    {
                        double m;

                        if (c.CostTags.TryGetValue(tag, out m))
                        {
                            multiplier *= m;
                        }
                    }
                }

                double edgeCost = e.Kind == NavEdgeKind.Gate
                    ? NavGraph.GateBaseCost * multiplier
                    : NavGraph.Chebyshev(a.Location, b.Location) * multiplier;

                if (e.Kind != NavEdgeKind.Gate
                    && NavGraph.Chebyshev(a.Location, b.Location) > c.HopCap)
                {
                    // AddEdge drops an over-cap walk edge, so the oracle must too or it would be
                    // answering about a different graph.
                    continue;
                }

                adjacency[e.From].Add(new KeyValuePair<string, double>(e.To, edgeCost));
                adjacency[e.To].Add(new KeyValuePair<string, double>(e.From, edgeCost));
            }

            var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string id in adjacency.Keys)
            {
                best[id] = Double.PositiveInfinity;
            }

            best[c.From] = 0.0;

            // A linear scan for the cheapest unsettled node: these graphs have five nodes, and a
            // heap here would be more code than the thing it speeds up.
            while (true)
            {
                string current = null;
                double lowest = Double.PositiveInfinity;

                foreach (KeyValuePair<string, double> pair in best)
                {
                    if (!settled.Contains(pair.Key) && pair.Value < lowest)
                    {
                        lowest = pair.Value;
                        current = pair.Key;
                    }
                }

                if (current == null)
                {
                    break;
                }

                settled.Add(current);

                foreach (KeyValuePair<string, double> link in adjacency[current])
                {
                    double candidate = lowest + link.Value;

                    if (candidate < best[link.Key])
                    {
                        best[link.Key] = candidate;
                        previous[link.Key] = current;
                    }
                }
            }

            if (Double.IsPositiveInfinity(best[c.To]))
            {
                return false;
            }

            cost = best[c.To];

            string node = c.To;

            while (node != null)
            {
                path.Insert(0, node);

                string parent;
                node = previous.TryGetValue(node, out parent) ? parent : null;
            }

            return true;
        }

        private static HealthResult BuildHealthResult()
        {
            IList<string> report;

            Run(out report);

            // Ok while the LIVE graph cannot hit the fault, however many constructed cases show
            // it. The demonstrations keep the mechanism honest and reproducible; what this check
            // polices is whether the data has drifted into range of one. A red light for a case
            // the shard cannot reach is one people stop reading, and then it is worth nothing on
            // the day it matters.
            //
            // Warn, never Fail: the consequence is a more expensive route, not a broken one.
            return _lastAgreed ? HealthResult.Ok(_lastDetail) : HealthResult.Warn(_lastDetail);
        }
    }
}
