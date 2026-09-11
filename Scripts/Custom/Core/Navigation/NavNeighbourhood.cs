// NavNeighbourhood.cs — can a bot that stopped NEXT to a waypoint still leave it?
//
// WHY THIS EXISTS
// ---------------
// Both existing instruments start every measurement ON a waypoint, and NavAudit.cs says so in its
// own header:
//
//     START TILE. The audit paths waypoint-tile to waypoint-tile. A walker starts the next hop
//     from wherever it stopped, which is anywhere within `ArrivalRangeFor` (2 tiles by default)
//     of the waypoint - a 5x5 box the audit never validated.
//
// [WalkAudit inherited the same blind spot for the same reason: BuildWorkList seeds each walk at
// the authored tile. Commit 118be12f already wrote down that a sweep "structurally could not see"
// a bot's failure to route AWAY from where it stood.
//
// THE MEASUREMENT THAT NAMED IT. `uo-wp-194-s1 -> uo-wp-194` failed once in window C and four
// times in window D. [NavHop paths that edge in both directions and in segments, [TileProbe says
// the goal is clean stone with no statics, no items and all eight neighbour steps ALLOWED,
// [NavAudit walks it clean, and [WalkAudit walked all 2304 edges clean. But the bot was not on the
// waypoint: it gave up at 2035,2832, and [NavHop from THAT tile to the same goal answers ok:false.
// A displacement of two tiles drops a bot into a pocket the pathfinder will not route out of.
//
// WHAT A CLIFF IS, EXACTLY
//
//     The waypoint can path to a graph neighbour. A standable tile inside the waypoint's own
//     arrival tolerance cannot path to that same neighbour.
//
// Both halves are load-bearing. Without the first, every NavAudit BLOCKED edge would be re-reported
// here twenty-four times over. Without the second this is just NavAudit again.
//
// THE BOX IS THE ARRIVAL TOLERANCE, NOT THE EIGHT NEIGHBOURS, and that is not a detail: the tile
// that produced the finding is at Chebyshev 2. NavWalker.ArrivalRangeFor is the authority on how
// far off a walker may legitimately stop - the waypoint's own ArrivalRange when it authored one,
// and DefaultArrivalRange (2) when it did not, which is 996 of the 1000 records - so this reads
// that same method's rule rather than a constant of its own. A radius-1 check would have been
// cheaper by two thirds and blind to the case it was built for.
//
// AND THE GOAL IS THE NEXT WAYPOINT, NOT THIS ONE. "Can a displaced bot get back onto its
// waypoint" is a different and weaker question: the walker does not route via the waypoint it
// stopped near, it plans straight at the next goal. Asking the weak question would have passed
// 2035,2832 if the pocket happens to open toward the waypoint.
//
// COST. It is engine-only - no probe mobile, no walker, no world mutation - but it is the most
// MovementPath calls anything in this tree makes: every standable tile of every box against every
// neighbour. Which is why it is not on the default [NavAudit path; see NavAudit for the gating.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using Server.Custom;

namespace Server.Custom
{
    /// <summary>
    /// One (waypoint, neighbour) pair that a displaced bot cannot complete.
    ///
    /// Aggregated per pair rather than per tile deliberately. A bad pocket typically disables most
    /// of a box at once, and twenty-four rows saying the same thing about one waypoint would bury
    /// the twenty other waypoints in the report. The count and one sample tile are what an author
    /// acts on; the full tile list is recoverable by running [TileProbe on the sample.
    /// </summary>
    public sealed class NavCliff
    {
        public string WaypointId { get; set; }

        public string NeighbourId { get; set; }

        public string MapName { get; set; }

        /// <summary>The waypoint's own tile, at its resolved standing Z.</summary>
        public Point3D At { get; set; }

        /// <summary>How far off a walker may legitimately stop here - the box's radius.</summary>
        public int Range { get; set; }

        /// <summary>Standable tiles in the box, the waypoint's own tile excluded.</summary>
        public int Standable { get; set; }

        /// <summary>How many of those cannot path to this neighbour.</summary>
        public int Stranded { get; set; }

        /// <summary>
        /// How many of the stranded tiles are ADJACENT to the waypoint - Chebyshev 1.
        ///
        /// Its own count because it settles a design question that a sample tile cannot: whether a
        /// radius-1 check would have found this cliff. Measured on the current graph the answer is
        /// no for every one of them, which is why this scan reads NavWalker.ArrivalRangeFor's box
        /// rather than the eight neighbours.
        /// </summary>
        public int StrandedAdjacent { get; set; }

        /// <summary>
        /// The NEAREST stranded tile, for [TileProbe and for the editor to jump to.
        ///
        /// Nearest rather than first-found, and the difference is not cosmetic: the scan walks the
        /// box from its corner, so "the first one" is always a corner tile and every sample would
        /// report distance 2 whatever the truth was - a number that looks like a finding and is an
        /// artefact of a loop order.
        /// </summary>
        public Point3D Sample { get; set; }

        /// <summary>How far the sample tile is from the waypoint, in tiles.</summary>
        public int SampleDistance { get; set; }
    }

    /// <summary>What one scan found, and what it cost.</summary>
    public sealed class NavNeighbourhoodResult
    {
        public readonly List<NavCliff> Cliffs = new List<NavCliff>();

        public int Waypoints { get; set; }

        public int StandableTiles { get; set; }

        public int Paths { get; set; }

        public double Seconds { get; set; }

        /// <summary>Waypoints carrying at least one cliff neighbour.</summary>
        public int AffectedWaypoints
        {
            get
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < Cliffs.Count; i++)
                {
                    seen.Add(Cliffs[i].WaypointId);
                }

                return seen.Count;
            }
        }

        public string Summary
        {
            get
            {
                return String.Format(
                    "{0} waypoint(s), {1} standable approach tile(s), {2} engine path(s) in "
                    + "{3:F1}s: {4} cliff pair(s) on {5} waypoint(s).",
                    Waypoints, StandableTiles, Paths, Seconds, Cliffs.Count, AffectedWaypoints);
            }
        }
    }

    public static class NavNeighbourhood
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>
        /// Every waypoint on a facet, every standable tile a walker may stop on near it, every
        /// graph neighbour it is supposed to be able to leave for.
        /// </summary>
        public static NavNeighbourhoodResult Scan(Map map)
        {
            var result = new NavNeighbourhoodResult();

            if (map == null || map == Map.Internal)
            {
                return result;
            }

            var timer = Stopwatch.StartNew();

            Dictionary<string, List<NavWaypoint>> neighbours = BuildAdjacency(map);
            IList<NavWaypoint> nodes = NavigationSystem.Graph.NodesOn(map);

            foreach (NavWaypoint waypoint in nodes)
            {
                List<NavWaypoint> outward;

                if (!neighbours.TryGetValue(waypoint.Id, out outward) || outward.Count == 0)
                {
                    // An isolated waypoint has nowhere to be stranded from. Nav.Data already warns
                    // about islands and [NavAudit repeats it; this is not the instrument for that.
                    continue;
                }

                result.Waypoints++;

                Point3D home = Surface(map, waypoint.Location);

                // ONLY THE HOPS THE WAYPOINT ITSELF CAN MAKE. A neighbour the authored tile cannot
                // reach is a BLOCKED edge, which [NavAudit already reports by name - re-reporting it
                // once per box tile would drown this list in findings that are not cliffs.
                var reachable = new List<Point3D>();
                var reachableIds = new List<string>();

                for (int i = 0; i < outward.Count; i++)
                {
                    Point3D goal = Surface(map, outward[i].Location);

                    result.Paths++;

                    if (Paths(map, home, goal))
                    {
                        reachable.Add(goal);
                        reachableIds.Add(outward[i].Id);
                    }
                }

                if (reachable.Count == 0)
                {
                    continue;
                }

                int range = RangeFor(waypoint);

                // Counted per neighbour, so the report can say "eleven of the fourteen standable
                // approach tiles are stranded" rather than only "some are".
                var stranded = new int[reachable.Count];
                var strandedAdjacent = new int[reachable.Count];
                var sample = new Point3D[reachable.Count];
                var sampleDistance = new int[reachable.Count];
                int standable = 0;

                for (int i = 0; i < reachable.Count; i++)
                {
                    sampleDistance[i] = Int32.MaxValue;
                }

                for (int dx = -range; dx <= range; dx++)
                {
                    for (int dy = -range; dy <= range; dy++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        Point3D tile;

                        if (!TryStand(map, home, waypoint.X + dx, waypoint.Y + dy, out tile))
                        {
                            continue;
                        }

                        standable++;

                        for (int i = 0; i < reachable.Count; i++)
                        {
                            result.Paths++;

                            if (Paths(map, tile, reachable[i]))
                            {
                                continue;
                            }

                            int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

                            stranded[i]++;

                            if (distance <= 1)
                            {
                                strandedAdjacent[i]++;
                            }

                            if (distance < sampleDistance[i])
                            {
                                sample[i] = tile;
                                sampleDistance[i] = distance;
                            }
                        }
                    }
                }

                result.StandableTiles += standable;

                for (int i = 0; i < reachable.Count; i++)
                {
                    if (stranded[i] == 0)
                    {
                        continue;
                    }

                    result.Cliffs.Add(new NavCliff
                    {
                        WaypointId = waypoint.Id,
                        NeighbourId = reachableIds[i],
                        MapName = map.Name,
                        At = home,
                        Range = range,
                        Standable = standable,
                        Stranded = stranded[i],
                        StrandedAdjacent = strandedAdjacent[i],
                        Sample = sample[i],
                        SampleDistance = sampleDistance[i],
                    });
                }
            }

            timer.Stop();

            result.Seconds = timer.Elapsed.TotalSeconds;

            // Worst first: the pair that strands the largest share of its box is the one an author
            // should look at, and a raw count would put a wide box ahead of a hopeless narrow one.
            result.Cliffs.Sort(Compare);

            return result;
        }

        /// <summary>The report lines, in [NavAudit's and [WalkAudit's prefix style.</summary>
        public static IList<string> Describe(NavNeighbourhoodResult result, int cap)
        {
            var lines = new List<string>();

            if (result == null)
            {
                return lines;
            }

            for (int i = 0; i < result.Cliffs.Count && i < cap; i++)
            {
                NavCliff cliff = result.Cliffs[i];

                lines.Add(String.Format(
                    "CLIFF    '{0}' ({1},{2}) -> '{3}': {4} of {5} standable approach tile(s) "
                    + "within {6} cannot path it ({7} of them adjacent), nearest {8},{9} at {10} tile(s)",
                    cliff.WaypointId,
                    cliff.At.X,
                    cliff.At.Y,
                    cliff.NeighbourId,
                    cliff.Stranded,
                    cliff.Standable,
                    cliff.Range,
                    cliff.StrandedAdjacent,
                    cliff.Sample.X,
                    cliff.Sample.Y,
                    cliff.SampleDistance));
            }

            if (result.Cliffs.Count > cap)
            {
                lines.Add(String.Format(
                    "CLIFF    ... and {0} more; the full list is in the snapshot.",
                    result.Cliffs.Count - cap));
            }

            return lines;
        }

        /// <summary>The `cliffs` array, shared by nav-audit.json and walk-audit.json.</summary>
        public static void AppendJson(System.Text.StringBuilder builder, NavNeighbourhoodResult result)
        {
            builder.Append("\"cliffs\":[");

            if (result != null)
            {
                for (int i = 0; i < result.Cliffs.Count; i++)
                {
                    NavCliff cliff = result.Cliffs[i];

                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append("{\"waypoint\":").Append(Json.Quote(cliff.WaypointId));
                    builder.Append(",\"neighbour\":").Append(Json.Quote(cliff.NeighbourId));
                    builder.Append(",\"map\":").Append(Json.Quote(cliff.MapName));
                    builder.Append(",\"x\":").Append(cliff.At.X);
                    builder.Append(",\"y\":").Append(cliff.At.Y);
                    builder.Append(",\"z\":").Append(cliff.At.Z);
                    builder.Append(",\"range\":").Append(cliff.Range);
                    builder.Append(",\"standable\":").Append(cliff.Standable);
                    builder.Append(",\"stranded\":").Append(cliff.Stranded);
                    builder.Append(",\"strandedAdjacent\":").Append(cliff.StrandedAdjacent);
                    builder.Append(",\"tileX\":").Append(cliff.Sample.X);
                    builder.Append(",\"tileY\":").Append(cliff.Sample.Y);
                    builder.Append(",\"tileZ\":").Append(cliff.Sample.Z);
                    builder.Append(",\"tileDistance\":").Append(cliff.SampleDistance);
                    builder.Append('}');
                }
            }

            builder.Append(']');
        }

        // ---- internals -------------------------------------------------------------------------

        private static int Compare(NavCliff a, NavCliff b)
        {
            double left = a.Standable > 0 ? (double)a.Stranded / a.Standable : 0.0;
            double right = b.Standable > 0 ? (double)b.Stranded / b.Standable : 0.0;

            int order = right.CompareTo(left);

            return order != 0 ? order : b.Stranded.CompareTo(a.Stranded);
        }

        /// <summary>
        /// Walk edges only, both directions, resolved to records on this facet.
        ///
        /// Gate edges are excluded for the same reason [NavAudit excludes them: a moongate hop is
        /// not walked, so no amount of standing in the wrong place can strand it.
        /// </summary>
        private static Dictionary<string, List<NavWaypoint>> BuildAdjacency(Map map)
        {
            var adjacency = new Dictionary<string, List<NavWaypoint>>(StringComparer.OrdinalIgnoreCase);

            foreach (NavEdge edge in NavigationSystem.Store.Edges)
            {
                if (edge.Kind != NavEdgeKind.Walk)
                {
                    continue;
                }

                NavWaypoint a = Nav.Waypoint(edge.From);
                NavWaypoint b = Nav.Waypoint(edge.To);

                if (a == null || b == null || a.Map != map || b.Map != map || a.Id == b.Id)
                {
                    continue;
                }

                Add(adjacency, a.Id, b);
                Add(adjacency, b.Id, a);
            }

            return adjacency;
        }

        private static void Add(
            Dictionary<string, List<NavWaypoint>> adjacency, string id, NavWaypoint node)
        {
            List<NavWaypoint> list;

            if (!adjacency.TryGetValue(id, out list))
            {
                list = new List<NavWaypoint>();
                adjacency[id] = list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (Insensitive.Equals(list[i].Id, node.Id))
                {
                    return;
                }
            }

            list.Add(node);
        }

        /// <summary>
        /// NavWalker.ArrivalRangeFor's rule for a Walk step, which is the only rule that decides
        /// how far off a walker is allowed to stop. Kept as a mirror rather than a constant so a
        /// change there cannot leave this box the wrong size and silently narrow the check.
        /// </summary>
        private static int RangeFor(NavWaypoint waypoint)
        {
            return waypoint.ArrivalRange > 0 ? waypoint.ArrivalRange : NavWalker.DefaultArrivalRange;
        }

        /// <summary>
        /// Where a mobile would stand on this tile, HINTED FROM THE WAYPOINT'S OWN Z.
        ///
        /// The hint is the anchor's Z rather than the tile's, exactly as NavArrivals.Scatter hints
        /// it, and for the same measured reason: a waypoint on a raised shop floor asked about the
        /// tile beside it gets the street underneath unless it says which storey it means. A false
        /// answer means nothing standable near that level, which is not a cliff - it is a tile no
        /// walker can stop on, so there is nothing to strand.
        /// </summary>
        private static bool TryStand(Map map, Point3D hint, int x, int y, out Point3D stand)
        {
            int z;

            stand = Point3D.Zero;

            if (!NavWalker.TryResolveZ(map, new Point3D(x, y, hint.Z), out z))
            {
                return false;
            }

            // The same fit test NavWalkFailures.CauseFor uses to decide a goal is unstandable, so
            // the two instruments agree about which tiles a mobile can occupy.
            if (!map.CanFit(x, y, z, 16, false, false, true))
            {
                return false;
            }

            stand = new Point3D(x, y, z);

            return true;
        }

        /// <summary>
        /// One direction only, unlike [NavAudit's CanWalk.
        ///
        /// The question here is directional by nature: a bot standing off the waypoint is LEAVING.
        /// Whether anything can get back is the edge's own business and [NavAudit already tests
        /// both ways on the authored tiles.
        /// </summary>
        private static bool Paths(Map map, Point3D from, Point3D to)
        {
            if (from.X == to.X && from.Y == to.Y)
            {
                return true;
            }

            // MovementPath returns no path for an adjacent goal, which is a pass rather than a
            // failure - [NavAudit skips adjacent edges for exactly this reason.
            if (Math.Max(Math.Abs(from.X - to.X), Math.Abs(from.Y - to.Y)) <= 1)
            {
                return true;
            }

            return new MovementPath(from, to, map).Success;
        }

        private static Point3D Surface(Map map, Point3D point)
        {
            return new Point3D(point.X, point.Y, NavWalker.ResolveZ(map, point));
        }
    }
}
