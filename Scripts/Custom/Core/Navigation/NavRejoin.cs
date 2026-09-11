// NavRejoin.cs — re-pick every join by the road a bot actually walks, not by straight line.
//
// WHAT A JOIN IS. When Adopt proposes a road that reaches into ground we have already authored,
// the endpoint inside that ground is remapped onto one of our waypoints and the edge is walked
// like any other (NavAdopt.cs:553). Every one of the four sites that does this picks its target
// with NavGraph.Nearest, which is pure Chebyshev distance: Z ignored, walls ignored.
//
// WHY THAT IS WRONG, AND IT IS THE SAME MISTAKE THE MERGE RULE ALREADY LEARNED. NavAdopt.cs:1038
// records it for the rebase merge in so many words - "the waypoint that replaces it is four tiles
// away in a straight line and thirty-two by road, because the road goes round the building" - and
// the merge was fixed while the join was not. The evidence is the walk audit's own detour table:
// of the twenty highest-detour edges on the graph, EIGHTEEN are joins. The worst is
// `brit-tan-1 <-> uo-road-to-britain-forge`, four tiles apart and thirty-three by road, which is
// a bot walking round three sides of a building on every trip.
//
// WHICH END MOVES, and this was settled by measurement rather than by preference. A join is a
// pairing and either end could be re-picked:
//
//   - Re-pick OUR end (keep the adopted waypoint, choose a different one of ours). Measured:
//     seventeen of the thirty-eight joins have no other waypoint of ours within the hop cap at
//     all, and the worst offenders can only swap with each other - brit-tan-1 and brit-carp-2 are
//     two doors on the same block, both thirty-odd tiles by road from the same street. There is
//     almost nothing to choose from, and nothing better.
//   - Re-pick THEIR end (keep our waypoint, choose a different adopted waypoint). 160 alternatives
//     across the same 38 joins, and brit-tan-1 alone has four.
//
// So our end is fixed. That is also the half the brief requires: our records' places and ids are
// untouched, and only which road they hang off changes.
//
// IT CANNOT WRITE NAVIGATION DATA, exactly as NavAdopt cannot. It writes a proposal to
// Data/Live/nav-adopt.json in the shape tools/editor/js/adopt.js already reads, and
// tools/editor/accept-adopt.js accepts it through /api/save/navigation against a baseHash - the
// same replicated validator, the same mandatory dry run, the same .bak and atomic rename. There is
// no code path from this file to navigation.json.
//
// WHAT IT REFUSES, each one a rule rather than a warning:
//   - a swap that is not STRICTLY shorter by road is not made;
//   - a replacement whose hops the engine will not walk in BOTH directions is dropped, not saved;
//   - a waypoint is never left with fewer edges than it had, because every swap is a swap;
//   - it proposes no waypoint, moves none, renames none and deletes none.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Server.Custom
{
    /// <summary>One join edge, and the better road found for it.</summary>
    public sealed class NavRejoinSwap
    {
        /// <summary>Our waypoint. Fixed - never moved, renamed or replaced.</summary>
        public string OurId { get; set; }

        /// <summary>The adopted waypoint it joins to today.</summary>
        public string FromId { get; set; }

        /// <summary>The adopted waypoint it should join to instead. Null when nothing is better.</summary>
        public string ToId { get; set; }

        /// <summary>
        /// The engine's own route to the current target, BOTH DIRECTIONS SUMMED, in tiles.
        ///
        /// Both ways because a join is walked both ways and FastAStarAlgorithm is greedy and
        /// therefore directional - the same pair measured 24 one way and 46 the other on the first
        /// run of this. A one-way bargain is not one.
        /// </summary>
        public int CurrentWalked { get; set; }

        /// <summary>The same for the proposed target.</summary>
        public int ProposedWalked { get; set; }

        /// <summary>Straight-line distance to the current target - the number the old rule used.</summary>
        public int CurrentLine { get; set; }

        public int ProposedLine { get; set; }

        /// <summary>The stored edge record, so removedEdges can be minted in its own order.</summary>
        public string StoredFrom { get; set; }

        public string StoredTo { get; set; }

        /// <summary>Why a candidate was rejected, when none was taken. Null on a swap.</summary>
        public string Note { get; set; }

        public bool Improves
        {
            get { return ToId != null; }
        }

        /// <summary>The detour factor the walk audit would report for the current pairing.</summary>
        public double CurrentRatio
        {
            get { return CurrentLine <= 0 ? 0.0 : (double)CurrentWalked / CurrentLine; }
        }

        public double ProposedRatio
        {
            get { return ProposedLine <= 0 ? 0.0 : (double)ProposedWalked / ProposedLine; }
        }
    }

    public sealed class NavRejoinResult
    {
        public readonly List<NavRejoinSwap> Swaps = new List<NavRejoinSwap>();

        public readonly List<string> Failures = new List<string>();

        public int Joins { get; set; }

        public int Candidates { get; set; }

        /// <summary>Candidate pairs routed. Two MovementPath calls each, one per direction.</summary>
        public int Routed { get; set; }

        public double Seconds { get; set; }

        public int Improved
        {
            get
            {
                int n = 0;

                for (int i = 0; i < Swaps.Count; i++)
                {
                    if (Swaps[i].Improves)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>Engine tiles over every join, both ways, before and after - what the change is for.</summary>
        public int WalkedBefore { get; set; }

        public int WalkedAfter { get; set; }

        public string Summary
        {
            get
            {
                return String.Format(
                    "{0} join(s), {1} candidate(s), {2} pair(s) routed in {3:F1}s: {4} improved, "
                    + "{5} engine tile(s) both ways becoming {6}, {7} failure(s).",
                    Joins, Candidates, Routed, Seconds, Improved,
                    WalkedBefore, WalkedAfter, Failures.Count);
            }
        }
    }

    public static class NavRejoin
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>
        /// Measure every join in a region and propose the ones a shorter road improves.
        ///
        /// Synchronous, unlike NavAdopt's paced passes, and that is a measured choice rather than
        /// an oversight: an adopt floods several hundred edges AND engine-verifies every hop of
        /// each, where this floods a few hundred short candidate legs and verifies only the
        /// handful it intends to propose. It is an Administrator command, like the two audits.
        /// </summary>
        public static bool TryRun(Map map, Rectangle2D region, out NavRejoinResult result, out string error)
        {
            result = new NavRejoinResult();
            error = null;

            if (map == null || map == Map.Internal)
            {
                error = "no facet";
                return false;
            }

            if (World.Saving || World.Loading)
            {
                error = "the world is saving or loading";
                return false;
            }

            var watch = Stopwatch.StartNew();

            int cap = NavigationSystem.HopMaxTiles;

            Dictionary<string, HashSet<string>> adjacency = BuildAdjacency(map);
            IList<NavWaypoint> nodes = NavigationSystem.Graph.NodesOn(map);

            // A ROAD LENGTH IS CACHED PER PAIR. brit-prov-1 carries four joins and every one of
            // them ranks the same six candidates, so without this the flood runs four times for
            // each answer. The key is unordered because a flood is symmetric here.
            var walked = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (NavEdge edge in NavigationSystem.Store.Edges)
            {
                if (edge.Kind != NavEdgeKind.Walk)
                {
                    continue;
                }

                NavWaypoint a = Nav.Waypoint(edge.From);
                NavWaypoint b = Nav.Waypoint(edge.To);

                if (a == null || b == null || a.Map != map || b.Map != map)
                {
                    continue;
                }

                bool aMine = IsOurs(a);
                bool bMine = IsOurs(b);

                // A join has exactly one end of ours. An edge between two of ours is authored road
                // and an edge between two of theirs is their road; neither is this rule's business.
                if (aMine == bMine)
                {
                    continue;
                }

                NavWaypoint mine = aMine ? a : b;
                NavWaypoint theirs = aMine ? b : a;

                if (!region.Contains(new Point2D(mine.X, mine.Y)))
                {
                    continue;
                }

                result.Joins++;

                var swap = new NavRejoinSwap
                {
                    OurId = mine.Id,
                    FromId = theirs.Id,
                    StoredFrom = edge.From,
                    StoredTo = edge.To,
                    CurrentLine = NavGraph.Chebyshev(mine.Location, theirs.Location),
                };

                int current;

                if (!TryWalked(map, mine, theirs, walked, result, out current))
                {
                    // The join we already have will not flood. That is a finding about the graph
                    // rather than an opportunity, and replacing it on the strength of a number we
                    // could not measure would be guessing.
                    swap.Note = "the engine will not route the current join; left alone";
                    result.Swaps.Add(swap);
                    continue;
                }

                swap.CurrentWalked = current;
                result.WalkedBefore += current;

                NavWaypoint best = null;
                int bestWalked = current;
                int bestLine = swap.CurrentLine;

                foreach (NavWaypoint candidate in nodes)
                {
                    if (IsOurs(candidate)
                        || Insensitive.Equals(candidate.Id, theirs.Id)
                        || adjacency[mine.Id].Contains(candidate.Id))
                    {
                        continue;
                    }

                    int line = NavGraph.Chebyshev(mine.Location, candidate.Location);

                    // WITHIN THE HOP CAP, so a replacement never needs subdividing and this
                    // proposal never has to mint a waypoint. NavAdoptJoinReach is a hundred tiles
                    // precisely because an adopt subdivides what it walks; a swap does not, so the
                    // cap is the honest limit here.
                    if (line > cap)
                    {
                        continue;
                    }

                    result.Candidates++;

                    int length;

                    if (!TryWalked(map, mine, candidate, walked, result, out length))
                    {
                        continue;
                    }

                    // STRICTLY shorter. A tie is left alone: re-pointing a join to an equally long
                    // road churns the data and moves nothing.
                    if (length < bestWalked)
                    {
                        best = candidate;
                        bestWalked = length;
                        bestLine = line;
                    }
                }

                if (best == null)
                {
                    swap.Note = "no shorter road among the candidates";
                    result.WalkedAfter += current;
                    result.Swaps.Add(swap);
                    continue;
                }

                // THE ENGINE HAS THE LAST WORD, both ways, exactly as NavAdopt requires of every
                // hop it proposes. A flood and MovementPath answer different questions and can
                // disagree about a step; a replacement the flood liked and the engine refuses must
                // fail here rather than at the next audit.
                var hops = new List<Point3D>
                {
                    Surface(map, mine.Location),
                    Surface(map, best.Location),
                };

                string failure;

                if (!NavCorridor.TryVerifyHopsBothWays(map, hops, out failure))
                {
                    result.Failures.Add(String.Format(
                        "{0} -> {1}: routed at {2} tiles, TryVerifyHopsBothWays refused: {3}",
                        mine.Id, best.Id, bestWalked, failure));

                    swap.Note = "a shorter road was found and the engine refused it";
                    result.WalkedAfter += current;
                    result.Swaps.Add(swap);
                    continue;
                }

                swap.ToId = best.Id;
                swap.ProposedWalked = bestWalked;
                swap.ProposedLine = bestLine;

                result.WalkedAfter += bestWalked;
                result.Swaps.Add(swap);

                // So a second join on the same waypoint cannot propose the same target twice.
                adjacency[mine.Id].Add(best.Id);
            }

            watch.Stop();

            result.Seconds = watch.Elapsed.TotalSeconds;

            result.Swaps.Sort(delegate(NavRejoinSwap x, NavRejoinSwap y)
            {
                return (y.CurrentWalked - y.ProposedWalked).CompareTo(x.CurrentWalked - x.ProposedWalked);
            });

            return true;
        }

        /// <summary>The report lines, worst-improvement first.</summary>
        public static IList<string> Describe(NavRejoinResult result)
        {
            var lines = new List<string>();

            if (result == null)
            {
                return lines;
            }

            foreach (NavRejoinSwap swap in result.Swaps)
            {
                if (!swap.Improves)
                {
                    continue;
                }

                lines.Add(String.Format(
                    "REJOIN   '{0}' leaves '{1}' for '{2}': {3} engine tile(s) both ways becomes {4} "
                    + "(straight line {5} -> {6}, detour {7:F2} -> {8:F2})",
                    swap.OurId, swap.FromId, swap.ToId,
                    swap.CurrentWalked, swap.ProposedWalked,
                    swap.CurrentLine, swap.ProposedLine,
                    swap.CurrentRatio, swap.ProposedRatio));
            }

            foreach (NavRejoinSwap swap in result.Swaps)
            {
                if (swap.Improves || swap.Note == null)
                {
                    continue;
                }

                lines.Add(String.Format(
                    "KEPT     '{0}' -> '{1}': {2} engine tile(s) both ways for {3} straight - {4}",
                    swap.OurId, swap.FromId, swap.CurrentWalked, swap.CurrentLine, swap.Note));
            }

            foreach (string failure in result.Failures)
            {
                lines.Add("FAILED   " + failure);
            }

            return lines;
        }

        /// <summary>
        /// The proposal, in the shape tools/editor/js/adopt.js already reads.
        ///
        /// Deliberately the SAME file and the SAME shape as an adopt, so accept-adopt.js accepts it
        /// unchanged - the baseHash, the mandatory dry run against the replicated validator, the
        /// .bak, the atomic rename and the reload token all come for free. A second acceptance path
        /// is a second place for the survivors rule to drift.
        ///
        /// `waypoints` is empty and always will be: this proposal creates no records. `removals`
        /// and `rewrites` are empty for the same reason - nothing of ours is deleted or re-pointed,
        /// which is the guarantee the brief asked for and is worth being structural.
        /// </summary>
        public static bool TryWrite(Map map, NavRejoinResult result, out string error)
        {
            var builder = new StringBuilder(4096);

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"status\": \"done\",\n");
            builder.Append("  \"mode\": \"rejoin\",\n");
            builder.Append("  \"map\": ").Append(Json.Quote(map.Name)).Append(",\n");
            builder.Append("  \"blocked\": false,\n");
            builder.Append("  \"seconds\": ").Append(Fixed(result.Seconds)).Append(",\n");
            builder.Append("  \"joins\": ").Append(result.Joins).Append(",\n");
            builder.Append("  \"improved\": ").Append(result.Improved).Append(",\n");
            builder.Append("  \"walkedBefore\": ").Append(result.WalkedBefore).Append(",\n");
            builder.Append("  \"walkedAfter\": ").Append(result.WalkedAfter).Append(",\n");
            builder.Append("  \"waypoints\": [],\n");
            builder.Append("  \"destinations\": [],\n");
            builder.Append("  \"arrivals\": [],\n");
            builder.Append("  \"removals\": [],\n");
            builder.Append("  \"rewrites\": [],\n");
            builder.Append("  \"stranded\": [],\n");
            builder.Append("  \"unreachable\": [],\n");

            builder.Append("  \"edges\": [\n");

            bool first = true;

            foreach (NavRejoinSwap swap in result.Swaps)
            {
                if (!swap.Improves)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(",\n");
                }

                first = false;

                // OUR END FIRST and no `source`, matching what this shard writes for a record it
                // measured itself - the invariant being that an adopted record is one walked once
                // at adopt time, and this edge was flooded and engine-verified here.
                builder.Append("    {\"from\":").Append(Json.Quote(swap.OurId));
                builder.Append(",\"to\":").Append(Json.Quote(swap.ToId));
                builder.Append(",\"kind\":\"walk\",\"tags\":\"road\"}");
            }

            builder.Append("\n  ],\n");

            builder.Append("  \"removedEdges\": [");

            first = true;

            foreach (NavRejoinSwap swap in result.Swaps)
            {
                if (!swap.Improves)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(", ");
                }

                first = false;

                // Minted from the STORED order, because that is what the editor's shape id is
                // minted from and a reversed pair would silently match nothing.
                builder.Append(Json.Quote("edge:" + swap.StoredFrom + ">" + swap.StoredTo));
            }

            builder.Append("],\n");

            builder.Append("  \"failures\": [");

            for (int i = 0; i < result.Failures.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(Json.Quote(result.Failures[i]));
            }

            builder.Append("],\n");

            builder.Append("  \"rejoin\": [\n");

            first = true;

            foreach (NavRejoinSwap swap in result.Swaps)
            {
                if (!first)
                {
                    builder.Append(",\n");
                }

                first = false;

                builder.Append("    {\"our\":").Append(Json.Quote(swap.OurId));
                builder.Append(",\"from\":").Append(Json.Quote(swap.FromId));
                builder.Append(",\"to\":").Append(swap.ToId == null ? "null" : Json.Quote(swap.ToId));
                builder.Append(",\"currentWalked\":").Append(swap.CurrentWalked);
                builder.Append(",\"proposedWalked\":").Append(swap.ProposedWalked);
                builder.Append(",\"currentLine\":").Append(swap.CurrentLine);
                builder.Append(",\"proposedLine\":").Append(swap.ProposedLine);

                if (swap.Note != null)
                {
                    builder.Append(",\"note\":").Append(Json.Quote(swap.Note));
                }

                builder.Append('}');
            }

            builder.Append("\n  ]\n}\n");

            return AtomicFile.Write(NavAdopt.ProposalPath, builder.ToString(), out error);
        }

        /// <summary>
        /// How many of the nearest candidates a join ranks by road before giving up on the rest.
        ///
        /// Two MovementPath calls each - one per direction - so this is the dial between "the join
        /// is right" and "an adopt takes longer". Six is enough for every case Britain has: the
        /// worst join on the graph, brit-tan-1, has four adopted waypoints within the hop cap.
        /// </summary>
        public static int RoadCandidates
        {
            get { return Config.Get("Custom.NavJoinRoadCandidates", 6); }
        }

        /// <summary>
        /// The waypoint whose WALKED ROAD from a point is shortest - the join rule itself, shared
        /// by NavAdopt (which is choosing a target for a road it is about to propose) and by
        /// NavRejoin (which is re-choosing one for a road already saved).
        ///
        /// It is a two-stage pick rather than a flood over every node, and both stages are needed.
        /// Chebyshev first, because flooding a thousand candidates to place one join would cost
        /// more than the whole adopt; road second, because Chebyshev is the rule that put a
        /// thirty-three tile road on a four-tile edge.
        ///
        /// FALLS BACK TO THE NEAREST BY LINE when nothing floods, which keeps this a strict
        /// improvement on the old rule rather than a new way to fail: the previous behaviour was
        /// to take that waypoint unconditionally, so a graph where no candidate floods gets
        /// exactly what it got before.
        /// </summary>
        public static NavWaypoint NearestByRoad(Map map, Point3D from, int reach)
        {
            if (map == null || map == Map.Internal)
            {
                return null;
            }

            NavWaypoint fallback = NavigationSystem.Graph.Nearest(from, map, reach);

            if (fallback == null)
            {
                return null;
            }

            int limit = RoadCandidates;

            if (limit <= 1)
            {
                return fallback;
            }

            var shortlist = new List<NavWaypoint>();

            foreach (NavWaypoint node in NavigationSystem.Graph.NodesOn(map))
            {
                if (NavGraph.Chebyshev(from, node.Location) <= reach)
                {
                    shortlist.Add(node);
                }
            }

            shortlist.Sort(delegate(NavWaypoint x, NavWaypoint y)
            {
                return NavGraph.Chebyshev(from, x.Location)
                    .CompareTo(NavGraph.Chebyshev(from, y.Location));
            });

            NavWaypoint best = null;
            int bestLength = Int32.MaxValue;

            for (int i = 0; i < shortlist.Count && i < limit; i++)
            {
                // The engine's own route, both ways, for the reason TryWalked gives: a bot walks
                // MovementPath and not a flood, MovementPath is greedy and therefore directional,
                // and it is also the number the walk audit grades this against.
                Point3D goal = Surface(map, shortlist[i].Location);
                int line = NavGraph.Chebyshev(from, shortlist[i].Location);

                int length;

                if (line <= 1)
                {
                    length = line * 2;
                }
                else
                {
                    var there = new MovementPath(from, goal, map);
                    var back = new MovementPath(goal, from, map);

                    if (!there.Success || there.Directions == null
                        || !back.Success || back.Directions == null)
                    {
                        continue;
                    }

                    length = there.Directions.Length + back.Directions.Length;
                }

                if (length < bestLength)
                {
                    bestLength = length;
                    best = shortlist[i];
                }
            }

            return best ?? fallback;
        }

        // ---- internals -------------------------------------------------------------------------

        /// <summary>
        /// A record this shard measured, rather than one adopted from uo-offline.
        ///
        /// The invariant in navigation.json is exact and was checked before this leaned on it:
        /// 945 of 945 `uo-`-prefixed waypoints carry source "uo-offline" and 55 of 55 without the
        /// prefix carry none. The SOURCE is read rather than the prefix, because the source is the
        /// thing that means it.
        /// </summary>
        private static bool IsOurs(NavWaypoint waypoint)
        {
            return String.IsNullOrEmpty(waypoint.Source);
        }

        private static Dictionary<string, HashSet<string>> BuildAdjacency(Map map)
        {
            var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (NavWaypoint node in NavigationSystem.Graph.NodesOn(map))
            {
                adjacency[node.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (NavEdge edge in NavigationSystem.Store.Edges)
            {
                if (edge.Kind != NavEdgeKind.Walk
                    || !adjacency.ContainsKey(edge.From)
                    || !adjacency.ContainsKey(edge.To))
                {
                    continue;
                }

                adjacency[edge.From].Add(edge.To);
                adjacency[edge.To].Add(edge.From);
            }

            return adjacency;
        }

        /// <summary>
        /// The road between two waypoints as THE ENGINE WILL PLAN IT, both ways, summed.
        ///
        /// THIS WAS A SYMMETRIC FLOOD AND THE FLOOD WAS THE WRONG RULER. NavCorridor.TryPath is a
        /// breadth-first flood: exhaustive, and the same length in both directions. What a bot
        /// actually walks is MovementPath, which is greedy best-first with a 300-node budget and
        /// therefore DIRECTIONAL - and it is also the number [WalkAudit reports as the detour. So
        /// ranking by the flood optimised one function and was graded on another.
        ///
        /// Measured, on the first run of this: brit-tan-1's join was 32 flood-tiles and became 20,
        /// which the flood called a large win - while the engine's own route went from 33/33 to
        /// 24/46, a small LOSS. In aggregate the flood was a fair proxy (1350 engine tiles over the
        /// joins became 929) but on the one edge everybody would look at it inverted the verdict.
        ///
        /// Both directions summed, because a join is walked both ways and a one-way bargain is not
        /// one. A candidate the engine refuses in either direction is rejected here rather than
        /// ranked last - TryVerifyHopsBothWays would refuse it a moment later anyway.
        ///
        /// It is also about a hundred times cheaper than the flood it replaces, which is the rare
        /// case where the more honest measure is the faster one.
        /// </summary>
        private static bool TryWalked(
            Map map, NavWaypoint a, NavWaypoint b,
            Dictionary<string, int> cache, NavRejoinResult result, out int tiles)
        {
            string key = String.CompareOrdinal(a.Id, b.Id) <= 0
                ? a.Id + ">" + b.Id
                : b.Id + ">" + a.Id;

            if (cache.TryGetValue(key, out tiles))
            {
                return tiles > 0;
            }

            result.Routed++;

            int there, back;

            if (!TryRoute(map, a, b, out there) || !TryRoute(map, b, a, out back))
            {
                cache[key] = 0;
                tiles = 0;
                return false;
            }

            tiles = there + back;
            cache[key] = tiles;

            return true;
        }

        /// <summary>
        /// One direction of the engine's own route, in tiles.
        ///
        /// An adjacent goal has no path - MovementPath returns nothing for one (MovementPath.cs:34)
        /// - and its route length is its straight line by definition, which is what [NavAudit and
        /// [WalkAudit both already say about the same case.
        /// </summary>
        private static bool TryRoute(Map map, NavWaypoint from, NavWaypoint to, out int tiles)
        {
            int line = NavGraph.Chebyshev(from.Location, to.Location);

            if (line <= 1)
            {
                tiles = line;
                return true;
            }

            var path = new MovementPath(Surface(map, from.Location), Surface(map, to.Location), map);

            if (!path.Success || path.Directions == null)
            {
                tiles = 0;
                return false;
            }

            tiles = path.Directions.Length;

            return true;
        }

        private static Point3D Surface(Map map, Point3D point)
        {
            return new Point3D(point.X, point.Y, NavWalker.ResolveZ(map, point));
        }

        private static string Fixed(double value)
        {
            return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
